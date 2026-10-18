using Base.Shared;
using Kharos.Engine.Terrain;

namespace Kharos.Base.Scenario;

/// <summary>
/// Перенос packages/tests/builders.ts. В стартовом наборе строителей нет — их заказывают в главном здании, — а тестам
/// стройки они нужны сразу. Строители ставятся правками edit.* (<see cref="Sim.PutUnit"/>).
/// </summary>
public static class Builders
{
    /// <summary>Расстояние между местами группы, тайлы (GROUP_SPACING в units.ts).</summary>
    private const int GroupSpacing = 2;

    /// <summary>Самое дальнее кольцо поиска (MAX_GROUP_RADIUS в units.ts).</summary>
    private const int MaxGroupRadius = 40;

    /// <summary>
    /// Строители игрока рядом с (x, y), каждый на своём свободном тайле за стартовым набором — со второго кольца, ближние
    /// тайлы занял стартовый набор. Свободных мест меньше, чем count, — столько, сколько нашлось.
    /// </summary>
    public static List<uint> AddBuilders(Sim sim, uint player, int x, int y, int count = 2)
    {
        var tiles = FreeTilesNear(sim, x, y, count + 8, 2);
        var taken = new HashSet<(int, int)>();
        var builders = new List<uint>();
        for (var i = 0; i < tiles.Count && builders.Count < count; i++)
        {
            if (!taken.Add(tiles[i]))
                continue;
            builders.Add(sim.PutUnit("builder", player, tiles[i].X + 0.5, tiles[i].Y + 0.5));
        }
        return builders;
    }

    /// <summary>
    /// Проходимые свободные тайлы вокруг (x, y) кольцами через <see cref="GroupSpacing"/> (freeTilesNear в units.ts).
    /// Годится тайл на карте, не гора, не подножие обрыва и не под зданием. Когда инструмент FreeTiles движка (Д1) заработает,
    /// это место заменит его вызов.
    /// </summary>
    public static List<(int X, int Y)> FreeTilesNear(Sim sim, int x, int y, int count, int fromRadius = 0)
    {
        var blocked = Footprints(sim);
        var tiles = new List<(int X, int Y)>();
        var searchRadius = Math.Min(MaxGroupRadius, Math.Max(8, (int)Math.Ceiling(Math.Sqrt(count))));
        for (var radius = fromRadius; radius <= searchRadius && tiles.Count < count; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius || tiles.Count >= count)
                        continue;
                    var tile = (X: x + dx * GroupSpacing, Y: y + dy * GroupSpacing);
                    if (CanStand(sim, blocked, tile.X, tile.Y))
                        tiles.Add(tile);
                }
        return tiles;
    }

    /// <summary>Тайл на карте, проходим и не занят зданием.</summary>
    private static bool CanStand(Sim sim, HashSet<(int, int)> blocked, int x, int y)
    {
        if (sim.Land is not { } land || sim.Host.Map is not { } map || !map.Contains(x, y))
            return false;
        return land.TerrainAt(x, y) != TerrainType.Mountain && !land.IsCliffFoot(x + 0.5, y + 0.5) && !blocked.Contains((x, y));
    }

    /// <summary>Тайлы под зданиями и стройками (Footprint с Position).</summary>
    private static HashSet<(int, int)> Footprints(Sim sim)
    {
        var tiles = new HashSet<(int, int)>();
        foreach (var entity in sim.With(Names.Footprint.Name, Names.Position.Name))
        {
            var (px, py) = sim.At(entity);
            var width = (int)sim.Num(entity, Names.Footprint.Name, Names.Footprint.W);
            var height = (int)sim.Num(entity, Names.Footprint.Name, Names.Footprint.H);
            for (var dy = 0; dy < height; dy++)
                for (var dx = 0; dx < width; dx++)
                    tiles.Add(((int)Math.Floor(px) + dx, (int)Math.Floor(py) + dy));
        }
        return tiles;
    }
}
