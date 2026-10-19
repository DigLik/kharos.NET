using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Поля схемы, нужные обороне сверх <see cref="S"/>: берутся привязкой систем раздела (Bind — после Mod.Load, до первого
/// тика, приказа и запроса).
/// </summary>
internal sealed class Refs
{
    /// <summary>Поля текущей схемы.</summary>
    public static Refs R { get; private set; } = null!;

    public readonly ComponentRef Armed;
    public readonly FieldRef ShieldCharge, ShieldHit, ShieldDown, ArmedTarget, RepairTarget;
    public readonly FieldRef TacticsStance, TacticsAway, TacticsHomeX, TacticsHomeY, TacticsResupply, TacticsPatrol, TacticsLeg;

    /// <summary>Берёт поля схемы (Bind систем раздела); повторный вызов той же схемы ничего не меняет.</summary>
    public static void Load(Func<string, ComponentRef> component)
    {
        R = new Refs(component);
        Magazines.Reset();
    }

    private Refs(Func<string, ComponentRef> component)
    {
        Armed = component(Names.Armed.Name);
        ShieldCharge = S.Shield[Names.Shield.Charge];
        ShieldHit = S.Shield[Names.Shield.Hit];
        ShieldDown = S.Shield[Names.Shield.Down];
        ArmedTarget = S.Armed[Names.Armed.Target];
        RepairTarget = S.Repair[Names.Repair.Target];
        TacticsStance = S.Tactics[Names.Tactics.Stance];
        TacticsAway = S.Tactics[Names.Tactics.Away];
        TacticsHomeX = S.Tactics[Names.Tactics.HomeX];
        TacticsHomeY = S.Tactics[Names.Tactics.HomeY];
        TacticsResupply = S.Tactics[Names.Tactics.Resupply];
        TacticsPatrol = S.Tactics[Names.Tactics.Patrol];
        TacticsLeg = S.Tactics[Names.Tactics.Leg];
    }
}
