using Base.Shared.Data;

namespace Base.Shared.Rules;

/// <summary>
/// Правила энергии, общие у хоста и клиента (income.ts, busy.ts, buildings.ts): простой потребителей, теснота,
/// перегруз, подмога главного здания. Только числа и данные — мир читает тот, кто зовёт.
/// </summary>
public static class PowerRules
{
    /// <summary>Пока у игрока меньше стольких кредитов, его главное здание даёт подмогу (RELIEF_BELOW).</summary>
    public const double ReliefBelow = 600;

    /// <summary>Доход главного здания в подмогу, в секунду (RELIEF_INCOME): одна на игрока.</summary>
    public const double ReliefIncome = 1;

    /// <summary>Какую долю своей энергии просит потребитель, которому нечего делать (IDLE_SHARE).</summary>
    public const double IdleShare = 1.0 / 3;

    /// <summary>
    /// Доля прочности в секунду, которую теряет электростанция при спросе вдвое больше выработки (OVERLOAD_DAMAGE); при
    /// меньшем перегрузе — меньше во столько же раз, при большем — не растёт.
    /// </summary>
    public const double OverloadDamage = 0.01;

    /// <summary>Раз во сколько тиков хост решает, кто из потребителей занят делом (BUSY_TICKS).</summary>
    public const int BusyTicks = 10;

    /// <summary>Сколько секунд кончивший дело потребитель ещё значится занятым (HOLD).</summary>
    public const double BusyHold = 2;

    /// <summary>Сколько просит без дела потребитель, которому в работе нужно power (меньше нуля): треть, вверх (idlePower).</summary>
    public static double IdlePower(double power) => -Math.Ceiling(-power * IdleShare);

    /// <summary>Потребитель без простоя: пока включён, работает — генератор материи, радар (alwaysBusy).</summary>
    public static bool AlwaysBusy(BuildingSpec spec) => (spec.Income ?? 0) != 0 || (spec.Scan ?? 0) != 0;

    /// <summary>Выключается ли здание игроком: потребитель энергии и реактор — тот бережёт стержни (isSwitchable).</summary>
    public static bool Switchable(BuildingSpec spec) => (spec.Power ?? 0) < 0 || spec.Burns is not null;

    /// <summary>
    /// Как здание вида spec изменит баланс зоны, где уже crowd тесных зданий (powerOf): больше нуля — добавит
    /// выработки, меньше — попросит столько энергии.
    /// </summary>
    public static double PowerOf(BuildingSpec spec, double crowd)
    {
        var power = spec.Power ?? 0;
        return power < 0 && spec.Crowding ? power * (crowd + 1) : power;
    }

    /// <summary>То же для здания без дела (idlePowerOf): у вида без простоя — столько же, сколько в работе.</summary>
    public static double IdlePowerOf(BuildingSpec spec, double crowd)
    {
        var power = spec.Power ?? 0;
        if (power >= 0 || AlwaysBusy(spec))
            return PowerOf(spec, crowd);
        return IdlePower(power) * (spec.Crowding ? crowd + 1 : 1);
    }

    /// <summary>
    /// Перегруз зоны (overloadOf): 0 — энергии хватает и на полную нагрузку, 1 — полная нагрузка вдвое больше того, что
    /// есть (с запасом накопителей), и выше.
    /// </summary>
    public static double OverloadOf(double produced, double peak, double spare)
    {
        var able = produced + spare;
        return produced > 0 && peak > able ? Math.Min(1, peak / able - 1) : 0;
    }
}
