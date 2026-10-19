using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Base.Scenario;

namespace Kharos.Balance;

/// <summary>Прямоугольник в тайлах: левый верхний тайл и размеры (Rect в lib.ts).</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height);

/// <summary>
/// Общее для всех разделов (tools/harness/lib.ts): мир по умолчанию, шаг времени, поиск места под сцену. Сцены ставятся
/// той же партией и на той же карте, что и в игре: харнесс ничего не подкручивает.
/// </summary>
public static class Lib
{
    /// <summary>Шаг симуляции в секундах: 20 тиков в секунду, как в игре.</summary>
    public const double Tick = Sim.Dt;

    /// <summary>Тиков в секунду.</summary>
    public static readonly int Ticks = (int)Math.Round(1 / Tick);

    /// <summary>Таблицы base из mods/base/data — то, что читает мод.</summary>
    public static BaseTables Data { get; } = BaseTables.Load(name =>
    {
        var path = Path.Combine(Sim.ModDir, "data", name + ".json");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    });

    /// <summary>Сторона карты харнесса в TS (1024) — здесь полусторона 512.</summary>
    public const int HalfSize = 512;

    /// <summary>Вид здания или юнита по номеру Proto.</summary>
    public static string TypeOf(Sim sim, uint entity) => sim.Host.Content.Protos[(int)sim.Num(entity, Names.Proto.Name, Names.Proto.Id)].Name;

    /// <summary>
    /// Мир харнесса: карта по умолчанию (lands.json default, 1024² в TS), без защиты новичка, игроки заведены кошельками
    /// (в TS игрок появляется при первом обращении к счёту). Партия <c>test</c>: стартового набора нет.
    /// </summary>
    public static Sim NewSim(int half = HalfSize, object? rules = null, uint[]? players = null)
    {
        var sim = Sim.Create(new SimOptions { Size = half, Land = "default", Players = [], Rules = rules ?? new { protection = 0 } });
        foreach (var player in players ?? [1u, 2u])
            sim.EnsurePlayer(player);
        return sim;
    }

    /// <summary>Местность по умолчанию (lands.json default) с другим зерном: <c>{ ...generator, seed }</c>.</summary>
    public static System.Text.Json.Nodes.JsonNode LandWithSeed(int seed)
    {
        var lands = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(Sim.ModDir, "data", "lands.json")))!.AsObject();
        var land = lands["default"]!.DeepClone().AsObject();
        land["seed"] = seed;
        return land;
    }

    /// <summary>Крутит симуляцию на time секунд.</summary>
    public static void Seconds(Sim sim, double time) => sim.Seconds(time);

    public static double Round(double value, int digits = 2) => Report.Round(value, digits);

    // ===== Места =====

    /// <summary>Тайл проходим для пешего: не гора и не стена.</summary>
    public static bool IsWalkable(Sim sim, int x, int y) => sim.Walkable(x, y);

    /// <summary>Левый верхний тайл ровной проходимой площадки width×height — место под сцену боя или осады.</summary>
    public static (int X, int Y) Arena(Sim sim, int width, int height)
    {
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
                if (Open(sim, x, y, width, height))
                    return (x, y);
        throw new InvalidOperationException($"Не нашлось площадки {width}×{height}");
    }

    private static bool Open(Sim sim, int x, int y, int width, int height)
    {
        for (var dy = 0; dy < height; dy++)
            for (var dx = 0; dx < width; dx++)
                if (!IsWalkable(sim, x + dx, y + dy))
                    return false;
        return true;
    }

    /// <summary>Тайлы вокруг (x, y) кольцами, от ближних к дальним, до radius включительно.</summary>
    public static IEnumerable<(int X, int Y)> Rings(int x, int y, int radius)
    {
        for (var ring = 0; ring <= radius; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (x + dx, y + dy);
    }

    /// <summary>Ближайшее к (x, y) место, где подходит <paramref name="ok"/>; null — не нашлось.</summary>
    public static (int X, int Y)? SpotNear(int x, int y, Func<int, int, bool> ok, int radius = 16)
    {
        foreach (var tile in Rings(x, y, radius))
            if (ok(tile.X, tile.Y))
                return tile;
        return null;
    }

    /// <summary>Здание вида type встанет левым верхним тайлом в (x, y) с зазором gap (canPlace).</summary>
    public static bool CanPlace(Sim sim, string type, int x, int y, int gap = 0) =>
        sim.Query<bool>(KernelQueries.CanPlace, new PlaceArgs { Type = type, X = x, Y = y, Gap = gap });

    /// <summary>Ближайшее к (x, y) место, где здание этого вида поместится и не заденет соседей.</summary>
    public static (int X, int Y)? PlaceableNear(Sim sim, string type, double x, double y, int gap = 1, int radius = 16) =>
        SpotNear((int)Math.Round(x, MidpointRounding.ToPositiveInfinity), (int)Math.Round(y, MidpointRounding.ToPositiveInfinity), (tx, ty) => CanPlace(sim, type, tx, ty, gap), radius);

    /// <summary>Ставит готовое здание ближайшим подходящим местом к (x, y); 0 — места нет.</summary>
    public static uint PlaceNear(Sim sim, string type, double x, double y, uint player, int gap = 1) =>
        PlaceableNear(sim, type, x, y, gap) is { } spot ? sim.PutBuilding(type, spot.X, spot.Y, player) : 0;

    /// <summary>Размер здания в тайлах.</summary>
    public static (int Width, int Height) SizeOf(string type) => ((int)Data.Buildings[type].Width, (int)Data.Buildings[type].Height);

    /// <summary>Центр здания или место юнита — то, от чего считается расстояние.</summary>
    public static (double X, double Y) CenterOf(Sim sim, uint entity)
    {
        var (x, y) = sim.At(entity);
        if (!sim.Has(entity, Names.Building.Name) && !sim.Has(entity, Names.Site.Name))
            return (x, y);
        var (width, height) = SizeOf(TypeOf(sim, entity));
        return (x + width / 2.0, y + height / 2.0);
    }

    /// <summary>Живое здание этого вида на этом месте: 0 — места пусто или там уже другое.</summary>
    public static uint BuildingAt(Sim sim, string type, int x, int y)
    {
        foreach (var entity in sim.With(Names.Building.Name, Names.Position.Name))
        {
            var (px, py) = sim.At(entity);
            if (px == x && py == y && TypeOf(sim, entity) == type)
                return entity;
        }
        return 0;
    }

    /// <summary>Разошлись ли прямоугольники на gap тайлов: false — они пересекаются или стоят вплотную.</summary>
    public static bool RectsApart(Rect a, Rect b, int gap = 0) =>
        a.X >= b.X + b.Width + gap || b.X >= a.X + a.Width + gap || a.Y >= b.Y + b.Height + gap || b.Y >= a.Y + a.Height + gap;

    /// <summary>Прямоугольник здания этого вида с левым верхним тайлом в (x, y).</summary>
    public static Rect RectOf(string type, int x, int y) => new(x, y, SizeOf(type).Width, SizeOf(type).Height);

    // ===== Мир =====

    /// <summary>Живые юниты игрока.</summary>
    public static List<uint> UnitsOf(Sim sim, uint player) => sim.Of(player, Names.Unit.Name);

    /// <summary>Сколько у игрока живых юнитов этого вида.</summary>
    public static int CountUnits(Sim sim, uint player, string type) =>
        sim.Of(player, Names.Unit.Name, Names.Proto.Name).Count(entity => TypeOf(sim, entity) == type);

    /// <summary>Сколько готовых зданий этого вида у игрока; sites — считать и заложенные.</summary>
    public static int CountBuildings(Sim sim, uint player, string type, bool sites = false) =>
        sim.Of(player, Names.Building.Name, Names.Proto.Name).Count(entity => TypeOf(sim, entity) == type && (sites || !sim.Has(entity, Names.Site.Name)));

    /// <summary>Сколько груза в запасе сущности.</summary>
    public static double Amount(Sim sim, uint entity, string good) => sim.Has(entity, Names.Stock.Name) ? sim.Num(entity, Names.Stock.Name, good) : 0;
}
