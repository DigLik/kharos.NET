using Kharos.Audio.Dsp;
using Kharos.Audio.Engine;
using Kharos.Audio.Graph;

namespace Kharos.Audio;

/// <summary>Как завести голос (c.audio:voice в пилоте).</summary>
public sealed record VoiceOptions
{
    /// <summary>Шина; по умолчанию world.</summary>
    public Bus Bus { get; init; } = Bus.World;

    /// <summary>Узел другого голоса вместо шины (into): нота подложки уходит в фильтр фона.</summary>
    public Endpoint? Into { get; init; }

    /// <summary>Начало, секунды контекста; NaN — сейчас.</summary>
    public double At { get; init; } = double.NaN;

    /// <summary>Длина, секунды; NaN — пока не отпустят.</summary>
    public float Length { get; init; } = float.NaN;

    /// <summary>Частота осцилляторов без своей частоты, Гц.</summary>
    public float Note { get; init; } = 440f;

    /// <summary>Сила огибающих.</summary>
    public float Velocity { get; init; } = 1f;
}

/// <summary>Настройки движка звука.</summary>
public sealed record AudioEngineOptions
{
    /// <summary>Зерно случайности (варианты, смещения петель, разброс высоты); null — от часов.</summary>
    public uint? Seed { get; init; }

    /// <summary>Общая громкость.</summary>
    public float Master { get; init; } = 0.6f;

    /// <summary>Больше разовых звуков разом не звучит: в большом бою остальные пропускаются.</summary>
    public int MaxOneShots { get; init; } = 32;

    /// <summary>Компрессор на выходе; без него — линейный путь (для проверок и инструментов).</summary>
    public bool Compressor { get; init; } = true;
}

/// <summary>
/// Звук игры: банк записей, голоса из графов, шины, компрессор (перенос apps/client/src/audio.rs). Методы — для одного
/// потока игры; рендер идёт в аудиопотоке <see cref="OpenAlOutput"/> или по запросу (<see cref="Render"/>) без
/// устройства. Команды уходят в аудиопоток через очередь без блокировок и применяются в начале ближайшего кванта.
/// </summary>
public sealed class AudioEngine
{
    /// <summary>Как быстро петля догоняет новую громкость, секунды.</summary>
    public const float LoopGlide = 0.25f;

    private readonly Mixer _mixer;
    private readonly SampleBuffer[] _noise;
    private readonly Dictionary<string, VoiceGraph> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sound> _loops = new(StringComparer.Ordinal);
    private readonly VoiceContext _context;
    private uint _seed;
    private uint _nextId = 1;
    private int _rendering;

    public AudioEngine(AudioEngineOptions? options = null)
    {
        options ??= new AudioEngineOptions();
        Options = options;
        _seed = options.Seed ?? ((uint)DateTime.UtcNow.Ticks | 1);
        if (_seed == 0)
            _seed = 1;
        _mixer = new Mixer(options.Master, options.MaxOneShots, options.Compressor);
        _noise = [Noise(NoiseColor.White), Noise(NoiseColor.Pink), Noise(NoiseColor.Brown)];
        _context = new VoiceContext(Bank, _noise, Random);
    }

    public AudioEngineOptions Options { get; }

    /// <summary>Записи по именам.</summary>
    public SampleBank Bank { get; } = new();

    /// <summary>Время контекста, секунды: начало ещё не посчитанного кванта.</summary>
    public double Now => _mixer.PublishedFrame / (double)AudioFormat.SampleRate;

    /// <summary>Сколько разовых звуков звучит.</summary>
    public int OneShots => _mixer.OneShots;

    /// <summary>Сжатие компрессора на выходе, дБ (≤ 0; читать — из потока рендера или после офлайн-рендера).</summary>
    public float Reduction => _mixer.Reduction;

    /// <summary>Голосов в графе (после офлайн-рендера).</summary>
    public int ActiveVoices => _mixer.VoiceCount;

    /// <summary>Подключённый вывод (null — только офлайн-рендер).</summary>
    public OpenAlOutput? Output { get; internal set; }

    internal Mixer Mixer => _mixer;

    /// <summary>Шум по кругу (2 с), как у пилота: белый, розовый, бурый.</summary>
    public SampleBuffer NoiseBuffer(NoiseColor color) => _noise[(int)color];

    /// <summary>Случайное число [0, 1] (xorshift32, как random в audio.rs).</summary>
    public double Random()
    {
        var state = _seed;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        _seed = state;
        return state / (double)uint.MaxValue;
    }

    /// <summary>Запомнить голос по имени вида (виды раздела sounds/ с graph).</summary>
    public void RegisterVoice(string kind, VoiceGraph graph) => _kinds[kind] = graph;

    public bool HasVoice(string kind) => _kinds.ContainsKey(kind);

    /// <summary>Голос вида kind; null — такого вида нет.</summary>
    public Sound? Voice(string kind, VoiceOptions? options = null) =>
        _kinds.TryGetValue(kind, out var graph) ? Voice(graph, options) : null;

    /// <summary>Голос по графу.</summary>
    public Sound Voice(VoiceGraph graph, VoiceOptions? options = null)
    {
        var voice = VoiceFactory.Create(_nextId++, graph, options ?? new VoiceOptions(), _context, Now);
        Send(new StartVoice(voice));
        return new Sound(this, voice);
    }

    /// <summary>Голос по двоичной записи графа (от клиентского мода).</summary>
    public Sound Voice(ReadOnlySpan<byte> graph, VoiceOptions? options = null) => Voice(VoiceGraph.Decode(graph), options);

    /// <summary>Разовая запись банка: случайный вариант, громкость, сторона и скорость (высота), как play в audio.ts.</summary>
    public void Play(string name, float volume, float pan = 0f, float rate = 1f)
    {
        if (volume <= 0.01f || _mixer.OneShots >= Options.MaxOneShots || !Bank.TryGet(name, out var record))
            return;
        var count = record.Variants.Count;
        var buffer = record.Variants[Math.Min((int)(Random() * count), count - 1)];
        Send(new StartVoice(VoiceFactory.OneShot(_nextId++, buffer, record.Bus, volume, pan, rate, Now)));
    }

    /// <summary>
    /// Разовый звук в мире (c:sound): тише вдали и в своей стороне (hearing.ts), высота чуть случайна (detune — доля).
    /// </summary>
    public void PlayAt(string name, in Listener listener, double x, double y, float volume = 1f, float reach = 0.6f, float detune = 0.08f)
    {
        var gain = volume * Hearing.Nearness(listener, x, y, reach);
        var pan = Hearing.Side(listener, x) * 0.8f;
        var rate = (float)(1 + (Random() * 2 - 1) * detune);
        Play(name, gain, pan, rate);
    }

    /// <summary>Фоновая петля банка: громкость и сторона меняются плавно; первая просьба заводит её со случайного места.</summary>
    public void Loop(string name, float volume, float pan)
    {
        if (!_loops.TryGetValue(name, out var sound))
        {
            if (volume <= 0f || !Bank.TryGet(name, out var record))
                return;
            var builder = new VoiceGraphBuilder();
            builder.SetOut(new GainSpec(0f));
            builder.Sample(name, loop: true, variant: 0).To(builder.Out);
            sound = Voice(builder.Build(), new VoiceOptions { Bus = record.Bus });
            _loops[name] = sound;
        }
        sound.Automate(0, ParamId.Gain, AutomationKind.Target, MathF.Max(volume, 0f), double.NaN, LoopGlide);
        sound.Automate(Engine.Voice.PanIndex, ParamId.Pan, AutomationKind.Target, Math.Clamp(pan, -1f, 1f), double.NaN, LoopGlide);
    }

    /// <summary>Громкость шины — плавно за 50 мс.</summary>
    public void SetVolume(Bus bus, float volume) => Send(new BusVolume((int)bus, volume));

    /// <summary>Общая громкость: доля от <see cref="AudioEngineOptions.Master"/>.</summary>
    public void SetMasterVolume(float volume) => Send(new BusVolume(-1, Options.Master * volume));

    /// <summary>
    /// Офлайн-рендер без устройства: interleaved-стерео (L, R, …), кадров — длина / 2. Нельзя, пока подключён вывод.
    /// </summary>
    public void Render(Span<float> interleaved)
    {
        if (Output is not null)
            throw new InvalidOperationException("звук идёт в устройство: офлайн-рендер недоступен");
        RenderInternal(interleaved);
    }

    /// <summary>Офлайн-рендер frames кадров в новые массивы L и R.</summary>
    public (float[] Left, float[] Right) Render(int frames)
    {
        var interleaved = new float[frames * 2];
        Render(interleaved);
        var left = new float[frames];
        var right = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = interleaved[2 * i];
            right[i] = interleaved[2 * i + 1];
        }
        return (left, right);
    }

    /// <summary>Рендер для вывода (аудиопоток).</summary>
    internal void RenderInternal(Span<float> interleaved)
    {
        if (Interlocked.Exchange(ref _rendering, 1) != 0)
            throw new InvalidOperationException("рендер уже идёт в другом потоке");
        try
        {
            _mixer.Render(interleaved);
        }
        finally
        {
            Volatile.Write(ref _rendering, 0);
        }
    }

    internal void Send(Command command) => _mixer.Commands.Enqueue(command);

    /// <summary>Шум на две секунды (перенос noise() из audio.rs, те же зёрна и формулы во float).</summary>
    private static SampleBuffer Noise(NoiseColor color)
    {
        var data = new float[AudioFormat.SampleRate * 2];
        var seed = 0x9e3779b9u ^ (uint)color;
        float b0 = 0f, b1 = 0f, b2 = 0f, last = 0f;
        for (var i = 0; i < data.Length; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            var white = seed / (float)uint.MaxValue * 2f - 1f;
            switch (color)
            {
                case NoiseColor.Pink:
                    b0 = 0.99765f * b0 + white * 0.0990460f;
                    b1 = 0.96300f * b1 + white * 0.2965164f;
                    b2 = 0.57000f * b2 + white * 1.0526913f;
                    data[i] = (b0 + b1 + b2 + white * 0.1848f) * 0.18f;
                    break;
                case NoiseColor.Brown:
                    last = (last + 0.02f * white) / 1.02f;
                    data[i] = last * 3.5f;
                    break;
                default:
                    data[i] = white * 0.5f;
                    break;
            }
        }
        return new SampleBuffer(data);
    }
}

/// <summary>
/// Звучание голоса (Sound в пилоте): ручки параметров, place, release. Узлы — номера в графе (0 — выход). Время — секунды
/// контекста, NaN — сейчас.
/// </summary>
public sealed class Sound
{
    private readonly AudioEngine _engine;
    private readonly Voice _voice;

    internal Sound(AudioEngine engine, Voice voice)
    {
        _engine = engine;
        _voice = voice;
    }

    /// <summary>Номер звучания (для соединений из других голосов).</summary>
    public uint Id => _voice.Id;

    /// <summary>Голос снят: отзвучал, отпущен или не прошёл предел.</summary>
    public bool IsFinished => _voice.Finished;

    public int NodeCount => _voice.Nodes.Length;

    /// <summary>Вход узла index этого голоса — для into и соединений других голосов.</summary>
    public Endpoint Node(int index) => new(Id, checked((ushort)index));

    /// <summary>Параметр узла index этого голоса.</summary>
    public Endpoint Param(int index, ParamId param) => new(Id, checked((ushort)index), param);

    /// <summary>Событие автоматизации параметра узла.</summary>
    public void Automate(int node, ParamId param, AutomationKind kind, float value, double at = double.NaN, float timeConstant = 0f)
    {
        var index = checked((ushort)node);
        if (_voice.GetParam(index, param) is null)
            throw new ArgumentException($"у узла {node} нет параметра {param}");
        if (kind == AutomationKind.Exponential)
            value = MathF.Max(value, 1e-4f);
        if (kind == AutomationKind.Target)
            timeConstant = MathF.Max(timeConstant, 1e-4f);
        _engine.Send(new Automate(Id, index, param, kind, value, at, timeConstant));
    }

    /// <summary>set(v, at?).</summary>
    public void Set(int node, ParamId param, float value, double at = double.NaN) => Automate(node, param, AutomationKind.Set, value, at);

    /// <summary>ramp(v, at) — линейно к at.</summary>
    public void Ramp(int node, ParamId param, float value, double at) => Automate(node, param, AutomationKind.Linear, value, at);

    /// <summary>exp(v, at) — по экспоненте к at.</summary>
    public void Exp(int node, ParamId param, float value, double at) => Automate(node, param, AutomationKind.Exponential, value, at);

    /// <summary>target(v, at, tau).</summary>
    public void Target(int node, ParamId param, float value, double at, float timeConstant) =>
        Automate(node, param, AutomationKind.Target, value, at, timeConstant);

    /// <summary>glide(v, tau) — плавно к v начиная сейчас.</summary>
    public void Glide(int node, ParamId param, float value, float timeConstant) =>
        Automate(node, param, AutomationKind.Target, value, double.NaN, timeConstant);

    /// <summary>node.param = v — за 20 мс без щелчка (ручки меняются раз в кадр).</summary>
    public void Assign(int node, ParamId param, float value) => Glide(node, param, value, 0.02f);

    /// <summary>cancelScheduledValues.</summary>
    public void Cancel(int node, ParamId param, double at = double.NaN) => Automate(node, param, AutomationKind.Cancel, 0f, at);

    /// <summary>Громкость и сторона звучания — плавно (50 мс).</summary>
    public void Place(float gain, float pan = 0f) => _engine.Send(new PlaceVoice(Id, gain, pan, 0.05f));

    /// <summary>Отпустить: затихает за fade секунд и снимается.</summary>
    public void Release(float fade = 0.25f) => _engine.Send(new ReleaseVoice(Id, fade));
}
