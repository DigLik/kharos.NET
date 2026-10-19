using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared.Signals;

namespace Base.Host.Sections.Trade;

/// <summary>
/// Передача своих юнитов союзнику (gift.ts): выбранные юниты переходят под его управление там же, где стоят, — это приказ,
/// а не груз, и корабль для него не нужен. Работу они бросают: стройка, маршрут, цель, патруль и путь снимаются. Груз
/// остаётся в кузове: что юнит вёз, то и довозит — уже новому хозяину, в его хранилища и на его переработку. Турели уходят
/// вместе с носителем. Принимает только союзник.
/// </summary>
internal static class Gift
{
    /// <summary>Кого из названных юнитов можно отдать: свои, живые и не турели на носителе, без повторов (giftable).</summary>
    public static List<uint> Giftable(Call call, uint player, IEnumerable<uint> units)
    {
        var list = new List<uint>();
        foreach (var entity in units)
            if (!list.Contains(entity) && call.Has(entity, S.Unit) && call.IsOwn(player, entity) && !call.Has(entity, S.Mount) && !call.Has(entity, T.Doomed))
                list.Add(entity);
        return list;
    }

    /// <summary>Отдаёт юнитов союзнику. Возвращает, сколько перешло; ноль — отдавать было нечего или он не союзник (handOver).</summary>
    public static int HandOver(Call call, uint player, uint to, IEnumerable<uint> units)
    {
        if (to == player || !Ferry.Allied(call, player, to))
            return 0;
        var taken = Giftable(call, player, units);
        if (taken.Count == 0)
            return 0;
        foreach (var entity in taken)
        {
            // Приказ идти или стоять: разделы снимают с юнита своё — стройку, поручение, работу грузовика, цель, патруль.
            Commands.Free(call, entity);
            call.Remove(entity, S.Builds);
            // Груз остаётся в кузове и едет с юнитом: дальше его сгружает уже перевозка нового хозяина.
            if (call.Has(entity, S.Hauler) && call.Has(entity, S.Stock) && Carried(call, entity) is { } good)
                call.Set(entity, S.Hauler).Number(T.HaulerResource, Logistics.Cargo.Proto(Logistics.Cargo.Index(good))).Flag(T.HaulerFull, true);
            var turrets = Factory.TurretsOf(call, entity);
            foreach (var gunner in turrets.Prepend(entity))
                if (call.Has(gunner, S.Armed))
                    call.Set(gunner, S.Armed).Number(T.ArmedTarget, 0).Flag(T.ArmedChase, false).Flag(T.ArmedOrdered, false);
            if (call.Has(entity, S.Tactics))
                call.Set(entity, S.Tactics).Bytes(T.TacticsPatrol, []).Number(T.TacticsLeg, 0).Flag(T.TacticsAway, false).Flag(T.TacticsResupply, false);
            Commands.ClearOrders(call, [entity]);
            call.Remove(entity, S.Path);
            foreach (var turret in turrets)
                call.Set(turret, S.Owner).Number(S.OwnerPlayer, to);
            call.Set(entity, S.Owner).Number(S.OwnerPlayer, to);
        }
        call.SignalTo(GiftSignal.Name, new GiftSignal { Kind = GiftSignal.Sent, From = player, To = to, Count = taken.Count }, player);
        call.SignalTo(GiftSignal.Name, new GiftSignal { Kind = GiftSignal.Got, From = player, To = to, Count = taken.Count }, to);
        return taken.Count;
    }

    /// <summary>Первый груз, что лежит в кузове (по порядку грузов); пусто — null.</summary>
    private static string? Carried(Call call, uint entity)
    {
        foreach (var good in Goods.All)
            if (Stocks.Amount(call, entity, good) > Stocks.Epsilon)
                return good;
        return null;
    }
}
