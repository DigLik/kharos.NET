using System.Diagnostics;
using Base.Client;

namespace Kharos.Base.Tests;

/// <summary>Граница зоны строительства по сетке (packages/tests/zoneOutline.test.ts).</summary>
public sealed class ZoneOutlineTests
{
    /// <summary>Та же граница перебором каждой точки по всем кругам.</summary>
    private static double[] Slow(IReadOnlyList<double> zone)
    {
        var dots = new List<double>();
        for (var i = 0; i < zone.Count; i += 3)
        {
            var dashes = (int)Math.Floor(120 * zone[i + 2] / 12 + 0.5);
            for (var dash = 0; dash < dashes; dash++)
            {
                var angle = (double)dash / dashes * Math.PI * 2;
                var x = zone[i] + Math.Cos(angle) * zone[i + 2];
                var y = zone[i + 1] + Math.Sin(angle) * zone[i + 2];
                var covered = false;
                for (var other = 0; other < zone.Count && !covered; other += 3)
                    covered = other != i && Math.Sqrt((x - zone[other]) * (x - zone[other]) + (y - zone[other + 1]) * (y - zone[other + 1])) < zone[other + 2] - 0.01;
                if (covered)
                    continue;
                dots.Add(x);
                dots.Add(y);
            }
        }
        return [.. dots];
    }

    /// <summary>База на тысячу зданий и труб: главное здание и россыпь кругов поменьше, в том числе по обе стороны нуля.</summary>
    private static List<double> LargeBase()
    {
        // Тот же ряд, что в TS: seed * 16807 mod (2^31 - 1).
        long seed = 7;
        double Random() => (seed = seed * 16807 % 2147483647) / 2147483647.0;
        var zone = new List<double> { 1.5, 1.5, 12 };
        for (var i = 0; i < 1000; i++)
        {
            var x = Math.Floor(Random() * 120) - 40 + 0.5;
            var y = Math.Floor(Random() * 120) - 40 + 0.5;
            zone.AddRange([x, y, Random() < 0.5 ? 5 : 2]);
        }
        return zone;
    }

    [Fact(DisplayName = "граница зоны по сетке — та же, что перебором")]
    public void OutlineByGridMatchesBruteForce()
    {
        var zone = LargeBase();
        var dots = ZoneOutline.Of(zone);
        Assert.Equal(Slow(zone), dots);
        Assert.True(dots.Length > 0);
    }

    [Fact(Explicit = true, DisplayName = "замер: граница зоны по сетке на большой базе считается быстрее перебора")]
    [Trait("Category", "Bench")]
    public void OutlineByGridIsFast()
    {
        var zone = LargeBase();
        // Первый вызов прогревает JIT обоих путей: меряется работа, а не компиляция.
        _ = ZoneOutline.Of([1.5, 1.5, 12, 4, 4, 5]);
        _ = Slow([1.5, 1.5, 12, 4, 4, 5]);
        var started = Stopwatch.GetTimestamp();
        var dots = ZoneOutline.Of(zone);
        var took = Stopwatch.GetElapsedTime(started);
        started = Stopwatch.GetTimestamp();
        var expected = Slow(zone);
        var tookSlow = Stopwatch.GetElapsedTime(started);
        Assert.Equal(expected, dots);
        // Порог — относительно перебора, а не в миллисекундах: машины сборки бывают медленными.
        Assert.True(took < tookSlow / 2, $"по сетке {took.TotalMilliseconds:0.0} мс, перебором {tookSlow.TotalMilliseconds:0.0} мс");
    }
}
