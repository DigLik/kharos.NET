using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Base.Tests.Combat;
using static Kharos.Base.Tests.Combat.Arena;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Defense;

/// <summary>Щиты (packages/tests/shields.test.ts): купола, урон, снаряды, энергия, сохранение.</summary>
public sealed class ShieldsTests
{
    private static readonly global::Base.Shared.Data.ShieldSpec Shield = Tables.Buildings["shieldGenerator"].Shield!;
    private static double Radius => Shield.Radius;
    private static double Capacity => Shield.Capacity;
    private static double Regen => Shield.Regen;

    /// <summary>Пауза ремонта после попадания по умолчанию (REPAIR_PAUSE), секунды.</summary>
    private const double RepairPause = 3;

    /// <summary>На сколько секунд гаснет пробитый купол (SHIELD_DOWN).</summary>
    private const double ShieldDown = 10;

    private static readonly object Unprotected = new { protection = 0 };

    /// <summary>Генератор щита 2×2 в (x + 10, y + 9), электростанция и радар рядом; щит заряжен целиком (base).</summary>
    private static (Sim Sim, int X, int Y, uint Shield, uint Generator, uint Radar, double Cx, double Cy) Base(double? charge = null)
    {
        var (sim, x, y) = Field(36, 20, Unprotected, middle: false);
        foreach (var player in new uint[] { 1, 2, 3 })
            sim.SetCredits(player, 5000);
        var shield = Place(sim, "shieldGenerator", x + 10, y + 9, 1);
        var generator = Place(sim, "generator", x + 12, y + 9, 1);
        var radar = Place(sim, "radar", x + 8, y + 9, 1);
        sim.Set(shield, "Shield.charge", charge ?? Capacity);
        return (sim, x, y, shield, generator, radar, x + 11, y + 10);
    }

    [Fact(DisplayName = "артиллерия из-за купола бьёт в щит, а не в здания под ним")]
    public void ArtilleryHitsTheShield()
    {
        var (sim, x, y, shield, generator, radar, cx, _) = Base();
        using var _sim = sim;
        // Артиллерия снаружи купола, но на выстрел до радара.
        var gun = Spawn(sim, "artillery", 2, (int)Math.Floor(cx - Radius - 3), y + 10);
        Assert.True(Math.Abs(x + 9 - (cx - Radius - 3)) < 11);
        sim.Send(2, new AttackOrder { Units = [gun], Target = radar });
        sim.Seconds(12);
        Assert.True(sim.Charge(shield) < Capacity);
        foreach (var entity in new[] { shield, generator, radar })
            Assert.Equal(1, Health(sim, entity));
    }

    [Fact(DisplayName = "купол, пробитый в бою, гаснет на SHIELD_DOWN секунд")]
    public void BrokenDomeGoesDark()
    {
        var (sim, _, y, shield, _, radar, cx, _) = Base();
        using var _sim = sim;
        var gun = Spawn(sim, "artillery", 2, (int)Math.Floor(cx - Radius - 3), y + 10);
        sim.Send(2, new AttackOrder { Units = [gun], Target = radar });
        double Down() => sim.Num(shield, "Shield.down");
        // Как только снаряд в полёте, запаса купола остаётся на одно попадание.
        for (var i = 0; i < 12 / Tick && sim.Count(Names.Shot.Name) == 0; i++)
            sim.Advance();
        sim.Set(shield, "Shield.charge", 1);
        for (var i = 0; i < 12 / Tick && Down() <= sim.Tick; i++)
            sim.Advance();
        Assert.Equal(0, sim.Charge(shield));
        // Погас в этом тике: до подъёма — SHIELD_DOWN секунд от него; после шага тик партии уже следующий.
        Assert.Equal(ShieldDown / Tick - 1, Down() - sim.Tick);
    }

    [Fact(DisplayName = "кто зашёл под купол, бьёт мимо щита")]
    public void InsideTheDomeBypassesTheShield()
    {
        var (sim, _, y, shield, _, radar, cx, _) = Base();
        using var _sim = sim;
        var soldier = Spawn(sim, "infantry", 2, (int)Math.Floor(cx - 5), y + 13);
        sim.Send(2, new AttackOrder { Units = [soldier], Target = radar });
        sim.Seconds(4);
        Assert.True(Health(sim, radar) < 1);
        Assert.Equal(Capacity, sim.Charge(shield));
    }

    [Fact(DisplayName = "урон гасит купол своего и союзника, летающему купол не помеха; чужое под куполом не укрыто")]
    public void WhoseDamageTheDomeAbsorbs()
    {
        var (sim, _, _, shield, _, _, cx, cy) = Base();
        using var _sim = sim;
        var (outsideX, outsideY) = (cx + Radius + 2, cy);
        double Hit(uint player, double fromX, double fromY, bool fromAir = false) => sim.Absorb(player, cx + 2, cy, 50, fromX, fromY, fromAir);
        Assert.Equal(0, Hit(1, outsideX, outsideY));
        Assert.Equal(50, Hit(1, cx - 1, cy));
        Assert.Equal(0, Hit(1, cx - 1, cy, true));
        Assert.Equal(50, Hit(3, outsideX, outsideY));
        sim.Ally(1, 3);
        Assert.Equal(0, Hit(3, outsideX, outsideY));
        // Цель вне купола щит не укрывает.
        Assert.Equal(50, sim.Absorb(1, cx + Radius + 1, cy, 50, outsideX + 5, outsideY));
        Assert.Equal(Capacity - 150, sim.Charge(shield));
    }

    [Fact(DisplayName = "пробитый купол гаснет на время и потом растёт с нуля; под огнём не растёт")]
    public void BrokenDomeRegrowsFromZero()
    {
        var (sim, _, _, shield, _, _, cx, cy) = Base(30);
        using var _sim = sim;
        var outside = cx + Radius + 2;
        Assert.Equal(20, sim.Absorb(1, cx, cy, 50, outside, cy));
        Assert.Equal(0, sim.Charge(shield));
        Assert.Empty(sim.Domes());
        sim.Seconds(ShieldDown - 0.5);
        Assert.Equal(0, sim.Charge(shield));
        sim.Seconds(1.5);
        Assert.True(sim.Charge(shield) > 0);

        // Погашенное попадание останавливает рост на паузу ремонта.
        sim.Set(shield, "Shield.charge", 1000);
        sim.Absorb(1, cx, cy, 10, outside, cy);
        sim.Seconds(RepairPause - 0.5);
        Assert.Equal(990, sim.Charge(shield));
        sim.Seconds(1.5);
        Assert.True(sim.Charge(shield) > 990);
        Assert.True(sim.Charge(shield) <= 990 + Regen * 1.5 + 1e-6);
    }

    [Fact(DisplayName = "без энергии и выключенный купол гаснет")]
    public void NoPowerNoDome()
    {
        var (sim, _, _, shield, generator, _, _, _) = Base();
        using var _sim = sim;
        sim.SetWorking(1, shield, false);
        sim.Advance(2);
        Assert.Equal(0, sim.Charge(shield));
        Assert.Empty(sim.Domes());
        sim.SetWorking(1, shield, true);
        sim.Seconds(1);
        Assert.Equal(Regen, sim.Charge(shield), 0);

        sim.Erase(generator);
        sim.Seconds(0.5);
        Assert.Equal(0, sim.Charge(shield));
    }

    [Fact(DisplayName = "при нехватке энергии купол растёт медленнее — на долю энергии зоны")]
    public void ShortOfPowerGrowsSlower()
    {
        var (sim, x, y, shield, _, _, _, _) = Base(0);
        using var _sim = sim;
        // Ещё три радара: энергии электростанции на всех не хватает.
        foreach (var dx in new[] { 14, 16, 18 })
            Place(sim, "radar", x + dx, y + 9, 1);
        sim.Seconds(Tick);
        var share = sim.PowerSupply()[shield];
        Assert.True(share < 0.9);
        var before = sim.Charge(shield);
        sim.Seconds(1);
        Assert.True(Math.Abs(sim.Charge(shield) - before - Regen * share) < 0.5);
    }

    [Fact(DisplayName = "щит переживает сохранение")]
    public void ShieldSurvivesSaving()
    {
        var (sim, _, _, shield, _, _, _, _) = Base(1234);
        using var _sim = sim;
        using var loaded = sim.ThroughSave();
        Assert.Equal(1234, loaded.Charge(shield));
    }

    [Fact(DisplayName = "купол останавливает чужие снаряды на своём краю: внутрь не долетает ни снаряд, ни осколки; свои летят насквозь")]
    public void DomeStopsForeignProjectiles()
    {
        var (sim, _, y, shield, _, radar, cx, cy) = Base();
        using var _sim = sim;
        // Ракетчики снаружи бьют по радару под куполом.
        var shooters = new[] { 0, 2 }.Select(dy => Spawn(sim, "rocketeer", 2, (int)Math.Floor(cx - Radius - 3), y + 9 + dy)).ToArray();
        sim.Send(2, new AttackOrder { Units = shooters, Target = radar });
        var deepest = double.PositiveInfinity;
        var stopped = 0;
        for (var i = 0; i < Math.Round(8 / Tick); i++)
        {
            sim.Advance();
            foreach (var shot in sim.With(Names.Shot.Name, Names.Position.Name))
            {
                if (sim.Num(shot, "Shot.player") != 2 || WeaponOf(sim, shot) is not { } weapon || (Weapon(weapon).Speed ?? 0) == 0)
                    continue;
                var (px, py) = sim.At(shot);
                deepest = Math.Min(deepest, Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)));
            }
            if (sim.Charge(shield) < Capacity)
                stopped++;
        }
        Assert.True(stopped > 0);
        // Глубже края снаряд не заходит.
        Assert.True(deepest > Radius - 0.01);
        Assert.Equal(1, Health(sim, radar));

        // Свой снаряд изнутри купола наружу купол не держит: стрелок под куполом бьёт врага снаружи.
        // Под куполом, но не в генераторе: правка юнита места под зданием не даёт (в TS spawnUnit без проверок).
        var guard = Spawn(sim, "rocketeer", 1, (int)Math.Floor(cx), (int)cy + 2);
        var foe = Spawn(sim, "infantry", 3, (int)Math.Floor(cx + Radius + 2), (int)cy);
        sim.Send(1, new AttackOrder { Units = [guard], Target = foe });
        sim.Seconds(6);
        Assert.True((sim.Alive(foe) ? Health(sim, foe) : 0) < 1);
    }
}
