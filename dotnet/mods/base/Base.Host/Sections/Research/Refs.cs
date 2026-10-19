using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Research;

/// <summary>
/// Поля схемы, нужные исследованиям сверх <see cref="S"/>: лаборатория (Research), улучшение (Upgrade), наука игрока
/// (Science), выключатель (Off). Берутся привязкой систем раздела (Bind — до первого тика, приказа и запроса).
/// </summary>
internal static class R
{
    public static ComponentRef Research = null!, Upgrade = null!, Off = null!;
    public static FieldRef ResearchSpeed, ResearchLack, ResearchCause, ResearchGood, UpgradeProgress, UpgradeScale, SciencePlan, ScienceProgress;

    public static void Load(Func<string, ComponentRef> component)
    {
        Research = component(Names.Research.Name);
        Upgrade = component(Names.Upgrade.Name);
        Off = component(Names.Off.Name);
        ResearchSpeed = Research[Names.Research.Speed];
        ResearchLack = Research[Names.Research.Lack];
        ResearchCause = Research[Names.Research.Cause];
        ResearchGood = Research[Names.Research.Good];
        UpgradeProgress = Upgrade[Names.Upgrade.Progress];
        UpgradeScale = Upgrade[Names.Upgrade.Scale];
        SciencePlan = S.Science[Names.Science.Plan];
        ScienceProgress = S.Science[Names.Science.Progress];
    }
}
