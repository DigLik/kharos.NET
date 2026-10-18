using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Поля схемы, нужные стройке сверх <see cref="S"/>. Компоненты, которых нет в S (Dig, Upgrade, Off), даёт привязка
/// системы стройки при загрузке (<see cref="Load"/> — после Mod.Load, до первого тика и приказа).
/// </summary>
internal sealed class Refs
{
    /// <summary>Поля текущей схемы.</summary>
    public static Refs R { get; private set; } = null!;

    public readonly ComponentRef Dig, Upgrade, Off;
    public readonly FieldRef SiteProgress, SiteDemolish, SiteUndo;
    public readonly FieldRef BuildsSite, BuildsOrdered, BuildsMisses;
    public readonly FieldRef RepairRate, RepairTarget;
    public readonly FieldRef PaveWork, PaveDone;
    public readonly FieldRef DigKind, DigCost, DigWork, DigPeakX, DigPeakY;
    public readonly FieldRef UpgradeProgress, UpgradeScale;
    public readonly FieldRef HealthHit;

    /// <summary>Берёт поля схемы (Bind системы стройки).</summary>
    public static void Load(Func<string, ComponentRef> component) => R = new Refs(component);

    private Refs(Func<string, ComponentRef> component)
    {
        SiteProgress = S.Site[Names.Site.Progress];
        SiteDemolish = S.Site[Names.Site.Demolish];
        SiteUndo = S.Site[Names.Site.Undo];
        BuildsSite = S.Builds[Names.Builds.Site];
        BuildsOrdered = S.Builds[Names.Builds.Ordered];
        BuildsMisses = S.Builds[Names.Builds.Misses];
        RepairRate = S.Repair[Names.Repair.Rate];
        RepairTarget = S.Repair[Names.Repair.Target];
        PaveWork = S.Pave[Names.Pave.Work];
        PaveDone = S.Pave[Names.Pave.Done];
        HealthHit = S.Health[Names.Health.Hit];
        Dig = component(Names.Dig.Name);
        Upgrade = component(Names.Upgrade.Name);
        Off = component(Names.Off.Name);
        DigKind = Dig[Names.Dig.Kind];
        DigCost = Dig[Names.Dig.Cost];
        DigWork = Dig[Names.Dig.Work];
        DigPeakX = Dig[Names.Dig.PeakX];
        DigPeakY = Dig[Names.Dig.PeakY];
        UpgradeProgress = Upgrade[Names.Upgrade.Progress];
        UpgradeScale = Upgrade[Names.Upgrade.Scale];
    }
}
