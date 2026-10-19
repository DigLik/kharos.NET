using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Scenario;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Combat.Arena;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Defense;

/// <summary>Снабжение войск (packages/tests/supply.test.ts): магазины, сети, машины снабжения, станции поставки, аэродромы.</summary>
public sealed class SupplyTests
{
    /// <summary>На сколько секунд непрерывного огня всеми стволами хватает полного магазина (MAGAZINE_SECONDS).</summary>
    private const double MagazineSeconds = 50;

    /// <summary>То же у пехоты (INFANTRY_MAGAZINE_SECONDS).</summary>
    private const double InfantryMagazineSeconds = 120;

    private static readonly object Unprotected = new { protection = 0 };

    /// <summary>Главное здание и бункер боеприпасов в одной сети на скале, с запасом ammo; рядом — место для юнитов (base).</summary>
    private static (Sim Sim, uint Bunker, int X, int Y) Base(double ammo = 200, object? rules = null)
    {
        var sim = Create(rules ?? Unprotected);
        var bunker = Tables.Buildings["ammoBunker"];
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                if (!Scene.Rock(sim, x, y, 3, 3) || !Scene.Rock(sim, x + 5, y, (int)bunker.Width, (int)bunker.Height))
                    continue;
                if (!Scene.CanPlace(sim, "command", x, y) || !Scene.CanPlace(sim, "ammoBunker", x + 5, y))
                    continue;
                // Справа от базы — проходимая полоса: туда отводят юнитов из сети.
                var open = true;
                for (var dx = 8; dx <= 40 && open; dx++)
                    open = sim.Walkable(x + dx, y + 4);
                if (!open)
                    continue;
                Place(sim, "command", x, y, 1);
                var store = Place(sim, "ammoBunker", x + 5, y, 1);
                sim.ConnectAll(1);
                sim.SetStock(store, "ammo", ammo);
                return (sim, store, x, y);
            }
        sim.Dispose();
        throw new InvalidOperationException("Не нашлось места");
    }

    private static double Rounds(Sim sim, uint entity) => sim.Rounds(entity);

    private static double Stock(Sim sim, uint bunker) => sim.Stock(bunker, "ammo");

    private static uint SpawnAt(Sim sim, string type, uint player, int x, int y)
    {
        var (tx, ty) = Builders.FreeTilesNear(sim, x, y, 1)[0];
        return Spawn(sim, type, player, tx, ty);
    }

    private static void Until(Sim sim, Func<bool> done, double limit = 60) => Assert.True(sim.Until(done, limit), "Не дождались");

    private static double Cargo(Sim sim, uint entity) => sim.Stock(entity, "ammo");

    [Fact(DisplayName = "магазин: технике хватает на MAGAZINE_SECONDS огня, пехоте — дольше; лазер стреляет бесплатно")]
    public void MagazineLastsForSecondsOfFire()
    {
        using var sim = Create();
        var tank = sim.ShotCost("tank", "cannon");
        Assert.Equal(MagazineSeconds, sim.MagazineOf("tank") / tank * Weapon("cannon").Reload, 6);
        var rifle = sim.ShotCost("infantry", "rifle");
        Assert.Equal(InfantryMagazineSeconds, sim.MagazineOf("infantry") / rifle * Weapon("rifle").Reload, 6);
        // Три ракетные турели носителя делят один магазин.
        Assert.Equal(MagazineSeconds * 3, sim.MagazineOf("carrier") / sim.ShotCost("carrier", "launcher") * Weapon("launcher").Reload, 6);
        Assert.Equal(0, sim.MagazineOf("lancer"));
        Assert.Equal(0, sim.ShotCost("lancer", "laser"));
    }

    [Fact(DisplayName = "выстрел тратит магазин, пустой юнит молчит")]
    public void ShotSpendsTheMagazine()
    {
        var (sim, _, x, y) = Base();
        using var _sim = sim;
        var tank = SpawnAt(sim, "tank", 1, x + 30, y + 4);
        sim.SetStance("hold", tank);
        var (tx, ty) = sim.At(tank);
        var target = Place(sim, "metalYard", (int)Math.Floor(tx) + 3, (int)Math.Floor(ty) - 4, 2);
        // На два выстрела.
        sim.Set(tank, "Magazine.ammo", sim.ShotCost("tank", "cannon") * 2);
        Until(sim, () => sim.IsEmpty(tank));
        sim.Seconds(2);
        var health = Health(sim, target);
        Assert.True(health < 1);
        sim.Seconds(10);
        Assert.Equal(health, Health(sim, target));
    }

    [Fact(DisplayName = "в своей сети магазин пополняется из хранилища")]
    public void MagazineRefillsInTheNetwork()
    {
        var (sim, bunker, x, y) = Base();
        using var _sim = sim;
        var tank = SpawnAt(sim, "tank", 1, x + 4, y + 4);
        sim.Set(tank, "Magazine.ammo", 0);
        sim.Seconds(10);
        Assert.Equal(sim.MagazineOf("tank"), Rounds(sim, tank), 2);
        Assert.Equal(200 - sim.MagazineOf("tank"), Stock(sim, bunker), 2);
    }

    [Fact(DisplayName = "пустой вне сети сам идёт за боеприпасами и возвращается на место")]
    public void EmptyOutsideWalksForAmmoAndComesBack()
    {
        var (sim, bunker, x, y) = Base();
        using var _sim = sim;
        var tank = SpawnAt(sim, "tank", 1, x + 30, y + 4);
        var home = sim.At(tank);
        sim.Set(tank, "Magazine.ammo", 0);
        // Пустой ищет снабжение не сразу, а в свой черёд.
        sim.Seconds(3);
        Assert.Equal(1, sim.Num(tank, "Tactics.resupply"));
        // Встав у хранилища, он стоит, пока не наполнит магазин: возвращаться на место ему рано.
        var stood = false;
        var full = sim.MagazineOf("tank") - 1e-6;
        Until(sim, () =>
        {
            if (Rounds(sim, tank) > 0 && !sim.Has(tank, Names.Path.Name))
                stood = true;
            if (stood)
                Assert.False(sim.Has(tank, Names.Path.Name) && Rounds(sim, tank) < full);
            return Rounds(sim, tank) >= full;
        });
        Assert.True(stood);
        Assert.Equal(200 - sim.MagazineOf("tank"), Stock(sim, bunker), 2);
        Until(sim, () => Sim.Distance(sim.At(tank), home) < 2);
        Assert.Equal(0, sim.Num(tank, "Tactics.resupply"));
    }

    [Fact(DisplayName = "держащий позицию пустой стоит, а без боеприпасов в сетях никто не уходит")]
    public void HoldingEmptyStaysPut()
    {
        var (sim, _, x, y) = Base();
        using var _sim = sim;
        var held = SpawnAt(sim, "tank", 1, x + 30, y + 4);
        sim.SetStance("hold", held);
        sim.Set(held, "Magazine.ammo", 0);
        var at = sim.At(held);
        sim.Seconds(5);
        Assert.Equal(at, sim.At(held));

        var dry = Base(0);
        using var _dry = dry.Sim;
        var tank = SpawnAt(dry.Sim, "tank", 1, dry.X + 30, dry.Y + 4);
        dry.Sim.Set(tank, "Magazine.ammo", 0);
        var from = dry.Sim.At(tank);
        dry.Sim.Seconds(5);
        Assert.Equal(from, dry.Sim.At(tank));
        Assert.False(dry.Sim.Has(tank, Names.Tactics.Name) && dry.Sim.Num(tank, "Tactics.resupply") != 0);
    }

    [Fact(DisplayName = "приказ идти снимает поход за боеприпасами")]
    public void MoveOrderCancelsTheTrip()
    {
        var (sim, _, x, y) = Base();
        using var _sim = sim;
        var tank = SpawnAt(sim, "tank", 1, x + 30, y + 4);
        sim.Set(tank, "Magazine.ammo", 0);
        // Пустой ищет снабжение не сразу, а в свой черёд.
        sim.Seconds(3);
        Assert.Equal(1, sim.Num(tank, "Tactics.resupply"));
        var (tx, ty) = sim.At(tank);
        sim.Send(1, new MoveOrder { Units = [tank], X = (int)Math.Floor(tx) + 5, Y = (int)Math.Floor(ty) });
        sim.Advance();
        Assert.Equal(0, sim.Num(tank, "Tactics.resupply"));
    }

    [Fact(DisplayName = "с выключенным правилом юниты стреляют бесплатно")]
    public void WithoutTheRuleShotsAreFree()
    {
        var (sim, _, x, y) = Base(200, new { protection = 0, unitAmmo = false });
        using var _sim = sim;
        var tank = SpawnAt(sim, "tank", 1, x + 30, y + 4);
        var (tx, ty) = sim.At(tank);
        Place(sim, "metalYard", (int)Math.Floor(tx) + 3, (int)Math.Floor(ty) - 4, 2);
        sim.Seconds(10);
        Assert.Equal(sim.MagazineOf("tank"), Rounds(sim, tank));
    }

    [Fact(DisplayName = "машина снабжения пополняет магазины своих в радиусе со своего склада")]
    public void SupplierRefillsTheNeighbours()
    {
        var (sim, _, x, y) = Base();
        using var _sim = sim;
        var supplier = SpawnAt(sim, "supplier", 1, x + 30, y + 4);
        sim.SetStock(supplier, "ammo", 100);
        var near = SpawnAt(sim, "tank", 1, x + 33, y + 4);
        var far = SpawnAt(sim, "tank", 1, x + 30 + 12, y + 4);
        foreach (var tank in new[] { near, far })
        {
            sim.SetStance("hold", tank);
            sim.Set(tank, "Magazine.ammo", 0);
        }
        sim.Seconds(10);
        Assert.Equal(sim.MagazineOf("tank"), Rounds(sim, near), 2);
        Assert.Equal(0, Rounds(sim, far));
        Assert.Equal(100 - sim.MagazineOf("tank"), Cargo(sim, supplier), 2);
    }

    [Fact(DisplayName = "пустой вне сети идёт к машине снабжения, если она ближе хранилища")]
    public void EmptyWalksToANearerSupplier()
    {
        var (sim, bunker, x, y) = Base();
        using var _sim = sim;
        var supplier = SpawnAt(sim, "supplier", 1, x + 40, y + 4);
        sim.SetStance("hold", supplier);
        sim.SetStock(supplier, "ammo", 100);
        var tank = SpawnAt(sim, "tank", 1, x + 30, y + 4);
        sim.Set(tank, "Magazine.ammo", 0);
        Until(sim, () => Rounds(sim, tank) >= sim.MagazineOf("tank") - 1e-6);
        Assert.Equal(200, Stock(sim, bunker));
        Assert.Equal(100 - sim.MagazineOf("tank"), Cargo(sim, supplier), 2);
    }

    [Fact(DisplayName = "машине снабжения в поле грузовик сам везёт патроны из хранилища")]
    public void TruckBringsAmmoToTheSupplier()
    {
        var (sim, bunker, x, y) = Base();
        using var _sim = sim;
        var supplier = SpawnAt(sim, "supplier", 1, x + 30, y + 4);
        SpawnAt(sim, "truck", 1, x + 4, y + 5);
        Assert.Equal(0, Cargo(sim, supplier));
        Until(sim, () => Cargo(sim, supplier) > 0, 90);
        Until(sim, () => Cargo(sim, supplier) >= 25 - 1e-6, 30);
        Assert.True(Stock(sim, bunker) < 200);
    }

    [Fact(DisplayName = "в своей сети машина снабжения грузится из хранилищ, у станции поставки — быстрее")]
    public void SupplierLoadsInTheNetworkAndFasterAtADepot()
    {
        var (sim, bunker, x, y) = Base(400);
        using var _sim = sim;
        var supplier = SpawnAt(sim, "supplier", 1, x + 4, y + 5);
        sim.Seconds(5);
        var loaded = Cargo(sim, supplier);
        Assert.True(loaded > 30);
        Assert.True(loaded < 70);
        Assert.Equal(400 - loaded, Stock(sim, bunker), 2);

        var depotBase = Base(0);
        using var _depot = depotBase.Sim;
        uint? depot = null;
        for (var oy = -6; oy <= 6 && depot is null; oy++)
            for (var ox = -6; ox <= 10 && depot is null; ox++)
                if (Scene.CanPlace(depotBase.Sim, "supplyDepot", depotBase.X + ox, depotBase.Y + oy))
                    depot = Place(depotBase.Sim, "supplyDepot", depotBase.X + ox, depotBase.Y + oy, 1);
        if (depot is not { } station)
            throw new InvalidOperationException("Не нашлось места для станции");
        depotBase.Sim.SetStock(station, "ammo", 300);
        var (ax, ay) = depotBase.Sim.At(station);
        var rig = SpawnAt(depotBase.Sim, "supplier", 1, (int)ax + 1, (int)ay + 3);
        depotBase.Sim.Seconds(5);
        Assert.Equal(150, Cargo(depotBase.Sim, rig), 2);
    }

    /// <summary>Аэродром в сети базы: ближайшее к главному зданию место, подключённое трубами.</summary>
    private static uint AirfieldNear(Sim sim, int x, int y)
    {
        for (var r = 6; r < 20; r++)
            for (var oy = -r; oy <= r; oy++)
                for (var ox = -r; ox <= r; ox++)
                {
                    if (Math.Max(Math.Abs(ox), Math.Abs(oy)) != r || !Scene.CanPlace(sim, "airfield", x + ox, y + oy))
                        continue;
                    var airfield = Place(sim, "airfield", x + ox, y + oy, 1);
                    sim.ConnectAll(1);
                    return airfield;
                }
        throw new InvalidOperationException("Не нашлось места для аэродрома");
    }

    [Fact(DisplayName = "авиация в кругах сети не пополняется, а летит к аэродрому и возвращается")]
    public void AircraftFliesToTheAirfieldAndBack()
    {
        var (sim, bunker, x, y) = Base();
        using var _sim = sim;
        var drone = SpawnAt(sim, "drone", 1, x + 4, y + 4);
        sim.Set(drone, "Magazine.ammo", 0);
        // Без аэродрома ей лететь некуда: в сети она не пополнится и ждёт.
        sim.Seconds(5);
        Assert.Equal(0, Rounds(sim, drone));
        Assert.False(sim.Has(drone, Names.Tactics.Name) && sim.Num(drone, "Tactics.resupply") != 0);

        AirfieldNear(sim, x, y);
        var away = SpawnAt(sim, "drone", 1, x + 30, y + 4);
        var home = sim.At(away);
        sim.Set(away, "Magazine.ammo", 0);
        var full = sim.MagazineOf("drone") - 1e-6;
        Until(sim, () => Rounds(sim, drone) >= full && Rounds(sim, away) >= full);
        Assert.Equal(200 - 2 * sim.MagazineOf("drone"), Stock(sim, bunker), 2);
        Until(sim, () => Sim.Distance(sim.At(away), home) < 2);
        Assert.Equal(0, sim.Num(away, "Tactics.resupply"));
    }

    [Fact(DisplayName = "пустая авиация летит к машине снабжения, если та ближе аэродрома")]
    public void AircraftPrefersANearerSupplier()
    {
        var (sim, bunker, x, y) = Base();
        using var _sim = sim;
        AirfieldNear(sim, x, y);
        var supplier = SpawnAt(sim, "supplier", 1, x + 40, y + 4);
        sim.SetStance("hold", supplier);
        sim.SetStock(supplier, "ammo", 100);
        var drone = SpawnAt(sim, "drone", 1, x + 52, y + 4);
        sim.Set(drone, "Magazine.ammo", 0);
        Until(sim, () => Rounds(sim, drone) >= sim.MagazineOf("drone") - 1e-6);
        Assert.Equal(200, Stock(sim, bunker));
        Assert.Equal(100 - sim.MagazineOf("drone"), Cargo(sim, supplier), 2);
    }
}
