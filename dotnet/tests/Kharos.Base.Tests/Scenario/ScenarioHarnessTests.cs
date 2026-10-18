using Base.Shared;
using Base.Shared.Orders;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using MemoryPack;

namespace Kharos.Base.Tests.Harness;

/// <summary>
/// Обвязка сценариев (tests/Kharos.Base.Scenario): партия test, ход времени, приказы, мир по именам, сохранение и режимы
/// CLR и wasm. Механик base тут нет — проверяется сама обвязка; тесты разделов лежат в Sim/&lt;Раздел&gt;.
/// </summary>
public sealed class ScenarioHarnessTests
{
    private static void SkipUnlessAvailable(ModMode mode) => Assert.SkipWhen(Sim.Unavailable(mode) is not null, Sim.Unavailable(mode) ?? "");

    [Fact]
    public void CreatesTestGameAndAdvancesTime()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        Assert.Equal("test", sim.Host.Game.Name);
        Assert.Equal([1u, 2u], sim.Host.Players);
        Assert.True(sim.Host.Started);
        var tick = sim.Tick;
        sim.Advance(20);
        sim.Seconds(1);
        Assert.Equal(40, sim.Ticks);
        Assert.Equal(2.0, sim.Time, 9);
        Assert.Equal(tick + 40, sim.Tick);
        Assert.Equal(20, Sim.TicksIn(1));
        Assert.Equal(3, Sim.TicksIn(0.14));
        Assert.Empty(sim.Log);
    }

    [Fact]
    public void OptionsReachGameSettings()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create(new SimOptions
        {
            Size = 64,
            Fog = false,
            Land = new { blank = true },
            Rules = new { techTree = false, protection = 0 },
            Players = [3],
            Seed = 9,
        });
        Assert.Equal(64, sim.Host.Game.Size);
        Assert.False(sim.Host.Game.Fog);
        Assert.Null(sim.Host.Vision);
        Assert.Equal([3u], sim.Host.Players);
        Assert.Equal(9ul, sim.Host.Seed);
        var rules = sim.Host.Game.Data.GetProperty("rules");
        Assert.False(rules.GetProperty("techTree").GetBoolean());
        Assert.Equal(0, rules.GetProperty("protection").GetInt32());
        Assert.NotNull(sim.Land);
    }

    [Fact]
    public void UntilStopsOnConditionOrLimit()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        Assert.True(sim.Until(() => sim.Ticks >= 7));
        Assert.Equal(7, sim.Ticks);
        Assert.True(sim.Until(() => true, 1));
        Assert.Equal(7, sim.Ticks);
        Assert.False(sim.Until(() => false, 1.5));
        Assert.Equal(7 + 30, sim.Ticks);
    }

    [Fact]
    public void SendUsesOrderNameAndPayloadOfTheClass()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        var got = new List<(uint Player, uint[] Units, int X, OrderRights Rights)>();
        sim.Host.Scheduler.OnOrder(MoveOrder.Name, ctx =>
        {
            var order = MemoryPackSerializer.Deserialize<MoveOrder>(ctx.Payload)!;
            got.Add((ctx.Player, order.Units, order.X, ctx.Rights));
        });
        sim.Send(2, new MoveOrder { Units = [5, 6], X = 11, Y = 3 });
        sim.Send(1, new MoveOrder { Units = [7], X = -4 }, OrderRights.Remote);
        Assert.Empty(got);
        sim.Advance();
        Assert.Equal(2, got.Count);
        Assert.Equal((2u, 11, OrderRights.None), (got[0].Player, got[0].X, got[0].Rights));
        Assert.Equal([5u, 6u], got[0].Units);
        Assert.Equal((1u, -4, OrderRights.Remote), (got[1].Player, got[1].X, got[1].Rights));
        Assert.Equal([7u], got[1].Units);
        Assert.Throws<InvalidOperationException>(() => sim.Send(1, new List<int>()));
    }

    [Fact]
    public void ReadsAndWritesComponentsByName()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        var entity = sim.World.Spawn();
        sim.Add(entity, Names.Position.Name);
        sim.Add(entity, Names.Owner.Name);
        sim.Add(entity, Names.Health.Name);
        sim.Set(entity, "Position.x", 12.5);
        sim.Set(entity, "Position.y", -3);
        sim.Set(entity, "Owner.player", 2);
        sim.Set(entity, "Health.value", 40);
        Assert.Equal((12.5, -3.0), sim.At(entity));
        Assert.Equal(2u, sim.OwnerOf(entity));
        Assert.Equal(40, sim.Num(entity, Names.Health.Name, Names.Health.Value));
        Assert.Equal(40, sim.HealthOf(entity));
        Assert.True(sim.Has(entity, "Health"));
        Assert.Contains(entity, sim.With(Names.Health.Name, Names.Owner.Name));
        // У игрока 2 есть ещё сущность счёта (Player, Owner — ядро base): она без Position.
        Assert.Equal([sim.Wallet(2), entity], sim.With("Owner", "player", 2));
        Assert.Equal([entity], sim.Of(2, Names.Health.Name));
        Assert.Empty(sim.Of(1, Names.Health.Name));
        sim.Remove(entity, Names.Health.Name);
        Assert.False(sim.Has(entity, Names.Health.Name));
        Assert.True(double.IsNaN(sim.HealthOf(entity)));
        Assert.Throws<ArgumentException>(() => sim.Num(entity, "Position"));
        sim.World.Destroy(entity);
        Assert.False(sim.Alive(entity));
    }

    [Fact]
    public void ThroughSaveGivesTheSameWorldAndKeepsRunningTheSame()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create(new SimOptions { Seed = 5 });
        var entity = sim.World.Spawn();
        sim.Add(entity, Names.Position.Name);
        sim.Set(entity, "Position.x", 3.25);
        sim.Advance(30);
        using var copy = sim.ThroughSave();
        Assert.True(sim.SameWorld(copy));
        Assert.Equal(sim.Host.Game.Name, copy.Host.Game.Name);
        Assert.Equal(3.25, copy.Num(entity, "Position.x"));
        Assert.Equal(sim.Host.Players, copy.Host.Players);
        Assert.Equal(sim.Ticks, copy.Ticks);
        sim.Advance(40);
        copy.Advance(40);
        Assert.True(sim.SameWorld(copy));
        Assert.True(sim.Save().AsSpan().SequenceEqual(copy.Save()));
    }

    [Fact]
    public void ClrAndWasmGiveTheSameWorld()
    {
        SkipUnlessAvailable(ModMode.Clr);
        SkipUnlessAvailable(ModMode.Wasm);
        static Sim Play(ModMode mode)
        {
            var sim = Sim.Create(new SimOptions { Mode = mode, Seed = 3, Players = [1, 2, 3] });
            var entity = sim.World.Spawn();
            sim.Add(entity, Names.Position.Name);
            sim.Set(entity, "Position.x", 1.5);
            sim.Advance(100);
            return sim;
        }
        using var clr = Play(ModMode.Clr);
        using var wasm = Play(ModMode.Wasm);
        Assert.Equal(ModMode.Clr, clr.Mode);
        Assert.Equal(ModMode.Wasm, wasm.Mode);
        Assert.True(clr.SameWorld(wasm), "мир CLR и wasm разошёлся");
        Assert.True(clr.Save().AsSpan().SequenceEqual(wasm.Save()), "сохранение CLR и wasm разошлось");
    }

    [Fact]
    public void FlushAppliesOrdersWithoutAgeingTheWorld()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        var applied = 0;
        var dt = -1.0;
        sim.Host.Scheduler.OnOrder(StopOrder.Name, ctx =>
        {
            applied++;
            dt = ctx.Dt;
        });
        sim.Send(1, new StopOrder { Units = [1] });
        var tick = sim.Tick;
        sim.Flush();
        Assert.Equal(1, applied);
        Assert.Equal(0.0, dt);
        Assert.Equal(tick, sim.Tick);
        Assert.Equal(0, sim.Ticks);
        Assert.Equal(0.0, sim.Time);
        sim.Advance();
        Assert.Equal(1, applied);
    }

    [Fact]
    public void SystemErrorsFailAdvanceUnlessAllowed()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        sim.Host.Scheduler.OnOrder(StopOrder.Name, _ => throw new InvalidOperationException("сбой приказа"));
        sim.Send(1, new StopOrder());
        var error = Assert.Throws<SimErrorException>(() => sim.Advance());
        Assert.Contains("сбой приказа", error.Message);
        Assert.Single(error.Errors);
        // Ошибка отдана один раз: дальше партия идёт.
        sim.Advance(3);

        using var tolerant = Sim.Create(new SimOptions { ThrowOnError = false });
        tolerant.Host.Scheduler.OnOrder(StopOrder.Name, _ => throw new InvalidOperationException("сбой приказа"));
        tolerant.Send(1, new StopOrder());
        tolerant.Advance();
        Assert.Contains(tolerant.Log, line => line.Contains("сбой приказа"));
    }

    [Fact]
    public void QueryOfUnknownNameSaysSo()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create();
        var error = Assert.Throws<InvalidOperationException>(() => sim.Query<int>("nope.nothing"));
        Assert.Contains("nope.nothing", error.Message);
    }

    [Fact]
    public void EditHelpersWorkOrSayWhatIsMissing()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create(new SimOptions { Land = new { blank = true } });
        if (!sim.HasOrder(EditUnitOrder.Name))
        {
            var error = Assert.Throws<NotImplementedException>(() => sim.PutUnit("builder", 1, 10.5, 10.5));
            Assert.Contains("edit.unit", error.Message);
            Assert.Throws<NotImplementedException>(() => sim.SetCredits(1, 100));
            Assert.Throws<NotImplementedException>(() => sim.Erase(1));
            return;
        }
        // Обработчики есть (Б1): правка видна сразу, время не прошло.
        var unit = sim.PutUnit("builder", 1, 10.5, 10.5);
        Assert.True(sim.Alive(unit));
        Assert.Equal((10.5, 10.5), sim.At(unit));
        Assert.Equal(1u, sim.OwnerOf(unit));
        Assert.Equal(0.0, sim.Time);
        sim.SetCredits(1, 123);
        Assert.Equal(123, sim.Credits(1));
        sim.SetHealth(0.5, unit);
        sim.Erase(unit);
        Assert.False(sim.Alive(unit));
    }

    [Fact]
    public void BuildersFindFreeTilesTwoApart()
    {
        SkipUnlessAvailable(Sim.DefaultMode);
        using var sim = Sim.Create(new SimOptions { Land = new { blank = true } });
        var tiles = Builders.FreeTilesNear(sim, 10, 10, 6, 2);
        Assert.Equal(6, tiles.Count);
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
        // Со второго кольца: ближние тайлы (и сама точка) пропущены.
        Assert.All(tiles, tile => Assert.True(Math.Max(Math.Abs(tile.X - 10), Math.Abs(tile.Y - 10)) >= 4));
        Assert.All(tiles, tile => Assert.True((tile.X - 10) % 2 == 0 && (tile.Y - 10) % 2 == 0));
        if (sim.HasOrder(EditUnitOrder.Name))
        {
            var builders = Builders.AddBuilders(sim, 1, 10, 10, 3);
            Assert.Equal(3, builders.Count);
            Assert.All(builders, builder => Assert.Equal(1u, sim.OwnerOf(builder)));
        }
        else
        {
            Assert.Throws<NotImplementedException>(() => Builders.AddBuilders(sim, 1, 10, 10));
        }
    }
}
