using Base.Client.Sounds;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Моторы техники — перенос mods/base/audio/motors.luau (motors.ts). Каждая машина — своя езда (<see cref="Drive"/>):
/// заводится стартером, разгоняется по передачам, едет, сбрасывает газ, тормозит, стоит на холостых и глохнет. Ближайшие
/// <see cref="Voices"/> машин слышны каждая своим голосом, со своей стороны и со своими оборотами; остальные — общим
/// голосом толпы своего мотора. Мотор и тон юнита — data.motor и data.pitch его вида.
/// </summary>
internal sealed class MotorsAudio
{
    /// <summary>Громкость мотора вблизи: тяжёлые громче.</summary>
    public static readonly Dictionary<string, double> Loudness = new(StringComparer.Ordinal)
    {
        ["petrol"] = 0.3, ["diesel"] = 0.38, ["heavy"] = 0.48, ["rotor"] = 0.4, ["props"] = 0.2, ["jet"] = 0.38,
    };

    private static readonly Dictionary<string, Handling> Handlings = new(StringComparer.Ordinal)
    {
        ["petrol"] = Handling.Petrol, ["diesel"] = Handling.Diesel, ["heavy"] = Handling.Heavy, ["rotor"] = Handling.Air, ["props"] = Handling.Air, ["jet"] = Handling.Air,
    };

    /// <summary>Сколько машин слышно каждую своим голосом; звучащая держит голос, пока её не перекричат во столько раз.</summary>
    public const int Voices = 6;
    private const double Keep = 1.4;
    // За краем экрана мотор стихает на этом расстоянии, в долях полуширины экрана.
    private const double Hearing = 0.6;
    // Толпа: громкость — 1 − e^(−сумма/Saturate), глуше и тише своих голосов.
    private const double Saturate = 3;
    private const double CrowdLevel = 0.6;
    private const double CrowdClarity = 0.4;
    // Громкость стартера и тормоза против мотора; чаще EventGap они не звучат.
    private const double StartVolume = 0.5;
    private const double BrakeVolume = 0.7;
    private const double EventGap = 0.25;
    // Медленнее Still юнит стоит, если не сдвигается дольше StopHold; быстрее Jump — перенос, а не ход.
    private const double Still = 0.06;
    private const double StopHold = 0.3;
    private const double Jump = 2.2;

    /// <summary>Одна машина: езда, замер хода и голос.</summary>
    private sealed class Car(string motorName, Motor motor, UnitSound kind, Drive drive, double x, double y, double facing, ulong tick, double time, double pitch)
    {
        public readonly string MotorName = motorName;
        public readonly Motor Motor = motor;
        public readonly Drive Drive = drive;
        public double X = x, Y = y, Facing = facing, Time = time, Target, Turn, Still;
        public ulong Tick = tick;
        public readonly double Speed = kind.Speed, TurnRate = kind.Turn, Pitch = pitch;
        public Sound? Voice;
        public MotorSounds.Nodes Nodes;
    }

    /// <summary>Общий голос толпы одного мотора.</summary>
    private sealed class Crowd
    {
        public Sound? Voice;
        public MotorSounds.Nodes Nodes;
        public double Sum, Side, Rate, Load, Running;
    }

    private readonly record struct Heard(uint Entity, Car Car, float Near, float Side, MotorKnobs Sound, double Score);

    private readonly Dictionary<uint, Car> _cars = [];
    private readonly Dictionary<string, Crowd> _crowds = new(StringComparer.Ordinal);
    // null — вид без мотора (или не юнит): чтобы не спрашивать вид каждый кадр.
    private readonly Dictionary<uint, UnitSound?> _kinds = [];
    private readonly HashSet<uint> _alive = [];
    private readonly List<Heard> _heard = [];
    private readonly List<uint> _gone = [];
    private (ComponentRef Unit, ComponentRef Position, ComponentRef Facing)? _components;
    private KindData? _data;
    private (double X, double Y, double Angle)[] _poses = new (double, double, double)[64];
    // Пехота шагает общей петлёй: где была в прошлом кадре, сколько вблизи идущих и с какой стороны.
    private readonly Dictionary<uint, (double X, double Y)> _foot = [];
    private const double StepsLevel = 0.16;
    // Медленнее этого, в тайлах за секунду, пехотинец стоит (в TS — тысячная тайла за тик).
    private const double StepSpeed = 0.02;
    private double _clock, _lastStart = double.NegativeInfinity, _lastBrake = double.NegativeInfinity;

    private static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor(angle / (Math.PI * 2) + 0.5);

    private void Event(ClientContext c, bool brake, string name, double volume, float side)
    {
        ref var last = ref (brake ? ref _lastBrake : ref _lastStart);
        if (volume <= 0.01 || _clock - last < EventGap)
            return;
        last = _clock;
        c.Audio.Play(name, (float)volume, side * 0.8f, (float)(1 + (c.Audio.Random() * 2 - 1) * 0.08));
    }

    private static void Release(Car car)
    {
        if (car.Voice is null)
            return;
        car.Voice.Release(0.25f);
        car.Voice = null;
    }

    /// <summary>Голос мотора; squeal — со скрипом разворота (нужен, когда мотор ездит по земле).</summary>
    private static (Sound Voice, MotorSounds.Nodes Nodes) VoiceOf(ClientContext c, string motorName, bool squeal)
    {
        var running = MotorSounds.Motors[motorName].Running;
        var scrape = squeal && running is not null ? running == "clatter" ? "squeal" : "screech" : null;
        var offset = (float)(c.Audio.Random() * MotorSounds.LoopSeconds);
        var (graph, nodes) = MotorSounds.Voice(motorName, running, scrape, offset);
        return (c.Audio.Voice(graph, new VoiceOptions { Bus = Bus.World }), nodes);
    }

    /// <summary>
    /// Новый замер хода: юнит сдвинулся на тике. Скорость — доля его полной, поворот — доля наибыстрейшего. Места
    /// приходят сглаженными между тиками (чистых тиковых у мода нет): сглаженное место — то же, что тиковое, на тик
    /// позже, так что расстояние делится на прошедшее «время тиков» (тик + доля тика).
    /// </summary>
    private static void Measure(Car car, double x, double y, double facing, ulong tick, double time, double tickDt, double seconds)
    {
        if (tick == car.Tick)
            return;
        var elapsed = (time - car.Time) * tickDt;
        if (elapsed < 1e-4)
        {
            car.Tick = tick;
            return;
        }
        var speed = Math.Sqrt((x - car.X) * (x - car.X) + (y - car.Y) * (y - car.Y)) / elapsed / car.Speed;
        var turn = Math.Abs(Wrap(facing - car.Facing)) / elapsed / car.TurnRate;
        (car.X, car.Y, car.Facing, car.Tick, car.Time, car.Turn) = (x, y, facing, tick, time, turn);
        if (speed > Jump)
            return;
        if (speed > Still)
        {
            car.Still = 0;
            car.Target = speed;
        }
        else
        {
            car.Still += Math.Max(elapsed, seconds);
            if (car.Still >= StopHold)
                car.Target = 0;
        }
    }

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        _components ??= (c.Component("Unit"), c.Component("Position"), c.Component("Facing"));
        _data ??= KindData.Read(c);
        var seconds = c.Dt;
        _clock += seconds;
        _heard.Clear();
        _alive.Clear();
        double steps = 0, stepsSide = 0;
        var tick = c.Tick;
        var time = c.Tick + c.Alpha;
        var units = c.Each(_components.Value.Unit, _components.Value.Position, _components.Value.Facing);
        if (_poses.Length < units.Length)
            _poses = new (double, double, double)[Math.Max(units.Length, _poses.Length * 2)];
        var poses = _poses.AsSpan(0, units.Length);
        c.Poses(units, poses);
        for (var i = 0; i < units.Length; i++)
        {
            var e = units[i];
            _alive.Add(e);
            if (!_kinds.TryGetValue(e, out var kind))
            {
                var proto = c.Proto(e);
                _kinds[e] = kind = proto >= 0 && c.ProtoPath(proto) is { } path && _data.Units.TryGetValue(path, out var found) && MotorSounds.Motors.ContainsKey(found.Motor) ? found : null;
            }
            var (x, y, facing) = poses[i];
            if (double.IsNaN(x))
                continue;
            var near = c.Nearness(x, y, Hearing);
            var side = c.Side(x);
            if (kind is null)
            {
                // У пехоты мотора нет — она шагает.
                if (near > 0 && _foot.TryGetValue(e, out var before) && Math.Sqrt((x - before.X) * (x - before.X) + (y - before.Y) * (y - before.Y)) >= StepSpeed * seconds)
                {
                    steps += near;
                    stepsSide += near * side;
                }
                _foot[e] = (x, y);
                continue;
            }
            if (!_cars.TryGetValue(e, out var car))
            {
                var spec = MotorSounds.Motors[kind.Motor];
                _cars[e] = car = new Car(kind.Motor, spec, kind, new Drive(spec.Start is null), x, y, facing, tick, time, kind.Pitch * (0.96 + e * 0.618034 % 1 * 0.08));
            }
            Measure(car, x, y, facing, tick, time, c.TickDt, seconds);
            var motor = car.Motor;
            var happened = car.Drive.Step(motor, Handlings[car.MotorName], car.Target, seconds);
            var loudness = Loudness[car.MotorName] * near;
            if (happened == DriveEvent.Start && motor.Start is not null)
                Event(c, false, motor.Start, loudness * StartVolume, side);
            if (happened == DriveEvent.Brake)
                Event(c, true, "airBrake", loudness * BrakeVolume, side);
            // Заглохший слышен ещё секунду: мотор сбегает с оборотов.
            if (near <= 0 || (car.Drive.Phase == DrivePhase.Off && car.Drive.Timer > 1))
            {
                Release(car);
                continue;
            }
            var sound = car.Drive.Sound(motor, Math.Min(1, car.Turn));
            sound.Rate *= car.Pitch;
            _heard.Add(new Heard(e, car, near, side, sound, loudness * (0.6 + 0.4 * car.Drive.Throttle) * (car.Voice is not null ? Keep : 1)));
        }
        _gone.Clear();
        foreach (var entity in _cars.Keys)
            if (!_alive.Contains(entity))
                _gone.Add(entity);
        foreach (var entity in _gone)
        {
            Release(_cars[entity]);
            _cars.Remove(entity);
        }
        if (_foot.Count > units.Length)
        {
            _gone.Clear();
            foreach (var entity in _foot.Keys)
                if (!_alive.Contains(entity))
                    _gone.Add(entity);
            foreach (var entity in _gone)
                _foot.Remove(entity);
        }
        if (_kinds.Count > units.Length)
        {
            _gone.Clear();
            foreach (var entity in _kinds.Keys)
                if (!_alive.Contains(entity))
                    _gone.Add(entity);
            foreach (var entity in _gone)
                _kinds.Remove(entity);
        }

        // Громче всех слышные — своими голосами, остальные — толпой.
        _heard.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Entity.CompareTo(b.Entity));
        foreach (var crowd in _crowds.Values)
            crowd.Sum = crowd.Side = crowd.Rate = crowd.Load = crowd.Running = 0;
        for (var index = 0; index < _heard.Count; index++)
        {
            var entry = _heard[index];
            var car = entry.Car;
            var sound = entry.Sound;
            if (index < Voices)
            {
                if (car.Voice is null)
                    (car.Voice, car.Nodes) = VoiceOf(c, car.MotorName, car.Motor.Running is not null);
            }
            else
                Release(car);
            if (car.Voice is not null)
            {
                sound.Clarity = entry.Near;
                MotorSounds.Update(car.Voice, car.Nodes, sound);
                car.Voice.Place((float)(Loudness[car.MotorName] * entry.Near), entry.Side * 0.8f);
            }
            else if (sound.Level > 0)
            {
                if (!_crowds.TryGetValue(car.MotorName, out var crowd))
                    _crowds[car.MotorName] = crowd = new Crowd();
                crowd.Sum += entry.Near;
                crowd.Side += entry.Near * entry.Side;
                crowd.Rate += entry.Near * sound.Rate;
                crowd.Load += entry.Near * sound.Load;
                crowd.Running += entry.Near * sound.Running;
            }
        }
        foreach (var (name, crowd) in _crowds)
        {
            if (crowd.Sum == 0)
            {
                crowd.Voice?.Release(0.25f);
                crowd.Voice = null;
                continue;
            }
            if (crowd.Voice is null)
                (crowd.Voice, crowd.Nodes) = VoiceOf(c, name, false);
            var running = crowd.Running / crowd.Sum;
            MotorSounds.Update(crowd.Voice, crowd.Nodes, new MotorKnobs
            {
                Rate = crowd.Rate / crowd.Sum, Load = crowd.Load / crowd.Sum, Level = 1, Running = running, RunningRate = running, Squeal = 0, Clarity = CrowdClarity,
            });
            crowd.Voice.Place((float)(Loudness[name] * CrowdLevel * (1 - Math.Exp(-crowd.Sum / Saturate))), (float)(crowd.Side / crowd.Sum * 0.8));
        }
        c.Audio.Loop("steps", (float)(StepsLevel * (1 - Math.Exp(-steps / Saturate))), steps > 0 ? (float)(stepsSide / steps) : 0);
    }
}
