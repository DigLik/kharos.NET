using Base.Client.Audio;
using Base.Client.Sounds;
using Kharos.Base.Scenario;
using Kharos.Client.Game;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>
/// Моторы техники — packages/tests/motors.test.ts и motorsGame.test.ts. Езда (Drive) — над самой ездой, без мира; танк —
/// настоящим клиентом base на реплике партии: Viewer со звуком офлайн (команды Play, Voice — в <c>Sounds</c>, голоса —
/// в движке, который рендерит каждый кадр).
/// </summary>
public sealed class MotorsTests
{
    private const double Rate = 44100;

    private static double Rms(float[] data)
    {
        double energy = 0;
        foreach (var value in data)
            energy += value * (double)value;
        return Math.Sqrt(energy / data.Length);
    }

    [Fact(DisplayName = "петли моторов: конечные, не тишина, холостая и нагруженная одной длины")]
    public void MotorLoopsAreFiniteAudibleAndAlignedInLength()
    {
        foreach (var (name, motor) in MotorSounds.Motors)
        {
            var idle = MotorSounds.Design(Rate, motor, false);
            var load = MotorSounds.Design(Rate, motor, true);
            Assert.Equal(idle.Length, load.Length);
            foreach (var data in new[] { idle, load })
            {
                var rms = Rms(data);
                Assert.True(double.IsFinite(rms), name);
                Assert.True(rms > 0.05, $"{name}: {rms}");
            }
            Assert.NotEqual(idle, load);
        }
        foreach (var loop in new Func<double, float[]>[] { MotorSounds.Clatter, MotorSounds.Roll, MotorSounds.Squeal, MotorSounds.Screech })
            Assert.True(loop(Rate).Length > Rate);
    }

    private sealed record Sample(double Time, double Rpm, double Speed, int Gear, double Throttle);

    /// <summary>Едет по плану: (секунд, доля скорости); события — с их временем, езда — в конце.</summary>
    private static (Drive Drive, List<(DriveEvent Event, double Time)> Events, List<Sample> Samples) Ride(string name, params (double Seconds, double Target)[] plan)
    {
        var drive = new Drive(false);
        var events = new List<(DriveEvent, double)>();
        const double step = 1.0 / 60;
        double time = 0;
        var samples = new List<Sample>();
        var handling = name switch { "petrol" => Handling.Petrol, "diesel" => Handling.Diesel, _ => Handling.Heavy };
        foreach (var (seconds, target) in plan)
        {
            for (double t = 0; t < seconds; t += step)
            {
                var happened = drive.Step(MotorSounds.Motors[name], handling, target, step);
                if (happened != DriveEvent.None)
                    events.Add((happened, time));
                samples.Add(new Sample(time, drive.Rpm, drive.Speed, drive.Gear, drive.Throttle));
                time += step;
            }
        }
        return (drive, events, samples);
    }

    [Fact(DisplayName = "мотор заводится стартером, разгоняется по передачам, тормозит и глохнет, постояв")]
    public void MotorStartsCranksShiftsBrakesAndStalls()
    {
        var (drive, events, samples) = Ride("diesel", (1, 0), (6, 1), (Drive.IdleHold + 2, 0));
        Assert.Equal([DriveEvent.Start, DriveEvent.Brake], events.Select(e => e.Event));
        // Пока крутит стартер, машина для звука стоит.
        var crank = samples.Where(s => s.Time > 1 && s.Time < 1 + MotorSounds.StartCrank("ignitionDiesel") - 0.05).ToList();
        Assert.NotEmpty(crank);
        Assert.All(crank, s => Assert.Equal(0, s.Speed));
        // Передачи только вверх, пока разгоняется, и на каждой обороты сначала падают, потом растут.
        var gears = samples.Where(s => s.Time < 7).Select(s => s.Gear).ToList();
        Assert.Equal(Handling.Diesel.Gears.Length - 1, gears.Max());
        for (var i = 1; i < gears.Count; i++)
            Assert.True(gears[i] >= gears[i - 1]);
        // На ровном ходу газ не в пол.
        var cruise = samples.First(s => Math.Abs(s.Time - 6.5) < 0.01);
        Assert.Equal(1, cruise.Speed);
        Assert.True(cruise.Throttle < 0.7);
        Assert.Equal(DrivePhase.Off, drive.Phase);
        Assert.Equal(0, drive.Sound(MotorSounds.Motors["diesel"], 0).Level);
    }

    [Fact(DisplayName = "бензиновый не шипит тормозом, а короткая заминка на ходу — не остановка")]
    public void PetrolDoesNotHissAndAShortHitchIsNotAStop()
    {
        var (_, events, _) = Ride("petrol", (3, 1), (0.1, 0), (3, 1), (2, 0));
        Assert.Equal([DriveEvent.Start], events.Select(e => e.Event));
    }

    [Fact(DisplayName = "гусеничный скрипит, вертясь на месте, и не скрипит на ходу")]
    public void TracksSqueakTurningOnTheSpotButNotWhileDriving()
    {
        var standing = Ride("heavy", (3, 1), (2, 0)).Drive;
        Assert.True(standing.Sound(MotorSounds.Motors["heavy"], 1).Squeal > 0.9);
        var moving = Ride("heavy", (5, 1)).Drive;
        Assert.Equal(0, moving.Sound(MotorSounds.Motors["heavy"], 1).Squeal);
    }

    [Fact(DisplayName = "летающий не глохнет и раскручивает винты со взлёта")]
    public void FlierDoesNotStallAndSpinsUpItsRotors()
    {
        var drive = new Drive(true);
        for (double t = 0; t < 30; t += 1.0 / 60)
            Assert.Equal(DriveEvent.None, drive.Step(MotorSounds.Motors["rotor"], Handling.Air, 0, 1.0 / 60));
        Assert.Equal(DrivePhase.Run, drive.Phase);
        Assert.Equal(1, drive.SpoolUp);
        Assert.True(drive.Sound(MotorSounds.Motors["rotor"], 0).Level > 0.9);
    }

    // ===== Танк в партии =====

    private static (Sim Sim, Viewer Viewer, uint Tank) Tank()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        var tank = sim.PutUnit("tank", 1, 2.5, 2.5);
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 800, Audio = true });
        viewer.Camera = new CameraState(0, 0, 32);
        return (sim, viewer, tank);
    }

    /// <summary>Тик за тиком: танк едет вдоль x с полной скоростью (2,2 тайла в секунду) или стоит.</summary>
    private static void Move(Sim sim, Viewer viewer, uint tank, double seconds, bool moving)
    {
        var x = sim.Num(tank, "Position.x");
        for (var tick = 0; tick < Sim.TicksIn(seconds); tick++)
        {
            if (moving)
                sim.Set(tank, "Position.x", x += 2.2 * Sim.Dt);
            viewer.Run(Sim.Dt);
        }
    }

    /// <summary>Голоса мода, что звучат, пока их не отпустят (без начала и длины): основа фона и моторы.</summary>
    private static List<SoundCall> Held(Viewer viewer) =>
        [.. viewer.Sounds.Of(Ops.Voice).Where(call => call.Name is null && float.IsNaN(call.Values[1]) && float.IsNaN(call.Values[2]))];

    private static double Loudness(Viewer viewer, double from, double to)
    {
        var rate = Kharos.Audio.AudioFormat.SampleRate;
        var start = (int)(from * rate);
        var count = (int)((to - from) * rate);
        return Math.Sqrt(viewer.Output.Skip(start).Take(count).Sum(v => v * (double)v) / count);
    }

    [Fact(DisplayName = "танк заводится, тронувшись, звучит своим голосом, встав — шипит тормозом и глохнет")]
    public void TankStartsWhenMovingHasItsOwnVoiceAndHissesAndStallsWhenStopped()
    {
        // Опыт без танка на ходу: тот же фон, то же окно — чтобы слышно было, что добавил мотор.
        double quiet;
        {
            var (sim, viewer, tank) = Tank();
            using var _ = sim;
            Move(sim, viewer, tank, 4, moving: false);
            quiet = Loudness(viewer, 2, 4);
            Assert.DoesNotContain(viewer.Sounds.All, call => call.Name is "ignitionDiesel" or "ignition" or "airBrake");
        }

        var (world, watcher, driver) = Tank();
        using var __ = world;
        watcher.Run(0.2);
        Assert.Empty(watcher.Sounds.Of(Ops.Play));
        var bed = Held(watcher).Count;
        // Едет три секунды на полной скорости: тик за тиком.
        Move(world, watcher, driver, 3, moving: true);
        Assert.Equal(["ignitionDiesel"], watcher.Sounds.Of(Ops.Play).Select(call => call.Name!));
        var motors = Held(watcher).Skip(bed).ToList();
        Assert.Single(motors);
        var cruising = Loudness(watcher, 1.5, 3);
        Assert.True(cruising > 1.5 * quiet, $"мотор не слышен: {cruising} против фона {quiet}");
        var handle = motors[0].Voice;
        Assert.NotNull(watcher.Game.Mods[0].Sounds[handle]);

        // Встал: тормоз, потом холостые, потом заглох — и голос отпущен.
        Move(world, watcher, driver, 10, moving: false);
        Assert.Equal(["ignitionDiesel", "airBrake"], watcher.Sounds.Of(Ops.Play).Select(call => call.Name!));
        Assert.True(watcher.Game.Mods[0].Sounds[handle] is null or { IsFinished: true });
        Assert.Empty(watcher.Log);
    }
}
