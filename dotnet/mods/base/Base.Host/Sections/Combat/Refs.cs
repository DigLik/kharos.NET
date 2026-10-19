using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Поля схемы, нужные бою сверх <see cref="S"/>: берутся привязкой систем раздела (Bind — после Mod.Load, до первого тика,
/// приказа и запроса).
/// </summary>
internal sealed class Refs
{
    /// <summary>Поля текущей схемы.</summary>
    public static Refs R { get; private set; } = null!;

    public readonly ComponentRef Shot, Blast, Burst, Doomed, Off;
    public readonly FieldRef ArmedTarget, ArmedChase, ArmedCooldown, ArmedStuck, ArmedOrdered;
    public readonly FieldRef ShotPlayer, ShotSource, ShotTarget, ShotFromX, ShotFromY, ShotToX, ShotToY, ShotAge, ShotLife, ShotBlocked, ShotAir;
    public readonly FieldRef BlastSize, BlastAge, BlastLife, BurstRadius, BurstDamage, BurstCore, DoomedLeft;
    public readonly FieldRef HealthHit, TacticsStance, TacticsLeg, TacticsAway, TacticsHomeX, TacticsHomeY, TacticsResupply, TacticsPatrol;

    /// <summary>Берёт поля схемы (Bind систем раздела); повторный вызов той же схемы ничего не меняет.</summary>
    public static void Load(Func<string, ComponentRef> component)
    {
        R = new Refs(component);
        Arms.Reset();
    }

    private Refs(Func<string, ComponentRef> component)
    {
        Shot = component(Names.Shot.Name);
        Blast = component(Names.Blast.Name);
        Burst = component(Names.Burst.Name);
        Doomed = component(Names.Doomed.Name);
        Off = component(Names.Off.Name);
        ArmedTarget = S.Armed[Names.Armed.Target];
        ArmedChase = S.Armed[Names.Armed.Chase];
        ArmedCooldown = S.Armed[Names.Armed.Cooldown];
        ArmedStuck = S.Armed[Names.Armed.Stuck];
        ArmedOrdered = S.Armed[Names.Armed.Ordered];
        ShotPlayer = Shot[Names.Shot.Player];
        ShotSource = Shot[Names.Shot.Source];
        ShotTarget = Shot[Names.Shot.Target];
        ShotFromX = Shot[Names.Shot.FromX];
        ShotFromY = Shot[Names.Shot.FromY];
        ShotToX = Shot[Names.Shot.ToX];
        ShotToY = Shot[Names.Shot.ToY];
        ShotAge = Shot[Names.Shot.Age];
        ShotLife = Shot[Names.Shot.Life];
        ShotBlocked = Shot[Names.Shot.Blocked];
        ShotAir = Shot[Names.Shot.Air];
        BlastSize = Blast[Names.Blast.Size];
        BlastAge = Blast[Names.Blast.Age];
        BlastLife = Blast[Names.Blast.Life];
        BurstRadius = Burst[Names.Burst.Radius];
        BurstDamage = Burst[Names.Burst.Damage];
        BurstCore = Burst[Names.Burst.Core];
        DoomedLeft = Doomed[Names.Doomed.Left];
        HealthHit = S.Health[Names.Health.Hit];
        TacticsStance = S.Tactics[Names.Tactics.Stance];
        TacticsLeg = S.Tactics[Names.Tactics.Leg];
        TacticsAway = S.Tactics[Names.Tactics.Away];
        TacticsHomeX = S.Tactics[Names.Tactics.HomeX];
        TacticsHomeY = S.Tactics[Names.Tactics.HomeY];
        TacticsResupply = S.Tactics[Names.Tactics.Resupply];
        TacticsPatrol = S.Tactics[Names.Tactics.Patrol];
    }
}
