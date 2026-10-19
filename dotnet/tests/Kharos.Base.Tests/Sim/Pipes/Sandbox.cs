using Base.Shared;
using Base.Shared.Queries;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Pipes;

/// <summary>
/// Тестовая карта (spawnSandbox в sandbox.ts) на стороне теста — правками: готовая база игрока у ближайшего к началу мира
/// месторождения металла, соединённая трубами (edit.connectAll), запас в хранилищах, строители, войска и грузовики.
/// Партия «sandbox» из games.json сама её пока не ставит (вход игрока — ядро и раздел игроков).
/// </summary>
internal static class Sandbox
{
    public const double Credits = 10000;

    private static readonly (string Good, double Amount)[] Stock =
        [("metal", 120), ("silicon", 40), ("fuel", 30), ("kharite", 12), ("blocks", 20), ("ammo", 60), ("parts", 30)];

    private const int SearchCells = 6, MineRoom = 2, BaseReach = 8000, Trucks = 4;

    private static readonly string[] Buildings =
    [
        "generator", "generator", "generator", "generator", "smelter", "siliconWorks", "blockPlant", "ammoPlant", "metalYard", "siliconStore", "fuelTank",
        "khariteVault", "blockYard", "ammoBunker", "partsLocker", "spaceport", "factory", "barracks", "matter", "lab", "radar",
    ];

    private static readonly string[] Units = ["builder", "builder", "infantry", "infantry", "rocketeer", "buggy", "tank", "tesla", "carrier"];

    private static (int W, int H) Size(string type) => ((int)Tables.Buildings[type].Width, (int)Tables.Buildings[type].Height);

    private static bool CanPlace(Sim sim, string type, int x, int y, int gap = 0) =>
        sim.Query<bool>(KernelQueries.CanPlace, new PlaceArgs { Type = type, X = x, Y = y, Gap = gap });

    /// <summary>Тайлы вокруг (x, y) кольцами, от ближних к дальним, до radius включительно (rings).</summary>
    private static IEnumerable<(int X, int Y)> Rings(int x, int y, int radius)
    {
        for (var ring = 0; ring <= radius; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (x + dx, y + dy);
    }

    /// <summary>Месторождение металла, у которого поместится база (findBaseSpot).</summary>
    private static DepositSpot? FindBaseSpot(Sim sim)
    {
        var cells = new List<(int X, int Y)>();
        for (var y = -SearchCells; y <= SearchCells; y++)
            for (var x = -SearchCells; x <= SearchCells; x++)
                cells.Add((x, y));
        cells = [.. cells.OrderBy(cell => cell.X * cell.X + cell.Y * cell.Y)];
        var (w, h) = Size("mine");
        foreach (var cell in cells)
        {
            if (sim.DepositIn(cell.X, cell.Y) is not { Kind: "metal" } spot || !CanPlace(sim, "mine", spot.X, spot.Y))
                continue;
            var open = true;
            for (var y = spot.Y - 1; y <= spot.Y + h && open; y++)
                for (var x = spot.X - 1; x <= spot.X + w; x++)
                {
                    var inside = x >= spot.X && x < spot.X + w && y >= spot.Y && y < spot.Y + h;
                    if (!inside && !sim.Walkable(x, y))
                        open = false;
                }
            if (open)
                return spot;
        }
        return null;
    }

    private static bool ClearOfMine(DepositSpot spot, string type, int x, int y)
    {
        var (w, h) = Size(type);
        var (mw, mh) = Size("mine");
        var apart = x >= spot.X + mw + MineRoom || x + w + MineRoom <= spot.X;
        return apart || y >= spot.Y + mh + MineRoom || y + h + MineRoom <= spot.Y;
    }

    private static bool Servable(HashSet<long> reach, string type, int x, int y)
    {
        var (w, h) = Size(type);
        for (var ty = y - 1; ty <= y + h; ty++)
            for (var tx = x - 1; tx <= x + w; tx++)
            {
                var inside = tx >= x && tx < x + w && ty >= y && ty < y + h;
                if (!inside && reach.Contains(Key(tx, ty)))
                    return true;
            }
        return false;
    }

    private static long Key(int x, int y) => ((long)y << 32) | (uint)x;

    private static uint PlaceNear(Sim sim, DepositSpot spot, HashSet<long> reach, string type, double x, double y, uint player, int gap)
    {
        foreach (var (tx, ty) in Rings((int)Math.Floor(x + 0.5), (int)Math.Floor(y + 0.5), 14))
            if (CanPlace(sim, type, tx, ty, gap) && ClearOfMine(spot, type, tx, ty) && Servable(reach, type, tx, ty))
                return sim.PutBuilding(type, tx, ty, player);
        return 0;
    }

    /// <summary>Ставит тестовую карту игроку; возвращает, куда смотрит камера; нет месторождения — null.</summary>
    public static (double X, double Y)? Spawn(Sim sim, uint player)
    {
        if (FindBaseSpot(sim) is not { } spot)
            return null;
        sim.SetCredits(player, sim.Credits(player) + Credits);
        var nav = sim.Host.Tools.Navigation!;
        var reach = new HashSet<long>();
        nav.VehicleReach(spot.X - 1, spot.Y, BaseReach, false, reach);
        sim.PutBuilding("mine", spot.X, spot.Y, player);
        var (mw, mh) = Size("mine");
        var (cx, cy) = (spot.X + mw / 2.0, spot.Y + mh / 2.0);
        var core = PlaceNear(sim, spot, reach, "command", cx + 4, cy, player, 2);
        var at = core == 0 ? (X: cx, Y: cy) : (X: cx + 6, Y: cy);
        var stores = new List<uint>();
        foreach (var type in Buildings)
        {
            var building = PlaceNear(sim, spot, reach, type, at.X, at.Y, player, 1);
            if (building != 0 && Tables.Buildings[type].Stores)
                stores.Add(building);
        }
        // База — одна сеть: здания соединены трубами.
        sim.ConnectAll(player);
        // Запас раскладывается по хранилищам, пока в них есть место.
        foreach (var (good, amount) in Stock)
        {
            var left = amount;
            foreach (var store in stores)
            {
                var spec = Tables.Buildings[PipesScene.TypeOf(sim, store)!];
                if (left <= 0 || (spec.Accepts.Length > 0 && !spec.Accepts.Contains(good)))
                    continue;
                var load = Tables.All.Resources.Keys.Concat(Tables.All.Products.Keys).Concat(Tables.All.Ores.Keys).Sum(name => sim.Stock(store, name));
                var room = sim.Num(store, "Stock.capacity") - load;
                if (spec.Limits.TryGetValue(good, out var limit))
                    room = Math.Min(room, limit - sim.Stock(store, good));
                var put = Math.Max(0, Math.Min(left, room));
                if (put > 0)
                    sim.SetStock(store, good, sim.Stock(store, good) + put);
                left -= put;
            }
        }
        // Места юнитам — только там, куда доедет техника (freeTilesNear с blocked).
        var count = Trucks + Units.Length;
        var tiles = new List<(int X, int Y)>();
        var (fx, fy) = ((int)Math.Floor(cx), (int)Math.Floor(cy));
        var radiusMax = Math.Max(8, (int)Math.Ceiling(Math.Sqrt(count)));
        for (var radius = 2; radius <= radiusMax && tiles.Count < count; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius || tiles.Count >= count)
                        continue;
                    var (tx, ty) = (fx + dx * 2, fy + dy * 2);
                    if (reach.Contains(Key(tx, ty)) && nav.CanStand(false, tx, ty))
                        tiles.Add((tx, ty));
                }
        for (var i = 0; i < tiles.Count; i++)
            Scene.Spawn(sim, i < Trucks ? "truck" : Units[i - Trucks], player, tiles[i].X, tiles[i].Y);
        // Шахта в сети базы: руду на переработку несут трубы, грузовик к ней не привязывают (assignHaulers не нужен).
        return at;
    }

    /// <summary>Запас игрока по хранилищам всех его зон (stockOf).</summary>
    public static double StockOf(Sim sim, uint player, string good) =>
        sim.Zones(player).SelectMany(zone => zone.Buildings).Where(entity => PipesScene.TypeOf(sim, entity) is { } type && Tables.Buildings[type].Stores)
            .Sum(entity => sim.Stock(entity, good));
}
