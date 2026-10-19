using Base.Client.Sounds;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Music;

/// <summary>
/// Музыка меню — перенос audio/music.ts: медленные минорные аккорды под эхо и неспешная мелодия поверх. Ре минор —
/// си-бемоль — до — ля минор, как подложка игры (audio/ambience.ts): музыка меню и фон игры одного рода. Звучит, пока
/// открыто меню (режим оболочки Menu): вступает за <see cref="FadeIn"/> секунд, а когда меню закрыли, стихает за
/// <see cref="FadeOut"/> и отпускается; новое меню заводит новый голос, и два перехода перекрываются. Всё идёт по шине
/// music: общую громкость, выключение и громкость группы «музыка» ставит движок (AudioPrefs). Ноты планируются чуть
/// вперёд: точное время — у звука, а не у кадров.
/// </summary>
internal sealed class MenuMusic
{
    /// <summary>Громкость музыки при полной громкости игры.</summary>
    public const float Level = 0.32f;

    /// <summary>За сколько секунд музыка вступает и за сколько стихает, когда меню закрыли.</summary>
    public const double FadeIn = 5, FadeOut = 0.8;

    // Сколько эха в музыке: она звучит как издалека, над пустой землёй.
    private const double Wet = 0.7;

    // Аккорды: первая нота — бас, остальные — пэд (номера MIDI).
    private static readonly int[][] Chords = [[38, 57, 62, 65, 69], [34, 53, 58, 62, 65], [36, 55, 60, 64, 67], [33, 52, 57, 60, 64]];
    private const double ChordTime = 12;

    // Мелодия — ре минорная пентатоника в верхней октаве: из неё любая нота ложится на любой аккорд.
    private static readonly int[] Melody = [74, 77, 79, 81, 84, 86];
    private static readonly double[] MelodyGap = [1.2, 3];
    private static readonly double[] MelodyFade = [1.5, 3];

    // Насколько вперёд планируются ноты, секунды, и как часто смотрится расписание.
    private const double Ahead = 0.6, Check = 0.2;

    private static double Midi(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

    private Sound? _bed;
    private int _bus;
    private Func<double> _next;
    private double _chordAt, _melodyAt, _since = Check;
    private int _chord = -1, _last = -1;

    /// <summary>Зерно случайности мелодии — по умолчанию текущее время, как Date.now() в TS.</summary>
    public MenuMusic(double? seed = null)
    {
        _next = Dsp.Random(seed ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100000);
    }

    /// <summary>Играет ли музыка (голос заведён и не отпущен).</summary>
    public bool Playing => _bed is not null;

    /// <summary>Основа: эхо и фильтр шины музыки, вступление за <see cref="FadeIn"/> секунд; now — миг начала, секунды звука.</summary>
    public static (VoiceGraph Graph, int Bus) Bed(double now)
    {
        var s = new VoiceGraphBuilder();
        var output = s.Out;
        s.SetOut(0);
        output[ParamId.Gain].Set(0, now).Ramp(Level, now + FadeIn);
        var reverb = s.Reverb("reverb");
        reverb.To(s.Gain((float)Wet)).To(output);
        // Нотам: фильтр шины — тёплый, без верхов; из него звук идёт и напрямую, и в эхо.
        var bus = s.Filter(FilterType.Lowpass, 2400, 0.7f);
        bus.To(s.Gain((float)(1 - Wet * 0.5))).To(output);
        bus.To(reverb);
        return (s.Build(), bus.Index);
    }

    /// <summary>
    /// Нота: всплывает за rise, держится hold, тает за fade. Две чуть разные волны — звук тёплый; bright — светлее.
    /// pan — сторона.
    /// </summary>
    public static VoiceGraph Note(double frequency, double at, double rise, double hold, double fade, double volume, double pan, bool bright)
    {
        var s = new VoiceGraphBuilder();
        var envelope = s.Gain(0);
        var endAt = at + rise + hold + fade;
        envelope[ParamId.Gain].Set(0, at).Ramp((float)volume, at + rise).Set((float)volume, at + rise + hold).Exp(0.0001f, endAt);
        envelope.To(s.Pan((float)pan)).To(s.Out);
        s.Osc(bright ? Wave.Triangle : Wave.Sine, (float)frequency, -5).To(s.Gain(1)).To(envelope);
        s.Osc(Wave.Triangle, (float)frequency, 6).To(s.Gain(bright ? 0.25f : 0.4f)).To(envelope);
        return s.Build();
    }

    private double Between(double[] range) => range[0] + _next() * (range[1] - range[0]);

    private void Play(ClientContext c, double frequency, double at, double rise, double hold, double fade, double volume, double pan = 0, bool bright = false) =>
        c.Audio.Voice(Note(frequency, at, rise, hold, fade, volume, pan, bright),
            new VoiceOptions { Bus = Bus.Music, Into = _bed!.Node(_bus), At = at, Length = (float)(rise + hold + fade + 0.1) });

    /// <summary>Кадр музыки: меню открыто — музыка играет, закрыто — стихает.</summary>
    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        var menu = (c.Shell & ShellMode.Menu) != 0;
        var now = c.Audio.Now;
        if (!menu)
        {
            if (_bed is null)
                return;
            // Меню закрыли: музыка стихает и всё освобождает; ноты, что ещё в пути, тают вместе с основой.
            _bed.Release((float)FadeOut);
            _bed = null;
            return;
        }
        if (_bed is null)
        {
            var (graph, bus) = Bed(now);
            _bus = bus;
            _bed = c.Audio.Voice(graph, new VoiceOptions { Bus = Bus.Music });
            _chord = _last = -1;
            _chordAt = now + 0.3;
            _melodyAt = _chordAt + 4;
            _since = Check;
        }
        _since += c.Dt;
        if (_since < Check)
            return;
        _since = 0;
        var ahead = now + Ahead;
        // Вкладку усыпляли — не догоняем упущенное, а продолжаем с этого мига.
        if (_chordAt < now - ChordTime)
            _chordAt = now;
        while (_chordAt <= ahead)
        {
            _chord = (_chord + 1) % Chords.Length;
            var chord = Chords[_chord];
            Play(c, Midi(chord[0]), _chordAt, 2, ChordTime - 2, 4, 0.22);
            Play(c, Midi(chord[0] + 12), _chordAt, 3, ChordTime - 3, 4, 0.06);
            for (var i = 1; i < chord.Length; i++)
                Play(c, Midi(chord[i]), _chordAt + (i - 1) * 0.35, 3, ChordTime - 4, 5, 0.05, (i - 1) / (double)(chord.Length - 2) * 1.2 - 0.6);
            _chordAt += ChordTime;
        }
        if (_melodyAt < now - 5)
            _melodyAt = now;
        while (_melodyAt <= ahead)
        {
            // Мелодия ходит шагами, а не скачет: следующая нота — рядом с прошлой.
            _last = _last < 0
                ? (int)Math.Floor(_next() * Melody.Length)
                : Math.Max(0, Math.Min(Melody.Length - 1, _last + (int)Math.Round((_next() * 2 - 1) * 2)));
            Play(c, Midi(Melody[_last]), _melodyAt, 0.02, 0.15, Between(MelodyFade), 0.07, (_next() * 2 - 1) * 0.5, true);
            _melodyAt += Between(MelodyGap) * (_next() < 0.15 ? 3 : 1);
        }
    }
}
