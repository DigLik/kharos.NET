using Kharos.Engine.Layers;
using Kharos.Engine.Tests.World;
using Kharos.Engine.Traces;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Layers;

/// <summary>Следы на земле (engine.traces, Д3): порт traces.test.ts — колея по ходу, предел густоты, сроки; правило слоя для следов боя.</summary>
public sealed class TraceTests
{
    private const double Dt = 0.05;
    private readonly GameWorld _world = Fixtures.World();
    private readonly LayerSet _layers;
    private readonly TraceLayer _traces;
    private readonly int _layer;
    private ulong _tick;

    public TraceTests()
    {
        _layers = new LayerSet(
        [
            new LayerSpec
            {
                Name = "traces", Scope = LayerScope.World, Cell = 16,
                Fields = [("id", Channel.U32), ("tick", Channel.U32), ("kind", Channel.U8), ("entity", Channel.U32), ("type", Channel.U16), ("facing", Channel.F64), ("player", Channel.U16), ("size", Channel.F64)],
            },
        ]);
        _layer = 0;
        _traces = TraceLayer.Create(_layers, () => _tick, chassis => chassis == "air")!;
    }

    private uint Unit(string chassis, double x, double y, uint player = 1, double facing = 0)
    {
        var entity = _world.At(x, y);
        _world.Add(entity, Builtin.Facing);
        _world.SetNumber(entity, Builtin.Facing, 0, facing);
        _world.Add(entity, Builtin.Owner);
        _world.SetNumber(entity, Builtin.Owner, 0, player);
        _world.Add(entity, Builtin.Proto);
        _world.SetNumber(entity, Builtin.Proto, 0, 4);
        _world.Add(entity, Builtin.Mover);
        _world.Set(entity, Builtin.Mover, "chassis", FieldValue.Of(chassis));
        return entity;
    }

    private void Run(double seconds, Action? each = null)
    {
        for (var i = 0; i < seconds / Dt; i++)
        {
            _tick++;
            each?.Invoke();
            _traces.Update(_world, _tick, Dt);
        }
    }

    private void Move(uint entity, double dx, double dy)
    {
        _world.SetNumber(entity, Builtin.Position, 0, _world.Number(entity, Builtin.Position, 0) + dx);
        _world.SetNumber(entity, Builtin.Position, 1, _world.Number(entity, Builtin.Position, 1) + dy);
    }

    /// <summary>Все следы слоя: ячейки по порядку, в ячейке — по записи.</summary>
    private List<LayerItem> All() => [.. _layers.Cells(_layer, LayerOwner.World).SelectMany(cell => _layers.Items(_layer, LayerOwner.World, cell))];

    private bool Put(int kind, double x, double y, double size = 1) =>
        _layers.Push(_layer, LayerOwner.World, new LayerItem(x, y, [0, 0, kind, 0, 0, 0, 0, size]));

    [Fact(DisplayName = "идущий юнит оставляет точки пути, летающий — нет; следы истекают")]
    public void WalkingUnitLeavesTracksFlyingDoesNot()
    {
        var tank = Unit("tracks", 0, 0);
        Unit("air", 0, 4);
        Run(Dt);
        Run(5, () => Move(tank, 2 * Dt, 0));
        var tracks = All().Where(item => (int)item.Fields[TraceLayer.Kind] == TraceLayer.Track).ToList();
        Assert.True(tracks.Count > 5);
        Assert.All(tracks, track => Assert.Equal((double)tank, track.Fields[TraceLayer.Entity]));
        Assert.All(tracks, track => Assert.Equal((1.0, 4.0), (track.Fields[TraceLayer.Player], track.Fields[TraceLayer.Type])));
        // Первая точка — там, где юнит стоял; id растут с единицы.
        Assert.Equal(1, tracks.Min(track => track.Fields[TraceLayer.Id]));
        // Юнит встал — новых точек нет; через срок жизни колеи не остаётся.
        Run(TraceLayer.Life[TraceLayer.Track] + 2);
        Assert.Empty(All());
    }

    [Fact(DisplayName = "тайл держит не больше двух следов одного вида, а истёкшие освобождают место")]
    public void TileHoldsTwoTracesOfAKindAndExpiryFreesRoom()
    {
        for (var i = 0; i < 10; i++)
            Put(TraceLayer.Scar, 5.2, 5.7);
        Put(TraceLayer.Burn, 5.5, 5.5);
        Put(TraceLayer.Scar, 6.5, 5.5);
        List<int> Kinds(int x) => [.. All().Where(item => Math.Floor(item.X) == x).Select(item => (int)item.Fields[TraceLayer.Kind])];
        Assert.Equal([TraceLayer.Scar, TraceLayer.Scar, TraceLayer.Burn], Kinds(5));
        Assert.Equal([TraceLayer.Scar], Kinds(6));

        Run(TraceLayer.Life[TraceLayer.Scar] + 1);
        Assert.True(Put(TraceLayer.Scar, 5.5, 5.5));
        Assert.Equal([TraceLayer.Scar], Kinds(5));
    }

    [Fact]
    public void ExpiryReleasesOnlyExpiredAndKeepsCounts()
    {
        Put(TraceLayer.Scar, 2.5, 2.5);
        Run(TraceLayer.Life[TraceLayer.Scar] / 2);
        Put(TraceLayer.Scar, 2.6, 2.6);
        // Первый истечёт, второй ещё нет: на тайле остаётся одно место.
        Run(TraceLayer.Life[TraceLayer.Scar] / 2 + 3);
        Assert.Single(All());
        Assert.True(Put(TraceLayer.Scar, 2.1, 2.1));
        Assert.False(Put(TraceLayer.Scar, 2.9, 2.9));
        Assert.Equal(2, All().Count);
    }

    [Fact(DisplayName = "толпа на одном месте не множит колею без предела")]
    public void CrowdDoesNotMultiplyTracksWithoutLimit()
    {
        var units = Enumerable.Range(0, 40).Select(i => Unit("legs", 10 + i % 8 * 0.6, 10 + i / 8 * 0.6)).ToList();
        Run(15, () => units.ForEach(unit => Move(unit, 0.15, 0)));
        var perTile = new Dictionary<(int, int), int>();
        foreach (var item in All().Where(item => (int)item.Fields[TraceLayer.Kind] == TraceLayer.Track))
        {
            var key = ((int)Math.Floor(item.X), (int)Math.Floor(item.Y));
            perTile[key] = perTile.GetValueOrDefault(key) + 1;
        }
        Assert.True(perTile.Count > 10);
        Assert.Equal(2, perTile.Values.Max());
    }

    [Fact]
    public void CombatTracesPushedByModsGetIdTickAndDensityFromTheRule()
    {
        _tick = 77;
        // Мод кладёт предмет командой Item: номер и тик назначает правило слоя, что мод дал в этих полях — не в счёт.
        _layers.Push(_layer, LayerOwner.World, new LayerItem(1.5, 1.5, [900, 5, TraceLayer.Wreck, 12, 3, 1.25, 2, 0]));
        _layers.Push(_layer, LayerOwner.World, new LayerItem(1.5, 1.5, [0, 0, TraceLayer.Wreck, 13, 3, 0, 2, 0]));
        var wrecks = All();
        Assert.Equal([1.0, 2.0], wrecks.Select(item => item.Fields[TraceLayer.Id]));
        Assert.All(wrecks, item => Assert.Equal(77.0, item.Fields[TraceLayer.Tick]));
        Assert.Equal((12.0, 3.0, 1.25, 2.0), (wrecks[0].Fields[TraceLayer.Entity], wrecks[0].Fields[TraceLayer.Type], wrecks[0].Fields[TraceLayer.Facing], wrecks[0].Fields[TraceLayer.Player]));
        // Остовы без предела, неизвестный вид не пускается, номер растёт только у принятых.
        for (var i = 0; i < 5; i++)
            Assert.True(Put(TraceLayer.Wreck, 1.5, 1.5));
        Assert.False(Put(9, 1.5, 1.5));
        Assert.False(Put(-1, 1.5, 1.5));
        Assert.Equal(8u, _traces.NextId);
        Assert.Equal(7, All().Count);
    }

    [Fact]
    public void DespawnedWalkerLeavesFirstPointAgainWhenBack()
    {
        var unit = Unit("wheels", 0, 0);
        Run(Dt * 2);
        Assert.Single(All());
        _world.Destroy(unit);
        Run(Dt);
        var again = Unit("wheels", 40, 40);
        Run(Dt);
        Assert.Equal(2, All().Count);
        Assert.Contains(All(), item => item.X == 40);
        Assert.NotEqual(unit, again);
    }

    [Fact]
    public void LayerWithOtherFieldsGetsNoTraces()
    {
        var other = new LayerSet([new LayerSpec { Name = "traces", Scope = LayerScope.World, Cell = 16, Fields = [("kind", Channel.U8)] }]);
        Assert.Null(TraceLayer.Create(other, () => 0, _ => false));
        Assert.Null(TraceLayer.Create(new LayerSet([]), () => 0, _ => false));
    }
}
