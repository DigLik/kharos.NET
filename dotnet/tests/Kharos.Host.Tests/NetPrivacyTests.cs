using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Что уходит в сеть (Д4, net.test.ts, wire.ts): @net owner — хозяину и союзникам, @net none — никому, @private —
/// чужим значениями по умолчанию, исключения TS; правила Sighting; адресные сигналы.
/// </summary>
public class NetPrivacyTests
{
    [Fact(DisplayName = "склад, очередь производства и цель стрелка видны только хозяину")]
    public void StockQueueAndTargetAreOwnersOnly()
    {
        using var arena = new Arena(fog: false);
        var mcv = arena.Unit(1, 0.5, 0.5);
        arena.Set(mcv, "Producer", "progress", 0.5);
        var soldier = arena.Unit(1, 2.5, 0.5);
        var enemy = arena.Unit(2, 5.5, 0.5);
        arena.Set(soldier, "Armed", "target", enemy);
        arena.Set(soldier, "Armed", "chase", 1);
        var first = arena.Join(1);
        var second = arena.Join(2);
        arena.Step();
        // Свой MCV — с очередью производства, чужой — без неё; сам юнит виден обоим.
        Assert.True(first.Has(mcv, "Producer"));
        Assert.True(second.Has(mcv, "Unit"));
        Assert.False(second.Has(mcv, "Producer"));
        // В кого целится стрелок, знает только хозяин: чужому поле приходит значением по умолчанию.
        Assert.Equal(enemy, first.Number(soldier, "Armed", "target"));
        Assert.Equal(0, second.Number(soldier, "Armed", "target"));
        Assert.Equal(1, second.Number(soldier, "Armed", "chase"));
        // Цель сменилась — хозяину новая, чужому — ничего.
        var other = arena.Unit(2, 6.5, 0.5);
        arena.Set(soldier, "Armed", "target", other);
        arena.Step();
        Assert.Equal(other, first.Number(soldier, "Armed", "target"));
        Assert.Equal(0, second.Number(soldier, "Armed", "target"));
    }

    [Fact(DisplayName = "вспышки радара видит только хозяин: его копия открывает туман там же, где хост, а чужой про них не знает")]
    public void RadarFlashesAreOwnersOnly()
    {
        using var arena = new Arena();
        var radar = arena.Building(1, 0, 0, 2, 2, sight: 5);
        arena.Set(radar, "Scan", "x", 40.5);
        arena.Set(radar, "Scan", "y", 0.5);
        arena.Set(radar, "Scan", "tick", 1);
        arena.Set(radar, "Scan", "wait", 17);
        // Вспышка — сущность с Reveal хозяину на месте разведки.
        var flash = arena.Spawn(1, 40.5, 0.5);
        arena.Set(flash, "Reveal", "radius", 6);
        arena.Set(flash, "Reveal", "until", 100);
        // Сосед стоит у самого радара: радар ему виден.
        arena.Unit(2, 4.5, 1.5);
        var first = arena.Join(1);
        var second = arena.Join(2);
        arena.Settle();
        // Далеко за постоянным обзором радара: открыто вспышкой — и на хосте, и в копии хозяина.
        Assert.True(arena.Host.Vision!.Sees(1, 40.5, 0.5));
        Assert.True(first.Sees(40.5, 0.5));
        Assert.Equal(40.5, first.Number(radar, "Scan", "x"), 3);
        // Сколько ждать следующей вспышки, не уходит никому.
        Assert.Equal(0, first.Number(radar, "Scan", "wait"));
        // Куда смотрит чужой радар, сосед не знает, даже видя сам радар.
        Assert.True(second.Has(radar));
        Assert.False(second.Has(radar, "Scan"));
        Assert.False(second.Sees(40.5, 0.5));
        Assert.False(second.Has(flash));
    }

    [Fact]
    public void NetNoneNeverLeavesAndHitIsStrippedForEveryone()
    {
        using var arena = new Arena(fog: false);
        var tank = arena.Unit(1, 0.5, 0.5);
        arena.Set(tank, "Burst", "radius", 3);
        arena.Set(tank, "Health", "value", 0.5);
        arena.Set(tank, "Health", "hit", 7);
        arena.Set(tank, "Sighting", "rule", Sightings.Seen);
        var first = arena.Join(1);
        var second = arena.Join(2);
        arena.Step();
        foreach (var peer in new[] { first, second })
        {
            Assert.False(peer.Has(tank, "Burst"));
            Assert.False(peer.Has(tank, "Sighting"));
            Assert.Equal(0.5, peer.Number(tank, "Health", "value"), 3);
            Assert.Equal(-1, peer.Number(tank, "Health", "hit"));
        }
        // Попадание меняет только hit — в сеть не уходит ничего.
        arena.Set(tank, "Health", "hit", 9);
        arena.Step();
        Assert.Equal(-1, first.Number(tank, "Health", "hit"));
    }

    [Fact]
    public void DropStockIsOpenAndDeploysOpensWhileConverting()
    {
        using var arena = new Arena(fog: false);
        var crate = arena.Spawn(2, 3.5, 3.5, "Drop");
        arena.Set(crate, "Stock", "metal", 12);
        var depot = arena.Building(2, 6, 6);
        arena.Set(depot, "Stock", "metal", 30);
        var mcv = arena.Unit(2, 0.5, 8.5);
        arena.Set(mcv, "Deploys", "x", 20);
        var first = arena.Join(1);
        arena.Step();
        // Склад ящика с грузом — не тайна хозяина; склад завода — тайна.
        Assert.Equal(12, first.Number(crate, "Stock", "metal"));
        Assert.False(first.Has(depot, "Stock"));
        Assert.False(first.Has(mcv, "Deploys"));
        // MCV разворачивается: где — видно всем; развернулся — снова тайна.
        arena.Set(mcv, "Converting", "left", 10);
        arena.Step();
        Assert.Equal(20, first.Number(mcv, "Deploys", "x"));
        arena.World.Remove(mcv, arena.Id("Converting"));
        arena.Step();
        Assert.False(first.Has(mcv, "Deploys"));
        // Ящик подобрали: метка снята — и склад скрыт.
        arena.World.Remove(crate, arena.Id("Drop"));
        arena.Step();
        Assert.False(first.Has(crate, "Stock"));
    }

    [Fact]
    public void GiftedBuildingRevealsItsSecretsToTheNewOwnerOnly()
    {
        using var arena = new Arena(fog: false);
        var depot = arena.Building(2, 6, 6);
        arena.Set(depot, "Stock", "metal", 30);
        var first = arena.Join(1);
        var second = arena.Join(2);
        arena.Step();
        Assert.False(first.Has(depot, "Stock"));
        Assert.True(second.Has(depot, "Stock"));
        arena.Set(depot, "Owner", "player", 1);
        arena.Step();
        Assert.Equal(30, first.Number(depot, "Stock", "metal"));
        Assert.False(second.Has(depot, "Stock"));
        Assert.Equal(1, second.Number(depot, "Owner", "player"));
    }

    [Fact]
    public void SightingRulesDecideWhoSees()
    {
        using var arena = new Arena();
        foreach (var player in (uint[])[1, 2, 3])
            arena.Account(player);
        arena.Ally(1, 2);
        arena.Unit(3, 0.5, 0.5);
        arena.Step();
        var (one, two, three) = (arena.View(1), arena.View(2), arena.View(3));

        // Everyone — всем, мимо тумана (ядерная ракета, вспышка).
        var missile = arena.Spawn(1, 100.5, 100.5);
        arena.Set(missile, "Sighting", "rule", Sightings.Everyone);
        Assert.True(three.Entity(missile));

        // Points — тем, кто видит хоть одну точку или место: выстрел из тумана виден по месту попадания.
        var shot = arena.Spawn(1, 90.5, 90.5);
        arena.Set(shot, "Sighting", "rule", Sightings.Points);
        foreach (var (x, y) in new[] { ("ax", "ay"), ("bx", "by"), ("cx", "cy") })
        {
            arena.Set(shot, "Sighting", x, 80.5);
            arena.Set(shot, "Sighting", y, 80.5);
        }
        Assert.False(three.Entity(shot));
        Assert.True(two.Entity(shot));
        arena.Set(shot, "Sighting", "bx", 1.5);
        arena.Set(shot, "Sighting", "by", 0.5);
        Assert.True(three.Entity(shot));

        // Без места: ничья — всем, чужая — никому, союзная — союзнику.
        var common = arena.World.Spawn();
        arena.Set(common, "Batch", "amount", 1);
        var foreign = arena.World.Spawn();
        arena.Set(foreign, "Owner", "player", 1);
        Assert.True(three.Entity(common));
        Assert.False(three.Entity(foreign));
        Assert.True(two.Entity(foreign));

        // Турель на носителе — там же и тем же, что носитель; свою видно всегда.
        var carrier = arena.Unit(1, 0.5, 2.5, sight: 0);
        var turret = arena.Unit(1, 50.5, 50.5, sight: 0);
        arena.Set(turret, "Mount", "parent", carrier);
        Assert.True(three.Entity(turret));
        arena.Set(carrier, "Position", "x", -60.5);
        Assert.False(three.Entity(turret));
        Assert.True(one.Entity(turret));
    }

    [Fact]
    public void SignalsReachEveryoneListedOrThoseWhoSeeThePlace()
    {
        using var arena = new Arena();
        arena.Unit(1, 0.5, 0.5);
        arena.Unit(2, 60.5, 0.5);
        arena.Host.Scheduler.On("test.signals", ctx =>
        {
            ctx.Commands.Signal("all");
            ctx.Commands.SignalTo("to2", [], [2u]);
            ctx.Commands.SignalAt("at1", [], 1.5, 0.5);
            ctx.Commands.SignalAt("nowhere", [], -90.5, -90.5);
        });
        var first = arena.Join(1);
        var second = arena.Join(2);
        arena.Settle();
        arena.Host.Scheduler.Emit("test.signals", []);
        arena.Step();
        Assert.Equal(new[] { "all", "at1" }, first.Signals);
        Assert.Equal(new[] { "all", "to2" }, second.Signals);
        // Без обзора (FullView) по месту слышат все, по списку — только адресаты.
        var full = new FullView(1);
        Assert.True(((IViewFilter)full).Hears(new Signal("x", []) { At = (-90.5, -90.5) }));
        Assert.False(((IViewFilter)full).Hears(new Signal("x", []) { To = [2] }));
    }
}
