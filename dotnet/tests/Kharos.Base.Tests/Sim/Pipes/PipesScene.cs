using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using MemoryPack;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Pipes;

/// <summary>
/// Помощники сцен труб (то, что pipes.test.ts берёт из sim-ts: rock, lay, kindAt, placeBuilding, put, addCredits). Размер
/// карты — полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class PipesScene
{
    /// <summary>Партия на местности TS без дерева технологий (options в pipes.test.ts).</summary>
    public static Sim Create() => Scene.Create(512, new { techTree = false });

    /// <summary>
    /// Левый верхний тайл скалы width×height (rock): в TS — любой скалы, здесь — без подножий обрыва, иначе правка
    /// здания места не даст.
    /// </summary>
    public static (int X, int Y) Rock(Sim sim, int width, int height)
    {
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
                if (Scene.Rock(sim, x, y, width, height))
                    return (x, y);
        throw new InvalidOperationException("Не нашлось скалы");
    }

    /// <summary>Готовое здание без стройки (placeBuilding).</summary>
    public static uint Place(Sim sim, string type, int x, int y, uint player = 1) => sim.PutBuilding(type, x, y, player);

    /// <summary>Наземная труба по ряду y от x до x + length − 1 (lay).</summary>
    public static List<uint> Lay(Sim sim, int x, int y, int length, uint player = 1) =>
        [.. Enumerable.Range(0, length).Select(i => Place(sim, "pipe", x + i, y, player))];

    /// <summary>Кладёт груз на склад сверх того, что там есть (put): прямо в мир — правка edit.stock округляет до целых.</summary>
    public static void Put(Sim sim, uint entity, string good, double amount) => sim.Set(entity, "Stock." + good, sim.Stock(entity, good) + amount);

    /// <summary>Добавляет игроку кредитов (addCredits).</summary>
    public static void AddCredits(Sim sim, uint player, double amount) => sim.SetCredits(player, sim.Credits(player) + amount);

    /// <summary>Юнит в центре тайла (spawnUnit).</summary>
    public static uint Spawn(Sim sim, string type, uint player, int x, int y) => Scene.Spawn(sim, type, player, x, y);

    /// <summary>Вид здания или стройки сущности.</summary>
    public static string? TypeOf(Sim sim, uint entity) =>
        sim.Has(entity, Names.Proto.Name) && sim.Host.Content.Protos[(int)sim.Num(entity, "Proto.id")] is { Section: "buildings" } proto ? proto.Name : null;

    /// <summary>Что лежит в тайле (kindAt): вид готового здания или заложенной стройки (с «?»); пусто — ничего.</summary>
    public static string KindAt(Sim sim, int x, int y)
    {
        var built = sim.OccupantAt(x, y);
        if (built != 0 && sim.Alive(built) && !sim.Has(built, Names.Site.Name))
            return TypeOf(sim, built) ?? "";
        var site = sim.SiteAt(x, y);
        return site == 0 ? "" : TypeOf(sim, site) + "?";
    }

    /// <summary>Сколько строек вида type.</summary>
    public static int SitesOf(Sim sim, string type) => sim.With(Names.Site.Name).Count(site => TypeOf(sim, site) == type);

    /// <summary>Цена здания.</summary>
    public static double Cost(string type) => Tables.Buildings[type].Cost;

    /// <summary>Сколько вернёт разбор здания (refundOf).</summary>
    public static double Refund(string type) => BuildRules.RefundOf(Tables.Buildings[type], 1);

    /// <summary>Площадки приказов «помочь» в очереди юнита по порядку (queued в pipes.test.ts); не помощь — 0.</summary>
    public static List<uint> Queued(Sim sim, uint unit)
    {
        if (!sim.Has(unit, Names.Orders.Name))
            return [];
        var bytes = sim.Bytes(unit, "Orders.list");
        var queue = bytes.Length == 0 ? null : MemoryPackSerializer.Deserialize<OrderQueue>(bytes);
        return [.. (queue?.Items ?? []).Select(item => item.Name == AssistOrder.Name ? MemoryPackSerializer.Deserialize<AssistOrder>(item.Payload)?.Site ?? 0 : 0)];
    }

    /// <summary>Крутит, пока условие не выполнится; не дождались — провал.</summary>
    public static void Until(Sim sim, Func<bool> done, double limit = 120) => Assert.True(sim.Until(done, limit), "Не дождались");
}
