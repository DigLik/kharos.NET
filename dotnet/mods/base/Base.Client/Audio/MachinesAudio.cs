using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Звук работы зданий — перенос mods/base/audio/machines.luau (machines.ts). Каждый вид звука — одна петля, сколько бы
/// ни звучало источников: громкость — по тому, сколько их и насколько они близки к середине экрана, сторона — по тому,
/// где они. Звучит только своё работающее: цех стучит, пока движется его работа.
/// </summary>
internal sealed class MachinesAudio
{
    private const double Period = 0.2;
    private const double Saturate = 3;
    private const double WorkHold = 0.6;
    private const double Hearing = 0.6;

    private static readonly (string Name, double Level)[] Levels = [("hum", 0.22), ("machinery", 0.25), ("drill", 0.2)];

    private readonly Dictionary<uint, (double Value, double Until)> _activity = [];
    private readonly Dictionary<string, (double Sum, double Side)> _sums = [];
    private readonly HashSet<uint> _seen = [];
    private readonly List<uint> _gone = [];
    private (ComponentRef Building, ComponentRef Position, ComponentRef Owner, ComponentRef Producer)? _components;
    private KindData? _data;
    private (double X, double Y, double Angle)[] _poses = new (double, double, double)[16];
    private double _since = Period, _clock;

    /// <summary>Работает ли здание: то, что в нём меняется, менялось не раньше WorkHold секунд назад.</summary>
    private bool Busy(uint entity, double value)
    {
        var state = _activity.TryGetValue(entity, out var known) ? known : (Value: value, Until: 0.0);
        if (Math.Abs(value - state.Value) > 1e-9)
            state.Until = _clock + WorkHold;
        state.Value = value;
        _activity[entity] = state;
        return _clock < state.Until;
    }

    private void Add(ClientContext c, string name, double x, double y)
    {
        var near = c.Nearness(x, y, Hearing);
        if (near <= 0)
            return;
        var (sum, side) = _sums.TryGetValue(name, out var known) ? known : (0.0, 0.0);
        _sums[name] = (sum + near, side + near * c.Side(x));
    }

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        _clock += c.Dt;
        _since += c.Dt;
        if (_since < Period)
            return;
        _since = 0;
        _components ??= (c.Component("Building"), c.Component("Position"), c.Component("Owner"), c.Component("Producer"));
        _data ??= KindData.Read(c);
        var (building, position, owner, producer) = _components.Value;
        _sums.Clear();
        _seen.Clear();
        var buildings = c.Each(building, position, owner);
        if (_poses.Length < buildings.Length)
            _poses = new (double, double, double)[Math.Max(buildings.Length, _poses.Length * 2)];
        var poses = _poses.AsSpan(0, buildings.Length);
        c.Poses(buildings, poses);
        for (var i = 0; i < buildings.Length; i++)
        {
            var e = buildings[i];
            _seen.Add(e);
            if (!c.Mine(e))
                continue;
            var (x, y, _) = poses[i];
            if (c.ProtoPath(c.Proto(e)) is { } path && _data.Power.TryGetValue(path, out var power) && power > 0)
                Add(c, "hum", x, y);
            if (c.Get(e, producer) is { } values && values.Number(producer["current"]) >= 0 && Busy(e, values.Number(producer["progress"])))
                Add(c, "machinery", x, y);
        }
        _gone.Clear();
        foreach (var entity in _activity.Keys)
            if (!_seen.Contains(entity))
                _gone.Add(entity);
        foreach (var entity in _gone)
            _activity.Remove(entity);
        foreach (var (name, level) in Levels)
        {
            var (total, side) = _sums.TryGetValue(name, out var known) ? known : (0.0, 0.0);
            c.Audio.Loop(name, (float)(level * (1 - Math.Exp(-total / Saturate))), total > 0 ? (float)(side / total) : 0);
        }
    }
}
