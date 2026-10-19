using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Research;

/// <summary>
/// Система triggers (research.ts): раз в секунду темы-открытия, у которых случилось условие, открываются сами — игрок
/// достроил лабораторию (lab), нашёл месторождение — видит его сейчас или поставил на него шахту (deposit — любое, или
/// своего вида), в его хранилище или переработке появился готовый ресурс (refined). Проверяются только у тех, у кого
/// они ещё не открыты.
/// <para>
/// Главный запрос — игроки; таблицы кадра — здания (вид, хозяин, место) и площадки (Site): по ним лаборатории, шахты,
/// хранилища и переработки находятся без вызовов. Месторождения — слой deposits целиком, видно ли — пакетом Sees.
/// </para>
/// </summary>
internal sealed class Triggers : ISystem
{
    /// <summary>Раз во столько тиков (Math.round(1 / step)).</summary>
    public static int Ticks => Math.Max(1, (int)Math.Round(1 / Core.Step));

    private Column _id, _proto, _owner, _x, _y;

    public void Bind(SystemBinding binding)
    {
        R.Load(binding.Component);
        _id = binding.Column(Names.Player.Name, Names.Player.Id);
        var buildings = binding.Table(0);
        _proto = buildings.Column(Names.Proto.Name, Names.Proto.Id);
        _owner = buildings.Column(Names.Owner.Name, Names.Owner.Player);
        _x = buildings.Column(Names.Position.Name, Names.Position.X);
        _y = buildings.Column(Names.Position.Name, Names.Position.Y);
    }

    /// <summary>Здание из таблицы кадра.</summary>
    private readonly record struct Built(uint Entity, uint Player, string Type, double X, double Y, bool Site);

    public void Run(SystemContext ctx)
    {
        var topics = Knowledge.Topics;
        var waiting = topics.Where(pair => pair.Value.Trigger is not null).Select(pair => pair.Key).ToList();
        if (waiting.Count == 0 || ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        var ids = ctx.Column(_id);
        var players = new List<(uint Entity, uint Id)>();
        for (var row = 0; row < ctx.Count; row++)
            players.Add((ctx.Entities[row], (uint)ids[row]));
        players.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        List<Built>? buildings = null;
        List<Spot>? spots = null;
        foreach (var (entity, player) in players)
        {
            if (player == 0)
                continue;
            var done = Knowledge.Done(call, entity);
            var open = waiting.Where(topic => !done.Contains(topic)).ToList();
            if (open.Count == 0)
                continue;
            buildings ??= Read(ctx);
            HashSet<string>? found = null;
            foreach (var topic in open)
            {
                var trigger = topics[topic].Trigger!;
                var happened = trigger switch
                {
                    "lab" => buildings.Any(b => b.Player == player && !b.Site && Factory.Building(b.Type)?.Researches == true),
                    "refined" => HasRefined(call, buildings, player),
                    _ => (found ??= Found(call, buildings, player, spots ??= AllDeposits())).Count > 0 && (trigger == "deposit" || found.Contains(trigger)),
                };
                if (happened)
                    Knowledge.Learn(call, entity, topic);
            }
        }
    }

    private List<Built> Read(in SystemContext ctx)
    {
        var sites = new HashSet<uint>(ctx.Table(1).Entities.ToArray());
        var table = ctx.Table(0);
        var protos = table.Column(_proto);
        var owners = table.Column(_owner);
        var xs = table.Column(_x);
        var ys = table.Column(_y);
        var list = new List<Built>(table.Count);
        for (var row = 0; row < table.Count; row++)
            if (Core.ProtoName((int)protos[row], "buildings") is { } type)
                list.Add(new Built(table.Entities[row], (uint)owners[row], type, xs[row], ys[row], sites.Contains(table.Entities[row])));
        list.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        return list;
    }

    /// <summary>Есть ли у игрока готовый ресурс в хранилище или переработке (hasRefined).</summary>
    private static bool HasRefined(Call call, List<Built> buildings, uint player)
    {
        foreach (var built in buildings)
        {
            if (built.Player != player || Factory.Building(built.Type) is not { } spec || !(spec.Stores || spec.Refines is not null) || !call.Has(built.Entity, S.Stock))
                continue;
            foreach (var resource in Core.Tables.Resources.Keys)
                if (Stocks.Amount(call, built.Entity, resource) > 0)
                    return true;
        }
        return false;
    }

    /// <summary>Все месторождения карты (слой deposits).</summary>
    private static List<Spot> AllDeposits()
    {
        var list = new List<Spot>();
        if (S.Deposits is not { } layer)
            return list;
        foreach (var item in Tools.Items(layer, 0, 0, 0, Core.Reach))
            list.Add(new Spot((int)item.X, (int)item.Y, (int)item.Fields[0], item.Fields[1]));
        return list;
    }

    /// <summary>
    /// Виды месторождений, которые игрок видит сейчас (depositsSeen: виден левый верхний тайл), и те, что под его шахтами,
    /// в том числе недостроенными (depositsMined).
    /// </summary>
    private static HashSet<string> Found(Call call, List<Built> buildings, uint player, List<Spot> spots)
    {
        var kinds = new HashSet<string>();
        if (spots.Count > 0)
        {
            var xs = spots.Select(spot => spot.X + 0.5).ToArray();
            var ys = spots.Select(spot => spot.Y + 0.5).ToArray();
            var seen = new byte[spots.Count];
            Tools.Sees(player, xs, ys, seen);
            for (var i = 0; i < spots.Count; i++)
                if (seen[i] == 2)
                    kinds.Add(spots[i].Name);
        }
        foreach (var built in buildings)
            if (built.Player == player && Factory.Building(built.Type)?.Extract == true && Deposits.At((int)built.X, (int)built.Y) is { } spot)
                kinds.Add(spot.Name);
        return kinds;
    }
}
