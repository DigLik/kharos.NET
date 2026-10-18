using Kharos.Engine.Layers;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

/// <summary>Планировщик Н5: Without, побочные таблицы (чтение и запись по битам), RowOf, права приказа, слои в командах.</summary>
public sealed class TableTests
{
    private static SystemSpec Spec(string name, Action<SystemContext> run, string[]? query = null, string[]? write = null, string[]? without = null, TableSpec[]? tables = null) => new()
    {
        Name = name,
        Query = query ?? [],
        Write = write ?? [],
        Without = without ?? [],
        Tables = tables ?? [],
        System = new DelegateSystem(run),
    };

    [Fact]
    public void WithoutFiltersFrame()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var health = world.Schema.Id("Health");
        uint[] seen = [];
        var plain = world.At(1, 1);
        world.Add(plain, drive);
        var hurt = world.At(2, 2);
        world.Add(hurt, drive);
        world.Add(hurt, health);
        var scheduler = new Scheduler(world, [Spec("s", ctx => seen = ctx.Entities.ToArray(), ["Position", "Drive"], without: ["Health"])]);
        scheduler.Step(0.05);
        Assert.Equal([plain], seen);
        Assert.Throws<ScheduleException>(() => new Scheduler(world, [Spec("w", _ => { }, without: ["Health"])]));
        Assert.Throws<ScheduleException>(() => new Scheduler(world, [Spec("both", _ => { }, ["Drive"], without: ["Drive"])]));
    }

    [Fact]
    public void TablesGatherSeparatelyAndWriteBackChangedBitsOnly()
    {
        var world = Fixtures.World();
        world.TrackChanges = true;
        var health = world.Schema.Id("Health");
        var drive = world.Schema.Id("Drive");
        var shooter = world.At(0, 0);
        world.Add(shooter, drive);
        var targets = new List<uint>();
        for (var i = 1; i <= 4; i++)
        {
            var target = world.At(i, 0);
            world.Add(target, health);
            targets.Add(target);
        }
        world.TakeChanges(new WorldChanges());
        var rows = new List<int>();
        var scheduler = new Scheduler(world,
        [
            Spec("fight", ctx =>
            {
                // Кадр — стрелки, таблица 0 — места целей (чтение), таблица 1 — здоровье (запись).
                var places = ctx.Table(0);
                var hp = ctx.Table(1);
                rows.Add(ctx.RowOf(shooter));
                rows.Add(ctx.RowOf(targets[0]));
                foreach (var target in targets.Where((_, i) => i % 2 == 1))
                {
                    var row = hp.RowOf(target);
                    hp.Column(health, "hp")[row] -= 30;
                    rows.Add(places.RowOf(target) >= 0 ? 1 : 0);
                }
                // Тот же столбец таблицы на чтение: правка в мир не вернётся.
                places.Column(Builtin.Position, "x")[0] = 999;
            }, ["Position", "Drive"], tables:
            [
                new TableSpec { Query = ["Position", "Health"] },
                new TableSpec { Query = ["Health"], Write = true },
            ]),
        ]);
        scheduler.Step(0.05);
        Assert.Empty(scheduler.Errors);
        Assert.Equal([0, -1, 1, 1], rows);
        Assert.Equal([100.0, 70, 100, 70], targets.Select(target => world.Number(target, health, 0)));
        Assert.Equal(1.0, world.Number(targets[0], Builtin.Position, 0));
        var changes = new WorldChanges();
        world.TakeChanges(changes);
        // Отмечены только изменённые по битам.
        Assert.Equal([targets[1], targets[3]], changes.Fields.Select(field => field.Entity).Order());
    }

    [Fact]
    public void ComponentWrittenByTwoPartsIsLoadError()
    {
        var world = Fixtures.World();
        var error = Assert.Throws<ScheduleException>(() => new Scheduler(world,
        [
            Spec("x", _ => { }, ["Health"], ["Health"], tables: [new TableSpec { Query = ["Health"], Write = true }]),
        ]));
        Assert.Contains("Health пишут и кадр, и таблица 0", error.Message);
        Assert.Throws<ScheduleException>(() => new Scheduler(world,
        [
            Spec("y", _ => { }, tables: [new TableSpec { Query = ["Health", "Drive"], Write = true }, new TableSpec { Query = ["Drive"], Write = true }]),
        ]));
        // Читать то, что пишет кадр, — можно.
        _ = new Scheduler(world, [Spec("z", _ => { }, ["Health"], ["Health"], tables: [new TableSpec { Query = ["Health"] }])]);
    }

    [Fact]
    public void OrdersCarryRightsAndCommandsReachLayers()
    {
        var world = Fixtures.World();
        var layers = new LayerSet([new LayerSpec { Name = "marks", Scope = LayerScope.World, Channels = [Channel.U8] }]);
        var scheduler = new Scheduler(world, []) { Layers = layers };
        var rights = new List<OrderRights>();
        scheduler.OnOrder("mark", ctx =>
        {
            rights.Add(ctx.Rights);
            ctx.Commands.Layer(0, 0, 0, 5, 6, 1, 1, 3);
        });
        scheduler.Send(1, "mark", [], OrderRights.Remote | OrderRights.Admin);
        scheduler.Send(1, "mark", []);
        scheduler.Step(0.05);
        Assert.Equal([OrderRights.Remote | OrderRights.Admin, OrderRights.None], rights);
        Assert.Equal(3, layers.Get(0, LayerOwner.World, 5, 6, 0));
        Assert.Empty(scheduler.Errors);
    }
}
