using Base.Shared.Orders;
using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;
using MemoryPack;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Players;

/// <summary>
/// Защита новичка (packages/tests/shelter.test.ts): Player.safe — срок по тикам мира. protect в тестах TS — запись срока
/// прямо в счёт (<see cref="Sim.Protect"/>); защиту при входе ставит ядро, весть о ней — система players/watch (сигнал
/// sheltered вместо списка игроков хоста TS).
/// </summary>
public sealed class ShelterTests
{
    /// <summary>Пехотинец игрока 1 и пехотинец игрока 2 в трёх тайлах друг от друга; оба игрока есть в мире (duel).</summary>
    private static (Sim Sim, int X, int Y, uint Mine, uint Foe) Duel()
    {
        var (sim, x, y) = Field();
        sim.SetCredits(1, 0);
        sim.SetCredits(2, 0);
        var mine = Spawn(sim, "infantry", 1, x, y);
        var foe = Spawn(sim, "infantry", 2, x + 3, y);
        return (sim, x, y, mine, foe);
    }

    [Fact(DisplayName = "новичка под защитой не ранят, и сам он не стреляет; срок вышел — бой идёт")]
    public void ShelteredNeitherHurtNorShoots()
    {
        var (sim, _, _, mine, foe) = Duel();
        using var _sim = sim;
        sim.Protect(2, 0.1);
        Assert.Equal(Sim.TicksIn(6), sim.ProtectionLeft(2));
        sim.Seconds(5);
        Assert.Equal(1, Health(sim, mine));
        Assert.Equal(1, Health(sim, foe));
        sim.Seconds(3);
        Assert.Equal(0, sim.ProtectionLeft(2));
        Assert.True(Health(sim, mine) < 1 || Health(sim, foe) < 1);
    }

    [Fact(DisplayName = "по защищённому приказ атаки не даётся: точка по нему — движение")]
    public void NoAttackOnSheltered()
    {
        var (sim, x, y, mine, foe) = Duel();
        using var _sim = sim;
        sim.Protect(2, 10);
        Assert.False(sim.CanAttack(1, foe));
        Assert.Equal("move", sim.Resolve(1, [mine], x + 3.5, y + 0.5, fog: false)[0].Name);
        sim.Send(1, new AttackOrder { Units = [mine], Target = foe });
        sim.Advance();
        Assert.Equal(0, sim.Num(mine, "Armed.ordered"));
    }

    [Fact(DisplayName = "приказ атаки снимает свою защиту")]
    public void AttackDropsOwnShelter()
    {
        var (sim, _, _, mine, foe) = Duel();
        using var _sim = sim;
        sim.Protect(1, 10);
        Assert.True(sim.CanAttack(1, foe));
        sim.Send(1, new AttackOrder { Units = [mine], Target = foe });
        sim.Advance();
        Assert.Equal(0, sim.ProtectionLeft(1));
        sim.Seconds(4);
        Assert.True(Health(sim, foe) < 1);
    }

    /// <summary>Партия со стартом (стартовый набор и защита при входе) на маленькой карте.</summary>
    private static Sim Started(double protection, params uint[] players)
    {
        Scene.SkipUnlessAvailable();
        return Sim.Create(new SimOptions { Size = 128, Land = Scene.Land, Rules = new { protection }, Settings = new { start = true }, Players = players });
    }

    [Fact(DisplayName = "без правила защиты нет")]
    public void NoRuleNoShelter()
    {
        using var sim = Started(0, 1);
        Assert.True(sim.Owned(1) > 0);
        Assert.Equal(0, sim.ProtectionLeft(1));
        sim.Protect(1, 0);
        Assert.Equal(0, sim.ProtectionLeft(1));
    }

    /// <summary>Последний список защищённых, что пришёл всем (sheltered); не было — null.</summary>
    private static uint[]? Sheltered(Sim sim) =>
        sim.TakeSignals(ShelteredSignal.Name) is [.., var last] ? MemoryPackSerializer.Deserialize<ShelteredSignal>(last.Payload)!.Players : null;

    [Fact(DisplayName = "хост даёт новому игроку защиту и сообщает всем, когда она кончилась")]
    public void HostSheltersNewcomer()
    {
        using var sim = Started(0.05);
        Assert.True(sim.Join(1));
        Assert.True(sim.ProtectionLeft(1) > 0);
        sim.Advance(Watch);
        Assert.Equal([1u], Sheltered(sim)!);
        sim.Seconds(4);
        Assert.Equal(0, sim.ProtectionLeft(1));
        Assert.Equal([], Sheltered(sim)!);
        // Весть — только когда список поменялся.
        sim.Seconds(1);
        Assert.Null(Sheltered(sim));
    }

    /// <summary>Раз во сколько тиков players/watch смотрит на игроков (SWEEP_TICKS).</summary>
    private const int Watch = 5;
}
