using Base.Shared;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Combat;

/// <summary>Ремонтная станция (packages/tests/repairStation.test.ts): чинит своё в радиусе на энергии зоны, пехоте помогает поправляться.</summary>
public sealed class RepairStationTests
{
    private const double InfantryRegen = 0.02, StationRegen = 4;

    /// <summary>Станция 3×3 в (x + 6, y + 4) и электростанция рядом с ней, в одной зоне (base).</summary>
    private static (Sim Sim, int X, int Y, uint Station, double Cx, double Cy) Base(bool powered = true)
    {
        var (sim, x, y) = Field(24, 14, middle: false);
        sim.SetCredits(1, 5000);
        var station = Place(sim, "repairStation", x + 6, y + 4, 1);
        if (powered)
            Place(sim, "generator", x + 9, y + 4, 1);
        return (sim, x, y, station, x + 7.5, y + 5.5);
    }

    private static void Damage(Sim sim, uint entity, double to = 0.5) => sim.Set(entity, "Health.value", to);

    [Fact(DisplayName = "станция чинит свою технику в радиусе за кредиты; дальнюю и чужую не чинит")]
    public void StationRepairsOwnInRange()
    {
        var (sim, _, _, station, cx, cy) = Base();
        using var _sim = sim;
        var radius = Kernel.Data.Buildings["repairStation"].Repair!.Value;
        var near = Spawn(sim, "tank", 1, (int)Math.Floor(cx - radius + 1), (int)Math.Floor(cy));
        var far = Spawn(sim, "tank", 1, (int)Math.Floor(cx - radius - 4), (int)Math.Floor(cy));
        Damage(sim, near);
        Damage(sim, far);
        var credits = sim.Credits(1);

        sim.Seconds(1);
        Assert.Equal([station], CombatTests.Repairing(sim));
        Assert.True(Health(sim, near) > 0.5);
        Assert.True(sim.Credits(1) < credits);
        sim.Seconds(40);
        Assert.Equal(1, Health(sim, near));
        Assert.Equal(0.5, Health(sim, far));

        // Чужого рядом не чинит; своих вооружённых рядом нет, чтобы его не расстреляли.
        var other = Base();
        using var _other = other.Sim;
        var foe = Spawn(other.Sim, "tank", 2, (int)Math.Floor(other.Cx - 3), (int)Math.Floor(other.Cy));
        Damage(other.Sim, foe);
        other.Sim.Seconds(2);
        Assert.Equal(0.5, Health(other.Sim, foe));
    }

    [Fact(DisplayName = "станция чинит и здания рядом, но не строит и не чинит саму себя")]
    public void StationRepairsBuildingsNotItself()
    {
        var (sim, x, y, station, _, _) = Base();
        using var _sim = sim;
        var radar = Place(sim, "radar", x + 2, y + 4, 1);
        Damage(sim, radar);
        Damage(sim, station);
        // Площадка рядом со станцией: её станция не берёт, даже когда больше чинить нечего.
        var site = sim.World.Spawn();
        sim.Add(site, Names.Position.Name);
        sim.Set(site, "Position.x", x + 5);
        sim.Set(site, "Position.y", y + 9);
        sim.Add(site, Names.Site.Name);
        sim.Add(site, Names.Owner.Name);
        sim.Set(site, "Owner.player", 1);
        sim.Add(site, Names.Proto.Name);
        sim.Set(site, "Proto.id", Kernel.Scene.Proto(sim, "buildings/pipe"));
        sim.Add(site, Names.Footprint.Name);
        sim.Set(site, "Footprint.w", 1);
        sim.Set(site, "Footprint.h", 1);
        sim.Seconds(3);
        Assert.True(Health(sim, radar) > 0.5);
        Assert.Equal(0.5, Health(sim, station));
        sim.Seconds(30);
        Assert.Equal(1, Health(sim, radar));
        Assert.DoesNotContain(station, sim.With(Names.Repair.Name).Where(entity => sim.Num(entity, "Repair.target") == site));
        Assert.Equal(0, sim.Num(site, "Site.progress"));
    }

    [Fact(DisplayName = "без энергии и выключенная станция не чинит")]
    public void UnpoweredStationIdles()
    {
        var unpowered = Base(false);
        using (unpowered.Sim)
        {
            var tank = Spawn(unpowered.Sim, "tank", 1, (int)Math.Floor(unpowered.Cx - 3), (int)Math.Floor(unpowered.Cy));
            Damage(unpowered.Sim, tank);
            unpowered.Sim.Seconds(2);
            Assert.Equal(0.5, Health(unpowered.Sim, tank));
        }

        var (sim, _, _, station, cx, cy) = Base();
        using var _sim = sim;
        var other = Spawn(sim, "tank", 1, (int)Math.Floor(cx - 3), (int)Math.Floor(cy));
        sim.Send(1, new WorkOrder { Building = station, On = false });
        Damage(sim, other);
        sim.Seconds(2);
        Assert.Equal(0.5, Health(sim, other));
        sim.Send(1, new WorkOrder { Building = station, On = true });
        sim.Seconds(2);
        Assert.True(Health(sim, other) > 0.5);
    }

    [Fact(DisplayName = "под огнём станция не чинит")]
    public void StationPausesUnderFire()
    {
        var (sim, _, _, _, cx, cy) = Base();
        using var _sim = sim;
        var tank = Spawn(sim, "tank", 1, (int)Math.Floor(cx - 3), (int)Math.Floor(cy));
        Damage(sim, tank);
        sim.Set(tank, "Health.hit", sim.Tick);
        sim.Seconds(3 - 0.5);
        Assert.Equal(0.5, Health(sim, tank));
        sim.Seconds(1.5);
        Assert.True(Health(sim, tank) > 0.5);
    }

    [Fact(DisplayName = "пехота у работающей станции поправляется быстрее, вдали — как обычно")]
    public void InfantryHealsFasterAtStation()
    {
        var (sim, _, _, _, cx, cy) = Base();
        using var _sim = sim;
        var near = Spawn(sim, "infantry", 1, (int)Math.Floor(cx - 3), (int)Math.Floor(cy));
        var far = Spawn(sim, "infantry", 1, (int)Math.Floor(cx - 13), (int)Math.Floor(cy));
        Damage(sim, near, 0.2);
        Damage(sim, far, 0.2);
        sim.Seconds(2);
        Assert.Equal(0.2 + InfantryRegen * 2, Health(sim, far), 2);
        Assert.Equal(0.2 + InfantryRegen * StationRegen * 2, Health(sim, near), 2);
    }
}
