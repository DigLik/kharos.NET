using Base.Shared.Data;

namespace Base.Shared.Rules;

/// <summary>
/// Правила ракет и радара, общие у хоста и клиента (silos.ts, radar.ts, vision.ts): сроки меток, высота полёта, вспышки
/// разведки. Только числа и данные — мир читает тот, кто зовёт.
/// </summary>
public static class MissileRules
{
    /// <summary>Сколько секунд место пуска отмечено у игроков с радаром (LAUNCH_MARK).</summary>
    public const double LaunchMark = 60;

    /// <summary>За сколько секунд до удара тем, чьё под ним, звучит второй сигнал (WARNING).</summary>
    public const double Warning = 15;

    /// <summary>Раз во сколько секунд сверяется, чьё под ударом (WATCH).</summary>
    public const double Watch = 1;

    /// <summary>Сколько секунд вспышка взрыва открывает всем его круг (GLARE).</summary>
    public const double Glare = 5;

    /// <summary>Высота полёта ядерной ракеты над землёй, тайлов, и за сколько секунд она набирается (CRUISE, CLIMB).</summary>
    public const double Cruise = 2.5, Climb = 3;

    /// <summary>Размер вспышки сбитой ракеты, тайлов: картинка, урона нет (AIRBURST).</summary>
    public const double Airburst = 1.5;

    /// <summary>Раз в сколько секунд радар вспыхивает (SCAN_EVERY).</summary>
    public const double ScanEvery = 10;

    /// <summary>С какой доли нужной энергии радар видит дальше обычного здания (RADAR_POWER).</summary>
    public const double RadarPower = 0.5;

    /// <summary>Раз во сколько тиков радары смотрят, не пора ли (CHECK_TICKS).</summary>
    public const int ScanCheckTicks = 10;

    /// <summary>Размер круга вспышки радара, тайлов, и сколько тиков он горит (SCAN_SIZE, SCAN_TICKS).</summary>
    public const double ScanSize = 6;

    public const int ScanTicks = 30;

    /// <summary>На высоте полёта ли ракета, которая летит age секунд и которой осталось left тайлов (missileLift).</summary>
    public static double Lift(double age, double left, double speed) => Cruise * Math.Max(0, Math.Min(1, Math.Min(age / Climb, left / (speed * Climb))));

    /// <summary>На сколько тайлов от своего центра разведывает радар этого вида и уровня; ноль — не разведывает (scanRadius).</summary>
    public static double ScanRadius(BuildingSpec spec, double level = 1) => (spec.Scan ?? 0) * BuildRules.LevelFactor(level);
}
