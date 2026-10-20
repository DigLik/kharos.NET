using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Mining;
using Base.Host.Sections.Pipes;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Тестовая карта (sandbox.ts) для партии с <c>sandbox</c> в games.json: готовая база игрока у ближайшего к началу мира
/// месторождения металла. Шахта, главное здание, переработка, сборочный цех, хранилища с запасом, космопорт,
/// электростанции и заводы уже стоят; строители, немного войск, грузовики и кредиты.
/// <para>
/// В TS всё встаёт за один вызов. Здесь здания, юниты, кредиты и запас ставятся сразу (<see cref="Spawn"/>, во входе
/// игрока): занятость и проходимость движка этого же вызова новых зданий ещё не знают, поэтому их основания помнит сама
/// расстановка. Трубы (connectAll) кладёт система players/sandbox по одной сети за тик — кэш сетей видит новые трубы только
/// в следующем вызове (<see cref="Piping.ConnectAll"/>); кого ещё соединять, помнит память мода (<see cref="Key"/>).
/// </para>
/// <para>
/// В TS юниты встают после труб и на трубу не встают. Здесь юниты встают сразу (на тех же кольцах), а когда база
/// соединена, система пересаживает их туда, куда их поставил бы TS: на те же места-кандидаты без легших труб
/// (<see cref="Seats"/>). Без этого юнит стоял внутри трубы, а места остальных сдвигались против TS.
/// </para>
/// </summary>
internal static class Sandbox
{
    /// <summary>Ключ памяти: базы, которые ещё соединяются трубами (игрок, шахта, главное здание, первый грузовик — u32).</summary>
    public const string Key = "players.sandbox";

    /// <summary>Ключ памяти: места юнитов ждущей базы (игрок, юниты, места-кандидаты по порядку колец).</summary>
    public const string Seats = "players.sandbox.seats";

    /// <summary>Кредиты игрока на тестовой карте (SANDBOX_CREDITS).</summary>
    public const double Credits = 10000;

    /// <summary>Что уже лежит в хранилищах (SANDBOX_STOCK).</summary>
    private static readonly (string Good, double Amount)[] Stock =
        [("metal", 120), ("silicon", 40), ("fuel", 30), ("kharite", 12), ("blocks", 20), ("ammo", 60), ("parts", 30)];

    /// <summary>Насколько далеко от начала мира ищется месторождение, в ячейках (SEARCH_CELLS).</summary>
    private const int SearchCells = 6;

    /// <summary>Сколько тайлов вокруг шахты свободны: там встают грузовики (MINE_ROOM).</summary>
    private const int MineRoom = 2;

    /// <summary>Сколько тайлов заливает поиск места, куда доедет техника от шахты (BASE_REACH).</summary>
    private const int BaseReach = 8000;

    /// <summary>Сколько грузовиков (SANDBOX_TRUCKS).</summary>
    private const int Trucks = 4;

    /// <summary>Шаг между юнитами группы и самый большой радиус поиска мест (GROUP_SPACING, MAX_GROUP_RADIUS).</summary>
    private const int GroupSpacing = 2, MaxGroupRadius = 40;

    private const string Mine = "mine", Truck = "truck";

    /// <summary>Что стоит на тестовой карте, кроме шахты и главного здания, от главного здания наружу (SANDBOX_BUILDINGS).</summary>
    private static readonly string[] Buildings =
    [
        "generator", "generator", "generator", "generator", "smelter", "siliconWorks", "blockPlant", "ammoPlant", "metalYard", "siliconStore", "fuelTank",
        "khariteVault", "blockYard", "ammoBunker", "partsLocker", "spaceport", "factory", "barracks", "matter", "lab", "radar",
    ];

    /// <summary>Юниты, кроме грузовиков (SANDBOX_UNITS).</summary>
    private static readonly string[] Units = ["builder", "builder", "infantry", "infantry", "rocketeer", "buggy", "tank", "tesla", "carrier"];

    private static (int W, int H) Size(string type) => Factory.Building(type) is { } spec ? ((int)spec.Width, (int)spec.Height) : (0, 0);

    private static long Key2(int x, int y) => ((long)y << 32) | (uint)x;

    /// <summary>Тайлы вокруг (x, y) кольцами, от ближних к дальним, до radius включительно (rings).</summary>
    private static IEnumerable<(int X, int Y)> Rings(int x, int y, int radius)
    {
        for (var ring = 0; ring <= radius; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (x + dx, y + dy);
    }

    /// <summary>Месторождение металла, у которого поместится база: вокруг шахты проходимо (findBaseSpot).</summary>
    private static Spot? FindBaseSpot(Call call)
    {
        var cells = new List<(int X, int Y)>();
        for (var y = -SearchCells; y <= SearchCells; y++)
            for (var x = -SearchCells; x <= SearchCells; x++)
                cells.Add((x, y));
        var (w, h) = Size(Mine);
        foreach (var cell in cells.OrderBy(cell => cell.X * cell.X + cell.Y * cell.Y))
        {
            if (Deposits.In(cell.X, cell.Y) is not [var spot, ..] || spot.Name != "metal" || !Placement.CanPlace(call, Mine, spot.X, spot.Y))
                continue;
            var open = true;
            for (var y = spot.Y - 1; y <= spot.Y + h && open; y++)
                for (var x = spot.X - 1; x <= spot.X + w; x++)
                {
                    var inside = x >= spot.X && x < spot.X + w && y >= spot.Y && y < spot.Y + h;
                    if (!inside && !Tools.Walkable(x, y))
                        open = false;
                }
            if (open)
                return spot;
        }
        return null;
    }

    /// <summary>Основания, поставленные в этом вызове: их движок ещё не знает.</summary>
    private sealed class Placed
    {
        public readonly List<(int X, int Y, int W, int H)> Rects = [];

        public bool Hits(int x, int y, int w, int h) =>
            Rects.Exists(rect => x < rect.X + rect.W && x + w > rect.X && y < rect.Y + rect.H && y + h > rect.Y);
    }

    /// <summary>Не подходит ли здание к шахте ближе MINE_ROOM тайлов (clearOfMine).</summary>
    private static bool ClearOfMine(Spot spot, string type, int x, int y)
    {
        var (w, h) = Size(type);
        var (mw, mh) = Size(Mine);
        var apart = x >= spot.X + mw + MineRoom || x + w + MineRoom <= spot.X;
        return apart || y >= spot.Y + mh + MineRoom || y + h + MineRoom <= spot.Y;
    }

    /// <summary>Стоит ли здание вплотную к тайлу, куда доедет техника (servable).</summary>
    private static bool Servable(HashSet<long> reach, string type, int x, int y)
    {
        var (w, h) = Size(type);
        for (var ty = y - 1; ty <= y + h; ty++)
            for (var tx = x - 1; tx <= x + w; tx++)
            {
                var inside = tx >= x && tx < x + w && ty >= y && ty < y + h;
                if (!inside && reach.Contains(Key2(tx, ty)))
                    return true;
            }
        return false;
    }

    private static uint Put(Call call, Placed placed, string type, int x, int y, uint player)
    {
        var (w, h) = Size(type);
        placed.Rects.Add((x, y, w, h));
        return Factory.PlaceBuilding(call, type, x, y, player);
    }

    /// <summary>Ставит здание на ближайшее к (x, y) место с проходом вокруг, куда подъедет техника (placeNear); нет — 0.</summary>
    private static uint PlaceNear(Call call, Placed placed, Spot spot, HashSet<long> reach, string type, double x, double y, uint player, int gap)
    {
        var (w, h) = Size(type);
        foreach (var (tx, ty) in Rings((int)Math.Floor(x + 0.5), (int)Math.Floor(y + 0.5), 14))
        {
            if (placed.Hits(tx - gap, ty - gap, w + gap * 2, h + gap * 2) || !Placement.CanPlace(call, type, tx, ty, gap) || !ClearOfMine(spot, type, tx, ty)
                || !Servable(reach, type, tx, ty))
                continue;
            return Put(call, placed, type, tx, ty, player);
        }
        return 0;
    }

    /// <summary>
    /// Ставит тестовую карту игроку (spawnSandbox). false — подходящего месторождения рядом нет: игрок начинает как в
    /// обычной партии.
    /// </summary>
    public static bool Spawn(Call call, uint player)
    {
        if (player == 0 || FindBaseSpot(call) is not { } spot)
            return false;
        Accounts.Add(call, player, Credits);
        // Куда доедет техника от шахты — до того, как встанут здания: между ними оставлены проходы.
        var reach = new HashSet<long>();
        foreach (var (rx, ry) in Tools.Reach(spot.X - 1, spot.Y, BaseReach))
            reach.Add(Key2(rx, ry));
        var placed = new Placed();
        var mine = Put(call, placed, Mine, spot.X, spot.Y, player);
        var (mw, mh) = Size(Mine);
        var (cx, cy) = (spot.X + mw / 2.0, spot.Y + mh / 2.0);
        var core = PlaceNear(call, placed, spot, reach, Kernel.Conversion.Command, cx + 4, cy, player, 2);
        var at = core == 0 ? (X: cx, Y: cy) : (X: cx + 6, Y: cy);
        var stores = new List<uint>();
        foreach (var type in Buildings)
        {
            var building = PlaceNear(call, placed, spot, reach, type, at.X, at.Y, player, 1);
            if (building != 0 && Factory.Building(type)?.Stores == true)
                stores.Add(building);
        }
        // Запас раскладывается по хранилищам, пока в них есть место.
        foreach (var (good, amount) in Stock)
        {
            var left = amount;
            foreach (var store in stores)
                left -= Stocks.Put(call, store, good, left);
        }
        // Места юнитам — только там, куда доедет техника, и не под новыми зданиями (freeTilesNear с blocked).
        var count = Trucks + Units.Length;
        // Все места-кандидаты на кольцах: часть из них займут трубы, и тогда юниты пересядут дальше (Reseat).
        var tiles = FreeTiles(placed, reach, (int)Math.Floor(cx), (int)Math.Floor(cy), count, 2, all: true);
        var trucks = new List<uint>();
        var units = new List<uint>();
        for (var i = 0; i < Math.Min(count, tiles.Count); i++)
        {
            var type = i < Trucks ? Truck : Units[i - Trucks];
            var unit = Factory.SpawnUnit(call, type, player, tiles[i].X + 0.5, tiles[i].Y + 0.5);
            units.Add(unit);
            if (type == Truck)
                trucks.Add(unit);
        }
        SaveSeats(call, player, units, tiles);
        // База — одна сеть: здания соединяет трубами система players/sandbox, начиная со следующего тика.
        Pending(call, [.. Pending(call), (player, mine, core, trucks.Count > 0 ? trucks[0] : 0)]);
        return true;
    }

    /// <summary>
    /// Свободные тайлы кольцами с шагом группы (freeTilesNear): проходимые, в reach и не под новыми зданиями. all — все
    /// до последнего кольца поиска, а не первые count.
    /// </summary>
    private static List<(int X, int Y)> FreeTiles(Placed placed, HashSet<long> reach, int x, int y, int count, int from, bool all = false)
    {
        var radiusMax = Math.Min(MaxGroupRadius, Math.Max(8, (int)Math.Ceiling(Math.Sqrt(count))));
        var tiles = new List<(int X, int Y)>();
        for (var radius = from; radius <= radiusMax && (all || tiles.Count < count); radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius || (!all && tiles.Count >= count))
                        continue;
                    var (tx, ty) = (x + dx * GroupSpacing, y + dy * GroupSpacing);
                    if (reach.Contains(Key2(tx, ty)) && !placed.Hits(tx, ty, 1, 1) && Tools.Walkable(tx, ty))
                        tiles.Add((tx, ty));
                }
        return tiles;
    }

    /// <summary>
    /// Система players/sandbox: соединяет трубами одну сеть ждущей базы за тик (connectAll); соединять больше нечего —
    /// шахта вне сети главного здания получает грузовик (assignHaulers), и база готова.
    /// </summary>
    public sealed class Connect : ISystem
    {
        public void Bind(SystemBinding binding) => P.Load(binding.Component);

        public void Run(SystemContext ctx)
        {
            if (!Core.Game.Sandbox)
                return;
            var call = Call.Of(ctx);
            var pending = Pending(call);
            if (pending.Count == 0)
                return;
            var (player, mine, core, truck) = pending[0];
            if (Piping.ConnectAll(call, player))
                return;
            Reseat(call, player);
            if (call.Alive(mine) && truck != 0 && call.Alive(truck) && !ReferenceEquals(Zones.NetworkOf(mine), Zones.NetworkOf(core != 0 ? core : mine)))
                Commands.Accept(call, player, HaulOrder.Name, new HaulOrder { Units = [truck], Mine = mine });
            Pending(call, pending.Skip(1).ToList());
        }
    }

    /// <summary>Помнит юниты базы и места-кандидаты: (игрок u32, юнитов u32, юниты u32…, мест u32, места i32 x, y…).</summary>
    private static void SaveSeats(Call call, uint player, List<uint> units, List<(int X, int Y)> tiles)
    {
        var old = Memory.Of(call).Get(Seats) ?? [];
        var bytes = new byte[old.Length + 12 + units.Count * 4 + tiles.Count * 8];
        old.CopyTo(bytes, 0);
        var at = old.Length;
        void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
            at += 4;
        }
        U32(player);
        U32((uint)units.Count);
        foreach (var unit in units)
            U32(unit);
        U32((uint)tiles.Count);
        foreach (var (x, y) in tiles)
        {
            U32((uint)x);
            U32((uint)y);
        }
        Memory.Of(call).Put(call, Seats, bytes);
    }

    /// <summary>
    /// Пересаживает юниты соединённой базы (freeTilesNear после connectAll): места-кандидаты, на которые не легли трубы
    /// и здания, по порядку колец. Пересаживается только тот, кто ещё стоит, где встал.
    /// </summary>
    private static void Reseat(Call call, uint player)
    {
        if (Memory.Of(call).Get(Seats) is not { Length: > 0 } bytes)
            return;
        var rest = new List<byte>();
        var at = 0;
        uint U32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            at += 4;
            return value;
        }
        while (at + 12 <= bytes.Length)
        {
            var start = at;
            var owner = U32();
            var units = new uint[U32()];
            for (var i = 0; i < units.Length; i++)
                units[i] = U32();
            var tiles = new (int X, int Y)[U32()];
            for (var i = 0; i < tiles.Length; i++)
                tiles[i] = ((int)U32(), (int)U32());
            if (owner != player)
            {
                rest.AddRange(bytes.AsSpan(start, at - start).ToArray());
                continue;
            }
            var free = tiles.Where(tile => Tools.Walkable(tile.X, tile.Y)).ToList();
            for (var i = 0; i < units.Length && i < free.Count; i++)
            {
                var (x, y) = (tiles[i].X + 0.5, tiles[i].Y + 0.5);
                if (free[i] == tiles[i] || !call.Alive(units[i]) || call.At(units[i]) != (x, y))
                    continue;
                call.Set(units[i], S.Position).Number(S.PositionX, free[i].X + 0.5).Number(S.PositionY, free[i].Y + 0.5);
                call.Remove(units[i], S.Path);
            }
        }
        Memory.Of(call).Put(call, Seats, [.. rest]);
    }

    private static List<(uint Player, uint Mine, uint Core, uint Truck)> Pending(Call call)
    {
        var list = new List<(uint, uint, uint, uint)>();
        if (Memory.Of(call).Get(Key) is not { } bytes)
            return list;
        for (var at = 0; at + 16 <= bytes.Length; at += 16)
            list.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 12))));
        return list;
    }

    private static void Pending(Call call, List<(uint Player, uint Mine, uint Core, uint Truck)> list)
    {
        var bytes = new byte[list.Count * 16];
        for (var i = 0; i < list.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 16), list[i].Player);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 16 + 4), list[i].Mine);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 16 + 8), list[i].Core);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 16 + 12), list[i].Truck);
        }
        Memory.Of(call).Put(call, Key, bytes);
    }
}
