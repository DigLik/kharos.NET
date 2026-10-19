using System.Buffers.Binary;
using Base.Shared;
using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

/// <summary>Грузовик (Hauler в TS) глазами теста: номера сущностей (0 — нет), груз — имя ("" — нет), фильтр — имена.</summary>
public sealed record HaulerState(uint Mine, uint From, uint To, string Resource, double Amount, bool Full, bool Loading, bool Waiting, double Misses,
    double Stop, string[] Filter, uint Pickup, uint Supply, bool Trapped, uint[] Route, uint[] Serve);

/// <summary>Ручной приказ грузовика (Carry): from 0 — груз из кузова.</summary>
public sealed record CarryState(uint From, uint To, string Resource, double Left, bool Full);

/// <summary>Поручение строителя (Fetch).</summary>
public sealed record FetchState(uint From, uint To, string Resource, double Amount, bool Full, double Misses);

/// <summary>Связь луча за тик: с кем, тянет ли, какой груз.</summary>
public sealed record BeamLink(uint Target, bool Pulling, string Resource);

// Логистика (раздел Logistics, С2b): грузовики, ручные приказы, поручения строителей, лучи, запросы logistics.*.
public sealed partial class Sim
{
    private static readonly string[] GoodSections = ["resources", "products", "ores"];

    /// <summary>Номер Proto груза по имени; нет — −1.</summary>
    public int GoodProto(string good)
    {
        foreach (var section in GoodSections)
            if (Host.Content.FindProto(section + "/" + good) is { } proto)
                return proto.Id;
        return -1;
    }

    /// <summary>Имя груза по номеру Proto; не груз — "".</summary>
    public string GoodName(double proto) =>
        proto >= 0 && proto < Host.Content.Protos.Count && GoodSections.Contains(Host.Content.Protos[(int)proto].Section) ? Host.Content.Protos[(int)proto].Name : "";

    private static uint[] Ids(byte[] bytes)
    {
        var ids = new uint[bytes.Length / 4];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        return ids;
    }

    /// <summary>Поля грузовика.</summary>
    public HaulerState Hauler(uint truck)
    {
        double N(string field) => Num(truck, Names.Hauler.Name, field);
        var mask = (int)N(Names.Hauler.Filter);
        var goods = GoodsOrder.Value;
        var filter = goods.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        return new HaulerState((uint)N(Names.Hauler.Mine), (uint)N(Names.Hauler.From), (uint)N(Names.Hauler.To), GoodName(N(Names.Hauler.Resource)),
            N(Names.Hauler.Amount), N(Names.Hauler.Full) != 0, N(Names.Hauler.Loading) != 0, N(Names.Hauler.Waiting) != 0, N(Names.Hauler.Misses),
            N(Names.Hauler.Stop), filter, (uint)N(Names.Hauler.Pickup), (uint)N(Names.Hauler.Supply), N(Names.Hauler.Trapped) != 0,
            Ids(Bytes(truck, "Hauler.route")), Ids(Bytes(truck, "Hauler.serve")));
    }

    /// <summary>Грузы по порядку столбцов Stock (ресурсы, изделия, руда).</summary>
    private static readonly Lazy<string[]> GoodsOrder = new(() =>
    {
        var content = Content();
        return [.. GoodSections.SelectMany(section => content.Data(section)!.Value.EnumerateObject().Select(property => property.Name))];
    });

    /// <summary>Записать поля работы грузовика прямо в мир (Object.assign(hauler, …) в тестах TS); груз — имя.</summary>
    public void SetHauler(uint truck, uint? from = null, uint? to = null, string? resource = null, double? amount = null, bool? full = null, uint? pickup = null, uint? supply = null)
    {
        if (from is { } f)
            Set(truck, "Hauler.from", f);
        if (to is { } t)
            Set(truck, "Hauler.to", t);
        if (resource is not null)
            Set(truck, "Hauler.resource", GoodProto(resource));
        if (amount is { } a)
            Set(truck, "Hauler.amount", a);
        if (full is { } u)
            Set(truck, "Hauler.full", u ? 1 : 0);
        if (pickup is { } p)
            Set(truck, "Hauler.pickup", p);
        if (supply is { } s)
            Set(truck, "Hauler.supply", s);
    }

    /// <summary>Ручной приказ грузовика; нет — null.</summary>
    public CarryState? CarryOf(uint truck) => Has(truck, Names.Carry.Name)
        ? new CarryState((uint)Num(truck, "Carry.from"), (uint)Num(truck, "Carry.to"), GoodName(Num(truck, "Carry.resource")), Num(truck, "Carry.left"), Num(truck, "Carry.full") != 0)
        : null;

    /// <summary>Поручение строителя; нет — null.</summary>
    public FetchState? FetchOf(uint builder) => Has(builder, Names.Fetch.Name)
        ? new FetchState((uint)Num(builder, "Fetch.from"), (uint)Num(builder, "Fetch.to"), GoodName(Num(builder, "Fetch.resource")), Num(builder, "Fetch.amount"),
            Num(builder, "Fetch.full") != 0, Num(builder, "Fetch.misses"))
        : null;

    /// <summary>Связи луча сущности за последний тик (Beam.links).</summary>
    public List<BeamLink> Links(uint entity)
    {
        var bytes = Bytes(entity, "Beam.links");
        var list = new List<BeamLink>();
        for (var at = 0; at + 9 <= bytes.Length; at += 9)
            list.Add(new BeamLink(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), bytes[at + 4] != 0, GoodName(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 5)))));
        return list;
    }

    /// <summary>Сколько груза уже едет на склад (incomingTo).</summary>
    public double Incoming(uint entity, string good) => Query<double>(LogisticsQueries.Incoming, new GoodArgs { Entity = entity, Good = good });

    /// <summary>Не подъехать к зданию или дропу (unreachable).</summary>
    public bool Unreachable(uint entity) => Query<bool>(LogisticsQueries.Unreachable, new EntityArgs { Entity = entity });

    /// <summary>Сколько груза поместится на склад для грузовика (spaceFor).</summary>
    public double SpaceFor(uint truck, uint to, string good) => Query<double>(LogisticsQueries.SpaceFor, new TruckArgs { Truck = truck, To = to, Good = good });

    /// <summary>Ближайшая переработка руды для грузовика (refineryFor); нет — 0.</summary>
    public uint RefineryFor(uint truck, string ore) => Query<uint>(LogisticsQueries.RefineryFor, new TruckArgs { Truck = truck, Good = ore });

    /// <summary>Можно ли послать грузовики обеспечить здание или стройку (canSupply).</summary>
    public bool CanSupply(uint player, uint target) => Query<bool>(LogisticsQueries.CanSupply, new TargetArgs { Player = player, Target = target });

    /// <summary>Заявки зон игрока (requestsOf).</summary>
    public RequestInfo[] Requests(uint player) => Query<RequestList>(LogisticsQueries.Requests, new PlayerArgs { Player = player }).Items;

    /// <summary>Расстояние между краями двух сущностей (gapBetween): основания по Footprint, юниты — круги по Body.</summary>
    public double Gap(uint a, uint b)
    {
        (double X, double Y, double W, double H, double R) Shape(uint entity)
        {
            var (x, y) = At(entity);
            if (Has(entity, Names.Footprint.Name))
                return (x, y, Num(entity, "Footprint.w"), Num(entity, "Footprint.h"), 0);
            if (Has(entity, Names.Drop.Name))
                return (x, y, 1, 1, 0);
            return (x, y, 0, 0, Has(entity, Names.Body.Name) ? Num(entity, "Body.radius") : 0);
        }
        var (one, two) = (Shape(a), Shape(b));
        var dx = Math.Max(0, Math.Max(one.X - two.X - two.W, two.X - one.X - one.W));
        var dy = Math.Max(0, Math.Max(one.Y - two.Y - two.H, two.Y - one.Y - one.H));
        return Math.Max(0, Math.Sqrt(dx * dx + dy * dy) - one.R - two.R);
    }
}
