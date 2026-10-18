using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using Kharos.Engine.Terrain;
using static Kharos.Base.Tests.Construction.Yard;

namespace Kharos.Base.Tests.Construction;

/// <summary>Ворота (packages/tests/gates.test.ts): открыты для своих, закрываются перед чужими, секции вплотную — одни ворота.</summary>
public sealed class GatesTests
{
    private const double GateClose = 6;

    /// <summary>Стена из девяти секций с воротами посередине на ровном месте; (x, y) — тайл ворот. Защиты новичка нет.</summary>
    private static (Sim Sim, uint Gate, int X, int Y) Wall()
    {
        var sim = Create(new { techTree = false, protection = 0 });
        var land = sim.Land!;
        bool Ground(int x, int y) => land.TerrainAt(x, y) is TerrainType.Rock or TerrainType.Sand && !land.IsCliffFoot(x + 0.5, y + 0.5);
        for (var y = -150; y < 150; y++)
            for (var x = -150; x < 150; x++)
            {
                var ok = true;
                for (var dy = -12; dy <= 12 && ok; dy++)
                    for (var dx = -4; dx <= 4 && ok; dx++)
                        ok = Ground(x + dx, y + dy);
                if (!ok)
                    continue;
                for (var dy = -12; dy <= 12 && ok; dy++)
                    for (var dx = -4; dx <= 4 && ok; dx++)
                        ok = Scene.CanPlace(sim, "wall", x + dx, y + dy);
                if (!ok)
                    continue;
                for (var dx = -4; dx <= 4; dx++)
                    if (dx != 0)
                        Scene.Place(sim, "wall", x + dx, y, 1);
                return (sim, Scene.Place(sim, "gate", x, y, 1), x, y);
            }
        sim.Dispose();
        throw new InvalidOperationException("Не нашлось места под стену");
    }

    /// <summary>Открыты ли ворота (isOpenGate): Gate.open и не стройка.</summary>
    private static bool IsOpen(Sim sim, uint gate) => sim.Num(gate, "Gate.open") != 0 && !sim.Has(gate, "Site");

    [Fact(DisplayName = "ворота открыты, пока рядом нет чужих: свои проезжают, а соседняя секция стены — нет")]
    public void OpenForOwn()
    {
        var (sim, gate, x, y) = Wall();
        using var _ = sim;
        sim.Seconds(0.5);
        Assert.True(IsOpen(sim, gate));
        Assert.True(sim.Walkable(x, y));
        Assert.False(sim.Walkable(x + 1, y));
        var buggy = Scene.Spawn(sim, "buggy", 1, x, y - 5);
        sim.Send(1, new MoveOrder { Units = [buggy], X = x, Y = y + 5 });
        sim.Seconds(10);
        Assert.True(sim.At(buggy).Y > y + 3);
    }

    [Fact(DisplayName = "ворота закрываются перед чужим наземным юнитом и открываются, когда он ушёл; летающий им безразличен")]
    public void CloseBeforeForeign()
    {
        var (sim, gate, x, y) = Wall();
        using var _ = sim;
        sim.Seconds(0.5);
        var drone = Scene.Spawn(sim, "drone", 2, x, y + 3);
        sim.Seconds(0.5);
        Assert.True(IsOpen(sim, gate));
        sim.Erase(drone);

        var foe = Scene.Spawn(sim, "truck", 2, x, y + (int)GateClose + 4);
        sim.Seconds(0.5);
        Assert.True(IsOpen(sim, gate));
        // Чужой грузовик едет в ворота — они закрываются раньше, чем он доедет, и становятся стеной для всех.
        sim.Send(2, new MoveOrder { Units = [foe], X = x, Y = y + 2 });
        sim.Seconds(6);
        Assert.Equal(0, sim.Num(gate, "Gate.open"));
        Assert.False(sim.Walkable(x, y));
        Assert.True(sim.At(foe).Y > y + 1);
        sim.Erase(foe);
        sim.Seconds(0.5);
        Assert.True(IsOpen(sim, gate));
    }

    [Fact(DisplayName = "пока в проёме кто-то стоит, ворота не закрываются")]
    public void HeldOpen()
    {
        var (sim, gate, x, y) = Wall();
        using var _ = sim;
        sim.Seconds(0.5);
        var own = Scene.Spawn(sim, "truck", 1, x, y);
        Scene.Spawn(sim, "truck", 2, x, y + 4);
        sim.Seconds(1);
        Assert.True(IsOpen(sim, gate));
        sim.Erase(own);
        sim.Seconds(0.5);
        Assert.False(IsOpen(sim, gate));
    }

    [Fact(DisplayName = "ворота тянутся линией, и секции вплотную — одни ворота: чужой у края поднимает все, кроме занятой")]
    public void GateLine()
    {
        var (sim, _, x, y) = Wall();
        using var _sim = sim;
        // Ещё ряд, отдельно от стены: пять секций протяжкой.
        var row = y - 8;
        int[] tiles = [.. Enumerable.Range(0, 5).SelectMany(dx => (int[])[x + dx, row])];
        Assert.All(sim.WallStroke(1, tiles, gate: true), Assert.True);
        var credits = sim.FreeCredits(1);
        sim.SetCredits(1, sim.Credits(1) + 1000);
        sim.Send(1, new WallsOrder { Tiles = tiles, Gate = true });
        sim.Advance();
        Assert.Equal(5, SitesOf(sim, "gate").Count);
        Assert.Equal(credits + 1000 - 5 * Tables.Buildings["gate"].Cost, sim.FreeCredits(1), 0);

        // Готовые ворота шириной в пять тайлов.
        var wide = Enumerable.Range(0, 5).Select(dx => Scene.Place(sim, "gate", x + dx, row + 4, 1)).ToList();
        sim.Seconds(0.5);
        Assert.All(wide, gate => Assert.True(IsOpen(sim, gate)));
        var own = Scene.Spawn(sim, "truck", 1, x + 4, row + 4);
        // Чужой — у левого края, до правого ему дальше порога: одна секция его бы не заметила.
        Scene.Spawn(sim, "truck", 2, x - 5, row + 4);
        sim.Seconds(0.5);
        Assert.Equal([false, false, false, false, true], wide.Select(gate => IsOpen(sim, gate)));
        sim.Erase(own);
        sim.Seconds(0.5);
        Assert.DoesNotContain(wide, gate => IsOpen(sim, gate));
    }
}
