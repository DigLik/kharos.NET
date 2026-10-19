using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Поля схемы, нужные ракетам сверх <see cref="S"/>: берутся привязкой систем раздела (Bind — после Mod.Load, до первого
/// тика, приказа и запроса).
/// </summary>
internal sealed class Refs
{
    /// <summary>Поля текущей схемы.</summary>
    public static Refs R { get; private set; } = null!;

    /// <summary>Правила Sighting.rule движка (Sightings): хозяин и союзники, все, список, точки.</summary>
    public const int Allies = 1, Everyone = 2, Listed = 3, Points = 4;

    public readonly ComponentRef Off, Reveal, Sighting, Mark, Missile, Chaser, Scan, Blast, Burst;
    public readonly FieldRef SiloProgress, SiloTicks, SiloCost, SiloReady, SiloStart, SiloToX, SiloToY;
    public readonly FieldRef MissileFromX, MissileFromY, MissileToX, MissileToY, MissileBorn, MissileX, MissileY, MissilePlayer, MissileMark,
        MissileHunted, MissileCourseX, MissileCourseY;
    public readonly FieldRef ChaserFromX, ChaserFromY, ChaserToX, ChaserToY, ChaserLift, ChaserBorn, ChaserArrive, ChaserPlayer, ChaserMissile;
    public readonly FieldRef InterceptorProgress, InterceptorTicks, InterceptorMissiles;
    public readonly FieldRef ScanX, ScanY, ScanTick, ScanWait, ScanDark;
    public readonly FieldRef MarkKind, MarkUntil, MarkPlayer;
    public readonly FieldRef SightingRule, SightingAx, SightingAy, SightingBx, SightingBy, SightingCx, SightingCy, SightingViewers;
    public readonly FieldRef RevealRadius, RevealUntil, RevealAll;
    public readonly FieldRef BlastSize, BlastLife, BlastLift, BurstRadius, BurstDamage, BurstCore;

    /// <summary>Берёт поля схемы (Bind систем раздела); повторный вызов той же схемы ничего не меняет.</summary>
    public static void Load(Func<string, ComponentRef> component) => R = new Refs(component);

    private Refs(Func<string, ComponentRef> component)
    {
        Off = component(Names.Off.Name);
        Reveal = component(Names.Reveal.Name);
        Sighting = component(Names.Sighting.Name);
        Mark = component(Names.Mark.Name);
        Missile = component(Names.Missile.Name);
        Chaser = component(Names.Chaser.Name);
        Scan = component(Names.Scan.Name);
        Blast = component(Names.Blast.Name);
        Burst = component(Names.Burst.Name);
        SiloProgress = S.Silo[Names.Silo.Progress];
        SiloTicks = S.Silo[Names.Silo.Ticks];
        SiloCost = S.Silo[Names.Silo.Cost];
        SiloReady = S.Silo[Names.Silo.Ready];
        SiloStart = S.Silo[Names.Silo.Start];
        SiloToX = S.Silo[Names.Silo.ToX];
        SiloToY = S.Silo[Names.Silo.ToY];
        MissileFromX = Missile[Names.Missile.FromX];
        MissileFromY = Missile[Names.Missile.FromY];
        MissileToX = Missile[Names.Missile.ToX];
        MissileToY = Missile[Names.Missile.ToY];
        MissileBorn = Missile[Names.Missile.Born];
        MissileX = Missile[Names.Missile.X];
        MissileY = Missile[Names.Missile.Y];
        MissilePlayer = Missile[Names.Missile.Player];
        MissileMark = Missile[Names.Missile.Mark];
        MissileHunted = Missile[Names.Missile.Hunted];
        MissileCourseX = Missile[Names.Missile.CourseX];
        MissileCourseY = Missile[Names.Missile.CourseY];
        ChaserFromX = Chaser[Names.Chaser.FromX];
        ChaserFromY = Chaser[Names.Chaser.FromY];
        ChaserToX = Chaser[Names.Chaser.ToX];
        ChaserToY = Chaser[Names.Chaser.ToY];
        ChaserLift = Chaser[Names.Chaser.Lift];
        ChaserBorn = Chaser[Names.Chaser.Born];
        ChaserArrive = Chaser[Names.Chaser.Arrive];
        ChaserPlayer = Chaser[Names.Chaser.Player];
        ChaserMissile = Chaser[Names.Chaser.Missile];
        InterceptorProgress = S.Interceptor[Names.Interceptor.Progress];
        InterceptorTicks = S.Interceptor[Names.Interceptor.Ticks];
        InterceptorMissiles = S.Interceptor[Names.Interceptor.Missiles];
        ScanX = Scan[Names.Scan.X];
        ScanY = Scan[Names.Scan.Y];
        ScanTick = Scan[Names.Scan.Tick];
        ScanWait = Scan[Names.Scan.Wait];
        ScanDark = Scan[Names.Scan.Dark];
        MarkKind = Mark[Names.Mark.Kind];
        MarkUntil = Mark[Names.Mark.Until];
        MarkPlayer = Mark[Names.Mark.Player];
        SightingRule = Sighting[Names.Sighting.Rule];
        SightingAx = Sighting[Names.Sighting.Ax];
        SightingAy = Sighting[Names.Sighting.Ay];
        SightingBx = Sighting[Names.Sighting.Bx];
        SightingBy = Sighting[Names.Sighting.By];
        SightingCx = Sighting[Names.Sighting.Cx];
        SightingCy = Sighting[Names.Sighting.Cy];
        SightingViewers = Sighting[Names.Sighting.Viewers];
        RevealRadius = Reveal[Names.Reveal.Radius];
        RevealUntil = Reveal[Names.Reveal.Until];
        RevealAll = Reveal[Names.Reveal.All];
        BlastSize = Blast[Names.Blast.Size];
        BlastLife = Blast[Names.Blast.Life];
        BlastLift = Blast[Names.Blast.Lift];
        BurstRadius = Burst[Names.Burst.Radius];
        BurstDamage = Burst[Names.Burst.Damage];
        BurstCore = Burst[Names.Burst.Core];
    }
}
