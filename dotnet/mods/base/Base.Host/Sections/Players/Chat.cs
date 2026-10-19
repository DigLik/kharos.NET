using System.Globalization;
using Base.Host.Kernel;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Команды чата base (host.ts: give, spawn, killunits, heal; docs/dotnet-n6.md, Н6-7). Объявлены в mod.json (commands),
/// хост находит игроков и вид юнита и шлёт приказ chat.&lt;имя&gt; с <see cref="ChatCall"/>; ответ — сигнал chat.reply
/// автору. Ников мод не знает: в ответе игрок — подстановка «player» (номер), хост дописывает к ней «name». Права
/// проверяет мод: админские команды берут только приказы с правом Admin (приказ может прийти и мимо чата). Reset и
/// reveal — дело движка (игрок начинает заново, туман), их хост держит сам.
/// </summary>
internal static class Chat
{
    /// <summary>Больше скольких юнитов /spawn за раз не ставит (SPAWN_MOST).</summary>
    public const int SpawnMost = 50;

    public const string Give = "chat.give", Spawn = "chat.spawn", KillUnits = "chat.killunits", Heal = "chat.heal", Credits = "chat.credits";

    public static void Setup(ModBuilder mod)
    {
        mod.Order(Give, ctx => Run(ctx, false, GiveTo));
        mod.Order(Spawn, ctx => Run(ctx, true, SpawnAt));
        mod.Order(KillUnits, ctx => Run(ctx, true, Kill));
        mod.Order(Heal, ctx => Run(ctx, true, Mend));
        mod.Order(Credits, ctx => Run(ctx, true, SetCredits));
    }

    private delegate void Command(Call call, Reply reply, uint author, ChatCall words);

    /// <summary>Ответы команды: автору или другому игроку.</summary>
    private readonly struct Reply(CommandWriter writer, uint author)
    {
        public void Say(string key, params (string Name, object Value)[] args) => To(author, key, args);

        public void To(uint player, string key, params (string Name, object Value)[] args) =>
            writer.SignalTo(ChatCall.ReplySignal, ChatCall.Text(key, args), [player]);
    }

    private static void Run(OrderContext ctx, bool admin, Command command)
    {
        var reply = new Reply(ctx.Commands, ctx.Player);
        var name = ctx.Name["chat.".Length..];
        if (admin && (ctx.Rights & OrderRights.Admin) == 0)
        {
            reply.Say("noCommand", ("name", name));
            return;
        }
        if (ChatCall.Read(ctx.Payload) is not { } words)
        {
            reply.Say("usage", ("command", "/" + name));
            return;
        }
        command(Call.Of(ctx), reply, ctx.Player, words);
    }

    /// <summary>Игрок, о котором речь: названный или сам автор; нет ни того ни другого — ответ «укажите игрока».</summary>
    private static uint? Target(Reply reply, uint author, ChatCall words, int index)
    {
        var named = index < words.Words.Count;
        var who = named ? words.Player(index) : author;
        if (who == 0)
            reply.Say(named ? "noPlayer" : "needPlayer", ("name", words.Word(index)));
        return who == 0 ? null : who;
    }

    /// <summary>Своё у игрока: юниты и здания, без турелей — они уходят вместе с носителем (ownedBy).</summary>
    private static (List<uint> Units, List<uint> Buildings) OwnedBy(Call call, uint player)
    {
        var units = new List<uint>();
        var buildings = new List<uint>();
        foreach (var entity in Core.All(S.Owner))
        {
            if (call.OwnerOf(entity) != player || call.Has(entity, S.Mount))
                continue;
            if (call.Has(entity, S.Unit))
                units.Add(entity);
            else if (call.Has(entity, S.Building))
                buildings.Add(entity);
        }
        units.Sort();
        buildings.Sort();
        return (units, buildings);
    }

    /// <summary>/give: союзнику — деньги сразу; себе, чужому и сверх счёта (в долг не дают) — отказ.</summary>
    private static void GiveTo(Call call, Reply reply, uint from, ChatCall words)
    {
        if (from == 0)
        {
            reply.Say("needPlayer");
            return;
        }
        var to = words.Player(0);
        if (to == 0)
        {
            reply.Say("noPlayer", ("name", words.Word(0)));
            return;
        }
        if (to == from)
        {
            reply.Say("giveSelf");
            return;
        }
        if (!Alliances.Allied(call, from, to))
        {
            reply.Say("giveNoAlly", ("player", to));
            return;
        }
        if (!double.TryParse(words.Word(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || amount != Math.Floor(amount) || amount > 9007199254740991)
        {
            reply.Say("usage", ("command", "/give <player> <amount>"));
            return;
        }
        if (!Accounts.Pay(call, from, amount))
        {
            reply.Say("giveShort", ("amount", amount));
            return;
        }
        Accounts.Add(call, to, amount);
        reply.To(from, "giveSent", ("player", to), ("amount", amount));
        reply.To(to, "giveGot", ("player", from), ("amount", amount));
    }

    /// <summary>/spawn: count юнитов вида unit на свободных тайлах у середины базы игрока (себе — без ника).</summary>
    private static void SpawnAt(Call call, Reply reply, uint author, ChatCall words)
    {
        var type = words.Word(0);
        if (Factory.Unit(type) is not { } spec)
        {
            reply.Say("unitTypes", ("list", string.Join(", ", Core.Tables.Units.Keys)));
            return;
        }
        var count = double.TryParse(words.Word(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var asked) && double.IsFinite(asked) ? (int)Math.Floor(Math.Clamp(asked, 0, 1000)) : 1;
        count = Math.Min(SpawnMost, Math.Max(1, count));
        if (Target(reply, author, words, 2) is not { } who)
            return;
        var (units, buildings) = OwnedBy(call, who);
        var points = (buildings.Count > 0 ? buildings : units).Select(call.At).ToList();
        if (points.Count == 0)
        {
            reply.Say("noBase", ("player", who), ("number", who));
            return;
        }
        var air = spec.Kind == "air";
        var tiles = Tools.FreeTiles((int)Math.Floor(points.Sum(p => p.X) / points.Count), (int)Math.Floor(points.Sum(p => p.Y) / points.Count), count, 2, air ? PathFlags.Air : PathFlags.None, spec.Radius);
        var made = 0;
        foreach (var (x, y) in tiles)
        {
            if (!air && !Tools.Walkable(x, y, spec.Kind == "infantry" ? PathFlags.Foot : PathFlags.None))
                continue;
            Factory.SpawnUnit(call, type, who, x + 0.5, y + 0.5);
            made++;
        }
        reply.Say("spawned", ("player", who), ("unit", type), ("count", made));
    }

    /// <summary>/killunits: убрать юнитов игрока или всех (all) без взрыва; здания остаются.</summary>
    private static void Kill(Call call, Reply reply, uint author, ChatCall words)
    {
        var all = string.Equals(words.Word(0), "all", StringComparison.OrdinalIgnoreCase);
        var units = new List<uint>();
        if (all)
        {
            foreach (var player in Accounts.Players(call))
                units.AddRange(OwnedBy(call, player).Units);
        }
        else if (Target(reply, author, words, 0) is { } who)
        {
            units = OwnedBy(call, who).Units;
        }
        else
        {
            return;
        }
        foreach (var unit in units)
        {
            Commands.Free(call, unit);
            call.Destroy(unit);
        }
        reply.Say("unitsRemoved", ("count", units.Count));
    }

    /// <summary>/credits: поставить игроку кредиты; «+N» и «-N» — прибавить и убавить.</summary>
    private static void SetCredits(Call call, Reply reply, uint author, ChatCall words)
    {
        var who = words.Player(0);
        if (who == 0)
        {
            reply.Say("noPlayer", ("name", words.Word(0)));
            return;
        }
        var text = words.Word(1);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || !double.IsFinite(amount))
        {
            reply.Say("usage", ("command", "/credits <player> <amount>"));
            return;
        }
        var relative = text.StartsWith('+') || text.StartsWith('-');
        Accounts.SetCredits(call, who, relative ? Accounts.Credits(call, who) + amount : amount);
        reply.Say("creditsNow", ("player", who), ("credits", Math.Max(0, Math.Floor((relative ? Accounts.Credits(call, who) + amount : amount) + 0.5))));
    }

    /// <summary>/heal: вся техника и здания игрока целы.</summary>
    private static void Mend(Call call, Reply reply, uint author, ChatCall words)
    {
        if (Target(reply, author, words, 0) is not { } who)
            return;
        var (units, buildings) = OwnedBy(call, who);
        foreach (var entity in units.Concat(buildings))
            if (call.Has(entity, S.Health))
                call.Set(entity, S.Health).Number(S.HealthValue, call.Num(entity, S.Health, S.HealthMax));
        reply.Say("healed", ("player", who), ("count", units.Count + buildings.Count));
    }
}
