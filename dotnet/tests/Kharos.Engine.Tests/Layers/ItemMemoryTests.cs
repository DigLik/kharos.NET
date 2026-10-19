using Kharos.Engine.Layers;

namespace Kharos.Engine.Tests.Layers;

/// <summary>Память предметных слоёв (Д6, ItemMemory): запоминаемые предметы и предметы только в обзоре, на движке без хоста.</summary>
public sealed class ItemMemoryTests
{
    /// <summary>Обзор теста: игрок видит прямоугольники; ячейки обзора — по ним.</summary>
    private sealed class Sight : IItemSight
    {
        public Dictionary<uint, List<(int X, int Y, int W, int H)>> Areas { get; } = [];

        public IEnumerable<uint> Players => Areas.Keys.Order();

        public bool Sees(uint player, double x, double y) =>
            Areas.TryGetValue(player, out var areas) && areas.Any(area => x >= area.X && x < area.X + area.W && y >= area.Y && y < area.Y + area.H);

        public void SeenCells(uint player, int cell, HashSet<(int X, int Y)> into)
        {
            foreach (var (x, y, w, h) in Areas.GetValueOrDefault(player) ?? [])
                for (var cy = (int)Math.Floor((double)y / cell); cy <= (int)Math.Floor((double)(y + h - 1) / cell); cy++)
                    for (var cx = (int)Math.Floor((double)x / cell); cx <= (int)Math.Floor((double)(x + w - 1) / cell); cx++)
                        into.Add((cx, cy));
        }

        public void Look(uint player, params (int X, int Y, int W, int H)[] areas) => Areas[player] = [.. areas];
    }

    private readonly LayerSet _layers = new(LayerSpec.ParseAll("""
        {
          "deposits": { "scope": "world", "shape": "items", "cell": 40, "fields": { "kind": "u8", "reserve": "f64" }, "save": true, "remember": true },
          "traces": { "scope": "world", "shape": "items", "cell": 16, "sight": true, "fields": { "kind": "u8" } },
          "marks": { "scope": "world", "shape": "items", "cell": 8, "fields": { "kind": "u8" } }
        }
        """));

    private readonly Sight _sight = new();
    private ulong _tick;

    private ItemMemory Memory() => ItemMemory.Create(_layers)!;

    /// <summary>Тики до следующей сверки обзора включительно.</summary>
    private void Look(ItemMemory memory)
    {
        do
            memory.Update(_sight, ++_tick);
        while (_tick % ItemMemory.LookTicks != 0);
    }

    private void Put(string layer, double x, double y, params double[] fields) => _layers.Push(_layers.Id(layer), LayerOwner.World, new LayerItem(x, y, fields));

    private void Remove(string layer, double x, double y)
    {
        var id = _layers.Id(layer);
        _layers.Retain(id, LayerOwner.World, _layers.CellOf(id, x, y), item => item.X != x || item.Y != y);
    }

    [Fact]
    public void OnlyFoggedItemLayersAreCovered()
    {
        var memory = Memory();
        Assert.True(memory.Covers("deposits"));
        Assert.True(memory.Covers("traces"));
        Assert.False(memory.Covers("marks"));
        Assert.Null(ItemMemory.Create(new LayerSet(LayerSpec.ParseAll("""{ "marks": { "scope": "world", "shape": "items", "cell": 8 } }"""))));
    }

    [Fact]
    public void SightAndRememberAreCheckedInDeclarations()
    {
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "grid", "channels": ["u8"], "sight": true } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "player", "shape": "items", "cell": 4, "sight": true } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "items", "cell": 4, "sight": true, "remember": true } }"""));
        Assert.True(LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "items", "cell": 4, "sight": true } }""")[0].Fogged);
    }

    [Fact]
    public void RememberedItemsStayAsSeenUntilTheirPlaceIsSeenAgain()
    {
        var memory = Memory();
        Put("deposits", 10, 10, 1, 300);
        Put("deposits", 100, 10, 2, 50);
        Look(memory);
        // Без обзора игрок не знает ничего.
        Assert.Empty(memory.Items("deposits", 1));

        _sight.Look(1, (0, 0, 20, 20));
        Look(memory);
        Assert.Equal([new LayerItem(10, 10, [1, 300])], memory.Items("deposits", 1));
        var learned = Assert.Single(memory.TakeLearned());
        Assert.Equal(1u, learned.Key);
        Assert.Equal(new LayerChange("deposits", LayerOwner.World, 0, 0, _layers.ChunkBytes(_layers.Id("deposits"), LayerOwner.World, 0, 0).ToArray()), Assert.Single(learned.Value));

        // Ушёл: в тумане месторождение убавилось, потом ушло — игрок помнит прежнее; новое рядом не знает.
        _sight.Look(1);
        Look(memory);
        Remove("deposits", 10, 10);
        Put("deposits", 10, 10, 1, 120);
        Look(memory);
        Assert.Equal([new LayerItem(10, 10, [1, 300])], memory.Items("deposits", 1));
        Remove("deposits", 10, 10);
        Put("deposits", 14, 10, 3, 500);
        Look(memory);
        Assert.Equal([new LayerItem(10, 10, [1, 300])], memory.Items("deposits", 1));
        Assert.Empty(memory.TakeLearned());

        // Вернулся: старого нет, новое найдено; ячейка уходит заново.
        _sight.Look(1, (0, 0, 20, 20));
        Look(memory);
        Assert.Equal([new LayerItem(14, 10, [3, 500])], memory.Items("deposits", 1));
        Assert.Single(Assert.Single(memory.TakeLearned()).Value);
        Assert.Equal(memory.Items("deposits", 1).Count, memory.Blocks(1).Count);
    }

    [Fact]
    public void ChangeInSightIsLearnedTheSameTick()
    {
        var memory = Memory();
        _sight.Look(1, (0, 0, 20, 20));
        Look(memory);
        Put("traces", 3.5, 3.5, 0);
        memory.Update(_sight, ++_tick);
        Assert.Equal([new LayerItem(3.5, 3.5, [0])], memory.Items("traces", 1));
    }

    [Fact]
    public void SightItemsAreForgottenOutOfSightAndSentAgainOnReturn()
    {
        var memory = Memory();
        Put("traces", 40.5, 40.5, 0);
        Put("traces", 41.5, 40.5, 1);
        Look(memory);
        Assert.Empty(memory.Items("traces", 1));

        _sight.Look(1, (36, 36, 10, 10));
        _sight.Look(2, (0, 0, 4, 4));
        Look(memory);
        Assert.Equal(2, memory.Items("traces", 1).Count);
        Assert.Empty(memory.Items("traces", 2));
        memory.TakeLearned();

        _sight.Look(1, (0, 0, 4, 4));
        Look(memory);
        Assert.Empty(memory.Items("traces", 1));
        // Клиенту — ячейка пустой: следы он выбрасывает.
        var change = Assert.Single(Assert.Single(memory.TakeLearned()).Value);
        Assert.Empty(change.Bytes);

        _sight.Look(1, (36, 36, 10, 10));
        Look(memory);
        Assert.Equal(2, memory.Items("traces", 1).Count);
    }

    [Fact]
    public void ForgottenPlayerKnowsNothingAndClientDropsTheCells()
    {
        var memory = Memory();
        Put("deposits", 10, 10, 1, 300);
        _sight.Look(1, (0, 0, 20, 20));
        Look(memory);
        memory.TakeLearned();
        memory.ForgetPlayer(1);
        Assert.Empty(memory.Items("deposits", 1));
        var change = Assert.Single(Assert.Single(memory.TakeLearned()).Value);
        Assert.Empty(change.Bytes);
    }

    [Fact]
    public void RememberedLayersSurviveEncodeAndOldSavesKnowWhatWasExplored()
    {
        var memory = Memory();
        Put("deposits", 10, 10, 1, 300);
        Put("deposits", -50, 90, 2, 70);
        Put("traces", 3.5, 3.5, 0);
        _sight.Look(1, (0, 0, 20, 20));
        _sight.Look(3, (-60, 80, 20, 20));
        Look(memory);
        Assert.True(memory.Any);
        var bytes = memory.Encode();

        var other = new LayerSet(_layers.Specs);
        foreach (var change in _layers.Save(LayerOwner.World))
            other.Apply(change);
        var loaded = ItemMemory.Create(other)!;
        loaded.Decode(bytes);
        foreach (var player in new uint[] { 1, 2, 3 })
            Assert.Equal(memory.Items("deposits", player), loaded.Items("deposits", player));
        // Следы не сохраняются: их пересчитает обзор.
        Assert.Empty(loaded.Items("traces", 1));
        Assert.Equal(bytes, loaded.Encode());
        Assert.Throws<FormatException>(() => loaded.Decode(bytes[..^1]));

        // Сохранение без памяти: знает то, что разведал.
        var old = ItemMemory.Create(other)!;
        old.KnowExplored([1, 2], (player, x, y) => player == 1 && x >= 0);
        Assert.Equal([new LayerItem(10, 10, [1, 300])], old.Items("deposits", 1));
        Assert.Empty(old.Items("deposits", 2));
        Assert.Empty(old.Items("traces", 1));
    }
}
