using Base.Client.Sounds;

namespace Base.Client.Audio;

/// <summary>Как машина разгоняется и тормозит (доля полной скорости в секунду) и где кончается каждая передача.</summary>
internal sealed record Handling(double Accel, double Brake, double[] Gears)
{
    public static readonly Handling Petrol = new(2.2, 2.5, [0.22, 0.45, 0.72, 1.6]);
    public static readonly Handling Diesel = new(1.4, 1.8, [0.3, 0.6, 1.6]);
    public static readonly Handling Heavy = new(1.2, 1.6, [0.35, 0.7, 1.4]);
    public static readonly Handling Air = new(0.6, 0.8, []);
}

/// <summary>Фаза мотора: заглушен, крутит стартер, работает.</summary>
internal enum DrivePhase { Off, Crank, Run }

/// <summary>Что случилось на шаге езды.</summary>
internal enum DriveEvent { None, Start, Brake }

/// <summary>
/// Езда машины для звука — перенос mods/base/lib/drive.luau (drive.ts). Хост двигает юнит сразу на полной скорости и
/// останавливает разом, а мотор так не умеет: здесь у машины своя скорость, которая догоняет настоящую, — с ней звучат
/// разгон по передачам, ровный ход, сброс газа и остановка. Постояв на холостых, мотор глохнет; тронувшись снова,
/// заводится стартером. Летающие не глохнут.
/// </summary>
internal sealed class Drive
{
    /// <summary>Столько секунд мотор стоит на холостых, а потом глохнет.</summary>
    public const double IdleHold = 6;

    private const double Shift = 0.1;
    private const double ShiftDrop = 0.35;
    private const double Still = 0.06;
    private const double BrakeFrom = 0.35;
    private const double Spool = 2.5;
    private const double ScreechTurn = 0.5;
    private const double ScreechSpeed = 0.5;
    private const double RpmLag = 0.12;
    private const double ThrottleLag = 0.08;

    public DrivePhase Phase;
    public double Speed, Rpm, Throttle;
    public int Gear;
    public double ShiftLeft, Timer, SpoolUp;

    /// <summary>Новая машина: наземная заглушена давно, летающая только взлетает.</summary>
    public Drive(bool air)
    {
        Phase = air ? DrivePhase.Run : DrivePhase.Off;
        Timer = air ? 0 : double.PositiveInfinity;
    }

    private static double Follow(double value, double target, double seconds, double lag) => value + (target - value) * (1 - Math.Exp(-seconds / lag));

    /// <summary>Шаг езды на seconds: target — с какой долей полной скорости юнит едет на самом деле.</summary>
    public DriveEvent Step(Motor motor, Handling handling, double target, double seconds)
    {
        var happened = DriveEvent.None;
        var air = motor.Start is null;
        var moving = target > Still;
        Timer += seconds;
        if (air)
            SpoolUp = Math.Min(1, SpoolUp + seconds / Spool);
        if (Phase == DrivePhase.Off)
        {
            if (!moving)
                return DriveEvent.None;
            Phase = DrivePhase.Crank;
            Timer = 0;
            happened = DriveEvent.Start;
        }
        if (Phase == DrivePhase.Crank)
        {
            // Пока крутит стартер, машина для звука ещё стоит: тронется, когда мотор схватит, — с перегазовкой.
            if (Timer < MotorSounds.StartCrank(motor.Start!))
                return happened;
            Phase = DrivePhase.Run;
            Timer = 0;
            Rpm = 0.6;
            Throttle = 1;
        }

        var before = Speed;
        ShiftLeft = Math.Max(0, ShiftLeft - seconds);
        var wanted = moving ? target : 0;
        if (wanted > Speed)
        {
            if (ShiftLeft == 0)
                Speed = Math.Min(wanted, Speed + handling.Accel * seconds);
        }
        else
            Speed = Math.Max(wanted, Speed - handling.Brake * seconds);
        if (motor.Brake && !moving && before >= BrakeFrom && Speed < BrakeFrom)
            happened = DriveEvent.Brake;

        // Газ: разгон — в пол, ровный ход — чуть, сброс — отпущен. На переключении газ сброшен.
        var accelerating = wanted > Speed + 0.02;
        var cruising = moving && !accelerating && wanted >= Speed - 0.02;
        var throttle = accelerating ? 1 : cruising ? 0.35 + 0.25 * Math.Min(1, Speed) : 0;
        if (ShiftLeft > 0)
            throttle = 0.1;

        double rpm;
        if (air)
            rpm = Math.Min(1, Speed * 0.75 + throttle * 0.25);
        else
        {
            // Передача — по скорости: вверх на пределе своей, вниз с запасом. Gears[Gear - 1] — предел текущей передачи.
            var gears = handling.Gears;
            while (Gear < gears.Length - 1 && Speed >= gears[Gear])
            {
                Gear++;
                if (accelerating)
                {
                    ShiftLeft = Shift;
                    Rpm = Math.Min(Rpm, ShiftDrop + 0.1);
                    throttle = 0.1;
                }
            }
            while (Gear > 0 && Speed < gears[Gear - 1] * 0.8)
                Gear--;
            var low = Gear > 0 ? gears[Gear - 1] : 0;
            var share = Math.Clamp((Speed - low) / (gears[Gear] - low), 0, 1);
            rpm = Gear > 0 ? ShiftDrop + (1 - ShiftDrop) * share : share;
            // Стоя, мотор держит холостые; на газу с места — чуть подвывает.
            if (Speed < Still)
                rpm = accelerating ? 0.15 : 0;
        }
        Rpm = Follow(Rpm, rpm, seconds, ShiftLeft > 0 ? RpmLag / 2 : RpmLag);
        Throttle = Follow(Throttle, throttle, seconds, ThrottleLag);

        if (!air)
        {
            if (moving || Speed > Still)
                Timer = 0;
            else if (Timer >= IdleHold)
            {
                Phase = DrivePhase.Off;
                Gear = 0;
                Timer = 0;
            }
        }
        return happened;
    }

    /// <summary>Как звучит мотор при этой езде; громкость, сторону и ясность ставит тот, кто знает, где машина. turn — доля поворота.</summary>
    public MotorKnobs Sound(Motor motor, double turn)
    {
        var air = motor.Start is null;
        var running = Phase == DrivePhase.Run;
        // Заглохший мотор, сбегая с оборотов, опускается ниже холостых.
        var rate = running ? 1 + (motor.Revs - 1) * Rpm : 0.6;
        double squeal = 0;
        if (running)
        {
            if (motor.Running == "clatter")
                squeal = Math.Min(1, turn) * Math.Max(0, 1 - Speed * 3);
            else if (motor.Running == "roll")
                squeal = Math.Clamp((turn - ScreechTurn) / (1 - ScreechTurn), 0, 1) * Math.Max(0, Speed - ScreechSpeed) / (1 - ScreechSpeed);
        }
        return new MotorKnobs
        {
            Rate = air ? rate * (0.6 + 0.4 * SpoolUp) : rate,
            Load = running ? Throttle : 0,
            Level = running ? (air ? 0.4 + 0.6 * SpoolUp : 0.55 + 0.45 * Throttle) : 0,
            Running = Math.Min(1, Speed * 1.2),
            RunningRate = Speed,
            Squeal = squeal,
        };
    }
}
