using System.Collections.Concurrent;
using Kharos.Audio.Dsp;
using Kharos.Audio.Graph;

namespace Kharos.Audio.Engine;

/// <summary>Команда потока игры аудиопотоку; применяется в начале кванта.</summary>
internal abstract class Command
{
    public abstract void Apply(Mixer mixer);
}

/// <summary>
/// Микшер в аудиопотоке: общий граф всех голосов (порядок — топологический, петли только через задержку), шины →
/// общая громкость → компрессор → выход. Касается его только аудиопоток (или офлайн-рендер); поток игры шлёт команды
/// через очередь без блокировок.
/// </summary>
internal sealed class Mixer
{
    private const int Q = AudioFormat.Quantum;

    private readonly List<Node> _nodes = [];
    private readonly List<Node> _order = [];
    private readonly List<DelayNode> _cycleDelays = [];
    private readonly Queue<Node> _queue = new();
    private readonly List<Node> _stuck = [];
    private readonly Dictionary<uint, Voice> _voices = [];
    private readonly List<Voice> _voiceList = [];
    private readonly GainNode[] _buses = new GainNode[Buses.Count];
    private readonly GainNode _master;
    private readonly CompressorNode _compressor;
    private readonly Node _output;
    private readonly float[] _outL = new float[Q];
    private readonly float[] _outR = new float[Q];
    private int _outAt = Q;
    private bool _dirty = true;
    private long _frame;
    private long _published;
    private int _oneShots;

    public Mixer(float master, int maxOneShots, bool compressor = true)
    {
        MaxOneShots = maxOneShots;
        // Компрессор не даёт залпу перегрузить звук (порог −18 дБ, отношение 6, прочее — по умолчанию).
        _compressor = new CompressorNode(-18f, 30f, 6f, 0.003f, 0.25f);
        _master = new GainNode(master);
        if (compressor)
            _master.Connect(_compressor);
        _output = compressor ? _compressor : _master;
        for (var i = 0; i < _buses.Length; i++)
        {
            _buses[i] = new GainNode(1f);
            _buses[i].Connect(_master);
            Add(_buses[i]);
        }
        Add(_master);
        Add(_compressor);
    }

    /// <summary>Команды от потока игры.</summary>
    public ConcurrentQueue<Command> Commands { get; } = new();

    public int MaxOneShots { get; }

    /// <summary>Кадр начала следующего кванта (для потока игры).</summary>
    public long PublishedFrame => Volatile.Read(ref _published);

    /// <summary>Сколько разовых звуков звучит (для потока игры).</summary>
    public int OneShots => Volatile.Read(ref _oneShots);

    /// <summary>Голосов в графе.</summary>
    public int VoiceCount => _voiceList.Count;

    /// <summary>Время начала текущего кванта, секунды.</summary>
    public double Now => _frame / (double)AudioFormat.SampleRate;

    public long Frame => _frame;

    /// <summary>Сжатие компрессора на выходе, дБ.</summary>
    public float Reduction => _compressor.Reduction;

    /// <summary>Заполнить interleaved-стерео (L, R, L, R…): кадров = длина / 2.</summary>
    public void Render(Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        var at = 0;
        while (at < frames)
        {
            if (_outAt == Q)
            {
                RenderQuantum();
                _outAt = 0;
            }
            var count = Math.Min(Q - _outAt, frames - at);
            for (var i = 0; i < count; i++)
            {
                interleaved[2 * (at + i)] = _outL[_outAt + i];
                interleaved[2 * (at + i) + 1] = _outR[_outAt + i];
            }
            _outAt += count;
            at += count;
        }
    }

    /// <summary>Один квант.</summary>
    public void RenderQuantum()
    {
        while (Commands.TryDequeue(out var command))
            command.Apply(this);
        if (_dirty)
            Sort();
        var frame = _frame;
        foreach (var node in _order)
        {
            if (node.Muted)
            {
                node.Channels = 0;
                continue;
            }
            foreach (var param in node.Params)
                param.Compute(frame);
            node.Process(frame);
        }
        foreach (var delay in _cycleDelays)
            delay.AfterQuantum();
        var output = _output;
        switch (output.Channels)
        {
            case 0:
                Array.Clear(_outL);
                Array.Clear(_outR);
                break;
            case 1:
                for (var i = 0; i < Q; i++)
                    _outL[i] = _outR[i] = Math.Clamp(output.L[i], -1f, 1f);
                break;
            default:
                for (var i = 0; i < Q; i++)
                {
                    _outL[i] = Math.Clamp(output.L[i], -1f, 1f);
                    _outR[i] = Math.Clamp(output.R[i], -1f, 1f);
                }
                break;
        }
        _frame += Q;
        Maintain();
        Volatile.Write(ref _published, _frame);
    }

    /// <summary>Подключить собранный голос.</summary>
    public void Link(Voice voice)
    {
        if (voice.OneShot)
        {
            if (_oneShots >= MaxOneShots)
            {
                voice.Finished = true;
                return;
            }
            Interlocked.Increment(ref _oneShots);
        }
        _voices[voice.Id] = voice;
        _voiceList.Add(voice);
        foreach (var node in voice.AllNodes())
            Add(node);
        if (voice.Into is { } into && Resolve(into) is { } target)
            Connect(voice.PlacePan, target);
        else
            voice.PlacePan.Connect(_buses[(int)voice.Bus]);
        foreach (var connection in voice.External)
            if (Resolve(connection.To) is { } external)
                Connect(voice.Nodes[connection.From], external);
        _dirty = true;
    }

    public Voice? Find(uint id) => _voices.GetValueOrDefault(id);

    /// <summary>Параметр голоса.</summary>
    public Param? FindParam(uint voice, ushort node, ParamId param) => Find(voice)?.GetParam(node, param);

    /// <summary>Громкость шины (bus &lt; 0 — общая).</summary>
    public Param BusGain(int bus) => bus < 0 ? _master.Gain : _buses[bus].Gain;

    /// <summary>Отпустить голос: громкость к нулю с постоянной fade/3, снять через 2·fade + 50 мс.</summary>
    public void Release(uint id, float fade)
    {
        if (Find(id) is not { Released: false } voice)
            return;
        voice.Released = true;
        var now = Now;
        var gain = voice.Place.Gain.Timeline;
        gain.Insert(AutomationKind.Cancel, 0f, now, 0, now);
        gain.Insert(AutomationKind.Target, 0f, now, Math.Max(fade / 3.0, 0.005), now);
        var end = now + fade * 2.0 + 0.05;
        foreach (var source in voice.Sources)
            source.Stop(end);
        voice.EndFrame = Math.Min(voice.EndFrame, Frames.Of(end));
    }

    private (Node Node, Param? Param)? Resolve(Endpoint endpoint)
    {
        if (Find(endpoint.Voice) is not { } voice || endpoint.Node >= voice.Nodes.Length)
            return null;
        var node = voice.Nodes[endpoint.Node];
        if (endpoint.Param is { } id)
            return node.GetParam(id) is { } param ? (node, param) : null;
        return (node, null);
    }

    private static void Connect(Node source, (Node Node, Param? Param)? target)
    {
        if (target is not { } value)
            return;
        if (value.Param is { } param)
            source.Connect(param);
        else
            source.Connect(value.Node);
    }

    private void Add(Node node)
    {
        _nodes.Add(node);
        foreach (var param in node.Params)
            param.Owner = node;
    }

    private void Remove(Voice voice)
    {
        foreach (var node in voice.AllNodes())
        {
            node.DisconnectAll();
            _nodes.Remove(node);
        }
        _voices.Remove(voice.Id);
        _voiceList.Remove(voice);
        if (voice.OneShot)
            Interlocked.Decrement(ref _oneShots);
        voice.Finished = true;
        _dirty = true;
    }

    /// <summary>Снять отзвучавшие голоса.</summary>
    private void Maintain()
    {
        for (var i = _voiceList.Count - 1; i >= 0; i--)
        {
            var voice = _voiceList[i];
            if (_frame >= voice.EndFrame)
            {
                Remove(voice);
                continue;
            }
            if (voice.Sources.Count == 0)
                continue;
            var ended = true;
            foreach (var source in voice.Sources)
                ended &= source.Ended;
            if (!ended || FedFromOutside(voice))
            {
                voice.Quiet = 0;
                continue;
            }
            voice.Quiet += Q;
            if (voice.Quiet > voice.Tail)
                Remove(voice);
        }
    }

    private static bool FedFromOutside(Voice voice)
    {
        foreach (var node in voice.Nodes)
        {
            foreach (var input in node.Inputs)
                if (input.Owner != voice)
                    return true;
            foreach (var param in node.Params)
                foreach (var input in param.Inputs)
                    if (input.Owner != voice)
                        return true;
        }
        return false;
    }

    /// <summary>
    /// Порядок обработки: Кан. Петля через задержку разрывается на ней (задержка ≥ кванта), узлы петли без задержки
    /// молчат — остальной граф звучит.
    /// </summary>
    private void Sort()
    {
        _dirty = false;
        foreach (var node in _nodes)
        {
            node.Muted = false;
            if (node is DelayNode delay)
                delay.InCycle = false;
        }
        while (!TrySort(out var stuck))
        {
            var broke = false;
            foreach (var node in stuck)
                if (node is DelayNode { InCycle: false } delay && ReachesItself(delay))
                {
                    delay.InCycle = true;
                    broke = true;
                }
            if (broke)
                continue;
            foreach (var node in stuck)
                if (!node.Muted && ReachesItself(node))
                {
                    node.Muted = true;
                    broke = true;
                }
            if (!broke)
                throw new InvalidOperationException("граф звука: узлы без порядка вне петель");
        }
        _cycleDelays.Clear();
        foreach (var node in _order)
            if (node is DelayNode { InCycle: true } delay)
                _cycleDelays.Add(delay);
    }

    /// <summary>Узел не ждёт входов: задержка, разрывающая петлю, или молчащий узел петли.</summary>
    private static bool Free(Node node) => node.Muted || node is DelayNode { InCycle: true };

    private bool TrySort(out List<Node> stuck)
    {
        _order.Clear();
        foreach (var node in _nodes)
        {
            node.Placed = false;
            node.InDegree = 0;
            if (Free(node))
                continue;
            node.InDegree += node.Inputs.Count;
            foreach (var param in node.Params)
                node.InDegree += param.Inputs.Count;
        }
        var queue = _queue;
        queue.Clear();
        foreach (var node in _nodes)
            if (node.InDegree == 0)
                queue.Enqueue(node);
        while (queue.TryDequeue(out var node))
        {
            node.Placed = true;
            _order.Add(node);
            foreach (var target in node.OutputNodes)
                Release(target, queue);
            foreach (var param in node.OutputParams)
                if (param.Owner is { } owner)
                    Release(owner, queue);
        }
        stuck = _stuck;
        stuck.Clear();
        if (_order.Count == _nodes.Count)
            return true;
        foreach (var node in _nodes)
            if (!node.Placed)
                stuck.Add(node);
        return false;

        static void Release(Node target, Queue<Node> queue)
        {
            if (Free(target))
                return;
            if (--target.InDegree == 0)
                queue.Enqueue(target);
        }
    }

    private static bool ReachesItself(Node start)
    {
        var seen = new HashSet<Node>();
        var stack = new Stack<Node>();
        Push(start);
        while (stack.TryPop(out var node))
        {
            foreach (var next in node.OutputNodes)
            {
                if (next == start)
                    return true;
                Push(next);
            }
            foreach (var param in node.OutputParams)
            {
                if (param.Owner == start)
                    return true;
                if (param.Owner is { } owner)
                    Push(owner);
            }
        }
        return false;

        void Push(Node node)
        {
            if (!node.Placed && seen.Add(node))
                stack.Push(node);
        }
    }
}

/// <summary>Подключить голос.</summary>
internal sealed class StartVoice(Voice voice) : Command
{
    public override void Apply(Mixer mixer) => mixer.Link(voice);
}

/// <summary>Событие автоматизации параметра звучащего голоса (время NaN — сейчас).</summary>
internal sealed class Automate(uint voice, ushort node, ParamId param, AutomationKind kind, float value, double time, float timeConstant) : Command
{
    public override void Apply(Mixer mixer) =>
        mixer.FindParam(voice, node, param)?.Timeline.Insert(kind, value, double.IsNaN(time) ? mixer.Now : time, timeConstant, mixer.Now);
}

/// <summary>Громкость и панорама звучания — плавно (постоянная tau).</summary>
internal sealed class PlaceVoice(uint voice, float gain, float pan, float tau) : Command
{
    public override void Apply(Mixer mixer)
    {
        if (mixer.Find(voice) is not { Released: false } target)
            return;
        var now = mixer.Now;
        target.Place.Gain.Timeline.Insert(AutomationKind.Target, MathF.Max(gain, 0f), now, tau, now);
        target.PlacePan.Pan.Timeline.Insert(AutomationKind.Target, Math.Clamp(pan, -1f, 1f), now, tau, now);
    }
}

/// <summary>Отпустить голос.</summary>
internal sealed class ReleaseVoice(uint voice, float fade) : Command
{
    public override void Apply(Mixer mixer) => mixer.Release(voice, fade);
}

/// <summary>Громкость шины (−1 — общая) — плавно за 50 мс.</summary>
internal sealed class BusVolume(int bus, float value) : Command
{
    public override void Apply(Mixer mixer) =>
        mixer.BusGain(bus).Timeline.Insert(AutomationKind.Target, value, mixer.Now, 0.05, mixer.Now);
}
