using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Звук работы зданий — перенос mods/base/audio/machines.luau (machines.ts). Каждый вид звука — одна петля, сколько бы
/// ни звучало источников: громкость — по тому, сколько их и насколько они близки к середине экрана, сторона — по тому,
/// где они. Звучит только своё работающее: электростанция гудит всегда, реактор — пока горит стержень; шахта бурит, пока
/// в ней прибывает или убывает руда, цех, переработка, сборка и ракетная шахта стучат, пока движется их работа, — стоящие
/// без материалов молчат.
/// </summary>
internal sealed class MachinesAudio
{
    private const double Period = 0.2;
    private const double Saturate = 3;
    private const double WorkHold = 0.6;
    private const double Hearing = 0.6;

    private static readonly (string Name, double Level)[] Levels = [("hum", 0.22), ("machinery", 0.25), ("drill", 0.2)];
    private static readonly HashSet<string> NotGoods = ["capacity", "reserve"];

    private readonly Dictionary<uint, (double Value, double Until)> _activity = [];
    private readonly Dictionary<string, (double Sum, double Side)> _sums = [];
    private readonly HashSet<uint> _seen = [];
    private readonly List<uint> _gone = [];
    private ComponentRef? _building, _position, _owner, _producer, _footprint, _reactor, _stock, _assembly, _silo, _site;
    private FieldRef[] _goods = [];
    private KindData? _data;
    private double[] _owners = new double[16];
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

    private void Add(ClientContext c, string name, (double X, double Y)? at)
    {
        if (at is not { } spot)
            return;
        var near = c.Nearness(spot.X, spot.Y, Hearing);
        if (near <= 0)
            return;
        var (sum, side) = _sums.TryGetValue(name, out var known) ? known : (0.0, 0.0);
        _sums[name] = (sum + near, side + near * c.Side(spot.X));
    }

    /// <summary>Середина основания здания.</summary>
    private (double X, double Y)? Center(ClientContext c, uint entity)
    {
        if (c.Pose(entity) is not { } pose)
            return null;
        return c.Get(entity, _footprint!) is { } footprint ? (pose.X + footprint.Number(_footprint!["w"]) / 2, pose.Y + footprint.Number(_footprint["h"]) / 2) : (pose.X, pose.Y);
    }

    private static double Number(ClientContext c, uint entity, ComponentRef? component, string field) =>
        component is not null && c.Get(entity, component) is { } values ? values.Number(component[field]) : double.NaN;

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        _clock += c.Dt;
        _since += c.Dt;
        if (_since < Period)
            return;
        _since = 0;
        if (_building is null)
        {
            (_building, _position, _owner, _producer) = (c.Component("Building"), c.Component("Position"), c.Component("Owner"), c.Component("Producer"));
            (_footprint, _reactor, _stock, _assembly, _silo) = (c.Component("Footprint"), c.Component("Reactor"), c.Component("Stock"), c.Component("Assembly"), c.Component("Silo"));
            _site = c.Component("Site");
            _goods = [.. _stock.Fields.Where(f => !NotGoods.Contains(f.Name))];
            _data = KindData.Read(c);
        }
        _sums.Clear();
        _seen.Clear();
        var buildings = c.Each(_building, _position!, _owner!);
        if (_owners.Length < buildings.Length)
            _owners = new double[Math.Max(buildings.Length, _owners.Length * 2)];
        if (buildings.Length > 0)
            c.Column(buildings, _owner!, "player", _owners);
        for (var i = 0; i < buildings.Length; i++)
        {
            var e = buildings[i];
            _seen.Add(e);
            // Своё и достроенное: площадка (Site) не гудит и не стучит.
            if (_owners[i] != c.Player || c.Has(e, _site!))
                continue;
            if (c.ProtoPath(c.Proto(e)) is not { } path || !_data!.Buildings.TryGetValue(path, out var spec))
                continue;
            var at = Center(c, e);
            if (spec.Power > 0 && (!c.Has(e, _reactor!) || Number(c, e, _reactor, "left") > 0))
                Add(c, "hum", at);
            double inventory = double.NaN;
            if ((spec.Extract || spec.Refines is not null) && c.Get(e, _stock!) is { } stock)
            {
                inventory = 0;
                foreach (var good in _goods)
                    inventory += stock.Number(good);
                if (spec.Refines is not null && _stock!.Find(spec.Refines) is { } ore)
                    inventory = stock.Number(ore);
            }
            if (spec.Extract && !double.IsNaN(inventory) && Busy(e, inventory))
                Add(c, "drill", at);
            // Шахта собирает боеголовку — тоже цех.
            var work = c.Has(e, _silo!) ? Number(c, e, _silo, "progress")
                : c.Has(e, _assembly!) ? Number(c, e, _assembly, "progress")
                : spec.Refines is not null ? inventory
                : Number(c, e, _producer, "current") >= 0 ? Number(c, e, _producer, "progress") : double.NaN;
            if (!double.IsNaN(work) && Busy(e, work))
                Add(c, "machinery", at);
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
