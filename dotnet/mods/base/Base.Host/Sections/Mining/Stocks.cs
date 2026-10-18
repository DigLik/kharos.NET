using Base.Host.Kernel;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Склады (inventory.ts): груз лежит в Stock по столбцу на вид, объём — Stock.capacity. Что склад принимает, решает вид
/// сущности (accepts в units.json и buildings.json; пусто — всё), сколько каждого — Limits (из вида при появлении
/// здания, свои — у площадки стройки). Дроп принимает всё, площадка стройки — то, для чего задан предел. Запись — командами
/// вызова: следующее чтение в том же вызове видит её (<see cref="Call"/>).
/// </summary>
internal static class Stocks
{
    /// <summary>Остатки меньше этого не в счёт (EPSILON в TS).</summary>
    public const double Epsilon = 1e-9;

    /// <summary>Сколько этого груза лежит на складе.</summary>
    public static double Amount(Call call, uint entity, string good) => Amount(call, entity, Goods.IndexOf(good));

    /// <summary>Сколько груза с номером good (в порядке столбцов) лежит на складе.</summary>
    public static double Amount(Call call, uint entity, int good) => good < 0 ? 0 : call.Num(entity, S.Stock, Goods.StockField(good));

    /// <summary>Сколько всего лежит на складе, любого груза вместе, вместе с рудой (loadOf).</summary>
    public static double Load(Call call, uint entity)
    {
        var sum = 0.0;
        for (var good = 0; good < Goods.All.Count; good++)
            sum += Amount(call, entity, good);
        return sum;
    }

    /// <summary>Принимает ли склад этот груз вообще (accepts).</summary>
    public static bool Accepts(Call call, uint entity, string good)
    {
        if (call.Has(entity, S.Drop))
            return true;
        if (call.Has(entity, S.Site))
            return LimitOf(call, entity, Goods.IndexOf(good)) >= 0;
        var accepts = call.UnitType(entity) is { } unit ? Factory.Unit(unit)?.Accepts : Factory.Building(call.BuildingType(entity))?.Accepts;
        return accepts is not { Length: > 0 } || Array.IndexOf(accepts, good) >= 0;
    }

    /// <summary>Предел груза на этом складе; нет предела — -1.</summary>
    public static double LimitOf(Call call, uint entity, int good) =>
        Goods.LimitField(good) is { } field && call.Has(entity, S.Limits) ? call.Num(entity, S.Limits, field, -1) : -1;

    /// <summary>Сколько этого груза ещё поместится; ноль — склад полон, такое сюда не кладут или предел набран (roomFor).</summary>
    public static double Room(Call call, uint entity, string good)
    {
        var index = Goods.IndexOf(good);
        if (index < 0 || !call.Has(entity, S.Stock) || !Accepts(call, entity, good))
            return 0;
        var limit = LimitOf(call, entity, index);
        var left = limit < 0 ? double.PositiveInfinity : limit - Amount(call, entity, index);
        return Math.Max(0, Math.Min(left, call.Num(entity, S.Stock, S.StockCapacity) - Load(call, entity)));
    }

    /// <summary>Кладёт на склад до amount груза, сколько поместится. Возвращает, сколько положено (put).</summary>
    public static double Put(Call call, uint entity, string good, double amount)
    {
        var moved = Math.Max(0, Math.Min(amount, Room(call, entity, good)));
        if (moved != 0)
        {
            var index = Goods.IndexOf(good);
            call.Set(entity, S.Stock).Number(Goods.StockField(index), Amount(call, entity, index) + moved);
        }
        return moved;
    }

    /// <summary>Забирает со склада до amount груза, сколько есть. Возвращает, сколько забрано (take).</summary>
    public static double Take(Call call, uint entity, string good, double amount)
    {
        var index = Goods.IndexOf(good);
        if (index < 0 || !call.Has(entity, S.Stock))
            return 0;
        var have = Amount(call, entity, index);
        var moved = Math.Max(0, Math.Min(amount, have));
        if (moved != 0)
            call.Set(entity, S.Stock).Number(Goods.StockField(index), have - moved);
        return moved;
    }
}
