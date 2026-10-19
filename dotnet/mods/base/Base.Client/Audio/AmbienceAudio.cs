using Base.Client.Sounds;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>Окраска ноты подложки биомом: нижняя октава, звон и дребезг.</summary>
internal readonly record struct NoteColor(double Sub, double Bell, double Grit);

/// <summary>
/// Фон заброшенной пустой земли под низким небом — перенос mods/base/audio/ambience.luau (ambience.ts и soundscape.ts).
/// Всё время дует ветер, порывами; в развалинах гудит и воет; в непогоду шуршат песок и дождь. Под этим — едва слышная
/// медленная подложка в миноре, а вдалеке, сквозь эхо, изредка скрипит ржавый металл, лязгает железо или, в бурю, рокочет
/// гром. Ветер крепнет с погодой и высотой камеры, подложка ночью темнее и реже. Тревога и бой (пульс, гул пилы) идут от
/// <see cref="Battle"/> через <see cref="Mood"/>. «Успехи» — открытая тема и отбитая атака (<see cref="Mood.Triumphs"/>) и
/// c.Shared["triumphs"] (число), если мод управления его ведёт.
/// </summary>
internal sealed class AmbienceAudio
{
    // Громкости слоёв фона. Все тихие: фон слышен, когда в игре затишье.
    private const double WindLevel = 0.14;
    private static readonly double[] HowlLevels = [2.4, 1.5];
    private const double HissLevel = 0.22;
    private const double PadLevel = 0.017;
    private const double DistantLevel = 0.18;
    private const float ReverbLevel = 0.55f;
    private const double FadeIn = 6;
    private const double Step = 0.1;
    private static readonly (float Frequency, double Drift, float Q)[] Howls = [(420, 0.25, 9), (780, 0.2, 14)];
    // Аккорды подложки: ре минор — си-бемоль — до — ля минор, медленно и по кругу; первая нота — бас.
    private static readonly int[][] Chords =
    [
        [38, 57, 62, 65, 69, 76],
        [34, 53, 57, 62, 65, 72],
        [36, 55, 60, 64, 67, 74],
        [33, 52, 57, 60, 64, 71],
    ];
    private const double ChordTime = 24;
    private static readonly double[] NoteGap = [2.5, 5];
    private static readonly double[] DistantGap = [9, 26];
    private static readonly (double Rise, double Fall) EaseThreat = (6, 30), EaseBattle = (4, 25), EaseBiome = (25, 25);
    private const double PulseLevel = 0.11;
    private static readonly double[] PulseBeat = [1.7, 0.8];
    private const double PulseEcho = 0.28;
    private const double PulseEchoLevel = 0.45;
    private const double DroneLevel = 0.025;
    private const double TriumphSeconds = 40;
    private const double ErgSub = 0.5;
    private const double SaltBell = 0.35;
    private const double RedGrit = 0.12;
    // Ветер погоды, тайлов в секунду, при котором фон дует во всю силу; масштабы камеры для высоты.
    private const double StrongWind = 2.5;
    private const double MinZoom = 4, MaxZoom = 96;

    private static double Midi(double note) => 440 * Math.Pow(2, (note - 69) / 12);

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static double Ease(double value, double target, double seconds, (double Rise, double Fall) spec) =>
        value + (target - value) * (1 - Math.Exp(-seconds / (target > value ? spec.Rise : spec.Fall)));

    /// <summary>Плавное число от −1 до 1, которое медленно бродит.</summary>
    public static Func<double, double> Wander(double seed)
    {
        double Value(double step)
        {
            var next = Dsp.Random(seed * 1000003 + step * 7919 + 1);
            next();
            return next() * 2 - 1;
        }
        return t =>
        {
            var step = Math.Floor(t);
            var f = t - step;
            var a = Value(step);
            return a + (Value(step + 1) - a) * f * f * (3 - 2 * f);
        };
    }

    // ------------------------------------------------------------------ голоса

    /// <summary>Узлы основы: ветер, гулы, шорох, фильтры подложки и пульса, гул боя, эхо.</summary>
    internal sealed record BedNodes(int Reverb, int WindFilter, int WindGain, int[] HowlBands, int[] HowlLevels, int[] HowlPans, int HissGain, int PadFilter, int PadGain, int PadDry, int PadWet, int PulseFilter, int DroneFilter, int DroneGain, int[] Drones);

    /// <summary>Основа фона: один голос, который звучит всегда; now — миг его начала, секунды звука.</summary>
    public static (VoiceGraph Graph, BedNodes Nodes) Bed(double now)
    {
        var s = new VoiceGraphBuilder();
        var output = s.Out;
        // Плавное вступление за FadeIn секунд.
        s.SetOut(0);
        output[ParamId.Gain].Set(0, now).Ramp(1, now + FadeIn);
        var reverb = s.Reverb("reverb");
        var reverbGain = s.Gain(ReverbLevel);
        reverb.To(reverbGain).To(output);

        var windFilter = s.Filter(FilterType.Lowpass, 400, 0.7f);
        var windGain = s.Gain(0);
        s.Sample("noise:brown", loop: true, offset: 0).To(windFilter).To(windGain).To(output);

        var bands = new int[Howls.Length];
        var levels = new int[Howls.Length];
        var pans = new int[Howls.Length];
        for (var i = 0; i < Howls.Length; i++)
        {
            var band = s.Filter(FilterType.Bandpass, Howls[i].Frequency, Howls[i].Q);
            var level = s.Gain(0);
            var panner = s.Pan(0);
            s.Sample("noise:pink", loop: true, offset: 2.1f + i * 3.3f).To(band).To(level).To(panner);
            panner.To(output);
            panner.To(reverb);
            (bands[i], levels[i], pans[i]) = (band.Index, level.Index, panner.Index);
        }

        var hissGain = s.Gain(0);
        s.Sample("noise:white", loop: true, offset: 0.7f).To(s.Filter(FilterType.Highpass, 1500, 0.7f)).To(s.Filter(FilterType.Lowpass, 6500, 0.7f)).To(hissGain).To(output);

        var padFilter = s.Filter(FilterType.Lowpass, 900, 0.7f);
        var padGain = s.Gain((float)PadLevel);
        padFilter.To(padGain);
        var padDry = s.Gain(1);
        var padWet = s.Gain(1);
        padGain.To(padDry).To(output);
        padGain.To(padWet).To(reverb);

        var pulseFilter = s.Filter(FilterType.Lowpass, 260, 0.7f);
        pulseFilter.To(s.Gain(1)).To(output);

        var droneFilter = s.Filter(FilterType.Lowpass, 150, 1.2f);
        var droneGain = s.Gain(0);
        droneFilter.To(droneGain);
        droneGain.To(output);
        droneGain.To(reverb);
        var drones = new int[2];
        float[] detunes = [-7, 7];
        for (var i = 0; i < drones.Length; i++)
        {
            var oscillator = s.Osc(Wave.Sawtooth, (float)Midi(Chords[0][0]), detunes[i]);
            oscillator.To(droneFilter);
            drones[i] = oscillator.Index;
        }
        return (s.Build(), new BedNodes(reverb.Index, windFilter.Index, windGain.Index, bands, levels, pans, hissGain.Index, padFilter.Index, padGain.Index, padDry.Index, padWet.Index, pulseFilter.Index, droneFilter.Index, droneGain.Index, drones));
    }

    /// <summary>Нота подложки: всплывает за rise секунд, держится hold и тает за fade. Окраска биома — нижняя октава, звон и дребезг.</summary>
    public static VoiceGraph Note(double at, double frequency, double rise, double hold, double fade, double level, NoteColor color)
    {
        var s = new VoiceGraphBuilder();
        var endAt = at + rise + hold + fade;
        var envelope = s.Gain(0);
        envelope[ParamId.Gain].Set(0, at).Ramp((float)level, at + rise).Set((float)level, at + rise + hold).Ramp(0, endAt);
        envelope.To(s.Out);
        (Wave Wave, double Ratio, float Detune, double Level)[] voices =
        [
            (Wave.Sine, 1, -4, 1),
            (Wave.Triangle, 1, 5, 0.45),
            (Wave.Sine, 0.5, 0, ErgSub * color.Sub),
            (Wave.Square, 1, 13, RedGrit * color.Grit),
        ];
        foreach (var voice in voices)
            if (voice.Level > 0.01)
                s.Osc(voice.Wave, (float)(frequency * voice.Ratio), voice.Detune).To(s.Gain((float)voice.Level)).To(envelope);
        // Звон — своя огибающая: вступает с нотой и тает раньше неё, как удар по стеклу.
        if (color.Bell > 0.01)
        {
            var ring = s.Gain(0);
            var peak = at + Math.Min(rise, 0.4);
            ring[ParamId.Gain].Set(0, at).Ramp((float)(level * SaltBell * color.Bell), peak).Exp(0.0001f, peak + 3);
            s.Osc(Wave.Sine, (float)(frequency * 3)).To(ring).To(s.Out);
        }
        return s.Build();
    }

    /// <summary>Удар пульса: низкая нота, вступает сразу и тает за секунду.</summary>
    public static VoiceGraph Beat(double at, double frequency, double level)
    {
        var s = new VoiceGraphBuilder();
        var envelope = s.Gain(0);
        envelope[ParamId.Gain].Set(0, at).Ramp((float)level, at + 0.04).Exp(0.0001f, at + 0.9);
        envelope.To(s.Out);
        s.Osc(Wave.Sine, (float)frequency).To(envelope);
        s.Osc(Wave.Triangle, (float)(frequency * 2)).To(s.Gain(0.35f)).To(envelope);
        return s.Build();
    }

    /// <summary>Что-то далеко: запись сквозь даль — глухо, в стороне и больше эхом, чем напрямую.</summary>
    public static VoiceGraph Far(string name, double rate, double muffle, double pan, double dry, double level, VoiceEndpoint reverb)
    {
        var s = new VoiceGraphBuilder();
        var source = s.Sample(name, rate: (float)rate);
        var muffled = s.Filter(FilterType.Lowpass, (float)muffle, 0.7f);
        var panner = s.Pan((float)pan);
        source.To(muffled).To(panner);
        panner.To(s.Gain((float)dry)).To(s.Out);
        panner.To(s.Gain((float)level)).To(reverb);
        return s.Build();
    }

    // ------------------------------------------------------------------ состояние

    private Sound? _bed;
    private BedNodes _nodes = null!;
    private Func<double> _next;
    private double _time, _sinceStep = Step;
    private int _chord;
    private double _chordAt, _noteAt, _distantAt, _pulseAt;
    private double _threat, _battle;
    private readonly double[] _biomes = [0.25, 0.25, 0.25, 0.25];
    private double? _triumphs;
    private double _brightUntil = double.NegativeInfinity;
    private bool _fanfare;
    private readonly Func<double, double>[] _gust = [Wander(1), Wander(2)];
    private readonly Func<double, double>[] _howlSwell = [Wander(10), Wander(11)];
    private readonly Func<double, double>[] _howlPitch = [Wander(20), Wander(21)];
    private readonly Func<double, double>[] _howlPan = [Wander(30), Wander(31)];

    private readonly Mood _mood;

    /// <summary>Зерно случайности фона — по умолчанию текущее время, как os.time пилота; обстановка боя — от <see cref="Battle"/>.</summary>
    public AmbienceAudio(double? seed = null, Mood? mood = null)
    {
        _next = Dsp.Random(seed ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000);
        _mood = mood ?? new Mood();
    }

    private double Between(double[] range) => range[0] + _next() * (range[1] - range[0]);

    private void PlayNote(ClientContext c, double frequency, double at, double rise, double hold, double fade, double level, NoteColor color) =>
        c.Audio.Voice(Note(at, frequency, rise, hold, fade, level, color), new VoiceOptions { Into = _bed!.Node(_nodes.PadFilter), At = at, Length = (float)(rise + hold + fade + 0.1) });

    private void PlayBeat(ClientContext c, double frequency, double at, double level) =>
        c.Audio.Voice(Beat(at, frequency, level), new VoiceOptions { Into = _bed!.Node(_nodes.PulseFilter), At = at, Length = 1 });

    private void PlayFar(ClientContext c, string name, double level)
    {
        var thunder = name == "thunder";
        var rate = 0.85 + _next() * 0.3;
        var muffle = thunder ? 400 : 1200 + _next() * 1500;
        var pan = (_next() * 2 - 1) * 0.8;
        var graph = Far(name, rate, muffle, pan, level * (thunder ? 0.8 : 0.3), level, _bed!.Node(_nodes.Reverb));
        c.Audio.Voice(graph, new VoiceOptions { Bus = Bus.World, Length = 7 });
    }

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        var seconds = c.Dt;
        var now = c.Audio.Now;
        if (_bed is null)
        {
            var (graph, nodes) = Bed(now);
            _nodes = nodes;
            _bed = c.Audio.Voice(graph, new VoiceOptions { Bus = Bus.World });
            _chordAt = now;
            _noteAt = now + 1;
            _distantAt = now + Between(DistantGap);
            _pulseAt = now;
            PlayNote(c, Midi(Chords[0][0]), now + 0.5, 6, ChordTime - 6, 8, 0.8, default);
        }
        _time += seconds;
        _sinceStep += seconds;
        if (_sinceStep < Step)
            return;
        var elapsed = _sinceStep;
        _sinceStep = 0;

        // Обстановка (soundscape.ts): погода, высота камеры, биом под ней; тревога и бой — от звуков боя.
        var atmosphere = c.Atmosphere;
        var wind = Clamp01(Math.Sqrt((double)atmosphere.WindX * atmosphere.WindX + (double)atmosphere.WindY * atmosphere.WindY) / StrongWind);
        var precipitation = Clamp01(atmosphere.Precipitation);
        var light = Clamp01(atmosphere.Light);
        var altitude = Clamp01(Math.Log(MaxZoom / c.Camera.Zoom) / Math.Log(MaxZoom / MinZoom));
        _threat = Ease(_threat, Clamp01(_mood.Threat), elapsed, EaseThreat);
        _battle = Ease(_battle, Clamp01(_mood.Battle), elapsed, EaseBattle);
        var here = c.Terrain(c.Camera.X, c.Camera.Y, out var terrain) ? terrain.Biome : -1;
        for (var i = 0; i < 4; i++)
            _biomes[i] = Ease(_biomes[i], here == i ? 1 : 0, elapsed, EaseBiome);
        var (erg, salt, red, marsh) = (_biomes[0], _biomes[1], _biomes[2], _biomes[3]);
        var color = new NoteColor(erg, salt, red);
        var successes = (c.Shared.TryGetValue("triumphs", out var shared) && shared is not null ? Convert.ToDouble(shared) : 0) + _mood.Triumphs;
        if (_triumphs is null)
            _triumphs = successes;
        else if (successes > _triumphs)
        {
            _triumphs = successes;
            _brightUntil = now + TriumphSeconds;
            _fanfare = true;
        }
        var n = _nodes;
        var bed = _bed;

        // Ветер дует всегда. Порыв — два медленных колебания разом.
        var strength = 0.3 + 0.7 * Math.Max(wind, precipitation);
        var gusting = Clamp01(0.5 + 0.35 * _gust[0](_time / 7) + 0.25 * _gust[1](_time / 2.3));
        var blow = WindLevel * (0.4 + 0.6 * strength) * (0.6 + 0.8 * gusting) * (0.75 + 0.5 * altitude);
        bed.Glide(n.WindGain, ParamId.Gain, (float)blow, 0.3f);
        bed.Glide(n.WindFilter, ParamId.Frequency, (float)(200 + (500 + 400 * altitude) * strength * (0.5 + gusting)), 0.3f);

        // Гулы накатывают и уходят; в сильный ветер чаще и громче, у земли слышнее.
        for (var i = 0; i < Howls.Length; i++)
        {
            var spec = Howls[i];
            var swell = Math.Pow(Clamp01(_howlSwell[i](_time / 9) + 0.15 * strength), 1.5);
            var level = HowlLevels[i] * swell * (0.25 + 0.75 * strength) * (1 - 0.4 * altitude);
            bed.Glide(n.HowlLevels[i], ParamId.Gain, (float)level, 0.5f);
            bed.Glide(n.HowlBands[i], ParamId.Frequency, (float)(spec.Frequency * (1 + spec.Drift * _howlPitch[i](_time / 5) + 0.15 * gusting)), 0.5f);
            bed.Glide(n.HowlPans[i], ParamId.Pan, (float)(0.7 * _howlPan[i](_time / 13)), 0.5f);
        }

        bed.Glide(n.HissGain, ParamId.Gain, (float)(HissLevel * Math.Pow(precipitation, 1.2) * (0.7 + 0.6 * gusting)), 0.4f);

        // Подложка: днём светлее, ночью темнее и чуть громче; в бурю отступает за ветер. Солончаки звонче, топи глуше.
        var open = (1 + 0.5 * salt) * (1 - 0.45 * marsh) * (1 + 0.3 * _battle);
        bed.Glide(n.PadFilter, ParamId.Frequency, (float)((450 + 900 * light * (1 - 0.5 * precipitation)) * open), 1);
        bed.Glide(n.PadGain, ParamId.Gain, (float)(PadLevel * (1 + 0.4 * (1 - light)) * (1 - 0.5 * precipitation) * (1 + 0.3 * _threat + 0.25 * _battle)), 1);
        bed.Glide(n.PadDry, ParamId.Gain, (float)(1 - 0.5 * marsh), 1);
        bed.Glide(n.PadWet, ParamId.Gain, (float)(1 + 0.6 * marsh), 1);

        bed.Glide(n.DroneGain, ParamId.Gain, (float)(DroneLevel * Math.Pow(_battle, 1.5)), 1);
        bed.Glide(n.DroneFilter, ParamId.Frequency, (float)(150 + 350 * _battle), 1);

        // Ноты подложки планируются чуть вперёд: точное время — у звука, не у кадров.
        var ahead = now + 0.5;
        if (ahead >= _chordAt + ChordTime)
        {
            _chordAt += ChordTime;
            if (_chordAt + ChordTime < now)
                _chordAt = now;
            _chord = (_chord + 1) % Chords.Length;
            PlayNote(c, Midi(Chords[_chord][0]), Math.Max(now, _chordAt), 6, ChordTime - 6, 8, 0.8, new NoteColor(color.Sub, 0, color.Grit));
            foreach (var drone in n.Drones)
                bed.Target(drone, ParamId.Frequency, (float)Midi(Chords[_chord][0]), Math.Max(now, _chordAt), 2);
        }
        var chord = Chords[_chord];
        var bass = chord[0];
        if (ahead >= _noteAt)
        {
            _noteAt = Math.Max(_noteAt, now);
            if (_fanfare)
            {
                // Успех: мажорное трезвучие от баса аккорда, нота за нотой вверх, звонко.
                _fanfare = false;
                int[] steps = [24, 28, 31, 36];
                for (var k = 0; k < steps.Length; k++)
                    PlayNote(c, Midi(bass + steps[k]), _noteAt + k * 0.7, 0.8, 1 + (3 - k) * 0.5, 5, 0.55, new NoteColor(color.Sub, Math.Max(0.6, salt), color.Grit));
                _noteAt += 4;
            }
            else
            {
                // Светлые аккорды после успеха: малая терция над басом становится большой.
                var notes = new int[chord.Length - 1];
                for (var i = 1; i < chord.Length; i++)
                    notes[i - 1] = now < _brightUntil && (chord[i] - bass) % 12 == 3 ? chord[i] + 1 : chord[i];
                var pitch = notes[(int)Math.Floor(_next() * notes.Length)];
                if (_next() < 0.35 * Math.Max(_threat, _battle))
                    pitch = bass + 25;
                else if (_next() < 0.4 * (1 - light))
                    pitch -= 12;
                var urgent = 1 - 0.6 * _battle;
                var rise = Between([2.5, 5]) * urgent;
                var hold = Between([1, 4]) * urgent;
                var fade = Between([4, 7]) * (1 - 0.4 * _battle);
                PlayNote(c, Midi(pitch), _noteAt, rise, hold, fade, 0.35 + _next() * 0.3, color);
                _noteAt += Between(NoteGap) * (1 + 0.3 * (1 - light)) * (1 - 0.2 * _threat) * (1 - 0.45 * _battle);
            }
        }

        // Пульс: слышен, пока есть тревога или бой.
        var pulseLevel = Math.Max(0.6 * _threat, _battle);
        if (pulseLevel < 0.03)
            _pulseAt = Math.Max(_pulseAt, now);
        else if (ahead >= _pulseAt)
        {
            _pulseAt = Math.Max(_pulseAt, now);
            var level = PulseLevel * pulseLevel;
            PlayBeat(c, Midi(bass), _pulseAt, level);
            PlayBeat(c, Midi(bass), _pulseAt + PulseEcho, level * PulseEchoLevel);
            _pulseAt += PulseBeat[0] + (PulseBeat[1] - PulseBeat[0]) * _battle;
        }

        // Вдалеке: в бурю — чаще гром, иначе скрип или лязг. С высоты земля дальше и тише.
        if (now >= _distantAt)
        {
            var name = _next() < precipitation * 0.8 ? "thunder" : _next() < 0.55 - 0.35 * Math.Max(_battle, 0.6 * red) ? "creak" : "clank";
            PlayFar(c, name, DistantLevel * (0.5 + 0.5 * _next()) * (1 - 0.5 * altitude) * (name == "thunder" ? 0.7 + precipitation : 1));
            _distantAt = now + Between(DistantGap) * (1 - 0.4 * precipitation) * (1 - 0.5 * _battle);
        }
    }
}
