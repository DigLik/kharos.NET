using Base.Shared;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Mining;

/// <summary>Помощники сцен раздела Mining: месторождения и здания рядом с ними (то, что тесты TS берут из sim-ts).</summary>
internal static class MiningScene
{
    /// <summary>Правила TS-тестов добычи: без дерева технологий.</summary>
    public static readonly object NoTree = new { techTree = false };

    /// <summary>Партия добычи: size — полусторона (TS size 1024 — здесь 512).</summary>
    public static Sim Create(int size = 512, object? rules = null, bool? fog = null) => Scene.Create(size, rules ?? NoTree, fog);

    /// <summary>Мир 256×256 со всеми месторождениями; смена — через полминуты, чтобы не ждать в тесте 15 минут (world в renewal.test.ts).</summary>
    public static Sim Small(bool fog = false, double renewal = 0.5) =>
        Scene.Create(128, new { depositRenewal = renewal, techTree = false, protection = 0 }, fog);

    /// <summary>Месторождение ближе всех к середине карты: вокруг него есть куда отвести юнит, не уходя за край (central).</summary>
    public static DepositSpot Central(Sim sim) => sim.Deposits().OrderBy(spot => Math.Sqrt((double)spot.X * spot.X + (double)spot.Y * spot.Y)).First();

    /// <summary>Металлическое месторождение, рядом с которым встают плавильня, электростанция и труба (refineryAt в mining.test.ts).</summary>
    public static DepositSpot RefinerySpot(Sim sim)
    {
        for (var cellY = -6; cellY < 6; cellY++)
            for (var cellX = -6; cellX < 6; cellX++)
                if (sim.DepositIn(cellX, cellY) is { Kind: "metal" } spot
                    && Scene.CanPlace(sim, "smelter", spot.X + 12, spot.Y + 2)
                    && Scene.CanPlace(sim, "generator", spot.X + 14, spot.Y)
                    && Scene.CanPlace(sim, "pipe", spot.X + 15, spot.Y + 2))
                    return spot;
        throw new InvalidOperationException("В мире не нашлось месторождения с местом под переработку");
    }

    /// <summary>Переработка с электростанцией, соединённые трубой: завод работает, только когда есть энергия (refineryAt).</summary>
    public static uint RefineryAt(Sim sim, DepositSpot spot)
    {
        var refinery = sim.PutBuilding("smelter", spot.X + 12, spot.Y + 2, 1);
        sim.PutBuilding("generator", spot.X + 14, spot.Y, 1);
        sim.PutBuilding("pipe", spot.X + 15, spot.Y + 2, 1);
        return refinery;
    }

    /// <summary>Шахта игрока 1 на месторождении.</summary>
    public static uint MineAt(Sim sim, DepositSpot spot) => sim.PutBuilding("mine", spot.X, spot.Y, 1);

    /// <summary>Любое месторождение вида kind (нужной скалы вокруг не ищется).</summary>
    public static DepositSpot Of(Sim sim, string kind) => sim.Deposits().First(spot => spot.Kind == kind);

    /// <summary>count свободных проходимых тайлов рядом с месторождением (юниту есть где встать), ближние к нему — первыми.</summary>
    public static List<(int X, int Y)> FreeTiles(Sim sim, DepositSpot spot, int count)
    {
        var navigation = sim.Host.Tools.Navigation!;
        var tiles = new List<(int X, int Y)>();
        for (var radius = 3; radius < 12 && tiles.Count < count; radius++)
            for (var y = spot.Y - radius; y <= spot.Y + radius + 1 && tiles.Count < count; y++)
                for (var x = spot.X - radius; x <= spot.X + radius + 1 && tiles.Count < count; x++)
                    if (Math.Max(Math.Abs(x - spot.X), Math.Abs(y - spot.Y)) == radius && navigation.IsWalkable(x, y) && !tiles.Contains((x, y)))
                        tiles.Add((x, y));
        return tiles.Count == count ? tiles : throw new InvalidOperationException("нет свободных тайлов у месторождения");
    }

    /// <summary>Здание вида type прямо в мир, без проверок места и без хозяйства (placeBuilding без canPlace): только основание.</summary>
    public static uint Block(Sim sim, string type, int x, int y, int w, int h)
    {
        var entity = sim.World.Spawn();
        sim.Add(entity, Names.Position.Name);
        sim.Set(entity, "Position.x", x);
        sim.Set(entity, "Position.y", y);
        sim.Add(entity, Names.Building.Name);
        sim.Add(entity, Names.Proto.Name);
        sim.Set(entity, "Proto.id", Scene.Proto(sim, "buildings/" + type));
        sim.Add(entity, Names.Footprint.Name);
        sim.Set(entity, "Footprint.w", w);
        sim.Set(entity, "Footprint.h", h);
        sim.Set(entity, "Footprint.solid", 1);
        return entity;
    }
}
