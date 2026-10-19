using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Поля схемы, нужные энергии сверх <see cref="S"/>: берутся привязкой систем раздела (Bind — после Mod.Load, до
/// первого тика, приказа и запроса).
/// </summary>
internal sealed class Refs
{
    /// <summary>Поля текущей схемы.</summary>
    public static Refs R { get; private set; } = null!;

    public readonly ComponentRef Off, Busy, Research, Trade, Transfer, Burst, Scan;
    public readonly FieldRef AccumulatorCharge, AccumulatorFlow, ReactorLeft, AssemblyOn, AssemblyProgress;
    public readonly FieldRef ResearchSpeed, ResearchLack, SiloTicks, SiloReady, SiloStart, InterceptorTicks;
    public readonly FieldRef ShieldCharge, ArmedTarget, ArmedCooldown, RepairTarget, BurstRadius, BurstDamage, ScanDark;
    public readonly FieldRef PlayerAway, PlayerOnline;

    /// <summary>Берёт поля схемы (Bind систем раздела); повторный вызов той же схемы ничего не меняет.</summary>
    public static void Load(Func<string, ComponentRef> component) => R = new Refs(component);

    private Refs(Func<string, ComponentRef> component)
    {
        Off = component(Names.Off.Name);
        Busy = component(Names.Busy.Name);
        Research = component(Names.Research.Name);
        Trade = component(Names.Trade.Name);
        Transfer = component(Names.Transfer.Name);
        Burst = component(Names.Burst.Name);
        Scan = component(Names.Scan.Name);
        AccumulatorCharge = S.Accumulator[Names.Accumulator.Charge];
        AccumulatorFlow = S.Accumulator[Names.Accumulator.Flow];
        ReactorLeft = S.Reactor[Names.Reactor.Left];
        AssemblyOn = S.Assembly[Names.Assembly.On];
        AssemblyProgress = S.Assembly[Names.Assembly.Progress];
        ResearchSpeed = Research[Names.Research.Speed];
        ResearchLack = Research[Names.Research.Lack];
        SiloTicks = S.Silo[Names.Silo.Ticks];
        SiloReady = S.Silo[Names.Silo.Ready];
        SiloStart = S.Silo[Names.Silo.Start];
        InterceptorTicks = S.Interceptor[Names.Interceptor.Ticks];
        ShieldCharge = S.Shield[Names.Shield.Charge];
        ArmedTarget = S.Armed[Names.Armed.Target];
        ArmedCooldown = S.Armed[Names.Armed.Cooldown];
        RepairTarget = S.Repair[Names.Repair.Target];
        BurstRadius = Burst[Names.Burst.Radius];
        BurstDamage = Burst[Names.Burst.Damage];
        ScanDark = Scan[Names.Scan.Dark];
        PlayerAway = S.Player[Names.Player.Away];
        PlayerOnline = S.Player[Names.Player.Online];
    }
}
