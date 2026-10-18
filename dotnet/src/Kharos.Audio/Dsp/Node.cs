using Kharos.Audio.Graph;

namespace Kharos.Audio.Dsp;

/// <summary>
/// AudioParam: расписание, входы-модуляторы (сводятся в моно и прибавляются), ограничение номинальным диапазоном.
/// a-rate — значение на каждый кадр, k-rate — одно на квант (по первому кадру).
/// </summary>
internal sealed class Param(float value, float min, float max, bool kRate = false)
{
    private const int Q = AudioFormat.Quantum;

    public readonly ParamTimeline Timeline = new(value);
    public readonly float Min = min;
    public readonly float Max = max;
    public readonly bool KRate = kRate;
    public readonly List<Node> Inputs = [];
    public readonly float[] Values = new float[Q];

    /// <summary>Узел, которому принадлежит параметр (для порядка обработки).</summary>
    public Node? Owner;

    /// <summary>Значение одно на весь квант (<see cref="Constant"/>).</summary>
    public bool Steady = true;

    public float Constant = Math.Clamp(value, min, max);

    public float this[int frame] => Steady ? Constant : Values[frame];

    /// <summary>Значения на квант, начинающийся с кадра frame.</summary>
    public void Compute(long frame)
    {
        var t0 = frame / (double)AudioFormat.SampleRate;
        var modulated = false;
        foreach (var input in Inputs)
            modulated |= input.Channels > 0;
        if (KRate)
        {
            var value = Timeline.At(t0);
            if (modulated)
                foreach (var input in Inputs)
                    if (input.Channels > 0)
                        value += input.Channels == 1 ? input.L[0] : 0.5f * (input.L[0] + input.R[0]);
            Steady = true;
            Constant = Clamp(value);
            return;
        }
        var steady = Timeline.Steady(t0, t0 + (Q - 1) / (double)AudioFormat.SampleRate, out var first);
        if (steady && !modulated)
        {
            Steady = true;
            Constant = Clamp(first);
            return;
        }
        Steady = false;
        var values = Values;
        if (steady)
            values.AsSpan().Fill(first);
        else
            for (var i = 0; i < Q; i++)
                values[i] = Timeline.At(t0 + i / (double)AudioFormat.SampleRate);
        if (modulated)
            foreach (var input in Inputs)
            {
                if (input.Channels == 1)
                    Mix.Add(input.L, values);
                else if (input.Channels == 2)
                    for (var i = 0; i < Q; i++)
                        values[i] += 0.5f * (input.L[i] + input.R[i]);
            }
        for (var i = 0; i < Q; i++)
            values[i] = Clamp(values[i]);
    }

    private float Clamp(float value) => float.IsNaN(value) ? Min > 0 ? Min : 0f : Math.Clamp(value, Min, Max);
}

/// <summary>Узел графа в аудиопотоке: выход — квант L/R (Channels: 0 — тишина, 1 — моно в L, 2 — стерео).</summary>
internal abstract class Node
{
    protected const int Q = AudioFormat.Quantum;

    public readonly float[] L = new float[Q];
    public readonly float[] R = new float[Q];

    /// <summary>Каналов на выходе в этом кванте; 0 — тишина.</summary>
    public int Channels;

    /// <summary>Что подключено ко входу.</summary>
    public readonly List<Node> Inputs = [];

    /// <summary>Куда идёт выход: узлы и параметры (для отключения).</summary>
    public readonly List<Node> OutputNodes = [];

    public readonly List<Param> OutputParams = [];

    /// <summary>Голос, которому принадлежит узел (null — шины и общий выход).</summary>
    public object? Owner;

    /// <summary>В петле без задержки: молчит (как требует спецификация).</summary>
    public bool Muted;

    internal int InDegree;
    internal bool Placed;

    private float[]? _mixL;
    private float[]? _mixR;

    /// <summary>Параметры узла.</summary>
    public virtual Param? GetParam(ParamId id) => null;

    /// <summary>Все параметры (для порядка обработки).</summary>
    public virtual ReadOnlySpan<Param> Params => [];

    /// <summary>Обработать квант с кадра frame; параметры уже посчитаны.</summary>
    public abstract void Process(long frame);

    /// <summary>Задержка в петле: вход пишется после всех узлов кванта.</summary>
    public virtual void AfterQuantum()
    {
    }

    /// <summary>Сколько ещё кадров узел может звучать без входа (хвост задержки, свёртки, фильтра).</summary>
    public virtual long TailFrames => 0;

    public void Connect(Node target)
    {
        OutputNodes.Add(target);
        target.Inputs.Add(this);
    }

    public void Connect(Param target)
    {
        OutputParams.Add(target);
        target.Inputs.Add(this);
    }

    /// <summary>Отключить узел от всех, кто его слушает, и от всех, кого слушает он.</summary>
    public void DisconnectAll()
    {
        foreach (var target in OutputNodes)
            target.Inputs.Remove(this);
        foreach (var target in OutputParams)
            target.Inputs.Remove(this);
        OutputNodes.Clear();
        OutputParams.Clear();
        foreach (var source in Inputs)
            source.OutputNodes.Remove(this);
        Inputs.Clear();
        foreach (var param in Params)
        {
            foreach (var source in param.Inputs)
                source.OutputParams.Remove(param);
            param.Inputs.Clear();
        }
    }

    /// <summary>Сведённый вход: каналы (0 — тишина); для моно l и r — один массив.</summary>
    protected int Input(out float[] l, out float[] r)
    {
        Node? single = null;
        int count = 0, channels = 0;
        foreach (var input in Inputs)
            if (input.Channels > 0)
            {
                count++;
                single = input;
                channels = Math.Max(channels, input.Channels);
            }
        if (count == 0)
        {
            l = r = L;
            return 0;
        }
        if (count == 1)
        {
            l = single!.L;
            r = channels == 2 ? single.R : single.L;
            return channels;
        }
        l = _mixL ??= new float[Q];
        r = channels == 2 ? _mixR ??= new float[Q] : l;
        Array.Clear(l);
        if (channels == 2)
            Array.Clear(r);
        foreach (var input in Inputs)
        {
            if (input.Channels == 0)
                continue;
            Mix.Add(input.L, l);
            if (channels == 2)
                Mix.Add(input.Channels == 2 ? input.R : input.L, r);
        }
        return channels;
    }
}

/// <summary>Источник с расписанием start/stop (AudioScheduledSourceNode).</summary>
internal abstract class SourceNode : Node
{
    public long StartFrame = long.MaxValue;
    public long StopFrame = long.MaxValue;

    /// <summary>Отзвучал: остановлен или запись кончилась.</summary>
    public bool Ended;

    public void Start(double time) => StartFrame = Frames.Of(time);

    public void Stop(double time)
    {
        var frame = Frames.Of(time);
        if (frame < StopFrame)
            StopFrame = frame;
    }

    /// <summary>Какие кадры кванта звучат: [from, to); false — весь квант тихий.</summary>
    protected bool Active(long frame, out int from, out int to)
    {
        from = (int)Math.Clamp(StartFrame - frame, 0, Q);
        to = (int)Math.Clamp(StopFrame - frame, 0, Q);
        if (StopFrame <= frame + Q)
            Ended = true;
        return from < to;
    }
}

/// <summary>Пересчёт времени в кадры.</summary>
internal static class Frames
{
    /// <summary>Первый кадр не раньше time секунд.</summary>
    public static long Of(double time)
    {
        if (double.IsNaN(time) || time <= 0)
            return 0;
        if (time * AudioFormat.SampleRate >= long.MaxValue / 2)
            return long.MaxValue;
        // Допуск в тысячную кадра: время из float (0,1f) не попадает в кадр точно.
        return (long)Math.Ceiling(time * AudioFormat.SampleRate - 1e-3);
    }
}
