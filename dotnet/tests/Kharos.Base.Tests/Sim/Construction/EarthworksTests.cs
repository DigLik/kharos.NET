using Base.Shared;
using Base.Shared.Orders;
using Kharos.Engine.Terrain;
using static Kharos.Base.Tests.Construction.Yard;

namespace Kharos.Base.Tests.Construction;

/// <summary>Землеройные работы (packages/tests/earthworks.test.ts): горы срывают, обрывы сглаживают, отмена с возвратом.</summary>
public sealed class EarthworksTests
{
    private const double PeakCost = 40, CliffCost = 25;

    /// <summary>Тайлы горы с той же вершиной, что у тайла (x, y) (peakTiles).</summary>
    private static List<(int X, int Y)> PeakTiles(Sim sim, Peak peak)
    {
        var tiles = new List<(int, int)>();
        var span = (int)Math.Ceiling(peak.Radius + 1);
        var map = sim.Host.Map!.Value;
        for (var y = (int)Math.Floor(peak.Y) - span; y <= (int)Math.Floor(peak.Y) + span; y++)
            for (var x = (int)Math.Floor(peak.X) - span; x <= (int)Math.Floor(peak.X) + span; x++)
                if (map.Contains(x, y) && sim.Land!.PeakOf(x, y) is { } other && other.X == peak.X && other.Y == peak.Y)
                    tiles.Add((x, y));
        return tiles;
    }

    [Fact(DisplayName = "гору срывают целиком: строители работают с края, на её месте — скала; плата по числу её тайлов")]
    public void PeakRazedWhole()
    {
        using var sim = Create();
        sim.SetCredits(1, sim.Credits(1) + 5000);
        var at = Near((x, y) => sim.Land!.PeakOf(x, y) is not null);
        var peak = sim.Land!.PeakOf(at.X, at.Y)!.Value;
        var tiles = PeakTiles(sim, peak);
        var builders = Builders.AddBuilders(sim, 1, (int)Math.Floor(peak.X), (int)Math.Floor(peak.Y), 3);
        var credits = sim.Credits(1);
        sim.Send(1, new DigOrder { Kind = "peak", Tiles = [at.X, at.Y], Builders = [.. builders] });
        sim.Advance();
        Assert.Equal(credits - tiles.Count * PeakCost, sim.Credits(1));
        // Ту же гору второй раз не заказывают.
        Assert.False(sim.CanDig(1, "peak", at.X, at.Y));
        sim.Seconds(60);
        foreach (var (x, y) in tiles)
        {
            Assert.Equal(TerrainType.Rock, sim.Land.TerrainAt(x, y));
            Assert.True(sim.Walkable(x, y));
        }
        Assert.Equal(0, sim.Count(Names.Dig.Name));
    }

    [Fact(DisplayName = "обрыв сглаживают по кромке: рамка по подножию берёт кромки над ним, и подножие становится проездом")]
    public void CliffSmoothed()
    {
        using var sim = Create();
        sim.SetCredits(1, sim.Credits(1) + 5000);
        // Подножие, к которому можно подъехать: снизу проходимый тайл.
        var foot = Near((x, y) => sim.Land!.IsCliffFoot(x + 0.5, y + 0.5) && sim.Walkable(x, y + 2) && sim.Land.TerrainAt(x, y) != TerrainType.Mountain);
        Assert.False(sim.Walkable(foot.X, foot.Y));
        var builders = Builders.AddBuilders(sim, 1, foot.X, foot.Y + 3, 3);
        var credits = sim.Credits(1);
        sim.Send(1, new DigOrder { Kind = "cliff", Tiles = [foot.X, foot.Y], Builders = [.. builders] });
        sim.Advance();
        var edges = sim.With(Names.Dig.Name).Count(dig => sim.Text(dig, "Dig.kind") == "cliff");
        Assert.True(edges > 0);
        Assert.Equal(credits - edges * CliffCost, sim.Credits(1));
        sim.Seconds(60);
        Assert.Equal(0, sim.Count(Names.Dig.Name));
        Assert.False(sim.Land!.IsCliffFoot(foot.X + 0.5, foot.Y + 0.5));
        Assert.True(sim.Walkable(foot.X, foot.Y));
    }

    [Fact(DisplayName = "отмена землеройной работы возвращает кредиты; без исследования — нельзя")]
    public void CancelDigRefunds()
    {
        (int X, int Y) at;
        using (var sim = Create())
        {
            sim.SetCredits(1, sim.Credits(1) + 5000);
            at = Near((x, y) => sim.Land!.PeakOf(x, y) is not null);
            var builders = Builders.AddBuilders(sim, 1, at.X + 6, at.Y, 1);
            var credits = sim.Credits(1);
            sim.Send(1, new DigOrder { Kind = "peak", Tiles = [at.X, at.Y] });
            sim.Advance();
            Assert.True(sim.Credits(1) < credits);
            sim.Send(1, new UnpaveOrder { Tiles = [at.X, at.Y], Builders = [.. builders] });
            sim.Advance();
            Assert.Equal(credits, sim.Credits(1));
            Assert.Equal(0, sim.Count(Names.Dig.Name));
        }

        // Без исследования землеройных работ гору не тронуть.
        using var closed = Create(new { techTree = true });
        Assert.False(closed.CanDig(1, "peak", at.X, at.Y));
    }
}
