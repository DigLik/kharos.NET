using Base.Host.Sections.Mining;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Побочные таблицы систем логистики (docs/dotnet-n5.md, Н5-3): всё, что логистике нужно знать о складах мира, приходит
/// с кадром — склады, хозяева, виды, дропы, отметки, заказы, лучи, поручения строителей, пачки труб, — и собирается в
/// снимок (<see cref="View.Snapshot"/>) без единого вызова инструмента. Порядок таблиц — <see cref="Parts"/>; система
/// объявляет их подряд с номера first и может добавить свои после.
/// </summary>
internal sealed class Frame
{
    private static readonly string[][] Parts =
    [
        [Names.Stock.Name, Names.Position.Name],
        [Names.Owner.Name, Names.Stock.Name],
        [Names.Building.Name, Names.Proto.Name, Names.Stock.Name],
        [Names.Site.Name, Names.Proto.Name, Names.Stock.Name],
        [Names.Drop.Name, Names.Stock.Name],
        [Names.Unit.Name, Names.Proto.Name, Names.Body.Name, Names.Stock.Name],
        [Names.Converting.Name],
        [Names.Off.Name],
        [Names.Unreachable.Name],
        [Names.Limits.Name, Names.Stock.Name],
        [Names.Wanted.Name, Names.Stock.Name],
        [Names.Trade.Name, Names.Stock.Name],
        [Names.Assembly.Name, Names.Stock.Name],
        [Names.Producer.Name, Names.Stock.Name],
        [Names.Beam.Name, Names.Stock.Name],
        [Names.Hauler.Name, Names.Path.Name],
        [Names.Carry.Name, Names.Hauler.Name],
        [Names.Fetch.Name, Names.Stock.Name],
        [Names.Batch.Name],
    ];

    /// <summary>Сколько таблиц объявляет <see cref="Declare"/>.</summary>
    public static int Count => Parts.Length;

    private const int StockT = 0, OwnerT = 1, BuildingT = 2, SiteT = 3, DropT = 4, UnitT = 5, ConvertingT = 6, OffT = 7, MarkT = 8, LimitsT = 9,
        WantedT = 10, TradeT = 11, AssemblyT = 12, ProducerT = 13, BeamT = 14, MovingT = 15, CarryT = 16, FetchT = 17, BatchT = 18;

    private readonly int _first;
    private Column _capacity, _reserve, _x, _y, _owner, _buildingProto, _siteProto, _demolish, _unitProto, _radius, _until;
    private Column[] _stock = [], _limits = [], _wanted = [];
    private Column _tradeResource, _tradeWanted, _tradeTotal, _tradeBuy, _on, _progress, _beamRadius, _beamRate, _beamGive, _beamTake;
    private Column _fetchFrom, _fetchTo, _fetchResource, _fetchAmount, _fetchFull, _batchTo, _batchResource, _batchAmount, _batchArrive;

    public Frame(int first) => _first = first;

    /// <summary>Объявляет таблицы снимка подряд (после своих, если есть).</summary>
    public static SystemDeclaration Declare(SystemDeclaration declaration)
    {
        foreach (var part in Parts)
            declaration = declaration.Table(part);
        return declaration;
    }

    /// <summary>Столбцы таблиц (Bind системы).</summary>
    public void Bind(SystemBinding binding)
    {
        PartBinding T(int i) => binding.Table(_first + i);
        var goods = Goods.All;
        _stock = [.. goods.Select(good => T(StockT).Column(Names.Stock.Name, good))];
        _capacity = T(StockT).Column(Names.Stock.Name, Names.Stock.Capacity);
        _reserve = T(StockT).Column(Names.Stock.Name, Names.Stock.Reserve);
        _x = T(StockT).Column(Names.Position.Name, Names.Position.X);
        _y = T(StockT).Column(Names.Position.Name, Names.Position.Y);
        _owner = T(OwnerT).Column(Names.Owner.Name, Names.Owner.Player);
        _buildingProto = T(BuildingT).Column(Names.Proto.Name, Names.Proto.Id);
        _siteProto = T(SiteT).Column(Names.Proto.Name, Names.Proto.Id);
        _demolish = T(SiteT).Column(Names.Site.Name, Names.Site.Demolish);
        _unitProto = T(UnitT).Column(Names.Proto.Name, Names.Proto.Id);
        _radius = T(UnitT).Column(Names.Body.Name, Names.Body.Radius);
        _until = T(MarkT).Column(Names.Unreachable.Name, Names.Unreachable.Until);
        _limits = [.. goods.Select(good => T(LimitsT).Column(Names.Limits.Name, good))];
        _wanted = [.. goods.Select(good => T(WantedT).Column(Names.Wanted.Name, good))];
        _tradeResource = T(TradeT).Column(Names.Trade.Name, Names.Trade.Resource);
        _tradeWanted = T(TradeT).Column(Names.Trade.Name, Names.Trade.Wanted);
        _tradeTotal = T(TradeT).Column(Names.Trade.Name, Names.Trade.Total);
        _tradeBuy = T(TradeT).Column(Names.Trade.Name, Names.Trade.Buy);
        _on = T(AssemblyT).Column(Names.Assembly.Name, Names.Assembly.On);
        _progress = T(ProducerT).Column(Names.Producer.Name, Names.Producer.Progress);
        _beamRadius = T(BeamT).Column(Names.Beam.Name, Names.Beam.Radius);
        _beamRate = T(BeamT).Column(Names.Beam.Name, Names.Beam.Rate);
        _beamGive = T(BeamT).Column(Names.Beam.Name, Names.Beam.Give);
        _beamTake = T(BeamT).Column(Names.Beam.Name, Names.Beam.Take);
        _fetchFrom = T(FetchT).Column(Names.Fetch.Name, Names.Fetch.From);
        _fetchTo = T(FetchT).Column(Names.Fetch.Name, Names.Fetch.To);
        _fetchResource = T(FetchT).Column(Names.Fetch.Name, Names.Fetch.Resource);
        _fetchAmount = T(FetchT).Column(Names.Fetch.Name, Names.Fetch.Amount);
        _fetchFull = T(FetchT).Column(Names.Fetch.Name, Names.Fetch.Full);
        _batchTo = T(BatchT).Column(Names.Batch.Name, Names.Batch.To);
        _batchResource = T(BatchT).Column(Names.Batch.Name, Names.Batch.Resource);
        _batchAmount = T(BatchT).Column(Names.Batch.Name, Names.Batch.Amount);
        _batchArrive = T(BatchT).Column(Names.Batch.Name, Names.Batch.Arrive);
    }

    /// <summary>Что прочитано из таблиц: снимок, грузовики в пути и с приказом, поручения, пачки, отмеченные, лучи.</summary>
    public sealed class Read
    {
        public readonly View.Snapshot Snapshot = new();
        public uint[] Holders = [];
        public readonly HashSet<uint> Moving = [], Carrying = [];
        public readonly List<FetchJob> Fetchers = [];
        public readonly List<(uint To, int Good, double Amount)> Batches = [];
        public uint[] Marked = [], Beams = [];
    }

    /// <summary>Снимок мира из таблиц кадра.</summary>
    public Read Take(in SystemContext ctx)
    {
        var read = new Read();
        var snap = read.Snapshot;
        var width = View.Width;
        var count = Goods.All.Count;

        var table = ctx.Table(_first + StockT);
        var entities = table.Entities;
        snap.Stock = new double[entities.Length * width];
        var capacity = table.Column(_capacity);
        var reserve = table.Column(_reserve);
        var xs = table.Column(_x);
        var ys = table.Column(_y);
        for (var row = 0; row < entities.Length; row++)
        {
            snap.StockRow[entities[row]] = row;
            snap.Position[entities[row]] = (xs[row], ys[row]);
            snap.Stock[row * width] = capacity[row];
            snap.Stock[row * width + 1] = reserve[row];
        }
        for (var good = 0; good < count; good++)
        {
            var column = table.Column(_stock[good]);
            for (var row = 0; row < entities.Length; row++)
                snap.Stock[row * width + 2 + good] = column[row];
        }
        read.Holders = [.. entities];
        Array.Sort(read.Holders);

        table = ctx.Table(_first + OwnerT);
        var owners = table.Column(_owner);
        for (var row = 0; row < table.Count; row++)
            snap.Owner[table.Entities[row]] = (uint)owners[row];
        table = ctx.Table(_first + BuildingT);
        var protos = table.Column(_buildingProto);
        for (var row = 0; row < table.Count; row++)
            snap.Building[table.Entities[row]] = (int)protos[row];
        table = ctx.Table(_first + SiteT);
        protos = table.Column(_siteProto);
        var demolish = table.Column(_demolish);
        for (var row = 0; row < table.Count; row++)
        {
            snap.Site[table.Entities[row]] = (int)protos[row];
            snap.SiteDemolish[table.Entities[row]] = demolish[row] != 0;
        }
        foreach (var entity in ctx.Table(_first + DropT).Entities)
            snap.Drop.Add(entity);
        table = ctx.Table(_first + UnitT);
        protos = table.Column(_unitProto);
        var radius = table.Column(_radius);
        for (var row = 0; row < table.Count; row++)
            snap.Unit[table.Entities[row]] = ((int)protos[row], radius[row]);
        foreach (var entity in ctx.Table(_first + ConvertingT).Entities)
            snap.Converting.Add(entity);
        foreach (var entity in ctx.Table(_first + OffT).Entities)
            snap.Off.Add(entity);
        table = ctx.Table(_first + MarkT);
        var until = table.Column(_until);
        for (var row = 0; row < table.Count; row++)
            snap.Until[table.Entities[row]] = until[row];
        read.Marked = [.. table.Entities];
        Array.Sort(read.Marked);
        FillGoods(ctx.Table(_first + LimitsT), _limits, snap.Limits);
        FillGoods(ctx.Table(_first + WantedT), _wanted, snap.Wanted);
        table = ctx.Table(_first + TradeT);
        var resource = table.Column(_tradeResource);
        var wanted = table.Column(_tradeWanted);
        var total = table.Column(_tradeTotal);
        var buy = table.Column(_tradeBuy);
        for (var row = 0; row < table.Count; row++)
            snap.Trade[table.Entities[row]] = (Cargo.Of(resource[row]), wanted[row], total[row], buy[row] != 0);
        table = ctx.Table(_first + AssemblyT);
        var on = table.Column(_on);
        for (var row = 0; row < table.Count; row++)
            snap.Assembly[table.Entities[row]] = on[row] != 0;
        table = ctx.Table(_first + ProducerT);
        var progress = table.Column(_progress);
        for (var row = 0; row < table.Count; row++)
            snap.Producer[table.Entities[row]] = progress[row];
        table = ctx.Table(_first + BeamT);
        var beamRadius = table.Column(_beamRadius);
        var rate = table.Column(_beamRate);
        var give = table.Column(_beamGive);
        var take = table.Column(_beamTake);
        for (var row = 0; row < table.Count; row++)
            snap.Beam[table.Entities[row]] = (beamRadius[row], rate[row], give[row] != 0, take[row] != 0);
        read.Beams = [.. table.Entities];
        Array.Sort(read.Beams);
        foreach (var entity in ctx.Table(_first + MovingT).Entities)
            read.Moving.Add(entity);
        foreach (var entity in ctx.Table(_first + CarryT).Entities)
            read.Carrying.Add(entity);
        table = ctx.Table(_first + FetchT);
        var from = table.Column(_fetchFrom);
        var to = table.Column(_fetchTo);
        var good2 = table.Column(_fetchResource);
        var amount = table.Column(_fetchAmount);
        var full = table.Column(_fetchFull);
        for (var row = 0; row < table.Count; row++)
            read.Fetchers.Add(new FetchJob(table.Entities[row], (uint)from[row], (uint)to[row], Cargo.Of(good2[row]), amount[row], full[row] != 0));
        read.Fetchers.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        table = ctx.Table(_first + BatchT);
        var batchTo = table.Column(_batchTo);
        var batchResource = table.Column(_batchResource);
        var batchAmount = table.Column(_batchAmount);
        var arrive = table.Column(_batchArrive);
        var order = new List<(uint Entity, uint To, int Good, double Amount)>();
        for (var row = 0; row < table.Count; row++)
            if (arrive[row] > ctx.Tick)
                order.Add((table.Entities[row], (uint)batchTo[row], Cargo.Of(batchResource[row]), batchAmount[row]));
        order.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        read.Batches.AddRange(order.Select(batch => (batch.To, batch.Good, batch.Amount)));
        return read;
    }

    /// <summary>Столбцы грузов таблицы — по сущности.</summary>
    private static void FillGoods(TableView table, Column[] columns, Dictionary<uint, double[]> into)
    {
        for (var row = 0; row < table.Count; row++)
        {
            var values = new double[columns.Length];
            for (var good = 0; good < columns.Length; good++)
                values[good] = table.Column(columns[good])[row];
            into[table.Entities[row]] = values;
        }
    }
}
