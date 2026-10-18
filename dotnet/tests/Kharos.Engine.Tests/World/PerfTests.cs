using System.Diagnostics;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

/// <summary>
/// Замер, как examples/bench.rs пилота: N багги на колёсах, доехавшему — новая цель. Только вручную (Release):
/// <c>bin/Release/net11.0/Kharos.Engine.Tests.exe -method "*PerfTests*" -explicit only</c>.
/// </summary>
public sealed class PerfTests
{
    [Theory(Explicit = true)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void BuggiesDrive(int units)
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        const double size = 250;
        var scheduler = new Scheduler(world,
        [
            new SystemSpec
            {
                Name = "moves/wheels.drive",
                Query = ["Position", "Facing", "Drive"],
                Where = [("Drive", "move", "wheels")],
                Write = ["Position", "Facing", "Drive"],
                System = new DelegateSystem(Wheels),
            },
            new SystemSpec
            {
                Name = "games/bench.retarget",
                Query = ["Drive"],
                Write = ["Drive"],
                After = ["drive"],
                System = new DelegateSystem(ctx =>
                {
                    var tx = ctx.Column(drive, "tx");
                    var ty = ctx.Column(drive, "ty");
                    var moving = ctx.Column(drive, "moving");
                    for (var i = 0; i < ctx.Count; i++)
                        if (moving[i] == 0)
                        {
                            tx[i] = ctx.Random() * size * 2 - size;
                            ty[i] = ctx.Random() * size * 2 - size;
                            moving[i] = 1;
                        }
                }),
            },
        ]);
        var commands = new CommandBuffer(world);
        for (var i = 1; i <= units; i++)
        {
            var (x, y) = ((double)(i * 7919L % (long)(size * 2)) - size, (double)(i * 104729L % (long)(size * 2)) - size);
            var unit = commands.Spawn();
            commands.Add(unit, Builtin.Position).Number(0, x).Number(1, y);
            commands.Add(unit, Builtin.Facing);
            commands.Add(unit, Builtin.Owner).Number(0, 1);
            commands.Add(unit, drive).Text(0, "wheels").Number(1, 4).Number(2, 3).Number(3, -x).Number(4, -y).Flag(5, true);
        }
        var started = Stopwatch.GetTimestamp();
        Commands.Apply(world, commands.Written, [], []);
        var spawnMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        for (var i = 0; i < 20; i++)
            scheduler.Step(0.05);
        const int ticks = 200;
        started = Stopwatch.GetTimestamp();
        for (var i = 0; i < ticks; i++)
            scheduler.Step(0.05);
        var tickMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds / ticks;
        Assert.Empty(scheduler.Errors);

        // Голая сборка и возврат столбцов без системы: цена границы на стороне движка.
        var query = new Query(world, ["Position", "Facing", "Drive"]);
        var columns = new QueryColumns();
        started = Stopwatch.GetTimestamp();
        for (var i = 0; i < ticks; i++)
        {
            query.Gather(columns);
            query.Scatter(columns, query.Components);
        }
        var copyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds / ticks;

        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine($"{units} юнитов: создание {spawnMs:F1} мс, тик {tickMs:F3} мс, сборка+возврат 9 столбцов {copyMs:F3} мс");
        foreach (var timing in scheduler.Timings())
        {
            double per(double ms) => ms / (ticks + 20);
            output.WriteLine($"  {timing.Name}: столбцы {per(timing.Gather):F3}, система {per(timing.Run):F3}, назад {per(timing.Scatter):F3}, команды {per(timing.Commands):F3} мс");
        }
    }

    /// <summary>Перенос mods/base/moves/wheels.luau: поворот с ограниченной скоростью и ход туда, куда смотрит.</summary>
    private static void Wheels(SystemContext ctx)
    {
        var drive = ctx.World.Schema.Id("Drive");
        var x = ctx.Column(Builtin.Position, "x");
        var y = ctx.Column(Builtin.Position, "y");
        var facing = ctx.Column(Builtin.Facing, "angle");
        var speed = ctx.Column(drive, "speed");
        var turnRate = ctx.Column(drive, "turn");
        var tx = ctx.Column(drive, "tx");
        var ty = ctx.Column(drive, "ty");
        var moving = ctx.Column(drive, "moving");
        var dt = ctx.Dt;
        for (var i = 0; i < ctx.Count; i++)
        {
            if (moving[i] == 0)
                continue;
            var (px, py) = (x[i], y[i]);
            var (dx, dy) = (tx[i] - px, ty[i] - py);
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var step = speed[i] * dt;
            var face = facing[i];
            if (distance <= step)
            {
                x[i] = px + dx;
                y[i] = py + dy;
                moving[i] = 0;
                continue;
            }
            var turn = turnRate[i] * dt;
            var off = Wrap(Math.Atan2(dy, dx) - face);
            face += Math.Abs(off) <= turn ? off : off > 0 ? turn : -turn;
            facing[i] = Wrap(face);
            var ahead = Math.Cos(off);
            if (ahead > 0)
            {
                x[i] = px + Math.Cos(face) * step * ahead;
                y[i] = py + Math.Sin(face) * step * ahead;
            }
        }
    }

    private static double Wrap(double angle) => angle - 2 * Math.PI * Math.Floor((angle + Math.PI) / (2 * Math.PI));
}
