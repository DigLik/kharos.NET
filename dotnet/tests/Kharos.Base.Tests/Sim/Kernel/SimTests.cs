using Base.Shared;
using static Kharos.Base.Tests.Kernel.Scene;

namespace Kharos.Base.Tests.Kernel;

/// <summary>Партия как целое (packages/tests/sim.test.ts): приказы в начале тика, границы карты, сохранение.</summary>
public sealed class SimTests
{
    /// <summary>Ближайший к началу мира тайл, где встаёт турель 1×1 (freeTile).</summary>
    private static (int X, int Y) FreeTile(Sim sim)
    {
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
                if (Fits(sim, "turret", x, y, 1, 1))
                    return (x, y);
        throw new InvalidOperationException("В мире не нашлось скалы");
    }

    [Fact(DisplayName = "команда выполняется в начале следующего тика, а не сразу", Skip = "ждёт С1: покрытие (pave)")]
    public void CommandRunsAtNextTick()
    {
    }

    [Fact(DisplayName = "негодная команда отбрасывается", Skip = "ждёт С1: покрытие (pave)")]
    public void BadCommandIsDropped()
    {
    }

    [Fact(DisplayName = "за границами карты строить нельзя")]
    public void NoBuildingBeyondMapEdge()
    {
        (int X, int Y) tile;
        using (var wide = Create(rules: new { techTree = false }))
            tile = FreeTile(wide);
        // Карта, которая кончается ровно перед этим тайлом (размер — полусторона).
        var edge = Math.Max(tile.X, tile.Y);
        using var narrow = Create(edge, new { techTree = false });
        Assert.Equal(edge, narrow.Host.Map!.Value.Right);
        Assert.False(CanPlace(narrow, "turret", tile.X, tile.Y));
        using var wider = Create(edge + 1, new { techTree = false });
        Assert.True(CanPlace(wider, "turret", tile.X, tile.Y));
    }

    [Fact(DisplayName = "сохранение восстанавливает мир, тик и номера сущностей")]
    public void SaveRestoresWorldTickAndEntityNumbers()
    {
        using var sim = Create(rules: new { techTree = false });
        var site = FreeTile(sim);
        Place(sim, "turret", site.X, site.Y, 1);
        var tick = sim.Tick;
        sim.Advance(3);
        var next = sim.World.NextEntity;
        using var loaded = sim.ThroughSave();
        Assert.Equal(tick + 3, loaded.Tick);
        Assert.Equal(sim.Time, loaded.Time);
        Assert.True(loaded.Save().AsSpan().SequenceEqual(sim.Save()));

        static List<(uint, double, double, double)> Rows(Sim sim) =>
            [.. sim.With(Names.Position.Name, Names.Building.Name).Select(entity => (entity, sim.At(entity).X, sim.At(entity).Y, sim.Num(entity, "Proto.id")))];
        Assert.Equal(Rows(sim), Rows(loaded));

        // Занятость восстановилась вместе со зданиями, новые сущности не занимают старые номера.
        var first = sim.With(Names.Position.Name, Names.Building.Name)[0];
        Assert.Equal(first, Occupant(loaded, (int)sim.At(first).X, (int)sim.At(first).Y));
        var (x, y) = FreeTile(loaded);
        Place(loaded, "turret", x, y, 1);
        Assert.True(Occupant(loaded, x, y) >= next);
    }

    [Fact(DisplayName = "две симуляции с одинаковыми командами дают одинаковый мир", Skip = "ждёт С1: покрытие (pave)")]
    public void SameCommandsGiveSameWorld()
    {
    }

    [Fact(DisplayName = "снимок мира берёт только перечисленные компоненты и не делит данные с миром", Skip = "ECS TS (World.snapshot): в движке .NET — WorldSave, покрыто Н1")]
    public void SnapshotTakesListedComponents()
    {
    }
}
