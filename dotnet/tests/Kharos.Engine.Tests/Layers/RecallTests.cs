using System.Buffers.Binary;
using Kharos.Engine.Layers;
using Kharos.Engine.Recall;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tests.World;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Layers;

/// <summary>Память игроков (engine.recallLand, engine.recallPaving; Д3): порт landMemory.test.ts и правил recall.rs на движке без хоста.</summary>
public sealed class RecallTests
{
    private readonly GameWorld _world = Fixtures.World();
    private readonly GeneratorConfig _config = new() { Seed = 5, Blank = true };
    private readonly Land _land;
    private ulong _tick;

    /// <summary>Обзор из делегата: кто что видит.</summary>
    private sealed class Eyes(params uint[] players) : IRecallVision
    {
        public Func<uint, double, double, bool> Sight { get; set; } = (_, _, _) => false;
        public IEnumerable<uint> Players => players;
        public bool Sees(uint player, double x, double y) => Sight(player, x, y);
    }

    public RecallTests() => _land = new Land(_config, LandArea.Of(-64, -64, 64, 64));

    private static readonly TileEdit Rock = new(TerrainType.Rock, 0, 1, false);
    private static readonly TileEdit Swamp = new(TerrainType.Swamp, 0, 0, false);

    /// <summary>Тиков вперёд: память сверяется на каждом, увиденное узнаётся на кратных LearnTicks.</summary>
    private void Ticks(PlayerMemory memory, Eyes eyes, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _tick++;
            memory.UpdateLand(eyes, _world, _tick);
            memory.UpdatePaving(eyes, _world, _tick);
        }
    }

    private static Func<uint, double, double, bool> Around(uint who, double x, double y, double radius) =>
        (player, px, py) => player == who && Math.Abs(px - x) <= radius && Math.Abs(py - y) <= radius;

    private static LayerSet PavingLayers() => new([new LayerSpec { Name = "paving", Scope = LayerScope.World, Channels = [Channel.U16], Save = true, Remember = true }]);

    [Fact]
    public void EditInFogIsLearnedOnlyWhenSeen()
    {
        var memory = new PlayerMemory(_land, PavingLayers());
        var eyes = new Eyes(1) { Sight = Around(1, 0, 0, 10) };
        var (near, far) = ((2, 2), (40, 40));
        var farBefore = _land.Stored(far.Item1, far.Item2);
        _land.SetTile(near.Item1, near.Item2, Swamp);
        _land.SetTile(far.Item1, far.Item2, Rock);
        // Увиденное игроки сверяют раз в LearnTicks тиков.
        Ticks(memory, eyes, LayerMemory.LearnTicks);
        var learned = memory.TakeLearnedLand();
        Assert.Equal([new LandTile(2, 2, _land.Stored(2, 2))], learned[1]);
        Assert.Equal([new LandTile(2, 2, _land.Stored(2, 2))], memory.KnownLand(1));
        // Подключившийся позже получает карту до правок: в ней обе правки не видны, дальняя — и в знании игрока.
        var pristine = Land.Unpack(_config, memory.PristineLand()!);
        Assert.Equal(TerrainType.Sand, pristine.TerrainAt(2, 2));
        Assert.Equal(farBefore, pristine.Stored(far.Item1, far.Item2));

        // Своё здание у дальнего тайла открывает его: правка доходит.
        eyes.Sight = (player, x, y) => player == 1 && (Math.Abs(x - 40) <= 10 && Math.Abs(y - 40) <= 10 || Math.Abs(x) <= 10 && Math.Abs(y) <= 10);
        Ticks(memory, eyes, LayerMemory.LearnTicks);
        Assert.Equal([new LandTile(40, 40, _land.Stored(40, 40))], memory.TakeLearnedLand()[1]);
        Assert.Equal(2, memory.KnownLand(1).Count);
    }

    [Fact]
    public void LateJoinerLearnsWhatHeSeesOfOldEdits()
    {
        var memory = new PlayerMemory(_land, PavingLayers());
        _land.SetTile(3, 3, Rock);
        _land.SetTile(-30, 12, Rock);
        var eyes = new Eyes(1);
        Ticks(memory, eyes, 4);
        Assert.Empty(memory.KnownLand(1));
        // Игрок 2 пришёл потом и видит только вторую правку.
        eyes = new Eyes(1, 2) { Sight = Around(2, -30, 12, 3) };
        Ticks(memory, eyes, 4);
        Assert.Equal([new LandTile(-30, 12, _land.Stored(-30, 12))], memory.KnownLand(2));
        Assert.Empty(memory.KnownLand(1));
    }

    [Fact]
    public void RevertingAnEditToTheOriginalForgetsIt()
    {
        var memory = new PlayerMemory(_land, PavingLayers());
        var eyes = new Eyes(1) { Sight = Around(1, 0, 0, 20) };
        var original = _land.Stored(1, 1);
        _land.SetTile(1, 1, Rock);
        Ticks(memory, eyes, 4);
        Assert.Single(memory.KnownLand(1));
        // Тайл вернули как был: игрок, увидев, знает исходный, а не правку.
        _land.SetStored(1, 1, original);
        Ticks(memory, eyes, 4);
        Assert.Empty(memory.KnownLand(1));
        Assert.Equal(new LandTile(1, 1, original), Assert.Single(memory.TakeLearnedLand()[1]));
    }

    [Fact]
    public void KnowledgeSurvivesSave()
    {
        var layers = PavingLayers();
        var memory = new PlayerMemory(_land, layers);
        var eyes = new Eyes(1, 2) { Sight = Around(1, 0, 0, 10) };
        _land.SetTile(2, 2, Rock);
        _land.SetTile(50, 50, Swamp);
        layers.Set(0, LayerOwner.World, 4, 4, 0, 4);
        Ticks(memory, eyes, 4);
        var bytes = memory.Encode();

        // Загрузка: карта — из сохранения, память — из раздела; знание то же, исходная карта — та же.
        var loadedLand = Land.Unpack(_config, _land.Pack());
        var loaded = new PlayerMemory(loadedLand, PavingLayers());
        loaded.Decode(bytes);
        Assert.Equal(memory.KnownLand(1), loaded.KnownLand(1));
        Assert.Equal(memory.Land!.OriginalTiles(), loaded.Land!.OriginalTiles());
        Assert.Equal(memory.Land.Original, loaded.Land.Original);
        Assert.Equal(memory.PristineLand(), loaded.PristineLand());
        Assert.Equal(memory.Encode(), loaded.Encode());
        Assert.Equal(memory.KnownPaving(1), loaded.KnownPaving(1));
        Assert.Throws<FormatException>(() => loaded.Decode(bytes[..^1]));
        Assert.Throws<FormatException>(() => loaded.Decode([2]));
        // Память партии без слоя покрытия — другой набор.
        Assert.Throws<FormatException>(() => new PlayerMemory(loadedLand, new LayerSet([])).Decode(bytes));
    }

    [Fact]
    public void MemoryOfPartyWithoutLandIsPavingOnly()
    {
        var memory = new PlayerMemory(null, PavingLayers());
        Assert.Null(memory.Land);
        Assert.NotNull(memory.Paving);
        Assert.Null(memory.PristineLand());
        Assert.Empty(memory.KnownLand(1));
        Assert.Null(new PlayerMemory(_land, new LayerSet([])).Paving);
    }

    // Покрытие: код = хозяин·2 + вид + 2.

    private static ushort Code(uint owner, int kind = 0) => (ushort)(owner * 2 + kind + 2);

    private void Team(uint player, double head)
    {
        var entity = _world.Spawn();
        _world.Add(entity, Builtin.Team);
        _world.SetNumber(entity, Builtin.Team, 0, player);
        _world.SetNumber(entity, Builtin.Team, 1, head);
    }

    [Fact]
    public void OwnAndAlliedPavingIsKnownAtOnceForeignWhenSeen()
    {
        var layers = PavingLayers();
        var memory = new PlayerMemory(null, layers);
        // Игроки 1 и 2 в союзе, 3 — сам по себе.
        Team(1, 1);
        Team(2, 1);
        Team(3, -1);
        var eyes = new Eyes(1, 2, 3);
        Ticks(memory, eyes, 1);
        layers.Set(0, LayerOwner.World, 5, 5, 0, Code(1));
        layers.Set(0, LayerOwner.World, 40, -3, 0, Code(1, 1));
        // Тик, где обзор не сверяется: хозяин и союзник узнают сразу, чужой не видит и не узнаёт.
        Ticks(memory, eyes, 1);
        var learned = memory.Paving!.TakeLearned();
        Assert.Equal([new Tile(40, -3), new Tile(5, 5)], learned[1]);
        Assert.Equal(2, learned[2].Count);
        Assert.False(learned.ContainsKey(3));

        // Чужой на тайле (5, 5): увидел — узнал, на сверке (тик кратен 4).
        eyes.Sight = Around(3, 5, 5, 1);
        Ticks(memory, eyes, 4);
        Assert.Equal([new Tile(5, 5)], memory.Paving.TakeLearned()[3]);
        Assert.Equal([Code(1)], memory.Paving.Known[3].Values);
    }

    [Fact]
    public void LiftedPavingIsForgottenByOwnerAtOnceAndByOthersWhenSeen()
    {
        var layers = PavingLayers();
        var memory = new PlayerMemory(null, layers);
        Team(1, -1);
        Team(3, -1);
        var eyes = new Eyes(1, 3) { Sight = Around(3, 7, 7, 1) };
        layers.Set(0, LayerOwner.World, 7, 7, 0, Code(1));
        Ticks(memory, eyes, 4);
        memory.Paving!.TakeLearned();
        Assert.Single(memory.Paving.Known[3]);
        eyes.Sight = (_, _, _) => false;
        layers.Set(0, LayerOwner.World, 7, 7, 0, 0);
        Ticks(memory, eyes, 4);
        // Хозяин забыл покрытие сразу, чужой помнит его, пока не увидит.
        Assert.Empty(memory.Paving.Known[1]);
        Assert.Single(memory.Paving.Known[3]);
        Assert.Equal([new Tile(7, 7)], memory.Paving.TakeLearned()[1]);
        eyes.Sight = Around(3, 7, 7, 1);
        Ticks(memory, eyes, 4);
        Assert.Empty(memory.Paving.Known[3]);
    }

    [Fact]
    public void PavingChunksCarryOnlyWhatPlayerKnows()
    {
        var layers = PavingLayers();
        var memory = new PlayerMemory(null, layers);
        Team(1, -1);
        Team(2, -1);
        var eyes = new Eyes(1, 2);
        layers.Set(0, LayerOwner.World, 33, -1, 0, Code(1));
        layers.Set(0, LayerOwner.World, 34, -1, 0, Code(2));
        Ticks(memory, eyes, 2);
        var chunks = memory.KnownPaving(1);
        var change = Assert.Single(chunks);
        Assert.Equal(("paving", LayerOwner.World, 1, -1), (change.Layer, change.Owner, change.X, change.Y));
        Assert.Equal(LayerSet.ChunkTiles * 2, change.Bytes.Length);
        var at = (31 * LayerSet.Chunk + 1) * 2;
        Assert.Equal(Code(1), BinaryPrimitives.ReadUInt16LittleEndian(change.Bytes.AsSpan(at)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(change.Bytes.AsSpan(at + 2)));
        // Игрок ничего не знает — пустой блок не создаётся для чужих тайлов.
        Assert.Empty(memory.KnownPaving(9));
        var gone = memory.Paving!.Chunks(9, [new Tile(33, -1)]);
        Assert.Empty(Assert.Single(gone).Bytes);
    }
}
