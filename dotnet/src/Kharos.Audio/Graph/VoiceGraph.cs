namespace Kharos.Audio.Graph;

/// <summary>
/// Описание голоса: узлы, соединения и автоматизация параметров. Узел 0 — выход голоса (<see cref="GainSpec"/>), из
/// него звук идёт в громкость и панораму звучания, а оттуда — в шину. Описание неизменно и проверено при создании;
/// приходит из C# (<see cref="VoiceGraphBuilder"/>) или байтами от клиентского мода (<see cref="VoiceGraphCodec"/>).
/// </summary>
public sealed class VoiceGraph
{
    /// <summary>Больше узлов в одном голосе не бывает.</summary>
    public const int MaxNodes = 4096;

    public VoiceGraph(IReadOnlyList<NodeSpec> nodes, IReadOnlyList<Connection> connections, IReadOnlyList<Automation> automations)
    {
        Nodes = [.. nodes];
        Connections = [.. connections];
        Automations = [.. automations];
        Validate();
    }

    /// <summary>Узлы; 0 — выход голоса.</summary>
    public IReadOnlyList<NodeSpec> Nodes { get; }

    public IReadOnlyList<Connection> Connections { get; }

    public IReadOnlyList<Automation> Automations { get; }

    /// <summary>Двоичная запись (формат — <see cref="VoiceGraphCodec"/>).</summary>
    public byte[] Encode() => VoiceGraphCodec.Encode(this);

    /// <summary>Описание из двоичной записи.</summary>
    public static VoiceGraph Decode(ReadOnlySpan<byte> bytes) => VoiceGraphCodec.Decode(bytes);

    /// <summary>Есть ли у узла вида kind параметр param.</summary>
    public static bool HasParam(NodeKind kind, ParamId param) => kind switch
    {
        NodeKind.Gain or NodeKind.Env => param == ParamId.Gain,
        NodeKind.Osc => param is ParamId.Frequency or ParamId.Detune,
        NodeKind.Lfo => param is ParamId.Frequency or ParamId.Detune or ParamId.Gain,
        NodeKind.Noise or NodeKind.Sample => param is ParamId.Rate or ParamId.Detune,
        NodeKind.Const => param == ParamId.Offset,
        NodeKind.Filter => param is ParamId.Frequency or ParamId.Q or ParamId.Gain or ParamId.Detune,
        NodeKind.Delay => param == ParamId.DelayTime,
        NodeKind.Pan => param == ParamId.Pan,
        NodeKind.Compressor => param is ParamId.Threshold or ParamId.Knee or ParamId.Ratio or ParamId.Attack or ParamId.Release,
        _ => false,
    };

    /// <summary>Есть ли у узла вход (у источников входа нет).</summary>
    public static bool HasInput(NodeKind kind) => kind is not (NodeKind.Osc or NodeKind.Noise or NodeKind.Sample or NodeKind.Const or NodeKind.Lfo);

    private void Validate()
    {
        if (Nodes.Count == 0 || Nodes[0] is not GainSpec)
            throw new FormatException("граф голоса: узел 0 — выход голоса, усилитель");
        if (Nodes.Count > MaxNodes)
            throw new FormatException($"граф голоса: узлов больше {MaxNodes}");
        foreach (var node in Nodes)
            ValidateNode(node);
        foreach (var connection in Connections)
        {
            if (connection.From >= Nodes.Count)
                throw new FormatException($"соединение: нет узла {connection.From}");
            var to = connection.To;
            if (!to.IsLocal)
                continue;
            if (to.Node >= Nodes.Count)
                throw new FormatException($"соединение: нет узла {to.Node}");
            var kind = Nodes[to.Node].Kind;
            if (to.Param is { } param)
            {
                if (!HasParam(kind, param))
                    throw new FormatException($"соединение: у узла {to.Node} ({kind}) нет параметра {param}");
            }
            else if (!HasInput(kind))
                throw new FormatException($"соединение: у узла {to.Node} ({kind}) нет входа");
        }
        foreach (var automation in Automations)
        {
            if (automation.Node >= Nodes.Count)
                throw new FormatException($"автоматизация: нет узла {automation.Node}");
            if (!HasParam(Nodes[automation.Node].Kind, automation.Param))
                throw new FormatException($"автоматизация: у узла {automation.Node} нет параметра {automation.Param}");
            if (automation.Kind > AutomationKind.CancelAndHold)
                throw new FormatException($"автоматизация: неизвестный вид {(byte)automation.Kind}");
            if (!float.IsFinite(automation.Value) && automation.Kind is not (AutomationKind.Cancel or AutomationKind.CancelAndHold))
                throw new FormatException("автоматизация: значение не число");
            if (automation.Kind == AutomationKind.Exponential && automation.Value == 0f)
                throw new FormatException("автоматизация: экспонента к нулю");
            if (automation.Kind == AutomationKind.Target && !(automation.TimeConstant >= 0f))
                throw new FormatException("автоматизация: постоянная времени меньше нуля");
            if (double.IsInfinity(automation.Time) || automation.Time < 0)
                throw new FormatException("автоматизация: время вне контекста");
        }
    }

    private static void ValidateNode(NodeSpec node)
    {
        switch (node)
        {
            case OscSpec osc when osc.Wave > Wave.Triangle:
            case LfoSpec lfo when lfo.Wave > Wave.Triangle:
                throw new FormatException("осциллятор: неизвестная волна");
            case NoiseSpec noise when noise.Color > NoiseColor.Brown:
                throw new FormatException("шум: неизвестный цвет");
            case FilterSpec filter when filter.Type > FilterType.Allpass:
                throw new FormatException("фильтр: неизвестный вид");
            case SampleSpec sample when string.IsNullOrEmpty(sample.Name):
            case ReverbSpec reverb when string.IsNullOrEmpty(reverb.Name):
                throw new FormatException("запись: пустое имя");
            case DelaySpec delay when !(delay.MaxTime > 0f && delay.MaxTime < 180f):
                throw new FormatException("задержка: предел вне (0, 180) секунд");
            case ShapeSpec { Curve: { Length: < 2 } }:
                throw new FormatException("искажение: в кривой меньше двух точек");
        }
    }
}
