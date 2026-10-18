namespace Kharos.Audio.Graph;

/// <summary>Вид узла графа голоса (коды двоичного формата).</summary>
public enum NodeKind : byte
{
    /// <summary>GainNode: параметр <see cref="ParamId.Gain"/>.</summary>
    Gain = 1,
    /// <summary>OscillatorNode: <see cref="ParamId.Frequency"/>, <see cref="ParamId.Detune"/>.</summary>
    Osc = 2,
    /// <summary>Шум по кругу: AudioBufferSourceNode на двухсекундной записи (как в пилоте); <see cref="ParamId.Rate"/>, <see cref="ParamId.Detune"/>.</summary>
    Noise = 3,
    /// <summary>Запись банка: AudioBufferSourceNode; <see cref="ParamId.Rate"/>, <see cref="ParamId.Detune"/>.</summary>
    Sample = 4,
    /// <summary>ConvolverNode с откликом из банка.</summary>
    Reverb = 5,
    /// <summary>ConstantSourceNode: <see cref="ParamId.Offset"/>.</summary>
    Const = 6,
    /// <summary>BiquadFilterNode: <see cref="ParamId.Frequency"/>, <see cref="ParamId.Q"/>, <see cref="ParamId.Gain"/>, <see cref="ParamId.Detune"/>.</summary>
    Filter = 7,
    /// <summary>DelayNode: <see cref="ParamId.DelayTime"/>.</summary>
    Delay = 8,
    /// <summary>WaveShaperNode без передискретизации.</summary>
    Shape = 9,
    /// <summary>StereoPannerNode: <see cref="ParamId.Pan"/>.</summary>
    Pan = 10,
    /// <summary>Колебание для параметра: осциллятор, умноженный на размах (<see cref="ParamId.Frequency"/>, <see cref="ParamId.Detune"/>, <see cref="ParamId.Gain"/> — размах).</summary>
    Lfo = 11,
    /// <summary>Огибающая ADSR: GainNode с автоматизацией от начала и длины голоса; <see cref="ParamId.Gain"/>.</summary>
    Env = 12,
    /// <summary>DynamicsCompressorNode: <see cref="ParamId.Threshold"/>, <see cref="ParamId.Knee"/>, <see cref="ParamId.Ratio"/>, <see cref="ParamId.Attack"/>, <see cref="ParamId.Release"/>.</summary>
    Compressor = 13,
}

/// <summary>Форма волны осциллятора.</summary>
public enum Wave : byte
{
    Sine = 0,
    Square = 1,
    Sawtooth = 2,
    Triangle = 3,
}

/// <summary>Вид биквадратного фильтра (BiquadFilterType).</summary>
public enum FilterType : byte
{
    Lowpass = 0,
    Highpass = 1,
    Bandpass = 2,
    Lowshelf = 3,
    Highshelf = 4,
    Peaking = 5,
    Notch = 6,
    Allpass = 7,
}

/// <summary>Цвет шума.</summary>
public enum NoiseColor : byte
{
    White = 0,
    Pink = 1,
    Brown = 2,
}

/// <summary>Параметр узла (AudioParam).</summary>
public enum ParamId : byte
{
    Gain = 0,
    Frequency = 1,
    Detune = 2,
    Q = 3,
    Offset = 4,
    /// <summary>playbackRate записи.</summary>
    Rate = 5,
    DelayTime = 6,
    Pan = 7,
    Threshold = 8,
    Knee = 9,
    Ratio = 10,
    Attack = 11,
    Release = 12,
}

/// <summary>Событие автоматизации параметра.</summary>
public enum AutomationKind : byte
{
    /// <summary>setValueAtTime.</summary>
    Set = 0,
    /// <summary>linearRampToValueAtTime.</summary>
    Linear = 1,
    /// <summary>exponentialRampToValueAtTime.</summary>
    Exponential = 2,
    /// <summary>setTargetAtTime (постоянная — <see cref="Automation.TimeConstant"/>).</summary>
    Target = 3,
    /// <summary>cancelScheduledValues.</summary>
    Cancel = 4,
    /// <summary>cancelAndHoldAtTime.</summary>
    CancelAndHold = 5,
}

/// <summary>Узел графа голоса: вид и начальные значения.</summary>
public abstract record NodeSpec(NodeKind Kind);

/// <summary>Усилитель.</summary>
public sealed record GainSpec(float Gain = 1f) : NodeSpec(NodeKind.Gain);

/// <summary>Осциллятор; частота NaN — нота голоса.</summary>
public sealed record OscSpec(Wave Wave = Wave.Sine, float Frequency = float.NaN, float Detune = 0f) : NodeSpec(NodeKind.Osc);

/// <summary>Шум по кругу.</summary>
public sealed record NoiseSpec(NoiseColor Color = NoiseColor.White) : NodeSpec(NodeKind.Noise);

/// <summary>Запись банка: offset NaN — у петли случайное место, иначе начало; variant −1 — случайный вариант.</summary>
public sealed record SampleSpec(string Name, bool Loop = false, float Offset = float.NaN, int Variant = -1, float Rate = 1f, float Detune = 0f)
    : NodeSpec(NodeKind.Sample);

/// <summary>Свёртка с откликом из банка (вариант 0); Normalize — нормировка по спецификации.</summary>
public sealed record ReverbSpec(string Name, bool Normalize = true) : NodeSpec(NodeKind.Reverb);

/// <summary>Постоянный источник.</summary>
public sealed record ConstSpec(float Offset = 1f) : NodeSpec(NodeKind.Const);

/// <summary>Биквадратный фильтр.</summary>
public sealed record FilterSpec(FilterType Type = FilterType.Lowpass, float Frequency = 1000f, float Q = 1f, float Gain = 0f, float Detune = 0f)
    : NodeSpec(NodeKind.Filter);

/// <summary>Задержка; MaxTime — предел задержки в секундах.</summary>
public sealed record DelaySpec(float Time = 0.25f, float MaxTime = 2f) : NodeSpec(NodeKind.Delay);

/// <summary>Искажение: кривая (Curve) или сила (Amount, кривая как у пилота: (1 + k)x / (1 + k|x|), k = 50·amount).</summary>
public sealed record ShapeSpec(float Amount = 0.5f, float[]? Curve = null) : NodeSpec(NodeKind.Shape)
{
    /// <summary>Кривая: заданная или по силе.</summary>
    public float[] ResolveCurve()
    {
        if (Curve is not null)
            return Curve;
        var k = Amount * 50f;
        var curve = new float[1024];
        for (var i = 0; i < curve.Length; i++)
        {
            var x = i / 511.5f - 1f;
            curve[i] = (1f + k) * x / (1f + k * MathF.Abs(x));
        }
        return curve;
    }

    /// <inheritdoc />
    public bool Equals(ShapeSpec? other) =>
        other is not null && Amount.Equals(other.Amount) &&
        (Curve is null ? other.Curve is null : other.Curve is not null && Curve.AsSpan().SequenceEqual(other.Curve));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Amount, Curve?.Length ?? -1);
}

/// <summary>Стереопанорама.</summary>
public sealed record PanSpec(float Pan = 0f) : NodeSpec(NodeKind.Pan);

/// <summary>Колебание частоты Frequency размахом Depth.</summary>
public sealed record LfoSpec(float Frequency, float Depth, Wave Wave = Wave.Sine) : NodeSpec(NodeKind.Lfo);

/// <summary>Огибающая: в начале голоса поднимается до Peak·velocity за Attack, спадает к Sustain за Decay, в конце длины голоса гаснет за Release.</summary>
public sealed record EnvSpec(float Attack = 0.01f, float Decay = 0.1f, float Sustain = 0.7f, float Release = 0.2f, float Peak = 1f)
    : NodeSpec(NodeKind.Env);

/// <summary>Компрессор; значения по умолчанию — как у DynamicsCompressorNode.</summary>
public sealed record CompressorSpec(float Threshold = -24f, float Knee = 30f, float Ratio = 12f, float Attack = 0.003f, float Release = 0.25f)
    : NodeSpec(NodeKind.Compressor);

/// <summary>Куда идёт соединение: вход узла или его параметр; Voice ≠ 0 — узел другого (уже звучащего) голоса.</summary>
public readonly record struct Endpoint(uint Voice, ushort Node, ParamId? Param = null)
{
    /// <summary>Соединение внутри своего голоса.</summary>
    public bool IsLocal => Voice == 0;
}

/// <summary>Соединение: выход узла From → To.</summary>
public readonly record struct Connection(ushort From, Endpoint To);

/// <summary>
/// Событие автоматизации: параметр Param узла Node. Time — время контекста в секундах (NaN — начало голоса), TimeConstant —
/// для <see cref="AutomationKind.Target"/>.
/// </summary>
public readonly record struct Automation(ushort Node, ParamId Param, AutomationKind Kind, float Value, double Time, float TimeConstant = 0f);
