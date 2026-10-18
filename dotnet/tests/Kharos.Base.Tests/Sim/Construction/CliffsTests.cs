using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Construction;

/// <summary>
/// Обрывы и правила места (packages/tests/cliffs.test.ts, части canPlace, canDeploy и canPave; ход — в
/// Kharos.Engine.Tests/Movement/CliffsTests). Местность красится правками edit.paint.
/// </summary>
public sealed class CliffsTests
{
    /// <summary>Карта 256² (полусторона 128) с ровной скалой первого яруса 60×60 вокруг начала.</summary>
    private static Sim World()
    {
        var sim = Yard.Create(size: 128);
        Flat(sim, -30, -30, 60);
        return sim;
    }

    /// <summary>Квадрат side×side от (left, top) одной кистью.</summary>
    private static void Square(Sim sim, int left, int top, int side, int tier, bool cliff) =>
        sim.Paint(left + side / 2, top + side / 2, side, new Brush { Terrain = 2, Biome = 0, Tier = tier, Cliff = cliff, Dunes = 0, Shape = "square" });

    /// <summary>Ровная скала первого яруса.</summary>
    private static void Flat(Sim sim, int left, int top, int side) => Square(sim, left, top, side, 1, false);

    /// <summary>Плато второго яруса с обрывом по всему краю.</summary>
    private static void Mesa(Sim sim, int left, int top, int side) => Square(sim, left, top, side, 2, true);

    [Fact(DisplayName = "здание и разворачивание MCV — по одному правилу: не у подножия обрыва, а через пологий въезд — можно на два яруса")]
    public void Placement()
    {
        using var sim = World();
        Mesa(sim, 0, 0, 10);
        // Подножие под плато — ряд y = 10.
        Assert.False(Scene.CanPlace(sim, "generator", 4, 10));
        Assert.True(Scene.CanPlace(sim, "generator", 4, 11));
        // На самом плато у края — можно: стенка ниже.
        Assert.True(Scene.CanPlace(sim, "generator", 4, 8));

        // Главное здание встаёт вокруг MCV: над подножием — нельзя, в стороне — можно.
        var blocked = Scene.Spawn(sim, "mcv", 1, 5, 11);
        Assert.Equal(Scene.CanPlace(sim, "command", 4, 10), Scene.CanDeploy(sim, 1, blocked));
        Assert.False(Scene.CanDeploy(sim, 1, blocked));
        var open = Scene.Spawn(sim, "mcv", 1, -15, -15);
        Assert.True(Scene.CanDeploy(sim, 1, open));

        // Пологий въезд: второй ярус без обрыва — здание встаёт поперёк перепада.
        Square(sim, 0, 20, 6, 2, false);
        Assert.True(Scene.CanPlace(sim, "generator", 5, 22));
    }

    [Fact(DisplayName = "фундамент и дорогу у подножия обрыва не положить, рядом — можно")]
    public void Paving()
    {
        using var sim = World();
        Mesa(sim, 0, 0, 10);
        foreach (var kind in PaveRules.Kinds)
        {
            Assert.False(sim.CanPave(1, kind, 5, 10), kind);
            Assert.True(sim.CanPave(1, kind, 5, 11), kind);
            Assert.True(sim.CanPave(1, kind, 5, 9), kind);
        }
    }
}
