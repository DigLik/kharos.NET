using System.Buffers.Binary;
using System.Text;

namespace Kharos.Audio.Graph;

/// <summary>
/// Двоичный формат графа голоса — так граф приходит от клиентского мода (wasm). Всё little-endian, без выравнивания.
/// <code>
/// Заголовок (12 байт):
///   u32 magic = 0x4756484B ("KHVG")   u16 version = 1
///   u16 N — узлов (≥ 1; узел 0 — выход голоса, Gain)   u16 C — соединений   u16 A — событий автоматизации
/// Узел: u8 kind, затем по виду (str — u16 длина байт + UTF-8; f32 NaN, где сказано, — «по умолчанию»):
///   1 Gain        f32 gain
///   2 Osc         u8 wave (0 sine, 1 square, 2 sawtooth, 3 triangle), f32 frequency (NaN — нота голоса), f32 detune
///   3 Noise       u8 color (0 white, 1 pink, 2 brown)
///   4 Sample      u8 flags (1 — петля), str name, f32 offset (NaN — у петли случайно, иначе 0), i16 variant (−1 — случайный),
///                 f32 rate, f32 detune
///   5 Reverb      u8 flags (1 — без нормировки), str name
///   6 Const       f32 offset
///   7 Filter      u8 type (0 lowpass, 1 highpass, 2 bandpass, 3 lowshelf, 4 highshelf, 5 peaking, 6 notch, 7 allpass),
///                 f32 frequency, f32 q, f32 gain, f32 detune
///   8 Delay       f32 time, f32 maxTime
///   9 Shape       u8 mode: 0 — f32 amount (кривая пилота); 1 — u16 count, f32 × count (кривая)
///  10 Pan         f32 pan
///  11 Lfo         u8 wave, f32 frequency, f32 depth
///  12 Env         f32 attack, f32 decay, f32 sustain, f32 release, f32 peak
///  13 Compressor  f32 threshold, f32 knee, f32 ratio, f32 attack, f32 release
/// Соединение (9 байт): u16 from, u32 voice (0 — свой голос, иначе id звучащего голоса), u16 to,
///   u8 param (0xFF — вход узла, иначе ParamId)
/// Событие (20 байт): u16 node, u8 param (ParamId), u8 kind (0 set, 1 linear, 2 exp, 3 target, 4 cancel, 5 cancelAndHold),
///   f32 value, f64 time (секунды контекста; NaN — начало голоса), f32 timeConstant
/// ParamId: 0 gain, 1 frequency, 2 detune, 3 q, 4 offset, 5 rate, 6 delayTime, 7 pan, 8 threshold, 9 knee, 10 ratio,
///   11 attack, 12 release.
/// </code>
/// Длина записи должна совпасть с прочитанным до байта; всё прочее проверяет <see cref="VoiceGraph"/>.
/// </summary>
public static class VoiceGraphCodec
{
    public const uint Magic = 0x4756484B;
    public const ushort Version = 1;
    private const byte InputMark = 0xFF;

    /// <summary>Запись графа в байты.</summary>
    public static byte[] Encode(VoiceGraph graph)
    {
        var writer = new Writer();
        writer.U32(Magic);
        writer.U16(Version);
        writer.U16(checked((ushort)graph.Nodes.Count));
        writer.U16(checked((ushort)graph.Connections.Count));
        writer.U16(checked((ushort)graph.Automations.Count));
        foreach (var node in graph.Nodes)
        {
            writer.U8((byte)node.Kind);
            switch (node)
            {
                case GainSpec gain:
                    writer.F32(gain.Gain);
                    break;
                case OscSpec osc:
                    writer.U8((byte)osc.Wave);
                    writer.F32(osc.Frequency);
                    writer.F32(osc.Detune);
                    break;
                case NoiseSpec noise:
                    writer.U8((byte)noise.Color);
                    break;
                case SampleSpec sample:
                    writer.U8(sample.Loop ? (byte)1 : (byte)0);
                    writer.Str(sample.Name);
                    writer.F32(sample.Offset);
                    writer.I16(checked((short)sample.Variant));
                    writer.F32(sample.Rate);
                    writer.F32(sample.Detune);
                    break;
                case ReverbSpec reverb:
                    writer.U8(reverb.Normalize ? (byte)0 : (byte)1);
                    writer.Str(reverb.Name);
                    break;
                case ConstSpec constant:
                    writer.F32(constant.Offset);
                    break;
                case FilterSpec filter:
                    writer.U8((byte)filter.Type);
                    writer.F32(filter.Frequency);
                    writer.F32(filter.Q);
                    writer.F32(filter.Gain);
                    writer.F32(filter.Detune);
                    break;
                case DelaySpec delay:
                    writer.F32(delay.Time);
                    writer.F32(delay.MaxTime);
                    break;
                case ShapeSpec shape when shape.Curve is null:
                    writer.U8(0);
                    writer.F32(shape.Amount);
                    break;
                case ShapeSpec shape:
                    writer.U8(1);
                    writer.U16(checked((ushort)shape.Curve!.Length));
                    foreach (var value in shape.Curve)
                        writer.F32(value);
                    break;
                case PanSpec pan:
                    writer.F32(pan.Pan);
                    break;
                case LfoSpec lfo:
                    writer.U8((byte)lfo.Wave);
                    writer.F32(lfo.Frequency);
                    writer.F32(lfo.Depth);
                    break;
                case EnvSpec env:
                    writer.F32(env.Attack);
                    writer.F32(env.Decay);
                    writer.F32(env.Sustain);
                    writer.F32(env.Release);
                    writer.F32(env.Peak);
                    break;
                case CompressorSpec compressor:
                    writer.F32(compressor.Threshold);
                    writer.F32(compressor.Knee);
                    writer.F32(compressor.Ratio);
                    writer.F32(compressor.Attack);
                    writer.F32(compressor.Release);
                    break;
                default:
                    throw new FormatException($"узел {node.Kind} не записывается");
            }
        }
        foreach (var connection in graph.Connections)
        {
            writer.U16(connection.From);
            writer.U32(connection.To.Voice);
            writer.U16(connection.To.Node);
            writer.U8(connection.To.Param is { } param ? (byte)param : InputMark);
        }
        foreach (var automation in graph.Automations)
        {
            writer.U16(automation.Node);
            writer.U8((byte)automation.Param);
            writer.U8((byte)automation.Kind);
            writer.F32(automation.Value);
            writer.F64(automation.Time);
            writer.F32(automation.TimeConstant);
        }
        return writer.ToArray();
    }

    /// <summary>Граф из байтов; ошибки формата — <see cref="FormatException"/>.</summary>
    public static VoiceGraph Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes);
        if (reader.U32() != Magic)
            throw new FormatException("граф голоса: нет метки KHVG");
        var version = reader.U16();
        if (version != Version)
            throw new FormatException($"граф голоса: версия {version}, ожидалась {Version}");
        int nodeCount = reader.U16(), connectionCount = reader.U16(), automationCount = reader.U16();
        if (nodeCount == 0 || nodeCount > VoiceGraph.MaxNodes)
            throw new FormatException("граф голоса: число узлов вне 1…4096");
        var nodes = new NodeSpec[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            var kind = (NodeKind)reader.U8();
            nodes[i] = kind switch
            {
                NodeKind.Gain => new GainSpec(reader.F32()),
                NodeKind.Osc => new OscSpec((Wave)reader.U8(), reader.F32(), reader.F32()),
                NodeKind.Noise => new NoiseSpec((NoiseColor)reader.U8()),
                NodeKind.Sample => ReadSample(ref reader),
                NodeKind.Reverb => ReadReverb(ref reader),
                NodeKind.Const => new ConstSpec(reader.F32()),
                NodeKind.Filter => new FilterSpec((FilterType)reader.U8(), reader.F32(), reader.F32(), reader.F32(), reader.F32()),
                NodeKind.Delay => new DelaySpec(reader.F32(), reader.F32()),
                NodeKind.Shape => ReadShape(ref reader),
                NodeKind.Pan => new PanSpec(reader.F32()),
                NodeKind.Lfo => ReadLfo(ref reader),
                NodeKind.Env => new EnvSpec(reader.F32(), reader.F32(), reader.F32(), reader.F32(), reader.F32()),
                NodeKind.Compressor => new CompressorSpec(reader.F32(), reader.F32(), reader.F32(), reader.F32(), reader.F32()),
                _ => throw new FormatException($"граф голоса: неизвестный вид узла {(byte)kind}"),
            };
        }
        var connections = new Connection[connectionCount];
        for (var i = 0; i < connectionCount; i++)
        {
            var from = reader.U16();
            var voice = reader.U32();
            var to = reader.U16();
            var param = reader.U8();
            connections[i] = new Connection(from, new Endpoint(voice, to, param == InputMark ? null : ToParam(param)));
        }
        var automations = new Automation[automationCount];
        for (var i = 0; i < automationCount; i++)
        {
            var node = reader.U16();
            var param = ToParam(reader.U8());
            var kind = reader.U8();
            if (kind > (byte)AutomationKind.CancelAndHold)
                throw new FormatException($"автоматизация: неизвестный вид {kind}");
            automations[i] = new Automation(node, param, (AutomationKind)kind, reader.F32(), reader.F64(), reader.F32());
        }
        if (!reader.AtEnd)
            throw new FormatException("граф голоса: лишние байты в конце");
        return new VoiceGraph(nodes, connections, automations);
    }

    private static ParamId ToParam(byte value) =>
        value <= (byte)ParamId.Release ? (ParamId)value : throw new FormatException($"неизвестный параметр {value}");

    private static SampleSpec ReadSample(ref Reader reader)
    {
        var flags = reader.U8();
        var name = reader.Str();
        var offset = reader.F32();
        var variant = reader.I16();
        return new SampleSpec(name, (flags & 1) != 0, offset, variant, reader.F32(), reader.F32());
    }

    private static ReverbSpec ReadReverb(ref Reader reader)
    {
        var flags = reader.U8();
        return new ReverbSpec(reader.Str(), (flags & 1) == 0);
    }

    private static ShapeSpec ReadShape(ref Reader reader)
    {
        var mode = reader.U8();
        if (mode == 0)
            return new ShapeSpec(reader.F32());
        if (mode != 1)
            throw new FormatException($"искажение: неизвестный режим {mode}");
        var curve = new float[reader.U16()];
        for (var i = 0; i < curve.Length; i++)
            curve[i] = reader.F32();
        return new ShapeSpec(0f, curve);
    }

    private static LfoSpec ReadLfo(ref Reader reader)
    {
        var wave = (Wave)reader.U8();
        return new LfoSpec(reader.F32(), reader.F32(), wave);
    }

    private sealed class Writer
    {
        private byte[] _bytes = new byte[256];
        private int _length;

        public byte[] ToArray() => _bytes.AsSpan(0, _length).ToArray();

        public void U8(byte value) => Room(1)[0] = value;

        public void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Room(2), value);

        public void I16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Room(2), value);

        public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);

        public void F32(float value) => BinaryPrimitives.WriteSingleLittleEndian(Room(4), value);

        public void F64(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);

        public void Str(string value)
        {
            var count = Encoding.UTF8.GetByteCount(value);
            U16(checked((ushort)count));
            Encoding.UTF8.GetBytes(value, Room(count));
        }

        private Span<byte> Room(int count)
        {
            if (_length + count > _bytes.Length)
                Array.Resize(ref _bytes, Math.Max(_length + count, _bytes.Length * 2));
            var room = _bytes.AsSpan(_length, count);
            _length += count;
            return room;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool AtEnd => _at == _bytes.Length;

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public string Str()
        {
            var count = U16();
            try
            {
                return new UTF8Encoding(false, true).GetString(Take(count));
            }
            catch (DecoderFallbackException error)
            {
                throw new FormatException("граф голоса: имя не UTF-8", error);
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_at + count > _bytes.Length)
                throw new FormatException("граф голоса: запись оборвана");
            var span = _bytes.Slice(_at, count);
            _at += count;
            return span;
        }
    }
}
