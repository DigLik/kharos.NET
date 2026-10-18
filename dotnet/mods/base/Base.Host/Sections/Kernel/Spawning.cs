using System.Buffers.Binary;
using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Kernel;

/// <summary>
/// Вход игрока (player_joined): сущность счёта, а в партии со стартом (games.json start) — стартовый набор в случайном
/// месте на скале вдали от других, не запертом обрывами, где MCV развернётся сразу (spawnPoint и place в host.ts), и
/// защита новичка. Не нашлось места — на круге вокруг начала мира.
/// </summary>
internal static class Spawning
{
    /// <summary>С какой суммой и каким набором игрок появляется, если партия не задала своих (STARTING_CREDITS, STARTING_UNITS).</summary>
    public const double StartingCredits = 1500;
    public static readonly string[] StartingUnits = ["mcv", "buggy", "buggy", "infantry", "infantry", "infantry"];

    /// <summary>Сколько тайлов вокруг места должно быть доступно технике (SPAWN_ROOM).</summary>
    private const int SpawnRoom = 2000;

    /// <summary>Игрок вошёл: payload — номер u32.</summary>
    public static void OnJoined(EventContext ctx)
    {
        if (ctx.Payload.Length < 4)
            return;
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var call = Call.Of(ctx);
        var fresh = Accounts.Find(call, player) == 0;
        Accounts.Ensure(call, player);
        if (!fresh || !Core.Game.Start)
            return;
        var (x, y) = SpawnPoint(call) ?? Fallback(player);
        StartKit(call, player, x, y);
        Accounts.Protect(call, player);
    }

    /// <summary>Стартовый набор вокруг тайла (x, y) и стартовые кредиты (spawnStartingUnits); юниты — сколько встало.</summary>
    public static List<uint> StartKit(Call call, uint player, int x, int y)
    {
        var (credits, kit) = Core.Game.Units.Length > 0 ? (Core.Game.Credits, Core.Game.Units) : (StartingCredits, StartingUnits);
        Accounts.Add(call, player, credits);
        var tiles = Tools.FreeTiles(x, y, kit.Length);
        var units = new List<uint>();
        for (var i = 0; i < kit.Length && i < tiles.Length; i++)
            units.Add(Factory.SpawnUnit(call, kit[i], player, tiles[i].X + 0.5, tiles[i].Y + 0.5));
        return units;
    }

    /// <summary>Случайная точка появления: на скале, просторная, подальше от других; не нашлась — null.</summary>
    private static (int X, int Y)? SpawnPoint(Call call)
    {
        var spawn = Core.Game.Spawn;
        var size = Core.Game.Size ?? 0;
        if (size <= 0)
            return null;
        var others = new List<(double X, double Y)>();
        foreach (var entity in Core.All(S.Owner))
            if (call.OwnerOf(entity) != 0)
                others.Add(call.At(entity));
        var half = Math.Max(0, size - spawn.Margin);
        var area = (int)spawn.Area;
        var side = area * 2 + 1;
        var xs = new double[side * side];
        var ys = new double[side * side];
        var terrain = new TerrainInfo[side * side];
        var (w, h) = ((int)Factory.Building(Conversion.Command)!.Width, (int)Factory.Building(Conversion.Command)!.Height);
        for (var apart = spawn.Apart; apart >= 8; apart /= 2)
            for (var attempt = 0; attempt < spawn.Tries; attempt++)
            {
                var x = (int)Math.Floor((call.Random() * 2 - 1) * half);
                var y = (int)Math.Floor((call.Random() * 2 - 1) * half);
                if (!Tools.Terrain(x + 0.5, y + 0.5, out var here) || here.Terrain != 2 || !Tools.Walkable(x, y))
                    continue;
                if (others.Any(other => Math.Sqrt((other.X - x) * (other.X - x) + (other.Y - y) * (other.Y - y)) < apart))
                    continue;
                for (var i = 0; i < xs.Length; i++)
                {
                    xs[i] = x + i % side - area + 0.5;
                    ys[i] = y + i / side - area + 0.5;
                }
                Tools.Terrain(xs, ys, terrain);
                if (terrain.Count(tile => tile.Terrain == 2) < spawn.Rock)
                    continue;
                // Обрывы не должны запирать базу: технике надо куда-то выехать.
                if (Tools.Reach(x, y, SpawnRoom).Length < SpawnRoom)
                    continue;
                // MCV встаёт ровно в точку: развернуться он должен сразу, не ища места.
                var (siteX, siteY) = (x - w / 2, y - h / 2);
                if (Placement.CanPlace(call, Conversion.Command, siteX, siteY) && !Placement.InForeignZone(call, 0, siteX, siteY, w, h))
                    return (x, y);
            }
        return null;
    }

    /// <summary>Запасное место: на круге вокруг начала мира по номеру игрока, не запертое обрывами.</summary>
    private static (int X, int Y) Fallback(uint player)
    {
        var spawn = Core.Game.Spawn;
        var slot = (double)(player - 1);
        var radius = spawn.Radius * (1 + Math.Floor(slot / spawn.Slots));
        var angle = slot / spawn.Slots * Math.PI * 2;
        var size = Core.Game.Size ?? 64;
        static int Clamp(double value, int from, int to) => Math.Max(from + 2, Math.Min(to - 3, (int)Math.Floor(value + 0.5)));
        return Tools.OpenSpawn(Clamp(DetMath.Cos(angle) * radius, -size, size), Clamp(DetMath.Sin(angle) * radius, -size, size));
    }
}
