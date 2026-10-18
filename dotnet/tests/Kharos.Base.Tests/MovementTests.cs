using Base.Host;
using MemoryPack;
using KHost = Kharos.Host.Host;

namespace Kharos.Base.Tests;

/// <summary>Приказы юнитам, ходовая «колёса», теснота и турели на носителях (mechanics/orders, moves/wheels, crowd, mounts).</summary>
public class MovementTests
{
    [Fact]
    public void SpiralPlacesStartInCenterAndWalkOutward()
    {
        var places = Geom.Spread(10, 100, 50, 1.2);
        Assert.Equal((100.0, 50.0), places[0]);
        // Первое кольцо — восемь мест вокруг центра, обход с (0, −1) по часовой стрелке (y вниз).
        (int Dx, int Dy)[] ring = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];
        for (var k = 0; k < 8; k++)
            Assert.Equal((100 + ring[k].Dx * 1.2, 50 + ring[k].Dy * 1.2), places[k + 1]);
        // Следующее кольцо начинается с (−1, −2).
        Assert.Equal((100 - 1.2, 50 - 2 * 1.2), places[9]);
        Assert.Equal(25, Geom.Spread(25, 0, 0, 1).Distinct().Count());
    }

    [Fact]
    public void MoveOrderSpreadsGroupAroundPointNearestUnitTakesNearestPlace()
    {
        using var host = BaseGame.Started(1, 2);
        var mine = BaseGame.Units(host, 1);
        var foreign = BaseGame.Units(host, 2)[0];
        var factory = BaseGame.Factory(host, 1);
        var signals = new List<(string Name, byte[] Payload)>();
        // Чужой юнит, здание без хода, мёртвая сущность и повтор не в счёт: места делятся между тремя своими.
        BaseGame.Move(host, 1, 30, -12, [.. mine, foreign, factory, 99999]);
        BaseGame.Step(host, signals);
        (double, double) Target(uint e) => (BaseGame.Num(host, e, "Drive", "tx"), BaseGame.Num(host, e, "Drive", "ty"));
        // Места: центр, затем (0, −1) и (1, −1) от центра с шагом 1,2. Ближние к точке юниты — правые.
        Assert.Equal((30.0, -12.0), Target(mine[2]));
        Assert.Equal((30.0, -12.0 - 1.2), Target(mine[1]));
        Assert.Equal((30.0 + 1.2, -12.0 - 1.2), Target(mine[0]));
        Assert.All(mine, e => Assert.Equal(1, BaseGame.Num(host, e, "Drive", "moving")));
        Assert.Equal(0, BaseGame.Num(host, foreign, "Drive", "moving"));
        var moved = MemoryPackSerializer.Deserialize<MovedSignal>(Assert.Single(signals, s => s.Name == "moved").Payload)!;
        Assert.Equal((1u, 30.0, -12.0), (moved.Player, moved.X, moved.Y));

        // Едут, не наезжая друг на друга, и встают на свои места.
        var closest = double.MaxValue;
        for (var tick = 0; tick < 400 && mine.Any(e => BaseGame.Num(host, e, "Drive", "moving") != 0); tick++)
        {
            BaseGame.Step(host);
            for (var a = 0; a < 3; a++)
                for (var b = a + 1; b < 3; b++)
                    closest = Math.Min(closest, BaseGame.Distance(BaseGame.At(host, mine[a]), BaseGame.At(host, mine[b])));
        }
        Assert.All(mine, e => Assert.Equal(0, BaseGame.Num(host, e, "Drive", "moving")));
        // Прибывший встаёт ровно в точку, но стоящего расталкивают подъезжающие (он уступает втрое охотнее): около места.
        Assert.All(mine, e => Assert.True(BaseGame.Distance(BaseGame.At(host, e), Target(e)) < 0.6, $"юнит {e} не на месте"));
        for (var a = 0; a < 3; a++)
            for (var b = a + 1; b < 3; b++)
                Assert.True(BaseGame.Distance(BaseGame.At(host, mine[a]), BaseGame.At(host, mine[b])) >= 0.85);
        // На ходу корпуса могут слегка заходить друг на друга: расталкивание снимает лишь половину перекрытия за тик.
        Assert.True(closest >= 0.6, $"корпуса сошлись до {closest}");
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void WheelsMatchPilotKinematics()
    {
        using var host = BaseGame.Started(1);
        var unit = BaseGame.Units(host, 1)[2];
        var (x, y) = BaseGame.At(host, unit);
        var face = BaseGame.Angle(host, unit);
        BaseGame.Move(host, 1, 30, -12, unit);
        const double speed = 6, turn = 5;
        var arrived = -1;
        for (var tick = 1; tick <= 200; tick++)
        {
            BaseGame.Step(host);
            // Эталон — ходовая из moves/wheels.luau, написанная заново.
            if (arrived < 0)
            {
                double dx = 30 - x, dy = -12 - y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var step = speed * BaseGame.Dt;
                if (distance <= step)
                {
                    (x, y) = (x + dx, y + dy);
                    arrived = tick;
                }
                else
                {
                    var limit = turn * BaseGame.Dt;
                    var off = Wrap(Math.Atan2(dy, dx) - face);
                    face += Math.Abs(off) <= limit ? off : off > 0 ? limit : -limit;
                    face = Wrap(face);
                    var ahead = Math.Cos(off);
                    if (ahead > 0)
                        (x, y) = (x + Math.Cos(face) * step * ahead, y + Math.Sin(face) * step * ahead);
                }
            }
            var (ax, ay) = BaseGame.At(host, unit);
            Assert.True(Math.Abs(ax - x) < 1e-9 && Math.Abs(ay - y) < 1e-9, $"тик {tick}: ({ax}, {ay}) против ({x}, {y})");
            Assert.True(Math.Abs(BaseGame.Angle(host, unit) - face) < 1e-9, $"тик {tick}: угол");
        }
        Assert.True(arrived is > 0 and < 200, $"доехал на тике {arrived}");
        Assert.Equal((30.0, -12.0), BaseGame.At(host, unit));
        Assert.Equal(0, BaseGame.Num(host, unit, "Drive", "moving"));
        Assert.Empty(host.Errors);
    }

    /// <summary>Угол в (−π, π] — независимо от Geom.Wrap мода.</summary>
    private static double Wrap(double angle) => angle - 2 * Math.PI * Math.Floor(angle / (2 * Math.PI) + 0.5);

    [Fact]
    public void StopHaltsUnitsWhereTheyAre()
    {
        using var host = BaseGame.Started(1, 2);
        var mine = BaseGame.Units(host, 1);
        var foreign = BaseGame.Units(host, 2)[0];
        BaseGame.Move(host, 1, 50, 50, mine[0]);
        BaseGame.Move(host, 2, 70, 70, foreign);
        BaseGame.Run(host, 10);
        BaseGame.Stop(host, 1, mine[0], foreign);
        BaseGame.Step(host);
        var at = BaseGame.At(host, mine[0]);
        Assert.Equal(0, BaseGame.Num(host, mine[0], "Drive", "moving"));
        // Чужому стоять не приказать.
        Assert.Equal(1, BaseGame.Num(host, foreign, "Drive", "moving"));
        BaseGame.Run(host, 10);
        Assert.Equal(at, BaseGame.At(host, mine[0]));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void CrowdPushesOverlappingBodiesApart()
    {
        using var host = BaseGame.Started(1);
        var units = BaseGame.Units(host, 1);
        foreach (var unit in units)
            BaseGame.SetPosition(host, unit, 60, 60);
        BaseGame.SetPosition(host, units[1], 60.1, 60);
        BaseGame.SetPosition(host, units[2], 60, 60.2);
        BaseGame.Run(host, 20);
        for (var a = 0; a < 3; a++)
            for (var b = a + 1; b < 3; b++)
                Assert.True(BaseGame.Distance(BaseGame.At(host, units[a]), BaseGame.At(host, units[b])) >= 0.89, $"{a}–{b}");
        // Дальние друг от друга не трогаются: фабрика (без Unit) на месте.
        Assert.Equal((0.0, 0.0), BaseGame.At(host, BaseGame.Factory(host, 1)));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void MountedTurretsFollowCarrierAndDieWithIt()
    {
        using var host = BaseGame.Started(1);
        var units = BaseGame.Units(host, 1);
        BaseGame.Move(host, 1, -40, 30, units[0], units[1]);
        for (var tick = 0; tick < 60; tick++)
        {
            BaseGame.Step(host);
            foreach (var unit in units)
            {
                var turret = Assert.Single(BaseGame.Turrets(host), t => BaseGame.Num(host, t, "Attached", "parent") == unit);
                var (cx, cy) = BaseGame.At(host, unit);
                var angle = BaseGame.Angle(host, unit);
                var (tx, ty) = BaseGame.At(host, turret);
                // along −0,16, across 0: позади центра по оси носителя; смотрит куда он (Turret.angle 0).
                Assert.True(Math.Abs(tx - (cx - 0.16 * Math.Cos(angle))) < 1e-12 && Math.Abs(ty - (cy - 0.16 * Math.Sin(angle))) < 1e-12, $"тик {tick}");
                Assert.Equal(angle, BaseGame.Angle(host, turret));
            }
        }
        // Носитель на повороте: турель смотрит туда же, а не вперёд по миру.
        Assert.NotEqual(Math.PI / 2, BaseGame.Angle(host, units[0]));

        host.World.Destroy(units[1]);
        BaseGame.Step(host);
        Assert.DoesNotContain(BaseGame.Turrets(host), t => BaseGame.Num(host, t, "Attached", "parent") == units[1]);
        Assert.Equal(2, BaseGame.Turrets(host).Count);
        Assert.Empty(host.Errors);
    }
}
