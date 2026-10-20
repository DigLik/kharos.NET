using Base.Host.Kernel;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Что бою нужно от разделов, которых ещё нет или которые идут после него: каждый ставит своё в Setup. Не поставили — бой
/// обходится без этого (сказано у каждого). Тактику (стойка, поход, место) бой читает и пишет в компоненте Tactics сам —
/// раздел Tactics (С8) может поставить свои <see cref="LeaveHome"/> и <see cref="ClearTactics"/>.
/// </summary>
internal static class Hooks
{
    // Магазин юнита бой читает сам, побочной таблицей Magazine: has — есть ли компонент, ammo — сколько в нём с тратами
    // этого тика. Чтение каждого стрелка через сцену стоило бы вызова инструмента на юнит в каждом тике.

    /// <summary>
    /// Магазин юнита (supply.ts, С5b): хватит ли юниту вида unit с магазином (has, ammo) на выстрел оружием weapon
    /// (unitHasAmmo). Нет — хватает всегда.
    /// </summary>
    public static Func<string, Weapon, bool, double, bool>? UnitHasAmmo { get; set; }

    /// <summary>
    /// Списывает выстрел из магазина (has, ammo) юнита carrier вида unit (spendAmmo, С5b): пишет Magazine и возвращает
    /// остаток; NaN — магазин не тронут. Нет — ничего не тратится.
    /// </summary>
    public static Func<Call, uint, string, Weapon, bool, double, double>? SpendAmmo { get; set; }

    /// <summary>Пуст ли магазин (has, ammo) юнита вида unit (isEmpty, С5b): такой молчит и бросает цель. Нет — не пуст.</summary>
    public static Func<string, bool, double, bool>? Empty { get; set; }

    /// <summary>
    /// Купола щитов тика (domes в shields.ts, С5b). Нет — щитов нет: урон не гасится, снаряды долетают. Щит получает урон
    /// попадания (<see cref="IShields.Absorb"/>) и останавливает снаряды на краю купола (<see cref="IShields.Stopper"/>).
    /// </summary>
    public static Func<Call, IShields?>? Shields { get; set; }

    /// <summary>Уходит из дома в погоню (leaveHome, С8). Нет — бой ставит Tactics.away и место сам.</summary>
    public static Action<Call, uint>? LeaveHome { get; set; }

    /// <summary>Снимает патруль и поход (clearTactics, С8). Нет — бой чистит Tactics сам.</summary>
    public static Action<Call, uint>? ClearTactics { get; set; }

    /// <summary>Отпустить грузовик (releaseHauler, С2b) при его гибели. Нет — ничего.</summary>
    public static Action<Call, uint>? ReleaseHauler { get; set; }
}

/// <summary>Щиты тика (shields.ts) для боя: С5b реализует.</summary>
internal interface IShields
{
    /// <summary>Есть ли хоть один купол.</summary>
    bool Any { get; }

    /// <summary>
    /// Сколько урона дойдёт до цели игрока player в (x, y), пришедшего из (fromX, fromY) (absorb): остальное берёт купол.
    /// fromAir — стрелял летающий.
    /// </summary>
    double Absorb(uint player, double x, double y, double damage, double fromX, double fromY, bool fromAir);

    /// <summary>
    /// Купол чужого щита на отрезке полёта снаряда (stopper): точка, где снаряд рвётся, и купол; нет — null.
    /// </summary>
    (double X, double Y, uint Dome)? Stopper(uint player, double x0, double y0, double x1, double y1, bool fromAir);

    /// <summary>Купол принял снаряд (stopShot): урон оружия.</summary>
    void Stop(uint dome, double damage);
}
