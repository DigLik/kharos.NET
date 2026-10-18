namespace Kharos.Audio.Graph;

/// <summary>
/// Строитель графа голоса — как <c>s</c> у <c>graph(s)</c> в пилоте: <c>s.Osc(...).To(s.Filter(...)).To(s.Out)</c>,
/// <c>lfo.To(osc[ParamId.Frequency])</c>, <c>env[ParamId.Gain].Ramp(1, at)</c>. Время — секунды контекста
/// (<see cref="AudioEngine.Now"/>); NaN — начало голоса.
/// </summary>
public sealed class VoiceGraphBuilder
{
    private readonly List<NodeSpec> _nodes = [new GainSpec()];
    private readonly List<Connection> _connections = [];
    private readonly List<Automation> _automations = [];

    /// <summary>Выход голоса (узел 0).</summary>
    public NodeRef Out => new(this, 0);

    /// <summary>Сколько узлов уже есть.</summary>
    public int Count => _nodes.Count;

    /// <summary>Добавить узел.</summary>
    public NodeRef Add(NodeSpec spec)
    {
        if (_nodes.Count >= VoiceGraph.MaxNodes)
            throw new InvalidOperationException($"граф голоса: узлов больше {VoiceGraph.MaxNodes}");
        _nodes.Add(spec);
        return new NodeRef(this, (ushort)(_nodes.Count - 1));
    }

    /// <summary>Заменить начальные значения выхода (узла 0).</summary>
    public NodeRef SetOut(GainSpec spec)
    {
        _nodes[0] = spec;
        return Out;
    }

    public NodeRef Osc(Wave wave = Wave.Sine, float frequency = float.NaN, float detune = 0f) => Add(new OscSpec(wave, frequency, detune));

    public NodeRef Noise(NoiseColor color = NoiseColor.White) => Add(new NoiseSpec(color));

    public NodeRef Sample(string name, bool loop = false, float offset = float.NaN, int variant = -1, float rate = 1f) =>
        Add(new SampleSpec(name, loop, offset, variant, rate));

    public NodeRef Reverb(string name, bool normalize = true) => Add(new ReverbSpec(name, normalize));

    public NodeRef Const(float offset = 1f) => Add(new ConstSpec(offset));

    public NodeRef Filter(FilterType type = FilterType.Lowpass, float frequency = 1000f, float q = 1f, float gain = 0f) =>
        Add(new FilterSpec(type, frequency, q, gain));

    public NodeRef Gain(float gain = 1f) => Add(new GainSpec(gain));

    public NodeRef Delay(float time = 0.25f, float maxTime = 2f) => Add(new DelaySpec(time, maxTime));

    public NodeRef Shape(float amount = 0.5f) => Add(new ShapeSpec(amount));

    public NodeRef Shape(float[] curve) => Add(new ShapeSpec(0f, curve));

    public NodeRef Pan(float pan = 0f) => Add(new PanSpec(pan));

    public NodeRef Lfo(float frequency, float depth, Wave wave = Wave.Sine) => Add(new LfoSpec(frequency, depth, wave));

    public NodeRef Env(float attack = 0.01f, float decay = 0.1f, float sustain = 0.7f, float release = 0.2f, float peak = 1f) =>
        Add(new EnvSpec(attack, decay, sustain, release, peak));

    public NodeRef Compressor(float threshold = -24f, float knee = 30f, float ratio = 12f, float attack = 0.003f, float release = 0.25f) =>
        Add(new CompressorSpec(threshold, knee, ratio, attack, release));

    /// <summary>Соединить узел from с точкой to (свой узел, его параметр или узел другого голоса).</summary>
    public void Connect(ushort from, Endpoint to) => _connections.Add(new Connection(from, to));

    /// <summary>Событие автоматизации.</summary>
    public void Automate(Automation automation) => _automations.Add(automation);

    /// <summary>Готовый граф (проверяется).</summary>
    public VoiceGraph Build() => new(_nodes, _connections, _automations);
}

/// <summary>Узел строящегося графа.</summary>
public readonly record struct NodeRef(VoiceGraphBuilder Builder, ushort Index)
{
    /// <summary>Параметр узла.</summary>
    public ParamRef this[ParamId param] => new(Builder, Index, param);

    /// <summary>Соединить с узлом; возвращает его — для цепочек.</summary>
    public NodeRef To(NodeRef target)
    {
        Builder.Connect(Index, new Endpoint(0, target.Index));
        return target;
    }

    /// <summary>Соединить с параметром (модуляция).</summary>
    public void To(ParamRef target) => Builder.Connect(Index, new Endpoint(0, target.Node, target.Param));

    /// <summary>Соединить с узлом или параметром другого, уже звучащего голоса (<see cref="Sound.Node"/>).</summary>
    public void To(Endpoint external) => Builder.Connect(Index, external);
}

/// <summary>Параметр узла строящегося графа: set, ramp, exp, target — как у пилота.</summary>
public readonly record struct ParamRef(VoiceGraphBuilder Builder, ushort Node, ParamId Param)
{
    /// <summary>setValueAtTime; at NaN — начало голоса.</summary>
    public ParamRef Set(float value, double at = double.NaN) => Add(AutomationKind.Set, value, at);

    /// <summary>linearRampToValueAtTime.</summary>
    public ParamRef Ramp(float value, double at) => Add(AutomationKind.Linear, value, at);

    /// <summary>exponentialRampToValueAtTime (значение не меньше 1e-4, как у пилота).</summary>
    public ParamRef Exp(float value, double at) => Add(AutomationKind.Exponential, MathF.Max(value, 1e-4f), at);

    /// <summary>setTargetAtTime.</summary>
    public ParamRef Target(float value, double at, float timeConstant) =>
        Add(AutomationKind.Target, value, at, MathF.Max(timeConstant, 1e-4f));

    /// <summary>cancelScheduledValues.</summary>
    public ParamRef Cancel(double at) => Add(AutomationKind.Cancel, 0f, at);

    private ParamRef Add(AutomationKind kind, float value, double at, float timeConstant = 0f)
    {
        Builder.Automate(new Automation(Node, Param, kind, value, at, timeConstant));
        return this;
    }
}
