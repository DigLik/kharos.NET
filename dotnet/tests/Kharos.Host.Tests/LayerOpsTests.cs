using Hello;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.Traces;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>
/// Команды Layer, Item и Land и инструменты Layer, Items и Peak (Д3) через мод: hello получает приказы, кладёт команды и
/// читает их инструментами. Те же сценарии — в режиме CLR и на host.wasm.
/// </summary>
public sealed class LayerOpsTests
{
    private const byte Rock = 2, Swamp = 0;

    private static Host Started(ModMode mode, string? settings = null)
    {
        var host = LayerLab.Create(mode, settings);
        host.Start();
        host.Join(1);
        host.Join(2);
        host.Step();
        return host;
    }

    private static ReadReport Read(Host host, uint player, ReadOrder order)
    {
        host.Send(player, "hello.read", LayerLab.Read(order));
        host.Step();
        return LayerLab.ReadAnswer(host.TakeDelta(), player);
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void LayerOpWritesRectangleAndToolReadsItBack(ModMode mode)
    {
        using var host = Started(mode);
        host.Send(1, "hello.layer", LayerLab.Layer("scorch", 1, -3, 4, 3, 2, 2.5));
        host.Send(1, "hello.layer", LayerLab.Layer("scorch", 0, -1, 4, 1, 1, 300));
        host.Send(1, "hello.layer", LayerLab.Layer("notes", 0, 10, 10, 1, 1, 7, owner: 2));
        host.Step();
        var scorch = host.Layers.Id("scorch");
        for (var y = 4; y < 6; y++)
            for (var x = -3; x < 0; x++)
                Assert.Equal(2.5, host.Layers.Get(scorch, LayerOwner.World, x, y, 1));
        // u8 обрезается до 255, как value as u8 в Rust.
        Assert.Equal(255, host.Layers.Get(scorch, LayerOwner.World, -1, 4, 0));
        Assert.Equal(7, host.Layers.Get(host.Layers.Id("notes"), LayerOwner.Player(2), 10, 10, 0));

        var report = Read(host, 1, new ReadOrder { Layer = "scorch", Channel = 1, X = -4, Y = 4, W = 5, H = 2 });
        Assert.Equal([0, 2.5, 2.5, 2.5, 0, 0, 2.5, 2.5, 2.5, 0], report.Values);
        Assert.Equal([0.0, 7], Read(host, 2, new ReadOrder { Layer = "notes", Owner = 2, X = 9, Y = 10, W = 2, H = 1 }).Values);
        Assert.Empty(host.Errors);
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void ItemOpPushesAndDeletesAndToolFindsInCircle(ModMode mode)
    {
        using var host = Started(mode);
        host.Send(1, "hello.item", LayerLab.Item("marks", 3, 3, [1, 10]));
        host.Send(1, "hello.item", LayerLab.Item("marks", 2, 4, [2, 20]));
        host.Send(1, "hello.item", LayerLab.Item("marks", 9, 1, [3, 30]));
        host.Send(1, "hello.item", LayerLab.Item("marks", 300, 300, [4]));
        host.Step();
        var report = Read(host, 1, new ReadOrder { ItemLayer = "marks", ItemX = 4, ItemY = 4, ItemRadius = 6 });
        Assert.Equal(3, report.ItemCount);
        Assert.Equal([3.0, 3, 1, 10, 2, 4, 2, 20, 9, 1, 3, 30], report.Items);

        host.Send(1, "hello.item", LayerLab.Item("marks", 2, 4, [], delete: true));
        host.Step();
        report = Read(host, 1, new ReadOrder { ItemLayer = "marks", ItemX = 4, ItemY = 4, ItemRadius = 6 });
        Assert.Equal([3.0, 3, 1, 10, 9, 1, 3, 30], report.Items);
        // Дальний предмет — поля недостающие нули.
        Assert.Equal([300.0, 300, 4, 0], Read(host, 1, new ReadOrder { ItemLayer = "marks", ItemX = 300, ItemY = 300, ItemRadius = 0 }).Items);
        Assert.Empty(host.Errors);
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void LandOpEditsTilesAndPeaksAndPeakToolSeesThem(ModMode mode)
    {
        using var host = Started(mode, """{ "fog": false }""");
        var land = host.Land!;
        var before = land.Revision;
        host.Send(1, "hello.land", LayerLab.Tile(-5, 7, Rock, tier: 2, cliff: true));
        host.Send(1, "hello.land", LayerLab.Tile(6, 7, Swamp, tier: 0));
        host.Send(1, "hello.land", LayerLab.Peak(30, 30, 1, 30.5, 31, 2));
        host.Step();
        Assert.Equal(TerrainType.Rock, land.TerrainAt(-5, 7));
        Assert.Equal(TerrainType.Swamp, land.TerrainAt(6, 7));
        Assert.Equal(TerrainType.Mountain, land.TerrainAt(30, 30));
        Assert.True(land.Revision >= before + 3);
        var peak = land.PeakOf(30, 30)!.Value;
        var report = Read(host, 1, new ReadOrder { Peak = true, PeakX = 30, PeakY = 30 });
        Assert.Equal((true, peak.X, peak.Y, peak.Radius), (report.HasPeak, report.PeakX, report.PeakY, report.PeakRadius));
        Assert.False(Read(host, 1, new ReadOrder { Peak = true, PeakX = -5, PeakY = 7 }).HasPeak);
        Assert.Empty(host.Errors);
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void BadOpsRejectTheWholeBufferAndStrikeTheMod(ModMode mode)
    {
        using var host = Started(mode);
        // Канала 5 у scorch нет: буфер приказа отвергнут целиком, первая запись тоже не применена.
        host.Send(1, "hello.layer", LayerLab.Layer("scorch", 5, 0, 0, 1, 1, 1));
        host.Step();
        Assert.Contains(host.Errors, error => error.Message.Contains("канала"));
        Assert.Equal(0, host.Layers.Get(host.Layers.Id("scorch"), LayerOwner.World, 0, 0, 0));
        host.Errors.Clear();
        // Слой игрока у мира и предметы вместо сетки.
        host.Send(1, "hello.layer", LayerLab.Layer("notes", 0, 0, 0, 1, 1, 1, owner: 0));
        host.Send(1, "hello.layer", LayerLab.Layer("marks", 0, 0, 0, 1, 1, 1));
        host.Step();
        Assert.Equal(2, host.Errors.Count(error => error.Message.Contains("слой")));
        // Земля вне слоя: тайл не той формы.
        host.Errors.Clear();
        host.Send(1, "hello.land", LayerLab.Tile(0, 0, 3));
        host.Step();
        Assert.Contains(host.Errors, error => error.Message.Contains("Land"));
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void TraceItemsFromModsGoThroughTheTraceRule(ModMode mode)
    {
        using var host = Started(mode);
        Assert.NotNull(host.Traces);
        var layer = host.Layers.Id(TraceLayer.LayerName);
        // Следы боя кладёт мод командой Item: гарь на одном тайле — не больше двух, номер и тик назначает движок.
        for (var i = 0; i < 5; i++)
            host.Send(1, "hello.item", LayerLab.Item("traces", 5.2, 5.7, [900, 1, TraceLayer.Scar, 0, 0, 0, 1, 2]));
        host.Send(1, "hello.item", LayerLab.Item("traces", 5.5, 5.5, [0, 0, TraceLayer.Wreck, 12, 3, 1.5, 2, 0]));
        host.Step();
        var items = host.Layers.Items(layer, LayerOwner.World, host.Layers.CellOf(layer, 5.2, 5.7));
        Assert.Equal([TraceLayer.Scar, TraceLayer.Scar, TraceLayer.Wreck], items.Select(item => (int)item.Fields[TraceLayer.Kind]));
        Assert.Equal([1.0, 2.0, 3.0], items.Select(item => item.Fields[TraceLayer.Id]));
        Assert.All(items, item => Assert.Equal((double)host.Tick, item.Fields[TraceLayer.Tick]));
        Assert.Empty(host.Errors);
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void LayersAndEditedLandSurviveSaveAndLoad(ModMode mode)
    {
        using var host = Started(mode, """{ "fog": false }""");
        host.Send(1, "hello.layer", LayerLab.Layer("scorch", 1, 0, 0, 40, 3, 1.5));
        host.Send(1, "hello.layer", LayerLab.Layer("notes", 0, 1, 1, 1, 1, 9, owner: 1));
        host.Send(1, "hello.item", LayerLab.Item("marks", 12.5, 3.5, [6, 0.25]));
        host.Send(1, "hello.land", LayerLab.Tile(4, 4, Rock, tier: 1));
        host.Step();
        var save = host.Save();

        using var loaded = Host.Load(LayerLab.Content(), save, LayerLab.Options(mode));
        foreach (var name in new[] { "scorch", "marks" })
            Assert.Equal(host.Layers.Blocks(host.Layers.Id(name), LayerOwner.World), loaded.Layers.Blocks(loaded.Layers.Id(name), LayerOwner.World));
        Assert.Equal(host.Layers.Blocks(host.Layers.Id("notes"), LayerOwner.Player(1)), loaded.Layers.Blocks(loaded.Layers.Id("notes"), LayerOwner.Player(1)));
        Assert.Equal(TerrainType.Rock, loaded.Land!.TerrainAt(4, 4));
        Assert.Equal(host.Land!.Stored(4, 4), loaded.Land.Stored(4, 4));
        // Загруженный хост правит землю дальше и видит свои слои инструментами.
        loaded.Send(1, "hello.land", LayerLab.Tile(5, 4, Swamp, tier: 0));
        loaded.Step();
        Assert.Equal(TerrainType.Swamp, loaded.Land.TerrainAt(5, 4));
        var report = Read(loaded, 1, new ReadOrder { Layer = "scorch", Channel = 1, X = 39, Y = 2, W = 2, H = 1 });
        Assert.Equal([1.5, 0], report.Values);
        Assert.Empty(loaded.Errors);
    }

    // Тесты traces.test.ts, которые ждут других разделов.

    // «погибшая техника оставляет остов и гарь» — мод base: Kharos.Base.Tests/Sim/Combat/TracesTests.cs.

    [Fact(DisplayName = "следы приходят только в обзор: пришедший позже видит колею врага, ушедший её забывает, вернувшийся получает снова", Skip = "ждёт видимости слоёв предметов в сети (Д4): слои предметов мира пока уходят всем")]
    public void TracesReachOnlyTheViewOfPlayer()
    {
    }
}
