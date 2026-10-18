using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Дропы (drops.ts) — груз, брошенный на землю: что лежало на складе разрушенного или разобранного здания, в кузове
/// погибшей машины, на отменённой стройке. Это склад без здания на одном тайле и без хозяина (Drop, Position, Stock):
/// подобрать его может любой грузовик, а свободные везут его в хранилище, руду — на переработку (раздел Logistics).
/// Пустой дроп исчезает (<see cref="Sweep"/>), а стройка или развёрнутый MCV поверх дропа его уничтожают
/// (<see cref="Clear"/>). Другие разделы бросают груз через <see cref="DropCargo"/> и <see cref="Items"/>.
/// </summary>
internal static class Drops
{
    /// <summary>Дроп на тайле (x, y) или 0 (dropAt). Дропов немного, поэтому поиск простой.</summary>
    public static uint At(Call call, int x, int y)
    {
        foreach (var entity in Tools.Near(x + 0.5, y + 0.5, 0.8, S.Drop))
            if (call.Alive(entity) && call.At(entity) == (x, y))
                return entity;
        if (call.Writes)
            foreach (var born in call.Stage.Spawned)
                if (call.Alive(born) && call.Has(born, S.Drop) && call.At(born) == (x, y))
                    return born;
        return 0;
    }

    /// <summary>
    /// Бросает груз на тайл точки (x, y): к дропу, который уже там лежит, или новым дропом. Пустое (не больше
    /// <see cref="Stocks.Epsilon"/>) не бросают. Возвращает дроп; ничего не брошено — 0 (dropItems).
    /// </summary>
    public static uint Items(Call call, double x, double y, IReadOnlyDictionary<string, double> items)
    {
        // Грузы по порядку столбцов, как GOODS.filter в TS: от порядка зависит порядок записи полей.
        var goods = new List<(int Good, double Amount)>();
        for (var good = 0; good < Goods.All.Count; good++)
            if (items.TryGetValue(Goods.All[good], out var amount) && amount > Stocks.Epsilon)
                goods.Add((good, amount));
        if (goods.Count == 0)
            return 0;
        var (tileX, tileY) = ((int)Math.Floor(x), (int)Math.Floor(y));
        var drop = At(call, tileX, tileY);
        var had = drop != 0 ? Stocks.Load(call, drop) : 0;
        if (drop == 0)
        {
            drop = call.Spawn();
            call.Add(drop, S.Position).Number(S.PositionX, tileX).Number(S.PositionY, tileY);
            call.Add(drop, S.Drop);
            call.Add(drop, S.Stock);
        }
        var writer = call.Set(drop, S.Stock);
        var load = had;
        foreach (var (good, amount) in goods)
        {
            writer.Number(Goods.StockField(good), Stocks.Amount(call, drop, good) + amount);
            load += amount;
        }
        // Дроп принимает только то, что на него бросили: места ровно под груз.
        writer.Number(S.StockCapacity, load);
        return drop;
    }

    /// <summary>Что лежит на складе сущности: груз → количество (больше нуля).</summary>
    public static Dictionary<string, double> Cargo(Call call, uint entity)
    {
        var items = new Dictionary<string, double>();
        for (var good = 0; good < Goods.All.Count; good++)
            if (Stocks.Amount(call, entity, good) is var amount && amount > Stocks.Epsilon)
                items[Goods.All[good]] = amount;
        return items;
    }

    /// <summary>
    /// Бросает весь груз сущности на землю под ней: у здания — на тайл в середине основания, у юнита — на его тайл
    /// (dropCargo). Сам склад сущности не трогает: прибирает его тот, кто убирает сущность.
    /// </summary>
    public static void DropCargo(Call call, uint entity)
    {
        if (!call.Has(entity, S.Stock) || call.Has(entity, S.Drop) || !call.Has(entity, S.Position))
            return;
        var (x, y) = call.At(entity);
        var size = Factory.Building(call.BuildingType(entity));
        Items(call, x + Math.Floor((size?.Width ?? 0) / 2), y + Math.Floor((size?.Height ?? 0) / 2), Cargo(call, entity));
    }

    /// <summary>Уничтожает дропы под основанием x, y, w, h, на котором встаёт здание (clearDrops).</summary>
    public static void Clear(Call call, int x, int y, int w, int h)
    {
        foreach (var drop in Tools.Near(x + w / 2.0, y + h / 2.0, Math.Max(w, h), S.Drop))
            if (call.Alive(drop) && InRect(call, drop, x, y, w, h))
                call.Destroy(drop);
        if (call.Writes)
            foreach (var born in call.Stage.Spawned.ToList())
                if (call.Alive(born) && call.Has(born, S.Drop) && InRect(call, born, x, y, w, h))
                    call.Destroy(born);
    }

    private static bool InRect(Call call, uint drop, int x, int y, int w, int h)
    {
        var (px, py) = call.At(drop);
        return px >= x && px < x + w && py >= y && py < y + h;
    }

    /// <summary>Раз в тик: пустые дропы исчезают (sweepDrops). В TS это конец системы haul; здесь — своя система после refine.</summary>
    public sealed class Sweep : ISystem
    {
        private Column[] _goods = [];

        public void Bind(SystemBinding binding) =>
            _goods = [.. Goods.All.Select(good => binding.Column(Names.Stock.Name, good))];

        public void Run(SystemContext ctx)
        {
            for (var row = 0; row < ctx.Count; row++)
            {
                var load = 0.0;
                foreach (var good in _goods)
                    load += ctx.Column(good)[row];
                if (load <= Stocks.Epsilon)
                    ctx.Commands.Destroy(ctx.Entities[row]);
            }
        }
    }
}
