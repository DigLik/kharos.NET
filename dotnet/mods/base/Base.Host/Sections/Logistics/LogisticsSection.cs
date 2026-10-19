using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Раздел Logistics (С2b). Логистика: грузовики (<see cref="Hauling"/>), заявки зон и диспетчер (<see cref="Demand"/>,
/// <see cref="Dispatch"/>), маршруты, назначение и фильтр (<see cref="Routes"/>), ручной подвоз (<see cref="Carrying"/>),
/// транспортный луч (<see cref="Beams"/>), подвоз строителями (<see cref="Fetching"/>). Системы haul и fetchMaterials —
/// места в тике по <see cref="Order.Tick"/>; приказы haul, pickup, supply, route, serve, filter, carry, reserve, want
/// (<see cref="LogisticsOrders"/>); запросы logistics.*. Пустые дропы исчезают в конце haul, как в TS.
/// <para>
/// Доступ к чужому (Н5-3): главный запрос — свои сущности (грузовики, строители), всё о складах мира — побочными
/// таблицами (<see cref="Frame"/>, снимок без вызовов инструментов), свои записи — поверх снимка (<see cref="View"/>);
/// зоны — кэш по ревизии (<see cref="Construction.Zones"/>); подъезд — инструмент Stand с нормой поиска на тик.
/// </para>
/// </summary>
internal static class LogisticsSection
{
    public static void Setup(ModBuilder mod)
    {
        LogisticsOrders.Setup();
        // Стройка не держит строителя, который сам повезёт материал (volunteer в construction.ts).
        Construction.Hooks.WillFetch = Fetching.WillFetch;
        Frame.Declare(Order.System(mod, "haul", new Haul())
            .Query(Names.Hauler.Name, Names.Owner.Name, Names.Stock.Name, Names.Position.Name));
        Frame.Declare(Order.System(mod, "fetchMaterials", new Fetch())
            .Query(Names.Unit.Name, Names.Repair.Name, Names.Stock.Name, Names.Beam.Name).Without(Names.Converting.Name)
            .Table(Names.Builds.Name, Names.Repair.Name)
            .Table(Names.Path.Name, Names.Repair.Name));
        Queries(mod);
    }

    /// <summary>Система haul: грузовики, маршруты, ручные приказы, пустые дропы, диспетчер, подъезд.</summary>
    private sealed class Haul : ISystem
    {
        private Frame _frame = null!;

        public void Bind(SystemBinding binding)
        {
            L.Load(binding.Component);
            _frame = new Frame(0);
            _frame.Bind(binding);
        }

        public void Run(SystemContext ctx)
        {
            var call = Call.Of(ctx);
            var read = _frame.Take(ctx);
            var view = new View(call, read.Snapshot);
            var entities = ctx.Entities.ToArray();
            Array.Sort(entities);
            var routes = Tools.BytesMany(entities, S.Hauler, L.HaulerRoute);
            var serves = Tools.BytesMany(entities, S.Hauler, L.HaulerServe);
            var trucks = new List<Truck>(entities.Length);
            for (var i = 0; i < entities.Length; i++)
                trucks.Add(Truck.Read(view, entities[i], routes[i] ?? [], serves[i] ?? [], read.Moving.Contains(entities[i]), read.Carrying.Contains(entities[i])));
            var pass = new Pass(view, trucks, read.Fetchers, read.Batches, read.Holders);
            Hauling.Run(pass, read.Marked);
            foreach (var truck in pass.Trucks)
                truck.Save(call);
            Beams.Reset(pass, read.Beams, Tools.BytesMany(read.Beams, S.Beam, L.BeamLinks));
        }
    }

    /// <summary>Система fetchMaterials: строители возят материалы на свои стройки.</summary>
    private sealed class Fetch : ISystem
    {
        private Frame _frame = null!;
        private Column _site;

        public void Bind(SystemBinding binding)
        {
            L.Load(binding.Component);
            // Свои таблицы — первыми: Builds и Path строителей, за ними снимок.
            _site = binding.Table(0).Column(Names.Builds.Name, Names.Builds.Site);
            _frame = new Frame(2);
            _frame.Bind(binding);
        }

        public void Run(SystemContext ctx)
        {
            if (ctx.Count == 0)
                return;
            var call = Call.Of(ctx);
            var builds = new Dictionary<uint, uint>();
            var table = ctx.Table(0);
            var sites = table.Column(_site);
            for (var row = 0; row < table.Count; row++)
                builds[table.Entities[row]] = (uint)sites[row];
            var moving = new HashSet<uint>(ctx.Table(1).Entities.ToArray());
            var read = _frame.Take(ctx);
            var builders = ctx.Entities.ToArray();
            Array.Sort(builders);
            var pass = new Pass(new View(call, read.Snapshot), null, read.Fetchers, read.Batches, read.Holders);
            Fetching.Run(pass, builders, builds, moving);
            Beams.Append(pass);
        }
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(LogisticsQueries.Incoming, ctx =>
        {
            var args = Args<GoodArgs>(ctx);
            var pass = Pass.Of(Call.Of(ctx));
            ctx.Reply(Flows.Of(pass).In(args.Entity, Cargo.Index(args.Good)));
        });
        mod.Query(LogisticsQueries.Unreachable, ctx => ctx.Reply(new View(Call.Of(ctx)).Unreachable(Args<EntityArgs>(ctx).Entity)));
        mod.Query(LogisticsQueries.SpaceFor, ctx =>
        {
            var args = Args<TruckArgs>(ctx);
            var pass = Pass.Of(Call.Of(ctx));
            ctx.Reply(Demand.SpaceFor(pass.V, Flows.Of(pass), args.Truck, args.To, Cargo.Index(args.Good)));
        });
        mod.Query(LogisticsQueries.RefineryFor, ctx =>
        {
            var args = Args<TruckArgs>(ctx);
            var pass = Pass.Of(Call.Of(ctx));
            ctx.Reply(Demand.RefineryFor(pass, Flows.Of(pass), args.Truck, Cargo.Index(args.Good)));
        });
        mod.Query(LogisticsQueries.CanSupply, ctx =>
        {
            var args = Args<TargetArgs>(ctx);
            ctx.Reply(Hauling.CanSupply(Pass.Of(Call.Of(ctx)), args.Player, args.Target));
        });
        mod.Query(LogisticsQueries.Requests, ctx =>
        {
            var pass = Pass.Of(Call.Of(ctx));
            var requests = Demand.RequestsOf(pass, Args<PlayerArgs>(ctx).Player, Flows.Of(pass));
            ctx.Reply(new RequestList
            {
                Items = [.. requests.Select(request => new RequestInfo { To = request.To, Good = Cargo.Name(request.Good), Amount = request.Amount, Priority = request.Priority })],
            });
        });
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
