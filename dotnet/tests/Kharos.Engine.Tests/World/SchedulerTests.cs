using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

public sealed class SchedulerTests
{
    private static SystemSpec Spec(string name, Action<SystemContext>? run = null, string[]? after = null, string[]? before = null, string? instead = null, int every = 1) => new()
    {
        Name = name,
        After = after ?? [],
        Before = before ?? [],
        Instead = instead,
        Every = every,
        System = new DelegateSystem(run ?? (_ => { })),
    };

    private static List<string> Names(params SystemSpec[] specs) => [.. new Scheduler(Fixtures.World(), specs).Names];

    [Fact]
    public void OrderFollowsAfterBeforeThenDeclaration()
    {
        Assert.Equal(["a/x.one", "a/x.two", "a/x.three"], Names(Spec("a/x.one"), Spec("a/x.two"), Spec("a/x.three")));
        Assert.Equal(
            ["m/b.second", "m/a.first", "m/c.third"],
            Names(Spec("m/a.first", after: ["second"]), Spec("m/b.second"), Spec("m/c.third", after: ["m/a.first"])));
        Assert.Equal(["m/c.late", "m/a.x", "m/b.y"], Names(Spec("m/a.x"), Spec("m/b.y"), Spec("m/c.late", before: ["x", "y"])));
    }

    [Fact]
    public void KeyMatchesEverySystemWithIt()
    {
        // Ключ, общий у двух видов, ссылается на обе системы.
        Assert.Equal(["a.drive", "b.drive", "c.crowd"], Names(Spec("c.crowd", after: ["drive"]), Spec("a.drive"), Spec("b.drive")));
    }

    [Fact]
    public void CyclesAndUnknownNamesFail()
    {
        var cycle = Assert.Throws<ScheduleException>(() => Names(Spec("a", after: ["c"]), Spec("b", after: ["a"]), Spec("c", after: ["b"]), Spec("free")));
        Assert.Contains("по кругу: a, b, c", cycle.Message);
        Assert.Contains("нет системы ghost", Assert.Throws<ScheduleException>(() => Names(Spec("a", before: ["ghost"]))).Message);
        Assert.Contains("every", Assert.Throws<ScheduleException>(() => Names(Spec("a", every: 0))).Message);
        Assert.Throws<ScheduleException>(() => Names(new SystemSpec { Name = "w", Query = ["Drive"], Write = ["Position"], System = new DelegateSystem(_ => { }) }));
        Assert.Throws<ScheduleException>(() => Names(new SystemSpec { Name = "q", Query = ["Nope"], System = new DelegateSystem(_ => { }) }));
    }

    [Fact]
    public void InsteadTakesThePlaceAndTheName()
    {
        // fight встаёт после drive (ссылкой на прежний), замена наследует after прежнего.
        var names = Names(
            Spec("base/a.income"),
            Spec("base/b.drive", after: ["income"]),
            Spec("base/c.fight", after: ["drive"]),
            Spec("mod/x.better", instead: "drive"));
        Assert.Equal(["base/a.income", "mod/x.better", "base/c.fight"], names);
        // Замена заменённой: поздний мод главнее.
        Assert.Equal(["mod/z.best", "base/c.fight"], Names(Spec("base/b.drive"), Spec("base/c.fight", after: ["drive"]), Spec("mod/x.better", instead: "drive"), Spec("mod/z.best", instead: "drive")));
        Assert.Contains("instead — нет системы", Assert.Throws<ScheduleException>(() => Names(Spec("mod/x", instead: "drive"))).Message);
        Assert.Contains("несколько", Assert.Throws<ScheduleException>(() => Names(Spec("a.drive"), Spec("b.drive"), Spec("mod/x", instead: "drive"))).Message);
    }

    [Fact]
    public void EveryRunsOnMultiplesOfIt()
    {
        var ticks = new List<ulong>();
        var scheduler = new Scheduler(Fixtures.World(), [Spec("slow", ctx => ticks.Add(ctx.Tick), every: 4)]);
        for (var i = 0; i < 12; i++)
            scheduler.Step(0.05);
        Assert.Equal([4ul, 8, 12], ticks);
    }

    [Fact]
    public void SystemsSeeColumnsAndWriteOnlyWhatTheyDeclare()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var units = Enumerable.Range(0, 3).Select(i => world.At(i, 0)).ToList();
        foreach (var (unit, move) in units.Zip(["wheels", "legs", "wheels"]))
        {
            world.Add(unit, drive);
            world.Set(unit, drive, "move", move);
            world.Set(unit, drive, "speed", 2.0);
        }
        var seen = new List<uint>();
        var scheduler = new Scheduler(world, [new SystemSpec
        {
            Name = "moves/wheels.drive",
            Query = ["Position", "Drive"],
            Where = [("Drive", "move", "wheels")],
            Write = ["Position"],
            System = new DelegateSystem(ctx =>
            {
                seen.AddRange(ctx.Entities);
                var x = ctx.Column(Builtin.Position, "x");
                var speed = ctx.Column(drive, "speed");
                for (var i = 0; i < ctx.Count; i++)
                {
                    x[i] += speed[i] * ctx.Dt;
                    speed[i] = 0;
                }
            }),
        }]);
        scheduler.Step(0.5);
        Assert.Empty(scheduler.Errors);
        Assert.Equal([units[0], units[2]], seen);
        Assert.Equal([1.0, 1, 3], units.Select(unit => world.Number(unit, Builtin.Position, 0)));
        Assert.All(units, unit => Assert.Equal(2.0, world.Number(unit, drive, 1)));
    }

    [Fact]
    public void CommandsLandBetweenSystems()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var counts = new List<int>();
        var scheduler = new Scheduler(world,
        [
            Spec("spawn", ctx =>
            {
                var unit = ctx.Commands.Spawn();
                ctx.Commands.Add(unit, drive).Number(1, ctx.Tick);
            }),
            new SystemSpec { Name = "count", Query = ["Drive"], System = new DelegateSystem(ctx => counts.Add(ctx.Count)) },
        ]);
        scheduler.Step(0.05);
        scheduler.Step(0.05);
        Assert.Equal([1, 2], counts);
        Assert.Equal(2.0, world.Number(2, drive, 1));
    }

    [Fact]
    public void EventsReachHandlersAndTheirCommandsApply()
    {
        var world = Fixtures.World();
        var health = world.Schema.Id("Health");
        var log = new List<string>();
        var scheduler = new Scheduler(world, [Spec("make", ctx =>
        {
            ctx.Commands.Spawn();
            ctx.Commands.Emit("made", [7]);
        }, every: 2)]);
        scheduler.On("made", ctx => log.Add($"made {ctx.Payload[0]}"));
        scheduler.On(Commands.Spawned, ctx =>
        {
            var entity = BitConverter.ToUInt32(ctx.Payload);
            log.Add($"spawned {entity}");
            ctx.Commands.Add(entity, health);
            ctx.Commands.Emit("armed");
        });
        scheduler.On("armed", ctx => log.Add($"armed, alive {ctx.World.Count}"));
        scheduler.Emit("start", []);
        scheduler.Step(0.05);
        scheduler.Step(0.05);
        Assert.Empty(scheduler.Errors);
        Assert.Equal(["made 7", "spawned 1", "armed, alive 1"], log);
        Assert.True(world.Has(1, health));
    }

    [Fact]
    public void EndlessEventsAreCut()
    {
        var scheduler = new Scheduler(Fixtures.World(), []);
        var rounds = 0;
        scheduler.On("ping", ctx =>
        {
            rounds++;
            ctx.Commands.Emit("ping");
        });
        scheduler.Emit("ping", []);
        Assert.Equal(Scheduler.MaxEventRounds, rounds);
        Assert.Contains("без конца", Assert.Single(scheduler.Errors).Message);
    }

    [Fact]
    public void FailingSystemLeavesNoTrace()
    {
        var world = Fixtures.World();
        var unit = world.At(0, 0);
        var later = 0;
        var scheduler = new Scheduler(world,
        [
            new SystemSpec
            {
                Name = "broken",
                Query = ["Position"],
                Write = ["Position"],
                System = new DelegateSystem(ctx =>
                {
                    ctx.Column(Builtin.Position, "x")[0] = 50;
                    ctx.Commands.Spawn();
                    throw new InvalidOperationException("сломалось");
                }),
            },
            new SystemSpec
            {
                Name = "garbage",
                Query = ["Position"],
                Write = ["Position"],
                System = new DelegateSystem(ctx =>
                {
                    ctx.Column(Builtin.Position, "x")[0] = 60;
                    ctx.Commands.Append([0x7f]);
                }),
            },
            Spec("later", _ => later++),
        ]);
        scheduler.Step(0.05);
        Assert.Equal(["broken", "garbage"], scheduler.Errors.Select(error => error.Source));
        Assert.Equal(0.0, world.Number(unit, Builtin.Position, 0));
        Assert.Equal(1, world.Count);
        Assert.Equal(1, later);
    }

    [Fact]
    public void OrdersRunFirstInTheNextTick()
    {
        var world = Fixtures.World();
        var log = new List<string>();
        var scheduler = new Scheduler(world, [Spec("sys", ctx => log.Add($"system, alive {ctx.World.Count}"))]);
        scheduler.OnOrder("build", ctx =>
        {
            log.Add($"order {ctx.Name} from {ctx.Player}: {ctx.Payload[0]}");
            ctx.Commands.Spawn();
        });
        scheduler.Send(3, "build", [5]);
        scheduler.Send(3, "unknown", []);
        Assert.Empty(log);
        scheduler.Step(0.05);
        scheduler.Step(0.05);
        Assert.Equal(["order build from 3: 5", "system, alive 1", "system, alive 1"], log);
    }

    [Fact]
    public void RandomIsSplitMix64()
    {
        var scheduler = new Scheduler(Fixtures.World(), []);
        Assert.Equal(new[] { 0.5665615751722809, 0.7457817572627011, 0.9710027535867962 }, new[] { scheduler.NextRandom(), scheduler.NextRandom(), scheduler.NextRandom() });
        var copy = new Scheduler(Fixtures.World(), [], seed: 1);
        copy.RandomState = scheduler.RandomState;
        Assert.Equal(scheduler.NextRandom(), copy.NextRandom());
    }

    [Fact]
    public void SignalsLiveOneTick()
    {
        var scheduler = new Scheduler(Fixtures.World(), [Spec("shoot", ctx => ctx.Commands.Signal("shot", [1]), every: 2)]);
        scheduler.Step(0.05);
        Assert.Empty(scheduler.Signals);
        scheduler.Step(0.05);
        Assert.Equal("shot", Assert.Single(scheduler.Signals).Name);
        scheduler.Step(0.05);
        Assert.Empty(scheduler.Signals);
        Assert.Single(scheduler.Timings(), timing => timing.Name == "shoot");
    }

    [Fact]
    public void NearIsAvailableToSystems()
    {
        var world = Fixtures.World();
        var (a, b) = (world.At(0, 0), world.At(3, 4));
        world.At(30, 0);
        IReadOnlyList<uint> found = [];
        var scheduler = new Scheduler(world, [Spec("look", ctx => found = [.. ctx.Near(0, 0, 5)])]);
        scheduler.Step(0.05);
        Assert.Equal([a, b], found);
    }
}
