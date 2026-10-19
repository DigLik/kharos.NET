using Kharos.Engine.Movement;
using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Movement;

/// <summary>Покрытие стоящих (Navigation.Standing) — тот же набор, что standingUnits в HashSet, при любых ignore, корпусе и ходе.</summary>
public sealed class StandingTests
{
    private static readonly string[] Army = ["infantry", "buggy", "tank", "lancer", "rocketeer"];

    [Fact(DisplayName = "стоящие юниты: кэш покрытия даёт тот же набор тайлов, что перебор, и следует за миром")]
    public void SameAsScan()
    {
        var world = new MoveWorld(128, GeneratorConfig.Default with { Blank = true });
        var units = new List<uint>();
        var random = new Random(5);
        // Часть — у самого края карты: их покрытие выходит за край.
        for (var i = 0; i < 160; i++)
            units.Add(world.Spawn(Army[i % Army.Length], 1 + (uint)(i % 2), -64 + random.Next(128) + random.NextDouble(), -64 + random.Next(128) + random.NextDouble()));
        for (var i = 0; i < 40; i++)
            world.Nav.Order(units[i * 3], random.Next(-60, 60), random.Next(-60, 60));
        for (var round = 0; round < 6; round++)
        {
            foreach (var radius in (double[])[0.3, 0.5, 0.9, 1.6])
                foreach (var (air, jammed) in ((bool, bool)[])[(false, false), (false, true), (true, false)])
                {
                    var ignore = new HashSet<uint>(units.Where((_, i) => (i + round) % 7 == 0));
                    var exact = new HashSet<long>();
                    world.Nav.StandingUnits(ignore, radius, air, jammed, exact);
                    var cached = world.Nav.Standing(ignore, radius, air, jammed);
                    var none = world.Nav.Standing(null, radius, air, jammed);
                    var all = new HashSet<long>();
                    world.Nav.StandingUnits(null, radius, air, jammed, all);
                    for (var y = -70; y < 70; y++)
                        for (var x = -70; x < 70; x++)
                        {
                            Assert.Equal(exact.Contains(Navigation.Key(x, y)), cached.Contains(x, y));
                            Assert.Equal(all.Contains(Navigation.Key(x, y)), none.Contains(x, y));
                        }
                }
            world.Seconds(0.5);
        }
    }
}
