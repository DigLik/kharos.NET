using Base.Host.Kernel;
using Base.Host.Sections.Combat;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Магазины юнитов (supply.ts): у юнита с UnitSpec.ammo есть Magazine, выстрел тратит из него свою долю, а юнит, которому
/// не хватает на самый дешёвый выстрел, пуст — молчит и бросает цель. Боевые крючки (<see cref="Hooks"/> боя) ставит
/// <see cref="DefenseSection"/>. Без компонента магазин полный — так у юнитов, выставленных без него.
/// </summary>
internal static class Magazines
{
    /// <summary>На сколько секунд непрерывного огня всеми стволами хватает полного магазина (MAGAZINE_SECONDS).</summary>
    public const double Seconds = 50;

    /// <summary>То же у пехоты: запас больше, чтобы ответом на снабжение не стала толпа пехоты (INFANTRY_MAGAZINE_SECONDS).</summary>
    public const double InfantrySeconds = 120;

    /// <summary>Остатки меньше этого не в счёт (EPSILON).</summary>
    public const double Epsilon = 1e-9;

    private static readonly Dictionary<(string, double), double> Costs = [];
    private static readonly Dictionary<string, double> Cheapest = new(StringComparer.Ordinal);

    /// <summary>Производное от данных; схема загружена заново — забыть.</summary>
    public static void Reset()
    {
        Costs.Clear();
        Cheapest.Clear();
        _highest = null;
    }

    /// <summary>Сколько боеприпасов в полном магазине юнита; ноль — стреляет бесплатно (magazineOf).</summary>
    public static double CapacityOf(string type) => Factory.Unit(type)?.Ammo ?? 0;

    /// <summary>Оружие юнита и его вооружённых турелей: время перезарядки каждого ствола.</summary>
    private static List<double> ReloadsOf(string type)
    {
        var reloads = new List<double>();
        if (Factory.Unit(type) is not { } spec)
            return reloads;
        if (spec.Weapon is { } own && Core.Tables.Weapons.TryGetValue(own, out var weapon))
            reloads.Add(weapon.Reload);
        foreach (var mount in spec.Mounts)
            if (Core.Tables.Turrets.TryGetValue(mount.Turret, out var turret) && turret.Weapon is { } name && Core.Tables.Weapons.TryGetValue(name, out var gun))
                reloads.Add(gun.Reload);
        return reloads;
    }

    /// <summary>Сколько боеприпасов тратит выстрел юнита оружием с такой перезарядкой: магазин делится на секунды огня всех стволов (shotCost).</summary>
    public static double ShotCost(string type, double reload)
    {
        if (Costs.TryGetValue((type, reload), out var cost))
            return cost;
        var capacity = CapacityOf(type);
        var seconds = Factory.Unit(type)?.Kind == "infantry" ? InfantrySeconds : Seconds;
        cost = capacity != 0 ? capacity * reload / (seconds * Math.Max(1, ReloadsOf(type).Count)) : 0;
        Costs[(type, reload)] = cost;
        return cost;
    }

    /// <summary>Самый дешёвый выстрел юнита: меньше этого в магазине — юнит пуст (cheapestShot).</summary>
    public static double CheapestShot(string type)
    {
        if (Cheapest.TryGetValue(type, out var cost))
            return cost;
        var reloads = ReloadsOf(type);
        cost = reloads.Count > 0 ? reloads.Min(reload => ShotCost(type, reload)) : 0;
        Cheapest[type] = cost;
        return cost;
    }

    private static double? _highest;

    /// <summary>Самый дорогой из порогов «пуст» среди всех видов юнитов.</summary>
    private static double HighestThreshold() =>
        _highest ??= Core.Tables.Units.Keys.Select(CheapestShot).DefaultIfEmpty(0).Max();

    /// <summary>Сколько боеприпасов в магазине юнита; без компонента — полный (ammoOf).</summary>
    public static double AmmoOf(Call call, uint entity, string type) =>
        call.Has(entity, S.Magazine) ? call.Num(entity, S.Magazine, S.MagazineAmmo) : CapacityOf(type);

    /// <summary>Пуст ли магазин юнита: не хватает на самый дешёвый его выстрел. Без магазина и с выключенным правилом — не пуст (isEmpty).</summary>
    public static bool IsEmpty(Call call, uint entity)
    {
        // Без магазина юнит полон; полнее самого высокого порога — тоже не пуст: вид тогда не спрашиваем (горячий путь боя).
        if (!Core.Rules.UnitAmmo || !call.Has(entity, S.Magazine))
            return false;
        var ammo = call.Num(entity, S.Magazine, S.MagazineAmmo);
        if (ammo >= HighestThreshold() - Epsilon || call.UnitType(entity) is not { } type || CapacityOf(type) == 0)
            return false;
        return ammo < CheapestShot(type) - Epsilon;
    }

    /// <summary>Хватит ли юниту боеприпасов на выстрел этим оружием (unitHasAmmo).</summary>
    public static bool HasAmmo(Call call, uint entity, string type, Weapon weapon)
    {
        if (!Core.Rules.UnitAmmo)
            return true;
        var cost = ShotCost(type, weapon.Reload);
        return cost == 0 || AmmoOf(call, entity, type) >= cost - Epsilon;
    }

    /// <summary>Списывает выстрел из магазина юнита; проверять запас — <see cref="HasAmmo"/> (spendAmmo).</summary>
    public static void Spend(Call call, uint entity, string type, Weapon weapon)
    {
        if (!Core.Rules.UnitAmmo)
            return;
        var cost = ShotCost(type, weapon.Reload);
        if (cost == 0)
            return;
        if (call.Has(entity, S.Magazine))
            call.Set(entity, S.Magazine).Number(S.MagazineAmmo, Math.Max(0, call.Num(entity, S.Magazine, S.MagazineAmmo) - cost));
        else
            call.Add(entity, S.Magazine).Number(S.MagazineAmmo, Math.Max(0, CapacityOf(type) - cost));
    }
}
