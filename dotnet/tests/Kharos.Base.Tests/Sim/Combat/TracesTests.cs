using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Layers;
using Kharos.Engine.Traces;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Combat;

/// <summary>
/// Следы (packages/tests/traces.test.ts): колею кладёт система движка engine.traces по ходу наземных юнитов, остов и гарь —
/// бой командой Item в слой traces. Мир 256×256 (TS size 256) — Size 128. «Следы приходят только в обзор» — сеть:
/// Kharos.Host.Tests/LayerOpsTests.
/// </summary>
public sealed class TracesTests
{
    /// <summary>Все следы мира: ячейки по порядку, в ячейке — по записи (traces.all()).</summary>
    private static List<LayerItem> All(Sim sim)
    {
        var layers = sim.Host.Layers;
        var id = layers.Id(TraceLayer.LayerName);
        return [.. layers.Cells(id, LayerOwner.World).SelectMany(cell => layers.Items(id, LayerOwner.World, cell))];
    }

    private static List<LayerItem> Tracks(Sim sim) => [.. All(sim).Where(item => (int)item.Fields[TraceLayer.Kind] == TraceLayer.Track)];

    /// <summary>След боя, как его кладёт мод командой Item (traces.add).</summary>
    private static void Add(Sim sim, int kind, double x, double y, double size = 0) =>
        sim.Host.Layers.Push(sim.Host.Layers.Id(TraceLayer.LayerName), LayerOwner.World, new LayerItem(x, y, [0, 0, kind, 0, 0, 0, 0, size]));

    [Fact(DisplayName = "идущий юнит оставляет точки пути, летающий — нет; следы истекают")]
    public void WalkingUnitLeavesTracksFlyingDoesNot()
    {
        using var sim = Scene.Create(128);
        var tank = Spawn(sim, "tank", 1, 0, 0);
        Spawn(sim, "drone", 1, 0, 4);
        sim.Advance();
        sim.Send(1, new MoveOrder { Units = [tank], X = 10, Y = 0 });
        sim.Seconds(5);
        var tracks = Tracks(sim);
        Assert.True(tracks.Count > 5, $"точек пути {tracks.Count}");
        Assert.All(tracks, track => Assert.Equal((tank, 1.0), ((uint)track.Fields[TraceLayer.Entity], track.Fields[TraceLayer.Player])));
        var last = tracks.Max(track => track.Fields[TraceLayer.Tick]);
        sim.Seconds(TraceLayer.Life[TraceLayer.Track] + 2);
        Assert.DoesNotContain(Tracks(sim), track => track.Fields[TraceLayer.Tick] < last);
    }

    [Fact(DisplayName = "тайл держит не больше двух следов одного вида, а истёкшие освобождают место")]
    public void TileHoldsTwoTracesOfAKindAndExpiryFreesRoom()
    {
        using var sim = Scene.Create(128);
        for (var i = 0; i < 10; i++)
            Add(sim, TraceLayer.Scar, 5.2, 5.7, 1);
        Add(sim, TraceLayer.Burn, 5.5, 5.5);
        Add(sim, TraceLayer.Scar, 6.5, 5.5, 1);
        List<int> Kinds(int x) => [.. All(sim).Where(item => Math.Floor(item.X) == x).Select(item => (int)item.Fields[TraceLayer.Kind])];
        Assert.Equal([TraceLayer.Scar, TraceLayer.Scar, TraceLayer.Burn], Kinds(5));
        Assert.Equal([TraceLayer.Scar], Kinds(6));

        sim.Seconds(TraceLayer.Life[TraceLayer.Scar] + 1);
        Add(sim, TraceLayer.Scar, 5.5, 5.5, 1);
        Assert.Equal([TraceLayer.Scar], Kinds(5));
    }

    [Fact(DisplayName = "толпа на одном месте не множит колею без предела")]
    public void CrowdDoesNotMultiplyTracksWithoutLimit()
    {
        using var sim = Scene.Create(128);
        var units = Enumerable.Range(0, 40).Select(i => sim.PutUnit("infantry", 1, 10 + i % 8 * 0.6 + 0.5, 10 + i / 8 * 0.6 + 0.5)).ToArray();
        sim.Send(1, new MoveOrder { Units = units, X = 40, Y = 10 });
        sim.Seconds(15);
        var perTile = new Dictionary<(int, int), int>();
        foreach (var track in Tracks(sim))
        {
            var key = ((int)Math.Floor(track.X), (int)Math.Floor(track.Y));
            perTile[key] = perTile.GetValueOrDefault(key) + 1;
        }
        Assert.True(perTile.Count > 10, $"тайлов с колеёй {perTile.Count}");
        Assert.Equal(2, perTile.Values.Max());
    }

    [Fact(DisplayName = "погибшая техника оставляет остов и гарь")]
    public void DestroyedVehicleLeavesWreckAndScar()
    {
        using var sim = Create();
        var tank = Spawn(sim, "tank", 2, 0, 0);
        sim.Advance();
        for (var i = 0; i < 3; i++)
            Spawn(sim, "lancer", 1, 4, 0);
        sim.Seconds(30);
        Assert.False(sim.Alive(tank));
        Assert.NotEmpty(CombatTests.Traces(sim, 3));
        Assert.True(CombatTests.Traces(sim, 1).Count + CombatTests.Traces(sim, 2).Count > 0);
    }
}
