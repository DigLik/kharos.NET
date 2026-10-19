using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Система recover (combat.ts): те, чья прочность восстанавливается сама (Health.regen), понемногу поправляются — кроме
/// задетых в этот тик. У своей работающей ремонтной станции — быстрее, на долю её энергии (STATION_REGEN). Кадр — всё с
/// прочностью (Health, Position, Owner), станции — побочной таблицей (Repair, Building, Position, Owner, Proto, Footprint).
/// </summary>
internal sealed class Recover : ISystem
{
    /// <summary>Во сколько раз быстрее поправляется сама пехота у своей работающей станции с полной энергией.</summary>
    public const double StationRegen = 4;

    private Column _value, _max, _regen, _hit, _x, _y, _owner;
    private Column _stationX, _stationY, _stationOwner, _stationRadius, _stationProto, _stationW, _stationH;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Health.Name, Names.Position.Name, Names.Owner.Name).Write(Names.Health.Name)
        .Table([Names.Repair.Name, Names.Building.Name, Names.Position.Name, Names.Owner.Name, Names.Proto.Name, Names.Footprint.Name], [Names.Site.Name]);

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _value = binding.Column(Names.Health.Name, Names.Health.Value);
        _max = binding.Column(Names.Health.Name, Names.Health.Max);
        _regen = binding.Column(Names.Health.Name, Names.Health.Regen);
        _hit = binding.Column(Names.Health.Name, Names.Health.Hit);
        _x = binding.Column(Names.Position.Name, Names.Position.X);
        _y = binding.Column(Names.Position.Name, Names.Position.Y);
        _owner = binding.Column(Names.Owner.Name, Names.Owner.Player);
        var stations = binding.Table(0);
        _stationX = stations.Column(Names.Position.Name, Names.Position.X);
        _stationY = stations.Column(Names.Position.Name, Names.Position.Y);
        _stationOwner = stations.Column(Names.Owner.Name, Names.Owner.Player);
        _stationRadius = stations.Column(Names.Repair.Name, Names.Repair.Radius);
        _stationProto = stations.Column(Names.Proto.Name, Names.Proto.Id);
        _stationW = stations.Column(Names.Footprint.Name, Names.Footprint.W);
        _stationH = stations.Column(Names.Footprint.Name, Names.Footprint.H);
    }

    private readonly record struct Station(uint Player, double X, double Y, double Radius, double Share);

    public void Run(SystemContext ctx)
    {
        var value = ctx.Column(_value);
        var max = ctx.Column(_max);
        var regen = ctx.Column(_regen);
        var hit = ctx.Column(_hit);
        var xs = ctx.Column(_x);
        var ys = ctx.Column(_y);
        var owners = ctx.Column(_owner);
        List<Station>? stations = null;
        var read = false;
        for (var row = 0; row < ctx.Count; row++)
        {
            if (regen[row] <= 0 || hit[row] == ctx.Tick || value[row] >= max[row])
                continue;
            if (!read)
            {
                read = true;
                stations = Stations(ctx);
            }
            double boost = 1;
            if (stations is not null)
                foreach (var station in stations)
                {
                    var (dx, dy) = (xs[row] - station.X, ys[row] - station.Y);
                    if (station.Player == (uint)owners[row] && Math.Sqrt(dx * dx + dy * dy) <= station.Radius)
                        boost = Math.Max(boost, 1 + (StationRegen - 1) * station.Share);
                }
            value[row] = Math.Min(max[row], value[row] + regen[row] * boost * Core.Step);
        }
    }

    /// <summary>
    /// Работающие ремонтные станции (repairStations): здания с Repair, кроме площадок; потребитель — на долю энергии зоны,
    /// выключенный и вне сети не работает; без расхода — всегда.
    /// </summary>
    private List<Station>? Stations(SystemContext ctx)
    {
        var table = ctx.Table(0);
        if (table.Count == 0)
            return null;
        var xs = table.Column(_stationX);
        var ys = table.Column(_stationY);
        var owners = table.Column(_stationOwner);
        var radii = table.Column(_stationRadius);
        var protos = table.Column(_stationProto);
        var ws = table.Column(_stationW);
        var hs = table.Column(_stationH);
        var rows = new int[table.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = i;
        var entities = table.Entities.ToArray();
        Array.Sort(rows, (a, b) => entities[a].CompareTo(entities[b]));
        Call? call = null;
        var list = new List<Station>();
        foreach (var row in rows)
        {
            // Выключенный потребитель и потребитель вне сети в долях энергии не значатся; здание без расхода работает всегда.
            double share = 1;
            if (Arms.Building((int)protos[row]) is { Consumer: true })
                share = Books.ShareOf(call ??= Call.Of(ctx), entities[row]);
            if (share <= 0)
                continue;
            list.Add(new Station((uint)owners[row], xs[row] + ws[row] / 2, ys[row] + hs[row] / 2, radii[row], share));
        }
        return list.Count > 0 ? list : null;
    }
}
