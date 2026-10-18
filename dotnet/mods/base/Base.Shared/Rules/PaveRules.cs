namespace Base.Shared.Rules;

/// <summary>
/// Покрытие (paved.ts, paving.ts): фундамент и дорога. Готовое — слой paving движка: код вида и хозяина одним числом
/// (<see cref="Code"/>), 0 — нет. Работа над покрытием — сущность Pave на тайле.
/// </summary>
public static class PaveRules
{
    public const string Foundation = "foundation", Road = "road";

    /// <summary>Виды покрытия по порядку (PAVE_KINDS): номер вида — место в списке.</summary>
    public static readonly string[] Kinds = [Foundation, Road];

    /// <summary>Покрытия нет (NO_PAVE).</summary>
    public const int None = 0;

    /// <summary>Цены тайла: фундамент, дорога, мост — дорога по болоту.</summary>
    public const double FoundationCost = 15, RoadCost = 5, BridgeCost = 20;

    /// <summary>Скорость стройки на фундаменте на скале и на песке — к голой скале.</summary>
    public const double FoundationRock = 2, FoundationSand = 0.8;

    /// <summary>Сколько тайлов можно заложить одной командой (PAVE_LIMIT).</summary>
    public const int Limit = 400;

    /// <summary>Во сколько раз быстрее голой скалы идут по дороге и по фундаменту.</summary>
    public const double RoadSpeed = 1.5, FoundationSpeed = 1.2;

    /// <summary>Какие исследования открывают покрытие (PAVE_RESEARCH).</summary>
    public const string FoundationTopic = "foundation", RoadTopic = "roads", BridgeTopic = "bridges";

    /// <summary>Есть ли такой вид.</summary>
    public static bool Known(string? kind) => kind is Foundation or Road;

    /// <summary>Код готового покрытия (paveCode).</summary>
    public static int Code(string kind, uint owner) => (int)owner * 2 + Array.IndexOf(Kinds, kind) + 2;

    /// <summary>Вид по коду (paveKindOf); код 0 — null.</summary>
    public static string? KindOf(int code) => code == None ? null : Kinds[(code - 2) % 2];

    /// <summary>Хозяин по коду (paveOwnerOf).</summary>
    public static uint OwnerOf(int code) => (uint)Math.Floor((code - 2) / 2.0);

    /// <summary>Фундамент ли код.</summary>
    public static bool IsFoundation(int code) => code != None && (code - 2) % 2 == 0;

    /// <summary>Дорога ли код.</summary>
    public static bool IsRoad(int code) => code != None && (code - 2) % 2 == 1;

    /// <summary>Цена тайла покрытия (paveCost): дорога по болоту — мост.</summary>
    public static double Cost(string kind, bool swamp) => kind == Foundation ? FoundationCost : swamp ? BridgeCost : RoadCost;

    /// <summary>Тема, которая открывает покрытие этого вида на этой местности.</summary>
    public static string Topic(string kind, bool swamp) => kind == Road && swamp ? BridgeTopic : kind == Road ? RoadTopic : FoundationTopic;

    /// <summary>Скорость стройки на тайле основания: без фундамента — 1, на фундаменте — по местности под ним.</summary>
    public static double BuildSpeed(int code, bool sand) => !IsFoundation(code) ? 1 : sand ? FoundationSand : FoundationRock;
}
