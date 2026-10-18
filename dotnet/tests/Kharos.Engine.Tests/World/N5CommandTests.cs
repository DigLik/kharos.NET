using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

/// <summary>Команды Н5: слои (Layer, Item), земля (Land), адресные сигналы, уничтожение носителя с турелями.</summary>
public sealed class N5CommandTests
{
    private readonly GameWorld _world = Fixtures.World();
    private readonly List<WorldEvent> _events = [];
    private readonly List<Signal> _signals = [];

    private static LayerSet Layers() => new(
    [
        new LayerSpec { Name = "paving", Scope = LayerScope.World, Channels = [Channel.U8, Channel.F32] },
        new LayerSpec { Name = "memory", Scope = LayerScope.Player, Channels = [Channel.U8] },
        new LayerSpec { Name = "deposits", Scope = LayerScope.World, Cell = 8, Fields = [("kind", Channel.U8), ("amount", Channel.F64)] },
    ]);

    [Fact]
    public void LayerAndItemCommandsChangeLayers()
    {
        var layers = Layers();
        layers.AddPlayer(2);
        var scope = new CommandScope(layers, null);
        var commands = new CommandBuffer(_world);
        commands.Layer(0, 0, 1, -3, 4, 3, 2, 2.5);
        commands.Layer(1, 2, 0, 10, 10, 1, 1, 7);
        commands.PushItem(2, 0, 12.5, -3.25, [3, 40]);
        commands.PushItem(2, 0, 13, -3, [1]);
        var revision = layers.Revision;
        Commands.Apply(_world, commands.Written, _events, _signals, scope);
        for (var y = 4; y < 6; y++)
            for (var x = -3; x < 0; x++)
                Assert.Equal(2.5, layers.Get(0, LayerOwner.World, x, y, 1));
        Assert.Equal(0, layers.Get(0, LayerOwner.World, 0, 4, 1));
        Assert.Equal(7, layers.Get(1, LayerOwner.Player(2), 10, 10, 0));
        var items = layers.Items(2, LayerOwner.World, layers.CellOf(2, 12.5, -3.25));
        Assert.Equal([new LayerItem(12.5, -3.25, [3, 40]), new LayerItem(13, -3, [1, 0])], items);
        Assert.True(layers.Revision > revision);

        commands.Clear();
        commands.DeleteItems(2, 0, 12.5, -3.25);
        Commands.Apply(_world, commands.Written, _events, _signals, scope);
        Assert.Equal([new LayerItem(13, -3, [1, 0])], layers.Items(2, LayerOwner.World, layers.CellOf(2, 13, -3)));
    }

    [Theory]
    [InlineData(0, 2u, "у мира, а не у игроков")]
    [InlineData(1, 0u, "у игроков, а не у мира")]
    [InlineData(5, 0u, "нет слоя №5")]
    [InlineData(2, 0u, "предметы, а не сетка")]
    public void BadLayerCommandsRejectWholeBuffer(int layer, uint owner, string message)
    {
        var layers = Layers();
        var commands = new CommandBuffer(_world);
        commands.Layer(0, 0, 0, 0, 0, 1, 1, 1);
        commands.Layer(layer, owner, 0, 0, 0, 1, 1, 1);
        var error = Assert.Throws<CommandException>(() => Commands.Apply(_world, commands.Written, _events, _signals, new CommandScope(layers, null)));
        Assert.Contains(message, error.Message);
        Assert.Equal(0, layers.Get(0, LayerOwner.World, 0, 0, 0));
        // Без слоёв у партии — любая запись о слоях неверна.
        Assert.Contains("нет слоёв", Assert.Throws<CommandException>(() => Commands.Validate(_world, commands.Written)).Message);
    }

    [Fact]
    public void LandCommandsEditTilesAndPeaks()
    {
        var land = new Land(new GeneratorConfig { Seed = 3, Blank = true }, LandArea.Of(-64, -64, 64, 64));
        var commands = new CommandBuffer(_world);
        commands.LandTile(-5, 7, new TileEdit(TerrainType.Swamp, 2, 1, true));
        commands.LandPeak(3, 3, 1, 3.5, 4, 2);
        var before = land.Revision;
        Commands.Apply(_world, commands.Written, _events, _signals, new CommandScope(null, land));
        Assert.Equal(TerrainType.Swamp, land.TerrainAt(-5, 7));
        Assert.Equal(TerrainType.Mountain, land.TerrainAt(3, 3));
        Assert.Equal(new Peak(3.5, 4, land.PeakOf(3, 3)!.Value.Radius), land.PeakOf(3, 3));
        Assert.True(land.Revision > before);

        commands.Clear();
        commands.LandTile(0, 0, new TileEdit(TerrainType.Mountain, 0, 0, false));
        Assert.Contains("не той формы", Assert.Throws<CommandException>(() => Commands.Validate(_world, commands.Written, new CommandScope(null, land))).Message);
        Assert.Contains("нет земли", Assert.Throws<CommandException>(() => Commands.Validate(_world, commands.Written)).Message);
    }

    [Fact]
    public void AddressedSignalsKeepRecipients()
    {
        var commands = new CommandBuffer(_world);
        commands.Signal("all", [1]);
        commands.SignalTo("mine", [2], [3, 300]);
        commands.SignalAt("boom", [], -1.5, 2.25);
        Commands.Apply(_world, commands.Written, _events, _signals);
        Assert.Equal(["all", "mine", "boom"], _signals.Select(signal => signal.Name));
        Assert.True(_signals[0].Broadcast);
        Assert.Equal([3u, 300u], _signals[1].To!);
        Assert.Null(_signals[1].At);
        Assert.Equal((-1.5, 2.25), _signals[2].At);
        Assert.False(_signals[2].Broadcast);
    }

    [Fact]
    public void DestroyTakesMountedEntitiesAlong()
    {
        // Mount — компонент движка; объявление здесь совпадёт со встроенным (то же поле того же вида).
        var schema = Fixtures.Schema();
        schema.DeclareNumber("Mount", "parent", 0);
        var world = new GameWorld(schema);
        var mount = schema.Id("Mount");
        var parent = schema[mount].FieldIndex("parent");
        var carrier = world.Spawn();
        uint Mounted(uint on)
        {
            var entity = world.Spawn();
            world.Add(entity, mount);
            world.SetNumber(entity, mount, parent, on);
            return entity;
        }
        var turret = Mounted(carrier);
        var gun = Mounted(turret);
        var other = Mounted(world.Spawn());
        var commands = new CommandBuffer(world);
        commands.Destroy(carrier);
        Commands.Apply(world, commands.Written, _events, _signals);
        Assert.False(world.Alive(carrier) || world.Alive(turret) || world.Alive(gun));
        Assert.True(world.Alive(other));
    }

    [Fact]
    public void UnsavedComponentsStayOutOfWorldSave()
    {
        var schema = Fixtures.Schema();
        schema.DeclareNumber("Glow", "until", 0);
        schema.Attributes("Glow", save: false);
        var world = new GameWorld(schema);
        var glow = schema.Id("Glow");
        var entity = world.At(1, 2);
        world.Add(entity, glow);
        world.SetNumber(entity, glow, 0, 9);
        var loaded = WorldSave.Load(schema, WorldSave.Save(world));
        Assert.True(loaded.Alive(entity) && loaded.Has(entity, Builtin.Position));
        Assert.False(loaded.Has(entity, glow));
    }
}
