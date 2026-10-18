using System.Diagnostics;
using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Movement;

/// <summary>
/// Замер хода (planPaths + moveUnits) на пустой карте: бой 500×2 — две толпы навстречу (эталон TS: 3 мс на тик всего, ход —
/// 55 %), и 10 тыс. юнитов одним приказом. Только вручную (Release):
/// <c>bin/Release/net11.0/Kharos.Engine.Tests.exe -class "*MovementPerfTests*" -explicit only</c>.
/// </summary>
public sealed class MovementPerfTests(ITestOutputHelper output)
{
    private static readonly string[] Army = ["infantry", "buggy", "tank", "infantry", "lancer", "rocketeer"];

    /// <summary>Тики: всего, на тик в среднем и худший, мс.</summary>
    private (double Average, double Worst) Measure(MoveWorld world, int ticks)
    {
        var worst = 0.0;
        var total = Stopwatch.StartNew();
        for (var i = 0; i < ticks; i++)
        {
            var started = Stopwatch.GetTimestamp();
            world.Step();
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return (total.Elapsed.TotalMilliseconds / ticks, worst);
    }

    [Fact(Explicit = true)]
    public void Battle500x2()
    {
        var world = new MoveWorld(1024, GeneratorConfig.Default with { Blank = true });
        var red = Enumerable.Range(0, 500).Select(i => world.Spawn(Army[i % Army.Length], 1, -60 + i % 20 * 2, -25 + i / 20 * 2)).ToArray();
        var blue = Enumerable.Range(0, 500).Select(i => world.Spawn(Army[i % Army.Length], 2, 22 + i % 20 * 2, -25 + i / 20 * 2)).ToArray();
        world.Nav.OrderGroup(red, 30, 0);
        world.Nav.OrderGroup(blue, -30, 0);
        world.Seconds(1);
        var (average, worst) = Measure(world, 400);
        output.WriteLine($"бой 500×2: {average:F3} мс на тик, худший {worst:F2} мс; в пути {world.PathCount}, поиск {world.Nav.Search.Searched:F0} тайлов");
        Assert.True(average < 50);
    }

    /// <summary>Отпечаток мира после боя на земле по генератору: сверить между ОС (Windows, Linux) — биты одни.</summary>
    [Fact(Explicit = true)]
    public void Fingerprint()
    {
        var world = new MoveWorld(1024);
        var (x, y) = world.Plateau(40, 12, 3);
        var red = Enumerable.Range(0, 120).Select(i => world.Spawn(Army[i % Army.Length], 1, x + i % 10, y + i / 10)).ToArray();
        var blue = Enumerable.Range(0, 120).Select(i => world.Spawn(Army[i % Army.Length], 2, x + 30 + i % 10, y + i / 10)).ToArray();
        world.Nav.OrderGroup(red, x + 35, y + 6);
        world.Nav.OrderGroup(blue, x + 5, y + 6);
        world.Seconds(30);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Kharos.Engine.World.WorldSave.Save(world.World)));
        output.WriteLine($"отпечаток: {hash}, поиск {world.Nav.Search.Searched:F0} тайлов");
    }

    [Fact(Explicit = true)]
    public void TenThousand()
    {
        var world = new MoveWorld(1024, GeneratorConfig.Default with { Blank = true });
        var units = Enumerable.Range(0, 10_000).Select(i => world.Spawn(Army[i % Army.Length], 1 + (uint)(i % 4 / 3), -200 + i % 100 * 2, -100 + i / 100 * 2)).ToArray();
        // Четыре полка, у каждого своя цель.
        for (var group = 0; group < 4; group++)
            world.Nav.OrderGroup([.. units.Where((_, i) => i % 4 == group)], 150 - group * 40, group * 30 - 45);
        world.Seconds(1);
        var (average, worst) = Measure(world, 200);
        output.WriteLine($"10 тыс.: {average:F3} мс на тик, худший {worst:F2} мс; в пути {world.PathCount}, поиск {world.Nav.Search.Searched:F0} тайлов");
        Assert.True(average < 200);
    }
}
