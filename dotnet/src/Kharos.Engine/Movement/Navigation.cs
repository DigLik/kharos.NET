using Kharos.Engine.Layers;
using Kharos.Engine.Numerics;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

/// <summary>
/// Ход и пути мира (docs/dotnet-n5.md, Н5.2, системы 15–17): земля под ногами (местность, покрытие, занятость, ходовые),
/// поиск пути с нормой тика, приказы движения и инструменты Plan, Stand, GroupMove и др. Перенос units.ts, path.ts,
/// movement.ts (crates/sim: walk.rs, path.rs, systems/movement.rs). Юнит — сущность с Position и Body; ходит, если есть
/// Mover; путь — Path. Здание — Footprint. Союзы — Team. Не потокобезопасна.
/// </summary>
public sealed partial class Navigation
{
    /// <summary>Виды покрытия по коду слоя paving (PAVE_KINDS в paved.ts): (код − 2) mod 2 — 0 фундамент, 1 дорога.</summary>
    private const int RoadKind = 1;

    private readonly int _paving = -1;
    private ulong _pavingRevision = ulong.MaxValue;
    private bool _anyPaving;

    public Navigation(GameWorld world, Land? land, LayerSet? layers, TileBounds? bounds, ChassisTable chassis)
    {
        World = world;
        Land = land;
        Layers = layers;
        Bounds = bounds;
        Chassis = chassis;
        Occupancy = new Occupancy(world, bounds);
        if (layers?.Find("paving") is { } paving)
            _paving = paving;
        F = new Fields(world.Schema);
    }

    public GameWorld World { get; }
    public Land? Land { get; }
    public LayerSet? Layers { get; }

    /// <summary>Карта; null — без границ.</summary>
    public TileBounds? Bounds { get; }

    public ChassisTable Chassis { get; }
    public Occupancy Occupancy { get; }

    /// <summary>Поиск пути: счёт осмотренного и норма — общие для всех поисков движка.</summary>
    public PathSearch Search { get; } = new();

    internal Fields F { get; }

    /// <summary>Номер тайла в наборах: y в старших 32 битах.</summary>
    public static long Key(int x, int y) => ((long)y << 32) | (uint)x;

    public static (int X, int Y) Unkey(long key) => ((int)key, (int)(key >> 32));

    // Земля.

    /// <summary>Лежит ли тайл внутри карты.</summary>
    public bool InBounds(int x, int y) => Bounds is not { } map || map.Contains(x, y);

    /// <summary>Вид, биом и подножие обрыва тайла; без местности — скала.</summary>
    public (TerrainType Terrain, Biome Biome, bool Foot) TileAt(int x, int y)
    {
        if (Land is not { } land)
            return (TerrainType.Rock, Biome.Erg, false);
        var chunk = land.GetChunk(x >> TileFormat.ChunkShift, y >> TileFormat.ChunkShift);
        var index = ((y & (TileFormat.ChunkSize - 1)) * TileFormat.ChunkSize + (x & (TileFormat.ChunkSize - 1))) * TileFormat.TileBytes;
        var terrain = (TerrainType)chunk[index];
        return (terrain, (Biome)(chunk[index + 1] >> 6), terrain != TerrainType.Mountain && (chunk[index + 3] & TileFormat.FootBit) != 0);
    }

    /// <summary>Подножие обрыва.</summary>
    public bool IsCliffFoot(int x, int y) => TileAt(x, y).Foot;

    /// <summary>
    /// Может ли наземный юнит находиться в тайле (<c>isWalkable</c>): внутри карты, не гора, не подножие обрыва (кроме
    /// пехоты, foot), не здание — кроме открытых ворот.
    /// </summary>
    public bool IsWalkable(int x, int y, bool foot = false)
    {
        if (!InBounds(x, y))
            return false;
        var (terrain, _, cliff) = TileAt(x, y);
        if (terrain == TerrainType.Mountain || (!foot && cliff))
            return false;
        var building = Occupancy.At(x, y);
        return building == 0 || Occupancy.IsOpen(building);
    }

    /// <summary>Может ли юнит находиться в тайле (<c>canStand</c>): летающему — любой внутри карты.</summary>
    public bool CanStand(bool air, int x, int y, bool foot = false) => air ? InBounds(x, y) : IsWalkable(x, y, foot);

    private int Pave(int x, int y) => _paving < 0 || Layers is null ? 0 : (int)Layers.Get(_paving, LayerOwner.World, x, y, 0);

    /// <summary>Лежит ли на тайле готовая дорога (road) или фундамент (<c>isPaved</c>).</summary>
    public bool IsPaved(bool road, int x, int y)
    {
        var code = Pave(x, y);
        return code != 0 && ((code - 2) % 2 == RoadKind) == road;
    }

    /// <summary>Хватает ли юниту ширины покрытия в тайле (<c>pavedFor</c>): юниту шире тайла — полоса в два.</summary>
    private bool PavedFor(double radius, int x, int y)
    {
        var width = radius > 0.5 ? 2 : 1;
        for (var top = y - width + 1; top <= y; top++)
            for (var left = x - width + 1; left <= x; left++)
            {
                var all = true;
                for (var tileY = top; tileY < top + width && all; tileY++)
                    for (var tileX = left; tileX < left + width && all; tileX++)
                        all = Pave(tileX, tileY) != 0;
                if (all)
                    return true;
            }
        return false;
    }

    /// <summary>
    /// Доля полной скорости ходовой на тайле (<c>terrainSpeed</c>): покрытие снимает замедление местности и ускоряет по
    /// paved, если хватает ширины; подножие обрыва — cliff у пехоты; песок и болото (на солончаках — brine, в красных
    /// пустошах — toxic) — по таблице, не медленнее slowest.
    /// </summary>
    public double TerrainSpeed(Chassis chassis, double radius, int x, int y)
    {
        if (chassis.Air)
            return 1;
        if (chassis.Paved)
        {
            var road = IsPaved(true, x, y);
            if (road || IsPaved(false, x, y))
            {
                if (!PavedFor(radius, x, y))
                    return 1;
                return road ? chassis.Road : chassis.Foundation;
            }
        }
        var (terrain, biome, foot) = TileAt(x, y);
        if (chassis.Foot && foot)
            return Math.Max(chassis.Slowest, chassis.Cliff);
        return terrain switch
        {
            TerrainType.Sand => Math.Max(chassis.Slowest, chassis.Sand),
            TerrainType.Swamp => Math.Max(chassis.Slowest, biome switch
            {
                Biome.SaltFlats => chassis.Brine,
                Biome.RedWastes => chassis.Toxic,
                _ => chassis.Swamp,
            }),
            TerrainType.Rock => Math.Max(chassis.Slowest, chassis.Rock),
            _ => 1,
        };
    }

    /// <summary>Доля скорости юнита на тайле — по его ходовой и радиусу.</summary>
    public double TerrainSpeed(uint entity, int x, int y)
    {
        var unit = Unit(entity);
        return TerrainSpeed(unit.Chassis, unit.Radius, x, y);
    }

    /// <summary>
    /// Видит ли юнит готовую дорогу в радиусе обзора (<c>roadInSight</c>, обзор — Sight.radius): дорогу, которой не
    /// видно, поиск пути не ищет.
    /// </summary>
    public bool RoadInSight(uint entity)
    {
        if (!AnyPaving())
            return false;
        var unit = Unit(entity);
        if (!unit.Placed)
            return false;
        var sight = unit.Sight;
        var (centerX, centerY) = (PathSearch.Floor(unit.X), PathSearch.Floor(unit.Y));
        var reach = (int)Math.Ceiling(sight);
        for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (dx * dx + dy * dy > sight * sight)
                    continue;
                if (IsPaved(true, centerX + dx, centerY + dy))
                    return true;
            }
        return false;
    }

    /// <summary>Есть ли покрытие вообще (<c>paving.size</c>): нет — и смотреть нечего.</summary>
    private bool AnyPaving()
    {
        if (_paving < 0 || Layers is not { } layers)
            return false;
        if (_pavingRevision != layers.Revision)
        {
            _pavingRevision = layers.Revision;
            _anyPaving = layers.Cells(_paving, LayerOwner.World).Count > 0;
        }
        return _anyPaving;
    }

    /// <summary>
    /// Куда техника доедет из тайла заливкой по <see cref="IsWalkable"/> (<c>vehicleReach</c>), не больше limit тайлов;
    /// сам тайл — в наборе, даже если стоять в нём нельзя. order — тайлы по порядку заливки (null — не нужно).
    /// </summary>
    public void VehicleReach(int x, int y, int limit, bool foot, HashSet<long> reached, List<long>? order = null)
    {
        reached.Clear();
        order?.Clear();
        var queue = order ?? _queue;
        queue.Clear();
        reached.Add(Key(x, y));
        queue.Add(Key(x, y));
        for (var at = 0; at < queue.Count && reached.Count < limit; at++)
        {
            var (fromX, fromY) = Unkey(queue[at]);
            foreach (var (dx, dy) in Sides)
            {
                var (tileX, tileY) = (fromX + dx, fromY + dy);
                var key = Key(tileX, tileY);
                if (reached.Contains(key) || !IsWalkable(tileX, tileY, foot))
                    continue;
                reached.Add(key);
                queue.Add(key);
            }
        }
    }

    private static readonly (int Dx, int Dy)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private readonly List<long> _queue = [];
    private readonly HashSet<long> _reach = [];

    /// <summary>Сколько тайлов вокруг места появления должно быть доступно технике (SPAWN_ROOM).</summary>
    public const int SpawnRoom = 2000;

    /// <summary>Не заперто ли место (<c>notWalledIn</c>): техника отсюда доедет хотя бы до <see cref="SpawnRoom"/> тайлов.</summary>
    public bool NotWalledIn(int x, int y)
    {
        if (!IsWalkable(x, y))
            return false;
        VehicleReach(x, y, SpawnRoom, false, _reach);
        return _reach.Count >= SpawnRoom;
    }

    /// <summary>Ближайшее к (x, y) место появления, не запертое обрывами (<c>openSpawn</c>): кольцами через 4; нет — сама точка.</summary>
    public (int X, int Y) OpenSpawn(int x, int y, int radius = 64)
    {
        const int step = 4;
        for (var ring = 0; ring <= radius; ring += step)
            for (var dy = -ring; dy <= ring; dy += step)
                for (var dx = -ring; dx <= ring; dx += step)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        continue;
                    if (NotWalledIn(x + dx, y + dy))
                        return (x + dx, y + dy);
                }
        return (x, y);
    }

    /// <summary>Дальше скольких колец от цели места группе не ищутся.</summary>
    private const int MaxGroupRadius = 40;

    /// <summary>Шаг между местами юнитов в группе, в тайлах.</summary>
    private const int GroupSpacing = 2;

    /// <summary>
    /// Проходимые тайлы вокруг точки, от ближних к дальним, через шаг группы (<c>freeTilesNear</c>): не больше count
    /// тайлов x, y подряд в into; с кольца fromRadius; blocked — тоже не годятся; air — места летающим.
    /// </summary>
    public void FreeTilesNear(int x, int y, int count, int fromRadius, HashSet<long>? blocked, bool air, List<int> into, bool foot = false)
    {
        into.Clear();
        var searchRadius = Math.Min(MaxGroupRadius, Math.Max(8, (int)Math.Ceiling(Math.Sqrt(count))));
        for (var radius = fromRadius; radius <= searchRadius && into.Count < count * 2; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                        continue;
                    var (tileX, tileY) = (x + dx * GroupSpacing, y + dy * GroupSpacing);
                    if (into.Count >= count * 2 || (blocked is not null && blocked.Contains(Key(tileX, tileY))))
                        continue;
                    if (CanStand(air, tileX, tileY, foot))
                    {
                        into.Add(tileX);
                        into.Add(tileY);
                    }
                }
    }

    /// <summary>
    /// Тайлы, занятые стоящими юнитами (<c>standingUnits</c>): путь их обходит. Тайл занят, если, встав в его центр,
    /// юнит радиуса radius задел бы стоящего. ignore — кого не считать; air — считать летающих, а не наземных; jammed —
    /// считать и застрявших в заторе (Path.tries ≥ <see cref="JamTries"/>).
    /// </summary>
    public void StandingUnits(IReadOnlySet<uint>? ignore, double radius, bool air, bool jammed, HashSet<long> into)
    {
        into.Clear();
        var bodies = World.Store(Builtin.Body);
        var entities = bodies.Entities;
        var radii = bodies.RawColumn(F.BodyRadius);
        var positions = World.Store(Builtin.Position);
        var paths = World.Store(Builtin.Path);
        var tries = paths.RawColumn(F.PathTries);
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            if (ignore is not null && ignore.Contains(entity))
                continue;
            var at = positions.RowOf(entity);
            if (at < 0 || ChassisOf(entity).Air != air)
                continue;
            var path = paths.RowOf(entity);
            if (path >= 0 && !(jammed && tries[path] >= JamTries))
                continue;
            var (x, y) = (positions.RawColumn(0)[at], positions.RawColumn(1)[at]);
            var reach = radii[row] + radius;
            var (right, bottom) = (PathSearch.Floor(x + reach), PathSearch.Floor(y + reach));
            for (var tileY = PathSearch.Floor(y - reach); tileY <= bottom; tileY++)
            {
                var dy = tileY + 0.5 - y;
                for (var tileX = PathSearch.Floor(x - reach); tileX <= right; tileX++)
                {
                    var dx = tileX + 0.5 - x;
                    if (dx * dx + dy * dy < reach * reach)
                        into.Add(Key(tileX, tileY));
                }
            }
        }
    }

    /// <summary>Наземные юниты с центром в прямоугольнике (<c>unitsIn</c>), кроме ignore, — по возрастанию номеров.</summary>
    public void UnitsIn(double x, double y, double width, double height, uint ignore, List<uint> into)
    {
        into.Clear();
        var bodies = World.Store(Builtin.Body).Entities;
        var positions = World.Store(Builtin.Position);
        foreach (var entity in bodies)
        {
            var at = positions.RowOf(entity);
            if (entity == ignore || at < 0 || ChassisOf(entity).Air)
                continue;
            var (px, py) = (positions.RawColumn(0)[at], positions.RawColumn(1)[at]);
            if (px >= x && px < x + width && py >= y && py < y + height)
                into.Add(entity);
        }
        into.Sort();
    }

    // Юниты.

    /// <summary>Ходовая сущности (Mover.chassis); без Mover — наземная по умолчанию.</summary>
    public Chassis ChassisOf(uint entity)
    {
        var movers = World.Store(Builtin.Mover);
        var row = movers.RowOf(entity);
        return row < 0 ? Movement.Chassis.Ground : Chassis.Of(World, movers.RawColumn(F.MoverChassis)[row]);
    }

    /// <summary>Что движку нужно знать о юните.</summary>
    public UnitInfo Unit(uint entity)
    {
        var positions = World.Store(Builtin.Position);
        var at = positions.RowOf(entity);
        var info = new UnitInfo { Entity = entity, Chassis = ChassisOf(entity), Radius = 0 };
        if (at >= 0)
        {
            info.Placed = true;
            info.X = positions.RawColumn(0)[at];
            info.Y = positions.RawColumn(1)[at];
        }
        var bodies = World.Store(Builtin.Body);
        var body = bodies.RowOf(entity);
        if (body >= 0)
        {
            info.Body = true;
            info.Radius = bodies.RawColumn(F.BodyRadius)[body];
            info.Rooted = bodies.RawColumn(F.BodyRooted)[body] != 0;
        }
        var movers = World.Store(Builtin.Mover);
        var mover = movers.RowOf(entity);
        if (mover >= 0)
        {
            info.Speed = movers.RawColumn(F.MoverSpeed)[mover];
            info.Turn = movers.RawColumn(F.MoverTurn)[mover];
        }
        var sights = World.Store(Builtin.Sight);
        var sight = sights.RowOf(entity);
        if (sight >= 0)
            info.Sight = sights.RawColumn(0)[sight];
        info.HasPath = World.Has(entity, Builtin.Path);
        return info;
    }

    /// <summary>Точки пути юнита (Path.points) — в into; нет пути — false.</summary>
    public bool ReadPoints(uint entity, List<double> into)
    {
        into.Clear();
        var paths = World.Store(Builtin.Path);
        var row = paths.RowOf(entity);
        if (row < 0)
            return false;
        var bytes = paths.BytesAt(F.PathPointsSlot, row);
        for (var at = 0; at + 8 <= bytes.Length; at += 8)
            into.Add(System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[at..]));
        return true;
    }

    /// <summary>Байты точек пути (f64 x, y подряд, little-endian).</summary>
    public static byte[] PointBytes(IReadOnlyList<double> points, int start = 0, int count = -1)
    {
        if (count < 0)
            count = points.Count - start;
        var bytes = new byte[count * 8];
        for (var i = 0; i < count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), points[start + i]);
        return bytes;
    }

    /// <summary>Угол к промежутку от −π до π (<c>wrap</c>).</summary>
    public static double Wrap(double angle) => angle - Turn * JsMath.Round(angle / Turn);

    /// <summary>Куда будет смотреть тот, кто поворачивается от facing к wanted не больше чем на maxTurn (<c>turnToward</c>).</summary>
    public static double TurnToward(double facing, double wanted, double maxTurn)
    {
        var off = Wrap(wanted - facing);
        return Math.Abs(off) <= maxTurn ? wanted : Wrap(facing + JsMath.Sign(off) * maxTurn);
    }

    private const double Turn = Math.PI * 2;
}

/// <summary>Юнит глазами движка: место, корпус, ходовая, обзор.</summary>
public struct UnitInfo
{
    public uint Entity;
    /// <summary>Есть Position.</summary>
    public bool Placed;
    public double X, Y;
    /// <summary>Есть Body.</summary>
    public bool Body;
    public double Radius;
    public bool Rooted;
    public Chassis Chassis;
    public double Speed, Turn, Sight;
    public bool HasPath;
}

/// <summary>Номера столбцов и полей компонентов движка.</summary>
internal sealed class Fields
{
    public Fields(Schema schema)
    {
        var body = schema[Builtin.Body];
        BodyRadius = body.Field("radius")!.Slot;
        BodyMass = body.Field("mass")!.Slot;
        BodyRooted = body.Field("rooted")!.Slot;
        var mover = schema[Builtin.Mover];
        MoverChassis = mover.Field("chassis")!.Slot;
        MoverSpeed = mover.Field("speed")!.Slot;
        MoverTurn = mover.Field("turn")!.Slot;
        var path = schema[Builtin.Path];
        PathGoalX = path.Field("goalX")!;
        PathGoalY = path.Field("goalY")!;
        PathWait = path.Field("wait")!;
        PathTriesField = path.Field("tries")!;
        PathNear = path.Field("near")!;
        PathRoads = path.Field("roads")!;
        PathDirect = path.Field("direct")!;
        PathStuck = path.Field("stuck")!;
        PathBest = path.Field("best")!;
        PathSlot = path.Field("slot")!;
        PathPoints = path.Field("points")!;
        PathTries = PathTriesField.Slot;
        PathPointsSlot = PathPoints.Slot;
        var mount = schema[Builtin.Mount];
        MountParent = mount.Field("parent")!.Slot;
        MountAlong = mount.Field("along")!.Slot;
        MountAcross = mount.Field("across")!.Slot;
        MountAngle = mount.Field("angle")!.Slot;
        var team = schema[Builtin.Team];
        TeamPlayer = team.Field("player")!.Slot;
        TeamHead = team.Field("head")!.Slot;
        var footprint = schema[Builtin.Footprint];
        FootprintW = footprint.Field("w")!.Slot;
        FootprintH = footprint.Field("h")!.Slot;
    }

    public int BodyRadius, BodyMass, BodyRooted;
    public int MoverChassis, MoverSpeed, MoverTurn;
    public FieldDef PathGoalX, PathGoalY, PathWait, PathTriesField, PathNear, PathRoads, PathDirect, PathStuck, PathBest, PathSlot, PathPoints;
    public int PathTries, PathPointsSlot;
    public int MountParent, MountAlong, MountAcross, MountAngle;
    public int TeamPlayer, TeamHead;
    public int FootprintW, FootprintH;
}
