using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Combat;
using static Kharos.Base.Tests.Tactics.Strip;

namespace Kharos.Base.Tests.Tactics;

/// <summary>Стойки, патруль, поход, возвращение на место (packages/tests/tactics.test.ts).</summary>
public sealed class TacticsTests
{
    [Fact(DisplayName = "«не стрелять»: юнит не открывает огонь и не отвечает, но приказ атаки выполняет")]
    public void PassiveHoldsFire()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "infantry", 1, x + 5, y);
        var foe = Put(sim, "infantry", 2, x + 8, y);
        Stance(sim, 1, "passive", ours);
        // Враг тоже молчит: смотрим только, что наш никого не трогает сам.
        Stance(sim, 2, "passive", foe);
        sim.Seconds(3);
        Assert.False(Hurt(sim, foe));
        sim.Send(1, new AttackOrder { Units = [ours], Target = foe });
        sim.Seconds(3);
        Assert.True(Hurt(sim, foe));
    }

    [Fact(DisplayName = "«держать позицию»: под огнём издалека юнит с места не сходит; в обороне — идёт на стрелка")]
    public void HoldStaysPut()
    {
        double Run(string stance)
        {
            var (sim, x, y) = Field();
            using var _sim = sim;
            var ours = Put(sim, "infantry", 1, x + 5, y);
            // Ракетчик бьёт дальше пехотинца: тот его с места не достаёт.
            var foe = Put(sim, "rocketeer", 2, x + 11, y);
            Stance(sim, 1, stance, ours);
            Stance(sim, 2, "hold", foe);
            sim.Send(2, new AttackOrder { Units = [foe], Target = ours });
            sim.Seconds(3);
            return sim.Alive(ours) ? sim.At(ours).X - (x + 5.5) : double.PositiveInfinity;
        }
        Assert.True(Math.Abs(Run("hold")) < 0.5);
        Assert.True(Run("defensive") > 1);
    }

    [Fact(DisplayName = "«агрессивно»: юнит идёт на врага, которого видит, но не достаёт; в обороне — нет")]
    public void AggressiveWalksToSeenEnemy()
    {
        bool Run(string stance)
        {
            var (sim, x, y) = Field();
            using var _sim = sim;
            var ours = Put(sim, "infantry", 1, x + 5, y);
            var foe = Put(sim, "infantry", 2, x + 11, y);
            Stance(sim, 1, stance, ours);
            Stance(sim, 2, "passive", foe);
            sim.Seconds(4);
            return Hurt(sim, foe);
        }
        Assert.True(Run("aggressive"));
        Assert.False(Run("defensive"));
    }

    [Fact(DisplayName = "в обороне юнит не гонится дальше поводка и возвращается на место")]
    public void DefensiveStopsAtLeash()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "lancer", 1, x + 3, y);
        var foe = Put(sim, "buggy", 2, x + 12, y);
        Stance(sim, 2, "passive", foe);
        // Враг ранит нашего и уезжает далеко: наш погонится, бросит у края поводка и вернётся.
        sim.Set(ours, "Armed.target", foe);
        sim.Set(ours, "Armed.chase", 1);
        Away(sim, ours, x + 3, y);
        Move(sim, 2, [foe], x + 38, y);
        var farthest = 0.0;
        for (var i = 0; i < 20 / Tick; i++)
        {
            sim.Advance();
            farthest = Math.Max(farthest, sim.At(ours).X - (x + 3.5));
        }
        // Погнался, но погоню бросил у поводка: дальше — только доехал начатый путь.
        Assert.True(farthest > Leash, $"{farthest}");
        Assert.True(farthest < Leash * 2, $"{farthest}");
        Assert.True(Math.Abs(sim.At(ours).X - (x + 3.5)) < 1.5);
        Assert.Equal(0u, sim.TargetOf(ours));
    }

    [Fact(DisplayName = "патруль: юнит ходит туда и обратно, а приказ идти его снимает")]
    public void PatrolWalksBackAndForth()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "buggy", 1, x + 2, y);
        Patrol(sim, 1, [ours], [x + 20, y]);
        bool far = false, back = false;
        for (var i = 0; i < 60 / Tick; i++)
        {
            sim.Advance();
            var px = sim.At(ours).X;
            if (px > x + 19)
                far = true;
            if (far && px < x + 3)
                back = true;
        }
        Assert.True(far && back);
        Move(sim, 1, [ours], x + 10, y);
        sim.Seconds(1);
        Assert.Empty(sim.PatrolOf(ours));
        sim.Seconds(10);
        Assert.False(sim.Has(ours, Names.Path.Name));
    }

    [Fact(DisplayName = "стойки работают и на юнитах с турелями: багги «не стрелять» молчит, «агрессивно» — едет на врага")]
    public void StancesWorkOnTurretUnits()
    {
        bool Run(string stance)
        {
            var (sim, x, y) = Field();
            using var _sim = sim;
            var ours = Put(sim, "buggy", 1, x + 5, y);
            // Пулемётчик багги бьёт на 5 тайлов, а видит багги дальше: враг на 7 — в обзоре, но не на выстреле.
            var foe = Put(sim, "infantry", 2, x + (stance == "passive" ? 8 : 12), y);
            Stance(sim, 1, stance, ours);
            Stance(sim, 2, "passive", foe);
            sim.Seconds(5);
            return Hurt(sim, foe);
        }
        Assert.False(Run("passive"));
        Assert.True(Run("aggressive"));
        Assert.False(Run("defensive"));
    }

    [Fact(DisplayName = "«держать позицию» на юните с турелью: под огнём издалека багги с места не сходит")]
    public void HoldOnTurretUnit()
    {
        double Run(string stance)
        {
            var (sim, x, y) = Field();
            using var _sim = sim;
            var ours = Put(sim, "buggy", 1, x + 5, y);
            var foe = Put(sim, "rocketeer", 2, x + 11.5, (double)y);
            Stance(sim, 1, stance, ours);
            Stance(sim, 2, "hold", foe);
            sim.Send(2, new AttackOrder { Units = [foe], Target = ours });
            sim.Seconds(3);
            return sim.Alive(ours) ? sim.At(ours).X - (x + 5.5) : double.PositiveInfinity;
        }
        var (held, moved) = (Run("hold"), Run("defensive"));
        Assert.True(Math.Abs(held) < 0.5, $"hold {held}");
        Assert.True(moved > 1, $"defensive {moved}");
    }

    [Fact(DisplayName = "Shift к патрулю: точка добавляется к идущему патрулю, без Shift — патруль новый")]
    public void ShiftAppendsToPatrol()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "buggy", 1, x + 2, y);
        Patrol(sim, 1, [ours], [x + 10, y]);
        sim.Advance();
        Patrol(sim, 1, [ours], [x + 20, y + 2], append: true);
        sim.Advance();
        Assert.Equal([x + 2, y, x + 10, y, x + 20, y + 2], sim.PatrolOf(ours));
        sim.Seconds(2);
        Patrol(sim, 1, [ours], [x + 30, y]);
        sim.Advance();
        Assert.Equal([x + 30, y], sim.PatrolOf(ours).Skip(2));
    }

    [Fact(DisplayName = "поход: агрессивный, посланный идти, бьёт увиденного по дороге и идёт дальше, куда послали")]
    public void MarchFightsAndGoesOn()
    {
        foreach (var type in new[] { "buggy", "infantry" })
        {
            var (sim, x, y) = Field();
            using var _sim = sim;
            var ours = Put(sim, type, 1, x + 2, y);
            // Враг в стороне от пути: в обзоре, но не на выстреле. Вооружён, но ему велено не стрелять.
            var foe = Put(sim, "infantry", 2, x + 12, y + 3);
            Stance(sim, 2, "passive", foe);
            Stance(sim, 1, "aggressive", ours);
            Move(sim, 1, [ours], x + 22, y);
            for (var i = 0; i < 60 * 20 && sim.Alive(foe); i++)
                sim.Advance();
            Assert.False(sim.Alive(foe), type);
            // Бой кончился не там, куда шёл: юнит вспоминает о месте назначения и доходит.
            sim.Seconds(30);
            Assert.True(Distance(sim, ours, x + 22.5, y + 0.5) < 1.6, type);
            Assert.False(IsAway(sim, ours), type);
            Assert.False(sim.Has(ours, Names.Path.Name), type);
        }
    }

    [Fact(DisplayName = "поход: безобидное приказу идти не помеха — и тому, кто его уже разбирает, тоже")]
    public void HarmlessDoesNotStopMarch()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "buggy", 1, x + 2, y);
        var truck = Put(sim, "truck", 2, x + 6, y + 2);
        Stance(sim, 1, "aggressive", ours);
        // Стоя на месте, агрессивный берётся и за безобидного.
        sim.Seconds(1);
        Assert.True(Hurt(sim, truck));
        Assert.True(sim.Alive(truck));
        // Приказ идти — бросает его и уходит, хотя тот всё ещё на виду.
        Move(sim, 1, [ours], x + 22, y);
        // Пули, выпущенные до приказа, ещё долетают.
        sim.Seconds(0.5);
        var left = sim.HealthOf(truck);
        sim.Seconds(30);
        Assert.Equal(left, sim.HealthOf(truck));
        Assert.True(Distance(sim, ours, x + 22.5, y + 0.5) < 1.6);
    }

    [Fact(DisplayName = "опасный важнее безобидного: стрелок бросает безобидную цель, которую выбрал сам, ради того, кто может навредить")]
    public void DangerousBeatsHarmless()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "tank", 1, x + 2, y);
        var truck = Put(sim, "truck", 2, x + 5, y);
        sim.Seconds(1);
        Assert.Equal(truck, sim.TargetOf(sim.TurretsOf(ours)[0]));
        // Пехотинец дальше грузовика, но он вооружён.
        var foe = Put(sim, "infantry", 2, x + 7, y + 2);
        Stance(sim, 2, "passive", foe);
        sim.Seconds(1);
        Assert.Equal(foe, sim.TargetOf(sim.TurretsOf(ours)[0]));
    }

    [Fact(DisplayName = "поход — только в агрессивной стойке и только по приказу идти: в обороне и с приказом атаки на других не смотрят")]
    public void MarchOnlyWhenAggressiveAndMoving()
    {
        {
            // С приказом атаки агрессивный едет к своей цели мимо прочих.
            var (sim, x, y) = Field();
            using var _sim = sim;
            var hunter = Put(sim, "buggy", 1, x + 2, y + 2);
            var bystander = Put(sim, "truck", 2, x + 12, y - 3);
            var prey = Put(sim, "truck", 2, x + 22, y + 2);
            Stance(sim, 1, "aggressive", hunter);
            sim.Send(1, new AttackOrder { Units = [hunter], Target = prey });
            for (var i = 0; i < 60 * 20 && sim.Alive(prey); i++)
                sim.Advance();
            Assert.False(sim.Alive(prey));
            Assert.False(Hurt(sim, bystander));
        }
        {
            // В обороне приказ идти — просто идти: врага в обзоре, но не на выстреле, юнит не трогает.
            var (sim, x, y) = Field();
            using var _sim = sim;
            var calm = Put(sim, "infantry", 1, x + 2, y - 3);
            var bystander = Put(sim, "truck", 2, x + 12, y + 3);
            Move(sim, 1, [calm], x + 22, y - 3);
            sim.Seconds(40);
            Assert.Equal(x + 22, (int)Math.Floor(sim.At(calm).X));
            Assert.False(Hurt(sim, bystander));
            Assert.False(IsAway(sim, calm));
        }
    }

    [Fact(DisplayName = "приказ с Shift ждёт, пока агрессивный не дойдёт, куда шёл: бой по дороге — не конец приказа")]
    public void ShiftWaitsForMarchEnd()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "buggy", 1, x + 2, y);
        var foe = Put(sim, "truck", 2, x + 12, y + 3);
        Stance(sim, 1, "aggressive", ours);
        Move(sim, 1, [ours], x + 22, y);
        Move(sim, 1, [ours], x + 22, y + 3, queue: true);
        var reached = false;
        for (var i = 0; i < 90 * 20; i++)
        {
            sim.Advance();
            reached |= Distance(sim, ours, x + 22.5, y + 0.5) < 1.6;
            // Следующий приказ из очереди не берётся, пока не дошёл до первой точки.
            if (!reached)
                Assert.Equal(1, sim.QueueLength(ours));
        }
        Assert.False(sim.Alive(foe));
        Assert.True(reached);
        Assert.Equal(y + 3, (int)Math.Floor(sim.At(ours).Y));
    }

    [Fact(DisplayName = "точка патруля на здании засчитывается, когда ближе не подойти: патруль идёт дальше, путь не ищется без конца")]
    public void PatrolPointOnBuildingCounts()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var generator = Arena.Place(sim, "generator", x + 20, y - 1, 1);
        uint[] units = [.. Enumerable.Range(0, 5).Select(i => Put(sim, "infantry", 1, x + i, y))];
        Patrol(sim, 1, units, [x + 21, y]);
        sim.Seconds(10);
        var search = sim.Host.Tools.Navigation!.Search;
        var before = search.Searched;
        var legs = new HashSet<double>();
        for (var i = 0; i < 30 * 20; i++)
        {
            sim.Advance();
            legs.Add(sim.TacticsOf(units[0]).Leg);
        }
        Assert.True(sim.Alive(generator));
        // Ходит туда и обратно.
        Assert.Equal(2, legs.Count);
        Assert.True((search.Searched - before) / (30 * 20) < 1000);
    }

    [Fact(DisplayName = "ушедший в погоню вставший без цели за полсекунды идёт на своё место и, придя, забывает о нём")]
    public void AwayUnitReturnsHome()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        // Каждый в четырёх тайлах от своего места.
        uint[] units = [.. Enumerable.Range(0, 5).Select(i => Put(sim, "infantry", 1, x + 2 + i * 3, y + 2))];
        for (var i = 0; i < units.Length; i++)
            Away(sim, units[i], x + 2 + i * 3, y - 2);
        sim.Seconds(0.55);
        foreach (var entity in units)
            Assert.True(sim.Has(entity, Names.Path.Name));
        sim.Seconds(8);
        for (var i = 0; i < units.Length; i++)
        {
            Assert.True(Distance(sim, units[i], x + 2.5 + i * 3, y - 1.5) < 0.6);
            Assert.False(IsAway(sim, units[i]));
        }
    }

    [Fact(DisplayName = "к месту за стенами юнит подходит, сколько можно, и забывает о нём")]
    public void HomeBehindWalls()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        // Место — внутри кольца стен.
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == 2)
                    Arena.Place(sim, "wall", x + 20 + dx, y + dy, 1);
        var ours = Put(sim, "infantry", 1, x + 5, y);
        Away(sim, ours, x + 20, y);
        sim.Seconds(10);
        Assert.False(IsAway(sim, ours));
        Assert.False(sim.Has(ours, Names.Path.Name));
        Assert.True(sim.At(ours).X < x + 18);
    }

    [Fact(DisplayName = "патрульный, встретив врага, встаёт и бьёт его, а не идёт по обходу дальше")]
    public void PatrolStopsToFight()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "infantry", 1, x + 2, y);
        Patrol(sim, 1, [ours], [x + 36, y]);
        var foe = Put(sim, "tank", 2, x + 12, y + 3);
        Stance(sim, 2, "passive", foe);
        var fought = false;
        var far = 0.0;
        for (var i = 0; i < 20 / Tick && sim.Alive(foe); i++)
        {
            sim.Advance();
            if (sim.TargetOf(ours) == foe)
                fought = true;
            if (fought && sim.Alive(foe))
                far = Math.Max(far, sim.At(ours).X - x);
        }
        Assert.True(fought);
        // Встал там, откуда достаёт: пока цель жива, патруль ждёт.
        Assert.True(far < 10, $"{far}");
    }

    [Fact(DisplayName = "патрульный под огнём издалека отвечает стрелку, хоть и идёт по обходу")]
    public void PatrolAnswersFire()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var ours = Put(sim, "infantry", 1, x + 2, y + 3);
        Patrol(sim, 1, [ours], [x + 36, y + 3]);
        // Ракетчик бьёт дальше, чем пехотинец видит с места: без ответа на огонь тот прошёл бы мимо.
        var foe = Put(sim, "rocketeer", 2, x + 18, y - 3);
        Stance(sim, 2, "hold", foe);
        var answered = false;
        for (var i = 0; i < 10 / Tick && sim.Alive(ours); i++)
        {
            sim.Advance();
            if (sim.TargetOf(ours) == foe)
                answered = true;
        }
        Assert.True(answered);
    }
}
