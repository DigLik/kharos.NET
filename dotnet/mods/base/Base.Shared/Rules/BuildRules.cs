using Base.Shared.Data;

namespace Base.Shared.Rules;

/// <summary>
/// Правила стройки, общие у хоста и клиента (buildings.ts, construction.ts, zones.ts): время и цена работ, уровни,
/// разбор, зоны строительства, дерево технологий. Только числа и данные — мир читает тот, кто зовёт.
/// </summary>
public static class BuildRules
{
    /// <summary>Сколько кредитов цены здания один строитель возводит за секунду (BUILD_RATE).</summary>
    public const double BuildRate = 20;

    /// <summary>Доля цены, которую возвращает разбор готового здания (DEMOLISH_REFUND).</summary>
    public const double DemolishRefund = 0.5;

    /// <summary>Во сколько раз разбирать быстрее, чем строить (DEMOLISH_SPEED).</summary>
    public const double DemolishSpeed = 1.5;

    /// <summary>Доля прочности обороны на песке без фундамента (SAND_DURABILITY).</summary>
    public const double SandDurability = 0.7;

    /// <summary>С какой доли прочности начинается недостроенное здание (SITE_FLOOR).</summary>
    public const double SiteFloor = 0.02;

    /// <summary>С какого расстояния до основания свободный строитель сам берётся за работу, тайлы (WORK_RADIUS).</summary>
    public const double WorkRadius = 10;

    /// <summary>Раз во сколько тиков недоехавший строитель пробует снова (RETRY_TICKS).</summary>
    public const int RetryTicks = 20;

    /// <summary>Самая долгая отсрочка подъезда после промахов — степень двойки (MAX_BACKOFF).</summary>
    public const int MaxBackoff = 5;

    /// <summary>Недостача материалов меньше этого — погрешность (MATERIAL_EPSILON).</summary>
    public const double MaterialEpsilon = 1e-6;

    /// <summary>На сколько тайлов от центра расширяет зону готовое здание без своего числа (EXPAND_RADIUS).</summary>
    public const double ExpandRadius = 5;

    /// <summary>Дальше скольких тайлов колодец не видит парный (UNDERGROUND_REACH).</summary>
    public const int UndergroundReach = 10;

    /// <summary>Выше этого уровня здания не улучшают (MAX_LEVEL).</summary>
    public const int MaxLevel = 3;

    /// <summary>Прибавка каждого уровня к первому (LEVEL_BONUS).</summary>
    public const double LevelBonus = 0.5;

    private const double UpgradeShare = 0.5, UpgradeBlocks = 30, UpgradeParts = 60;

    /// <summary>Главное здание (CORE): его привозит MCV.</summary>
    public const string Core = "command";

    /// <summary>Math.round из JS: половина — вверх.</summary>
    public static double Round(double value) => Math.Floor(value + 0.5);

    /// <summary>Тиков работы одного строителя на то, что стоит cost кредитов (workTicks).</summary>
    public static int WorkTicks(double cost, double step) => (int)Round(cost / BuildRate / step);

    /// <summary>Тиков работы одного строителя на здание (siteTicks).</summary>
    public static int SiteTicks(BuildingSpec spec, double step) => WorkTicks(spec.Cost, step);

    /// <summary>Во сколько раз здание уровня level сильнее первого (levelFactor).</summary>
    public static double LevelFactor(double level) => 1 + LevelBonus * (level - 1);

    /// <summary>Что уровень даёт зданию (levelGain): extract, refine, store, trade, scan, power; null — не улучшают.</summary>
    public static string? LevelGain(BuildingSpec spec)
    {
        if (spec.Extract)
            return "extract";
        if (spec.Refines is not null)
            return "refine";
        if (spec.Stores)
            return "store";
        if (spec.Trades)
            return "trade";
        if (spec.Scan is not null)
            return "scan";
        return (spec.Power ?? 0) > 0 && spec.Burns is null ? "power" : null;
    }

    /// <summary>Цена подъёма с уровня level (upgradeCost): кредиты и готовое; scale — Rules.upgradeScale.</summary>
    public static (double Credits, Dictionary<string, double> Materials) UpgradeCost(BuildingSpec spec, double level, double scale = 1)
    {
        var materials = new Dictionary<string, double> { ["blocks"] = Math.Ceiling(Math.Ceiling(spec.Cost / UpgradeBlocks) * level * scale) };
        if (level >= 2)
            materials["parts"] = Math.Ceiling(Math.Ceiling(spec.Cost / UpgradeParts) * (level - 1) * scale);
        return (Round(spec.Cost * UpgradeShare * level * scale), materials);
    }

    /// <summary>Вложено в уровни сверх цены — по цене матча (upgradeSpent).</summary>
    public static double UpgradeSpent(BuildingSpec spec, double level)
    {
        double spent = 0;
        for (var below = 1; below < level; below++)
            spent += UpgradeCost(spec, below).Credits;
        return spent;
    }

    /// <summary>Тиков работы одного строителя на подъём с уровня level (upgradeTicks).</summary>
    public static int UpgradeTicks(BuildingSpec spec, double level, double step, double scale = 1) => WorkTicks(UpgradeCost(spec, level, scale).Credits, step);

    /// <summary>Сколько вернёт разбор здания этого уровня (refundOf).</summary>
    public static double RefundOf(BuildingSpec spec, double level = 1) => Math.Floor((spec.Cost + UpgradeSpent(spec, level)) * DemolishRefund);

    /// <summary>Сколько стоит дочинить до max (repairCostOf); share — доля цены за починку от нуля.</summary>
    public static double RepairCostOf(double cost, double health, double share, double max = 1) => Math.Ceiling(cost * share * (max - health));

    /// <summary>Строят ли этот вид строители: у него место в меню (BUILDABLE).</summary>
    public static bool Buildable(BuildingSpec spec) => spec.Menu is not null;

    /// <summary>
    /// Нужна ли зданию своя зона (needsZone): здание с собственной зоной начинает новую, оборону и трубы ставят где
    /// угодно, лишь бы не в чужой зоне.
    /// </summary>
    public static bool NeedsZone(BuildingSpec spec) => spec.Zone is null && !spec.Defense && spec.Pipe is null;

    /// <summary>Радиус круга зоны готового здания: своя зона, расширение или EXPAND_RADIUS.</summary>
    public static double ZoneRadius(BuildingSpec spec) => spec.Zone ?? spec.Expand ?? ExpandRadius;

    /// <summary>
    /// Открыто ли здание игроку (isUnlocked): строится строителями и, с деревом технологий, стоят готовыми все нужные
    /// здания и исследована его тема. ready — есть ли у игрока готовое здание вида, researched — открыта ли тема.
    /// </summary>
    public static bool Unlocked(BuildingSpec spec, bool techTree, Func<string, bool> ready, Func<string?, bool> researched)
    {
        if (!Buildable(spec))
            return false;
        if (!techTree)
            return true;
        foreach (var need in spec.Requires)
            if (!ready(need))
                return false;
        return researched(spec.Research);
    }

    /// <summary>Попадает ли точка хотя бы в один круг (inCircles); circles — x, y и радиус подряд.</summary>
    public static bool InCircles(IReadOnlyList<double> circles, double x, double y)
    {
        for (var i = 0; i + 2 < circles.Count; i += 3)
            if (Hypot(x - circles[i], y - circles[i + 1]) <= circles[i + 2])
                return true;
        return false;
    }

    /// <summary>Расстояние от точки до основания (linkGap): задевает ли основание круг.</summary>
    public static double LinkGap(double cx, double cy, double x, double y, double width, double height) =>
        Hypot(Math.Max(Math.Max(x - cx, 0), cx - x - width), Math.Max(Math.Max(y - cy, 0), cy - y - height));

    /// <summary>Расстояние от точки до прямоугольника; внутри — ноль (rectDistance).</summary>
    public static double RectDistance(double left, double top, double width, double height, double x, double y) =>
        Hypot(Math.Max(Math.Max(left - x, 0), x - left - width), Math.Max(Math.Max(top - y, 0), y - top - height));

    /// <summary>
    /// Задевает ли основание чужую зону (inForeignZone): проверяются центр и углы основания. circles — круги зон других
    /// игроков (x, y, радиус подряд).
    /// </summary>
    public static bool TouchesCircles(IReadOnlyList<double> circles, double x, double y, double width, double height) =>
        InCircles(circles, x + width / 2, y + height / 2)
        || InCircles(circles, x, y) || InCircles(circles, x + width, y)
        || InCircles(circles, x, y + height) || InCircles(circles, x + width, y + height);

    /// <summary>Math.hypot двух чисел: так же, как sqrt(x² + y²) у JS для конечных.</summary>
    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
