using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Shared.Rules;

namespace Base.Host.Sections.Research;

/// <summary>
/// Уровни зданий (upgrades.ts). Шахту, переработку, хранилище, космопорт, радар и электростанцию поднимают до
/// <see cref="BuildRules.MaxLevel"/>; что даёт уровень, считают их разделы (<see cref="BuildRules.LevelFactor"/>). Цена —
/// кредиты и готовое (<see cref="BuildRules.UpgradeCost"/>): списывается сразу при заказе, готовое — со складов энергосети
/// здания, сначала его сети труб. Работу делают строители, как стройку (Construction: Upgrade.progress, затем уровень);
/// здание всё это время работает. Второй и третий уровни открывают темы levels2 и levels3.
/// </summary>
internal static class Upgrades
{
    /// <summary>Готовое ли своё здание (isReady).</summary>
    private static bool IsReady(Call call, uint player, uint building) => call.Has(building, S.Building) && !call.Has(building, S.Site) && call.IsOwn(player, building);

    /// <summary>Поднимают ли это здание игрока сейчас (isUpgradable): своё, готовое, из тех, что улучшают, не на верхнем уровне и не в работе.</summary>
    public static bool IsUpgradable(Call call, uint player, uint building)
    {
        if (!IsReady(call, player, building) || call.Has(building, S.Converting) || call.Has(building, R.Upgrade))
            return false;
        return Factory.Building(call.BuildingType(building)) is { } spec && BuildRules.LevelGain(spec) is not null && Level(call, building) < BuildRules.MaxLevel;
    }

    private static double Level(Call call, uint building) => call.Num(building, S.Building, S.BuildingLevel, 1);

    private static IEnumerable<(string Good, double Amount)> Items(Dictionary<string, double> materials) => materials.Select(pair => (pair.Key, pair.Value));

    /// <summary>Чего не хватает на следующий уровень здания (upgradeLack): research, credits, materials; всего хватает — null.</summary>
    public static string? Lack(Call call, uint player, uint building)
    {
        if (!call.Has(building, S.Building) || Factory.Building(call.BuildingType(building)) is not { } spec)
            return null;
        var level = Level(call, building);
        if (!Accounts.Researched(call, player, ResearchRules.LevelTopic(level)))
            return "research";
        var (credits, materials) = BuildRules.UpgradeCost(spec, level, Core.Rules.UpgradeScale);
        if (Accounts.Credits(call, player) < credits)
            return "credits";
        return new Networks(call).Has(building, Items(materials), wide: true) ? null : "materials";
    }

    /// <summary>
    /// Заказывает следующий уровень своего готового здания и посылает к нему строителей (orderUpgrade): кредиты и готовое
    /// списываются сразу, готовое — со складов энергосети здания, сначала из хранилищ.
    /// </summary>
    public static bool Order(Call call, uint player, uint building, IReadOnlyList<uint> builders)
    {
        if (!IsUpgradable(call, player, building) || Lack(call, player, building) is not null)
            return false;
        var spec = Factory.Building(call.BuildingType(building))!;
        var scale = Core.Rules.UpgradeScale;
        var (credits, materials) = BuildRules.UpgradeCost(spec, Level(call, building), scale);
        if (!Accounts.Pay(call, player, credits))
            return false;
        new Networks(call).Take(building, Items(materials), wide: true);
        call.Add(building, R.Upgrade).Number(R.UpgradeProgress, 0).Number(R.UpgradeScale, scale);
        Sites.AssignBuilders(call, player, building, builders);
        return true;
    }

    /// <summary>
    /// Отменяет улучшение (cancelUpgrade): кредиты возвращаются целиком по цене заказа, готовое — в хранилища энергосети
    /// здания, а чему там нет места — на землю у здания. Вложенная работа пропадает.
    /// </summary>
    public static bool Cancel(Call call, uint player, uint building)
    {
        if (!call.Has(building, R.Upgrade) || !IsReady(call, player, building) || Factory.Building(call.BuildingType(building)) is not { } spec)
            return false;
        var (credits, materials) = BuildRules.UpgradeCost(spec, Level(call, building), call.Num(building, R.Upgrade, R.UpgradeScale, 1));
        call.Remove(building, R.Upgrade);
        Accounts.Add(call, player, credits);
        new Networks(call).Return(building, Items(materials), wide: true);
        return true;
    }
}
