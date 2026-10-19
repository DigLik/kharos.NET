using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Поля схемы, нужные разделу игроков сверх <see cref="S"/>: метки (сигналы союзникам), обречённые, правило видимости.
/// Берутся привязкой систем раздела (Bind — после Mod.Load, до первого тика, приказа, события и запроса).
/// </summary>
internal static class P
{
    public static ComponentRef Mark = null!, Doomed = null!, Sighting = null!;
    public static FieldRef MarkKind, MarkUntil, MarkPlayer, DoomedLeft, DoomedScuttled, SightingRule, PlayerOnline, DiplomacyOffered, DiplomacyAsked;

    /// <summary>Игроки из списка viewers и хозяин с союзниками (Sightings.Listed движка): так метку видят в TS (LISTED в vision.ts).</summary>
    public const int Listed = 3;

    public static void Load(Func<string, ComponentRef> component)
    {
        Mark = component(Names.Mark.Name);
        Doomed = component(Names.Doomed.Name);
        Sighting = component(Names.Sighting.Name);
        MarkKind = Mark[Names.Mark.Kind];
        MarkUntil = Mark[Names.Mark.Until];
        MarkPlayer = Mark[Names.Mark.Player];
        DoomedLeft = Doomed[Names.Doomed.Left];
        DoomedScuttled = Doomed[Names.Doomed.Scuttled];
        SightingRule = Sighting[Names.Sighting.Rule];
        PlayerOnline = S.Player[Names.Player.Online];
        DiplomacyOffered = S.Diplomacy[Names.Diplomacy.Offered];
        DiplomacyAsked = S.Diplomacy[Names.Diplomacy.Asked];
    }
}
