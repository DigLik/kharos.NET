using System.Text.Json;
using Kharos.Engine.Layers;
using Kharos.Engine.Movement;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tests.Layers;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Movement;

/// <summary>
/// Мир для тестов хода — замена createSim из TS на компонентах движка: земля по генератору, слой paving, ходовые из
/// mods/base/data/moves.json, юниты по units.json (Position, Facing, Owner, Body, Mover, Sight), здания — Footprint.
/// Тик — planPaths, moveUnits, followCarriers, как в sim.ts.
/// </summary>
internal sealed class MoveWorld
{
    public const double Tick = 1.0 / 20;

    private static readonly Lazy<JsonElement> Moves = new(() => Data("moves"));
    private static readonly Lazy<JsonElement> Units = new(() => Data("units"));

    private static JsonElement Data(string name) =>
        JsonDocument.Parse(File.ReadAllText(Repo.File(Path.Combine("dotnet", "mods", "base", "data", name + ".json")))).RootElement.Clone();

    /// <param name="size">Сторона карты (boundsOf в TS: от −size/2).</param>
    /// <param name="config">Генератор земли; null — по умолчанию.</param>
    /// <param name="chassis">Ходовые; null — из moves.json.</param>
    public MoveWorld(int size = 1024, GeneratorConfig? config = null, ChassisTable? chassis = null) : this(size, config, chassis, null)
    {
    }

    private MoveWorld(int size, GeneratorConfig? config, ChassisTable? chassis, GameWorld? world)
    {
        _size = size;
        _config = config;
        _chassis = chassis;
        World = world ?? new GameWorld(new Schema());
        var half = size / 2;
        Bounds = new TileBounds(-half, -half, size - half, size - half);
        Land = new Land(config ?? GeneratorConfig.Default, LandArea.Of(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom));
        Layers = new LayerSet([new LayerSpec { Name = "paving", Scope = LayerScope.World, Channels = [Channel.U16] }]);
        Nav = new Navigation(World, Land, Layers, Bounds, chassis ?? ChassisTable.Parse(Moves.Value));
    }

    private readonly int _size;
    private readonly GeneratorConfig? _config;
    private readonly ChassisTable? _chassis;

    public GameWorld World { get; }
    public Land Land { get; }
    public LayerSet Layers { get; }
    public TileBounds Bounds { get; }
    public Navigation Nav { get; }
    public ulong Ticks { get; private set; }

    /// <summary>Таблица ходовых base с правкой: имя → новые доли.</summary>
    public static ChassisTable MovesWith(Func<string, JsonElement, Chassis?> change)
    {
        var table = ChassisTable.Parse(Moves.Value);
        return new ChassisTable(table.All.Select(chassis => change(chassis.Name, Moves.Value.GetProperty(chassis.Name)) ?? chassis));
    }

    /// <summary>Копия через сохранение мира и памяти хода (земля — та же по генератору, покрытия нет).</summary>
    public MoveWorld Copy()
    {
        var copy = new MoveWorld(_size, _config, _chassis, WorldSave.Load(new Schema(), WorldSave.Save(World))) { Ticks = Ticks };
        copy.Nav.LoadMotion(Nav.SaveMotion());
        return copy;
    }

    /// <summary>Один тик.</summary>
    public void Step()
    {
        Ticks++;
        Nav.PlanPaths();
        Nav.MoveUnits(Ticks, Tick);
        Nav.FollowCarriers();
    }

    public void Seconds(double seconds)
    {
        for (var i = 0; i < Math.Round(seconds / Tick); i++)
            Step();
    }

    /// <summary>Вид юнита из units.json.</summary>
    public static (double Speed, double Turn, double Radius, string Chassis, double Sight) Spec(string type)
    {
        var spec = Units.Value.GetProperty(type);
        var kind = spec.GetProperty("kind").GetString();
        var sight = spec.TryGetProperty("sight", out var own) ? own.GetDouble() : kind switch { "infantry" => 7, "air" => 10, _ => 8 };
        return (spec.GetProperty("speed").GetDouble(), spec.GetProperty("turn").GetDouble(), spec.GetProperty("radius").GetDouble(), spec.GetProperty("chassis").GetString()!, sight);
    }

    /// <summary>Юнит в центре тайла (x, y), смотрит вниз — как spawnUnit.</summary>
    public uint Spawn(string type, uint player, double x, double y)
    {
        var (speed, turn, radius, chassis, sight) = Spec(type);
        var entity = World.Spawn();
        Set(entity, Builtin.Position, ("x", x + 0.5), ("y", y + 0.5));
        Set(entity, Builtin.Facing, ("angle", Math.PI / 2));
        Set(entity, Builtin.Owner, ("player", player));
        Set(entity, Builtin.Body, ("radius", radius));
        World.Add(entity, Builtin.Mover);
        World.Set(entity, Builtin.Mover, "chassis", chassis);
        Set(entity, Builtin.Mover, ("speed", speed), ("turn", turn));
        Set(entity, Builtin.Sight, ("radius", sight));
        return entity;
    }

    /// <summary>Здание w×h левым верхним углом в (x, y): занимает тайлы (Footprint.solid).</summary>
    public uint Building(int x, int y, int w, int h, uint player = 1, bool solid = true)
    {
        var entity = World.Spawn();
        Set(entity, Builtin.Position, ("x", x), ("y", y));
        Set(entity, Builtin.Owner, ("player", player));
        World.Add(entity, Builtin.Footprint);
        Set(entity, Builtin.Footprint, ("w", w), ("h", h));
        World.Set(entity, Builtin.Footprint, "solid", solid);
        return entity;
    }

    public void Set(uint entity, int component, params (string Field, double Value)[] fields)
    {
        World.Add(entity, component);
        foreach (var (field, value) in fields)
            World.SetNumber(entity, component, World.Schema[component].FieldIndex(field), value);
    }

    public (double X, double Y) At(uint entity) =>
        (World.Number(entity, Builtin.Position, 0), World.Number(entity, Builtin.Position, 1));

    public (int X, int Y) TileOf(uint entity)
    {
        var (x, y) = At(entity);
        return ((int)Math.Floor(x), (int)Math.Floor(y));
    }

    public double Facing(uint entity) => World.Number(entity, Builtin.Facing, 0);

    public bool HasPath(uint entity) => World.Has(entity, Builtin.Path);

    public double PathNumber(uint entity, string field) => World.Get(entity, Builtin.Path, field) is { Kind: FieldKind.Flag } flag ? (flag.Flag ? 1 : 0) : World.Get(entity, Builtin.Path, field).Number;

    public List<double> Points(uint entity)
    {
        var points = new List<double>();
        Nav.ReadPoints(entity, points);
        return points;
    }

    public int PathCount => World.Store(Builtin.Path).Count;

    /// <summary>Путь сразу в Path (как world.add(Path(…)) в тестах TS).</summary>
    public void GivePath(uint entity, double[] points, int goalX, int goalY, bool direct = false)
    {
        World.Add(entity, Builtin.Path);
        World.SetBytes(entity, Builtin.Path, World.Schema[Builtin.Path].FieldIndex("points"), Navigation.PointBytes(points));
        World.Set(entity, Builtin.Path, "goalX", (double)goalX);
        World.Set(entity, Builtin.Path, "goalY", (double)goalY);
        World.Set(entity, Builtin.Path, "direct", direct);
    }

    /// <summary>Ровная скала: на тайле можно стоять технике и строить основные здания.</summary>
    public bool Flat(int x, int y) => Nav.IsWalkable(x, y) && Land.TerrainAt(x, y) == TerrainType.Rock;

    /// <summary>Ровная скала w×h (поиск как field/plateau в TS): левый верхний тайл.</summary>
    public (int X, int Y) Plateau(int w, int h, int step = 1)
    {
        for (var y = -200; y < 200; y += step)
            for (var x = -200; x < 200; x += step)
            {
                var ok = true;
                for (var dy = 0; dy < h && ok; dy++)
                    for (var dx = 0; dx < w && ok; dx++)
                        ok = Flat(x + dx, y + dy);
                if (ok)
                    return (x, y);
            }
        throw new InvalidOperationException("нет ровной скалы");
    }

    public void SetTile(int x, int y, TerrainType terrain, int tier, bool cliff, byte biome = 0) =>
        Land.SetTile(x, y, new TileEdit(terrain, biome, tier, cliff));
}
