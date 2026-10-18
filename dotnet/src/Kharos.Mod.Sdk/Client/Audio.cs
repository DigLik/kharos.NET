using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk.Client;

/// <summary>Шины громкости: все сходятся в общую громкость и компрессор.</summary>
public enum Bus : byte { Battle = 0, World = 1, Interface = 2, Music = 3 }

/// <summary>Форма волны осциллятора.</summary>
public enum Wave : byte { Sine = 0, Square = 1, Sawtooth = 2, Triangle = 3 }

/// <summary>Вид биквадратного фильтра.</summary>
public enum FilterType : byte { Lowpass = 0, Highpass = 1, Bandpass = 2, Lowshelf = 3, Highshelf = 4, Peaking = 5, Notch = 6, Allpass = 7 }

/// <summary>Цвет шума.</summary>
public enum NoiseColor : byte { White = 0, Pink = 1, Brown = 2 }

/// <summary>Параметр узла (AudioParam).</summary>
public enum ParamId : byte { Gain = 0, Frequency = 1, Detune = 2, Q = 3, Offset = 4, Rate = 5, DelayTime = 6, Pan = 7, Threshold = 8, Knee = 9, Ratio = 10, Attack = 11, Release = 12 }

/// <summary>Событие автоматизации: set, linear, exp, target, cancel, cancelAndHold.</summary>
public enum AutomationKind : byte { Set = 0, Linear = 1, Exponential = 2, Target = 3, Cancel = 4, CancelAndHold = 5 }

/// <summary>Узел или параметр узла звучащего голоса мода (для into и соединений из других голосов).</summary>
public readonly record struct VoiceEndpoint(uint Voice, ushort Node, ParamId? Param = null);

/// <summary>
/// Граф голоса в двоичном формате KHVG (как VoiceGraphCodec клиента): узлы, соединения, автоматизация. Строится
/// <see cref="VoiceGraphBuilder"/>; клиент проверяет его при разборе.
/// </summary>
public sealed class VoiceGraph(byte[] bytes)
{
    /// <summary>Записанный граф.</summary>
    public byte[] Bytes { get; } = bytes;
}

/// <summary>
/// Строитель графа голоса — как <c>s</c> у <c>graph(s)</c> пилота: <c>s.Osc(...).To(s.Filter(...)).To(s.Out)</c>,
/// <c>lfo.To(osc[ParamId.Frequency])</c>, <c>env[ParamId.Gain].Ramp(1, at)</c>. Узел 0 — выход голоса (усилитель).
/// Время — секунды звука (<see cref="ClientAudio.Now"/>); NaN — начало голоса.
/// </summary>
public sealed class VoiceGraphBuilder
{
    private const uint Magic = 0x4756484B;
    private const ushort Version = 1;
    private const byte InputMark = 0xFF;

    private readonly ManagedBytes _nodes = new();
    private readonly ManagedBytes _connections = new();
    private readonly ManagedBytes _automations = new();
    private float _out = 1;
    private int _nodeCount = 1, _connectionCount, _automationCount;

    /// <summary>Выход голоса (узел 0).</summary>
    public VoiceNode Out => new(this, 0);

    /// <summary>Начальная громкость выхода.</summary>
    public VoiceNode SetOut(float gain = 1)
    {
        _out = gain;
        return Out;
    }

    public VoiceNode Gain(float gain = 1) => Add(1).F32(gain).Done(this);

    /// <summary>Осциллятор; frequency NaN — нота голоса.</summary>
    public VoiceNode Osc(Wave wave = Wave.Sine, float frequency = float.NaN, float detune = 0) => Add(2).U8((byte)wave).F32(frequency).F32(detune).Done(this);

    public VoiceNode Noise(NoiseColor color = NoiseColor.White) => Add(3).U8((byte)color).Done(this);

    /// <summary>Запись банка: offset NaN — у петли случайно, иначе с начала; variant −1 — случайный.</summary>
    public VoiceNode Sample(string name, bool loop = false, float offset = float.NaN, int variant = -1, float rate = 1, float detune = 0) =>
        Add(4).U8(loop ? (byte)1 : (byte)0).Str(name).F32(offset).U16(unchecked((ushort)checked((short)variant))).F32(rate).F32(detune).Done(this);

    /// <summary>Свёртка с откликом из банка.</summary>
    public VoiceNode Reverb(string name, bool normalize = true) => Add(5).U8(normalize ? (byte)0 : (byte)1).Str(name).Done(this);

    public VoiceNode Const(float offset = 1) => Add(6).F32(offset).Done(this);

    public VoiceNode Filter(FilterType type = FilterType.Lowpass, float frequency = 1000, float q = 1, float gain = 0, float detune = 0) =>
        Add(7).U8((byte)type).F32(frequency).F32(q).F32(gain).F32(detune).Done(this);

    public VoiceNode Delay(float time = 0.25f, float maxTime = 2) => Add(8).F32(time).F32(maxTime).Done(this);

    /// <summary>Искажение силой amount (кривая пилота).</summary>
    public VoiceNode Shape(float amount = 0.5f) => Add(9).U8(0).F32(amount).Done(this);

    /// <summary>Искажение своей кривой.</summary>
    public VoiceNode Shape(float[] curve)
    {
        var w = Add(9).U8(1).U16(checked((ushort)curve.Length));
        foreach (var value in curve) w.F32(value);
        return w.Done(this);
    }

    public VoiceNode Pan(float pan = 0) => Add(10).F32(pan).Done(this);

    /// <summary>Колебание частоты frequency размахом depth — для параметров.</summary>
    public VoiceNode Lfo(float frequency, float depth, Wave wave = Wave.Sine) => Add(11).U8((byte)wave).F32(frequency).F32(depth).Done(this);

    /// <summary>Огибающая ADSR от начала и длины голоса.</summary>
    public VoiceNode Env(float attack = 0.01f, float decay = 0.1f, float sustain = 0.7f, float release = 0.2f, float peak = 1) =>
        Add(12).F32(attack).F32(decay).F32(sustain).F32(release).F32(peak).Done(this);

    public VoiceNode Compressor(float threshold = -24, float knee = 30, float ratio = 12, float attack = 0.003f, float release = 0.25f) =>
        Add(13).F32(threshold).F32(knee).F32(ratio).F32(attack).F32(release).Done(this);

    /// <summary>Соединить узел from с точкой to (свой узел, параметр или узел другого голоса мода).</summary>
    public void Connect(ushort from, VoiceEndpoint to)
    {
        _connections.U16(from).U32(to.Voice).U16(to.Node).U8(to.Param is { } param ? (byte)param : InputMark);
        _connectionCount++;
    }

    /// <summary>Событие автоматизации параметра.</summary>
    public void Automate(ushort node, ParamId param, AutomationKind kind, float value, double at, float timeConstant = 0)
    {
        _automations.U16(node).U8((byte)param).U8((byte)kind).F32(value).F64(at).F32(timeConstant);
        _automationCount++;
    }

    /// <summary>Готовый граф в KHVG.</summary>
    public VoiceGraph Build()
    {
        var w = new ManagedBytes();
        w.U32(Magic).U16(Version).U16(checked((ushort)_nodeCount)).U16(checked((ushort)_connectionCount)).U16(checked((ushort)_automationCount));
        w.U8(1).F32(_out).Raw(_nodes.Written).Raw(_connections.Written).Raw(_automations.Written);
        return new VoiceGraph(w.Written.ToArray());
    }

    private ManagedBytes Add(byte kind)
    {
        if (_nodeCount >= 4096)
            throw new InvalidOperationException("граф голоса: узлов больше 4096");
        _nodeCount++;
        return _nodes.U8(kind);
    }

    internal VoiceNode Last => new(this, (ushort)(_nodeCount - 1));
}

/// <summary>Растущий буфер байтов в куче: записи графа голоса.</summary>
internal sealed class ManagedBytes
{
    private byte[] _bytes = new byte[64];
    private int _length;

    public ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _length);

    public ManagedBytes U8(byte value)
    {
        Room(1)[0] = value;
        return this;
    }

    public ManagedBytes U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Room(2), value);
        return this;
    }

    public ManagedBytes U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);
        return this;
    }

    public ManagedBytes F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Room(4), value);
        return this;
    }

    public ManagedBytes F64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);
        return this;
    }

    public ManagedBytes Str(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        U16(checked((ushort)count));
        Encoding.UTF8.GetBytes(value, Room(count));
        return this;
    }

    public ManagedBytes Raw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Room(bytes.Length));
        return this;
    }

    /// <summary>Конец записи узла: ссылка на него.</summary>
    public VoiceNode Done(VoiceGraphBuilder builder) => builder.Last;

    private Span<byte> Room(int count)
    {
        if (_length + count > _bytes.Length)
            Array.Resize(ref _bytes, Math.Max(_length + count, _bytes.Length * 2));
        var room = _bytes.AsSpan(_length, count);
        _length += count;
        return room;
    }
}

/// <summary>Узел строящегося графа.</summary>
public readonly record struct VoiceNode(VoiceGraphBuilder Builder, ushort Index)
{
    /// <summary>Параметр узла.</summary>
    public VoiceParam this[ParamId param] => new(Builder, Index, param);

    /// <summary>Соединить с узлом; возвращает его — для цепочек.</summary>
    public VoiceNode To(VoiceNode target)
    {
        Builder.Connect(Index, new VoiceEndpoint(0, target.Index));
        return target;
    }

    /// <summary>Соединить с параметром (модуляция).</summary>
    public void To(VoiceParam target) => Builder.Connect(Index, new VoiceEndpoint(0, target.Node, target.Param));

    /// <summary>Соединить с узлом или параметром другого звучащего голоса мода (<see cref="Sound.Node"/>).</summary>
    public void To(VoiceEndpoint external) => Builder.Connect(Index, external);
}

/// <summary>Параметр узла строящегося графа: set, ramp, exp, target.</summary>
public readonly record struct VoiceParam(VoiceGraphBuilder Builder, ushort Node, ParamId Param)
{
    public VoiceParam Set(float value, double at = double.NaN) => Add(AutomationKind.Set, value, at);
    public VoiceParam Ramp(float value, double at) => Add(AutomationKind.Linear, value, at);
    public VoiceParam Exp(float value, double at) => Add(AutomationKind.Exponential, MathF.Max(value, 1e-4f), at);
    public VoiceParam Target(float value, double at, float timeConstant) => Add(AutomationKind.Target, value, at, MathF.Max(timeConstant, 1e-4f));
    public VoiceParam Cancel(double at) => Add(AutomationKind.Cancel, 0, at);

    private VoiceParam Add(AutomationKind kind, float value, double at, float timeConstant = 0)
    {
        Builder.Automate(Node, Param, kind, value, at, timeConstant);
        return this;
    }
}

/// <summary>Как завести голос (c.audio:voice): шина или узел другого голоса, начало, длина, нота, сила.</summary>
public sealed record VoiceOptions
{
    public Bus Bus { get; init; } = Bus.World;
    /// <summary>Узел другого голоса мода вместо шины.</summary>
    public VoiceEndpoint? Into { get; init; }
    /// <summary>Начало, секунды звука; NaN — сейчас.</summary>
    public double At { get; init; } = double.NaN;
    /// <summary>Длина, секунды; NaN — пока не отпустят.</summary>
    public float Length { get; init; } = float.NaN;
    public float Note { get; init; } = 440;
    public float Velocity { get; init; } = 1;
}

/// <summary>Звучащий голос мода: ручки параметров, place, release. Время — секунды звука, NaN — сейчас.</summary>
public sealed class Sound
{
    private readonly ClientAudio _audio;

    internal Sound(ClientAudio audio, uint handle)
    {
        _audio = audio;
        Handle = handle;
    }

    /// <summary>Номер голоса у мода.</summary>
    public uint Handle { get; }

    /// <summary>Голос отзвучал (клиент сообщил во входе кадра).</summary>
    public bool Finished { get; internal set; }

    /// <summary>Вход узла index — для into и соединений других голосов.</summary>
    public VoiceEndpoint Node(int index) => new(Handle, checked((ushort)index));

    /// <summary>Параметр узла index.</summary>
    public VoiceEndpoint Param(int index, ParamId param) => new(Handle, checked((ushort)index), param);

    public void Automate(int node, ParamId param, AutomationKind kind, float value, double at = double.NaN, float timeConstant = 0)
    {
        if (kind == AutomationKind.Exponential) value = MathF.Max(value, 1e-4f);
        if (kind == AutomationKind.Target) timeConstant = MathF.Max(timeConstant, 1e-4f);
        _audio.Out.U8(ClientAbi.OpAutomate).U32(Handle).U16(checked((ushort)node)).U8((byte)param).U8((byte)kind).F32(value).F64(at).F32(timeConstant);
    }

    public void Set(int node, ParamId param, float value, double at = double.NaN) => Automate(node, param, AutomationKind.Set, value, at);
    public void Ramp(int node, ParamId param, float value, double at) => Automate(node, param, AutomationKind.Linear, value, at);
    public void Exp(int node, ParamId param, float value, double at) => Automate(node, param, AutomationKind.Exponential, value, at);
    public void Target(int node, ParamId param, float value, double at, float timeConstant) => Automate(node, param, AutomationKind.Target, value, at, timeConstant);

    /// <summary>glide(v, tau) — плавно к v начиная сейчас.</summary>
    public void Glide(int node, ParamId param, float value, float timeConstant) => Automate(node, param, AutomationKind.Target, value, double.NaN, timeConstant);

    /// <summary>node.param = v — за 20 мс без щелчка.</summary>
    public void Assign(int node, ParamId param, float value) => Glide(node, param, value, 0.02f);

    public void Cancel(int node, ParamId param, double at = double.NaN) => Automate(node, param, AutomationKind.Cancel, 0, at);

    /// <summary>Громкость и сторона звучания — плавно (50 мс).</summary>
    public void Place(float gain, float pan = 0) => _audio.Out.U8(ClientAbi.OpPlace).U32(Handle).F32(gain).F32(pan);

    /// <summary>Отпустить: затихает за fade секунд и снимается.</summary>
    public void Release(float fade = 0.25f) => _audio.Out.U8(ClientAbi.OpRelease).U32(Handle).F32(fade);
}

/// <summary>
/// Звук клиентского мода (c.audio пилота): разовые записи банка, петли, свои голоса по графу или по имени. Команды
/// уходят в выход кадра; у клиента без звука они молча пропадают.
/// </summary>
public sealed class ClientAudio
{
    private readonly Dictionary<uint, Sound> _sounds = [];
    private uint _next = 1;

    internal ClientAudio(ByteWriter output) => Out = output;

    internal ByteWriter Out { get; }

    /// <summary>Есть ли у клиента звук.</summary>
    public bool Available { get; private set; }

    /// <summary>Время звука, секунды: для расписания нот и автоматизации.</summary>
    public double Now { get; private set; }

    private uint _random = (uint)DateTime.UtcNow.Ticks | 1;

    /// <summary>Случайное число [0, 1] (c.audio:random пилота): xorshift32; высота разовых звуков, начала петель.</summary>
    public double Random()
    {
        var state = _random;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        _random = state;
        return state / (double)uint.MaxValue;
    }

    internal void Begin(ref ByteReader r)
    {
        Available = r.U8() != 0;
        Now = r.F64();
        var ended = r.U16();
        for (var i = 0; i < ended; i++)
            if (_sounds.Remove(r.U32(), out var sound))
                sound.Finished = true;
    }

    /// <summary>Запись банка сразу: громкость, сторона −1..1, скорость (1 — как есть).</summary>
    public void Play(string name, float volume, float pan = 0, float rate = 1) =>
        Out.U8(ClientAbi.OpPlay).Str(name).F32(volume).F32(pan).F32(rate);

    /// <summary>Фоновая петля банка: громкость (0 — молчит) и сторона меняются плавно, сколько ни зови.</summary>
    public void Loop(string name, float volume, float pan = 0) => Out.U8(ClientAbi.OpLoop).Str(name).F32(volume).F32(pan);

    /// <summary>Громкость шины.</summary>
    public void Volume(Bus bus, float volume) => Out.U8(ClientAbi.OpVolume).U8((byte)bus).F32(volume);

    /// <summary>Свой голос по графу.</summary>
    public Sound Voice(VoiceGraph graph, VoiceOptions? options = null)
    {
        var sound = Start(options);
        Out.U8(ClientAbi.VoiceGraph).Blob(graph.Bytes);
        Tail(options);
        return sound;
    }

    /// <summary>Голос по имени (ClientBuilder.Voice).</summary>
    public Sound Voice(string kind, VoiceOptions? options = null)
    {
        var sound = Start(options);
        Out.U8(ClientAbi.VoiceKind).Str(kind);
        Tail(options);
        return sound;
    }

    private Sound Start(VoiceOptions? options)
    {
        var sound = new Sound(this, _next++);
        _sounds[sound.Handle] = sound;
        Out.U8(ClientAbi.OpVoice).U32(sound.Handle);
        return sound;
    }

    private void Tail(VoiceOptions? options)
    {
        options ??= new VoiceOptions();
        var into = options.Into ?? default;
        Out.U8((byte)options.Bus).U32(into.Voice).U16(into.Node).F64(options.At).F32(options.Length).F32(options.Note).F32(options.Velocity);
    }
}

/// <summary>Запись банка: каналы f32 одной длины (1, 2 или 4) — что мод посчитал при загрузке.</summary>
public delegate float[][] BankRender(int rate, int variant);

/// <summary>Запись выхода kh_bank.</summary>
internal static class BankWriter
{
    public static void Write(ByteWriter w, float[][] channels)
    {
        if (channels.Length is 0 or 3 or > 4)
            throw new ArgumentException("в записи 1, 2 или 4 канала");
        var frames = channels[0].Length;
        foreach (var channel in channels)
            if (channel.Length != frames)
                throw new ArgumentException("каналы записи разной длины");
        w.U8((byte)channels.Length).U32((uint)frames);
        foreach (var channel in channels)
            foreach (var sample in channel)
                w.F32(sample);
    }
}

/// <summary>Сигнал хоста: тик, имя, байты (формат знает тот, кто его послал).</summary>
public sealed record HostSignal(ulong Tick, string Name, byte[] Payload)
{
    /// <summary>Payload, разобранный MemoryPack.</summary>
    public T? Read<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>() =>
        MemoryPack.MemoryPackSerializer.Deserialize<T>(Payload);
}
