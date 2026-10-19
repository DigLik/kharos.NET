using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Logistics;
using Base.Host.Sections.Mining;
using Base.Shared.Rules;
using Base.Shared.Signals;
using MemoryPack;

namespace Base.Host.Sections.Trade;

/// <summary>Груз кораблей с подарками (Transfer.goods в TS): космопорт → груз → сколько. Лежит в памяти мода: Transfer схемы груза не держит.</summary>
[MemoryPackable]
internal sealed partial class Holds
{
    public const string Key = "trade";

    public Dictionary<uint, Dictionary<string, double>> Ships { get; set; } = [];

    /// <summary>Трюмы кораблей из памяти мода.</summary>
    public static Holds Read(Call call) =>
        Memory.Of(call).Get(Key) is { Length: > 0 } bytes ? MemoryPackSerializer.Deserialize<Holds>(bytes) ?? new Holds() : new Holds();

    /// <summary>Пишет трюмы в память мода.</summary>
    public void Write(Call call) => Memory.Of(call).Put(call, Key, MemoryPackSerializer.Serialize(this));
}

/// <summary>
/// Корабль космопорта с подарком союзнику (ferry.ts): деньги и груз. Всё отдаётся при отправке — деньги списываются, груз
/// уходит из хранилищ, — и всё выгружается у главного здания получателя через <see cref="TradeRules.TransferSeconds"/>,
/// как рейс космопорта. Пока корабль летит, космопорт занят. Получатель без главного здания принять подарок не может.
/// Юнитов так не передают: их управление отдают сразу (<see cref="Gift"/>). Вести об отправке и прибытии — сигналы
/// <see cref="FerrySignal"/> обеим сторонам.
/// </summary>
internal static class Ferry
{
    /// <summary>Главное здание игрока: к нему летит подарок; нет его — 0 (coreOf).</summary>
    public static uint CoreOf(Call call, uint player)
    {
        uint best = 0;
        foreach (var entity in Core.All(S.Building, S.Owner, S.Position))
            if (call.Alive(entity) && call.BuildingType(entity) == BuildRules.Core && call.OwnerOf(entity) == player && (best == 0 || entity < best))
                best = entity;
        return best;
    }

    /// <summary>Готовый ли свой космопорт и свободен ли он: корабль у космопорта один — ни рейса, ни второго подарка (canFerry).</summary>
    public static bool CanFerry(Call call, uint player, uint port) => Trading.CanSell(call, player, port);

    /// <summary>В союзе ли два игрока: одинаковый Team.head не меньше нуля (allied).</summary>
    public static bool Allied(Call call, uint a, uint b)
    {
        if (a == b)
            return false;
        var head = call.Num(Accounts.Find(call, a), S.Team, S.TeamHead, -1);
        return head >= 0 && head == call.Num(Accounts.Find(call, b), S.Team, S.TeamHead, -1);
    }

    /// <summary>Кладёт груз в хранилища всех зон игрока. Возвращает, сколько вошло (putIntoStores).</summary>
    private static double PutIntoStores(Call call, uint player, string good, double amount)
    {
        var left = amount;
        foreach (var zone in Zones.Of(player))
            foreach (var entity in zone.Buildings)
            {
                if (!call.Has(entity, S.Stock) || !Trading.IsStore(call, entity))
                    continue;
                left -= Stocks.Put(call, entity, good, left);
                if (left <= 0)
                    return amount;
            }
        return amount - left;
    }

    /// <summary>Берёт груз из хранилищ всех зон игрока, сколько есть. Возвращает, сколько взято (takeFromStores).</summary>
    public static double TakeFromStores(Call call, uint player, string good, double amount)
    {
        var left = amount;
        foreach (var zone in Zones.Of(player))
            foreach (var entity in zone.Buildings)
            {
                if (!call.Has(entity, S.Stock) || !Trading.IsStore(call, entity))
                    continue;
                left -= Stocks.Take(call, entity, good, left);
                if (left <= 0)
                    return amount;
            }
        return amount - left;
    }

    /// <summary>
    /// Отправляет подарок союзнику. Всё, что летит, отдаётся сразу: деньги списываются, груз забирается из хранилищ по всему
    /// запасу игрока. Возвращает, вышел ли корабль (sendFerry).
    /// </summary>
    public static bool Send(Call call, uint player, uint port, uint to, double credits, IReadOnlyDictionary<string, double> goods)
    {
        if (to == player || !Allied(call, player, to) || !CanFerry(call, player, port))
            return false;
        var core = CoreOf(call, to);
        if (core == 0 || !double.IsFinite(credits) || credits < 0)
            return false;
        var asked = Math.Floor(credits);
        var cargo = new Dictionary<string, double>();
        foreach (var good in Goods.All)
        {
            var amount = goods.GetValueOrDefault(good);
            if (!double.IsFinite(amount) || amount < 0)
                return false;
            if (amount >= 1)
                cargo[good] = Math.Min(Math.Floor(amount), TradeRules.TransferCapacity);
        }
        if (asked == 0 && cargo.Count == 0)
            return false;
        if (asked != 0 && !Accounts.Pay(call, player, asked))
            return false;
        foreach (var (good, amount) in cargo.ToList())
        {
            var got = TakeFromStores(call, player, good, amount);
            if (got >= 1)
                cargo[good] = got;
            else
                cargo.Remove(good);
        }
        // Ничего не набралось: забирать было нечего, и корабль не выходит — деньги остаются у игрока.
        if (asked == 0 && cargo.Count == 0)
            return false;
        // Высадка — у центра главного здания: там груз и падает, если в хранилищах не нашлось места.
        var (x, y) = call.At(core);
        var ticks = Trading.FlightTicks(call, port, TradeRules.TransferSeconds);
        var size = Factory.Building(BuildRules.Core)!;
        var writer = call.Has(port, T.Transfer) ? call.Set(port, T.Transfer) : call.Add(port, T.Transfer);
        writer.Number(T.TransferTo, to).Number(T.TransferX, x + Math.Floor(size.Width / 2)).Number(T.TransferY, y + Math.Floor(size.Height / 2))
            .Number(T.TransferCredits, asked).Number(T.TransferLeft, ticks).Number(T.TransferTotal, ticks).Flag(T.TransferDone, false);
        var holds = Holds.Read(call);
        holds.Ships[port] = cargo;
        holds.Write(call);
        call.SignalTo(FerrySignal.Name, new FerrySignal { Kind = FerrySignal.Sent, From = player, To = to, Port = port, Credits = asked }, player);
        call.SignalTo(FerrySignal.Name, new FerrySignal { Kind = FerrySignal.Coming, From = player, To = to, Port = port, Credits = asked }, to);
        return true;
    }

    /// <summary>Корабль долетел: деньги — получателю, груз — в его хранилища, а что не вошло — на землю у главного здания.</summary>
    public static void Arrive(Call call, uint port, Holds holds)
    {
        var to = (uint)call.Num(port, T.Transfer, T.TransferTo);
        var credits = call.Num(port, T.Transfer, T.TransferCredits);
        if (credits != 0)
            Accounts.Add(call, to, credits);
        var spill = new Dictionary<string, double>();
        if (holds.Ships.Remove(port, out var cargo))
            foreach (var (good, amount) in cargo)
            {
                var stored = PutIntoStores(call, to, good, amount);
                if (amount - stored >= 1e-9)
                    spill[good] = amount - stored;
            }
        if (spill.Count > 0)
            Drops.Items(call, call.Num(port, T.Transfer, T.TransferX), call.Num(port, T.Transfer, T.TransferY), spill);
        call.Set(port, T.Transfer).Number(T.TransferCredits, 0).Flag(T.TransferDone, true);
        var from = call.OwnerOf(port);
        call.SignalTo(FerrySignal.Name, new FerrySignal { Kind = FerrySignal.Arrived, From = from, To = to, Port = port, Credits = credits }, from);
        call.SignalTo(FerrySignal.Name, new FerrySignal { Kind = FerrySignal.Got, From = from, To = to, Port = port, Credits = credits }, to);
    }
}
