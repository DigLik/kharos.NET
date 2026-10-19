using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Строители сами возят материалы (fetching.ts). Стройка, которой не хватает материалов, встаёт, когда построено всё,
/// что позволяет привезённое; если недостающее никто не везёт, её строитель едет к ближайшему своему зданию, которое
/// отдаёт нужное, берёт его своим лучом, сколько влезет в кузов, и сгружает на площадку. Поручение (Fetch) живёт, пока
/// строитель на этой стройке; остаток в кузове свободного строителя едет в ближайшее хранилище. Что строитель везёт,
/// учитывают заявки (<see cref="Flows"/>).
/// </summary>
internal static class Fetching
{
    /// <summary>Дальше этого от стройки строитель за материалом не едет (FETCH_REACH).</summary>
    public const double FetchReach = 40;

    /// <summary>Меньше этого строитель не возит (MIN_FETCH).</summary>
    public const double MinFetch = 0.5;

    /// <summary>Раз во сколько тиков строитель ищет, где взять материал, и пробует подъехать снова (RETRY_TICKS).</summary>
    public const int RetryTicks = 20;

    /// <summary>После скольких промахов подъезда подряд строитель бросает поручение (GIVE_UP).</summary>
    public const int GiveUp = 3;

    private const double Epsilon = Cargo.Epsilon;

    /// <summary>Поручение строителя на время вызова.</summary>
    private sealed class Job
    {
        public uint From, To;
        public int Resource;
        public double Amount, Misses;
        public bool Full;
    }

    /// <summary>Чего стройке не хватает сверх того, что к ней уже едет (shortOf).</summary>
    private static List<(int Good, double Need)> ShortOf(View view, uint site, Flows flows)
    {
        var list = new List<(int, double)>();
        foreach (var (good, lack) in Demand.MissingFor(view, site))
        {
            var left = lack - flows.In(site, good);
            if (left >= MinFetch)
                list.Add((good, left));
        }
        return list;
    }

    /// <summary>
    /// Где строителю взять материал для стройки (fetchSource): ближайшее к нему своё готовое здание со складом, которое
    /// отдаёт недостающее, не дальше FETCH_REACH от стройки; хранилища — сверх резерва. Нет — null.
    /// </summary>
    public static (uint From, int Good, double Amount)? Source(Pass pass, uint builder, uint site, Flows flows)
    {
        var view = pass.V;
        var player = view.Owner(builder);
        if (player == 0 || !view.HasStock(builder) || !view.IsSite(site))
            return null;
        var shortage = ShortOf(view, site, flows);
        if (shortage.Count == 0)
            return null;
        var (tx, ty) = view.Center(site);
        var (bx, by) = view.At(builder);
        (uint, int, double)? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var entity in pass.Buildings)
        {
            if (entity == site || view.IsSite(entity) || !view.IsOwn(player, entity))
                continue;
            var (ax, ay) = view.Center(entity);
            if (DetMath.Hypot(ax - tx, ay - ty) > FetchReach)
                continue;
            var distance = DetMath.Hypot(ax - bx, ay - by);
            if (distance >= bestDistance)
                continue;
            var offers = Demand.Offers(pass, entity);
            foreach (var (good, need) in shortage)
            {
                if (!offers.Contains(good))
                    continue;
                var spare = Demand.GivenBy(view, entity, good) - flows.Out(entity, good);
                var amount = Math.Min(Math.Min(need, spare), view.Room(builder, good));
                if (amount < MinFetch)
                    continue;
                best = (entity, good, amount);
                bestDistance = distance;
                break;
            }
        }
        return best;
    }

    /// <summary>Ближайшее своё готовое хранилище, куда поместится груз: туда свободный строитель отвозит остаток.</summary>
    private static uint StoreFor(Pass pass, uint builder, int good)
    {
        var view = pass.V;
        var player = view.Owner(builder);
        var (bx, by) = view.At(builder);
        uint best = 0;
        var bestDistance = double.PositiveInfinity;
        foreach (var entity in pass.Buildings)
        {
            if (!view.IsOwn(player, entity) || view.IsSite(entity) || !view.IsStore(entity) || view.Room(entity, good) < MinFetch)
                continue;
            var (ax, ay) = view.Center(entity);
            var distance = DetMath.Hypot(ax - bx, ay - by);
            if (distance < bestDistance)
                (best, bestDistance) = (entity, distance);
        }
        return best;
    }

    /// <summary>Может ли юнит возить материалы: ремонтник с кузовом и лучом (canFetch).</summary>
    public static bool CanFetch(Call call, uint unit) => call.Has(unit, S.Repair) && call.Has(unit, S.Stock) && call.Has(unit, S.Beam) && call.Has(unit, S.Unit);

    /// <summary>Будет ли строитель возить материал на свою стройку (willFetch): так считает и стройка (Hooks.WillFetch).</summary>
    public static bool WillFetch(Call call, uint builder, uint site)
    {
        if (!CanFetch(call, builder))
            return false;
        var pass = Pass.Of(call);
        return Source(pass, builder, site, Flows.Of(pass)) is not null;
    }

    /// <summary>
    /// Раз в тик, после грузовиков (fetchMaterials): строители ведут свои поручения, свободные от них — берут новые.
    /// builders — строители без Converting; builds — их стройки (Builds.site); moving — кто едет.
    /// </summary>
    public static void Run(Pass pass, IReadOnlyList<uint> builders, IReadOnlyDictionary<uint, uint> builds, ISet<uint> moving)
    {
        var view = pass.V;
        var call = pass.Call;
        var tick = pass.Tick;
        var jobs = new Dictionary<uint, Job>();
        foreach (var fetch in pass.Fetchers)
            jobs[fetch.Entity] = new Job { From = fetch.From, To = fetch.To, Resource = fetch.Resource, Amount = fetch.Amount, Full = fetch.Full, Misses = call.Num(fetch.Entity, S.Fetch, L.FetchMisses) };
        Flows? flows = null;
        foreach (var builder in builders)
        {
            if (jobs.TryGetValue(builder, out var job))
            {
                var before = (job.From, job.To, job.Resource, job.Amount, job.Full, job.Misses);
                var kept = Step(pass, builder, job, builds, moving);
                if (!kept)
                {
                    call.Commands.Remove(builder, S.Fetch);
                    jobs.Remove(builder);
                    Sync(pass, jobs);
                }
                else if (before != (job.From, job.To, job.Resource, job.Amount, job.Full, job.Misses))
                {
                    call.Set(builder, S.Fetch).Flag(L.FetchFull, job.Full).Number(L.FetchMisses, job.Misses);
                    Sync(pass, jobs);
                }
                continue;
            }
            var cargo = view.Carried(builder);
            var site = builds.GetValueOrDefault(builder);
            if (site != 0 && view.IsSite(site))
            {
                if (cargo >= 0)
                {
                    if (Demand.MissingFor(view, site).Exists(item => item.Good == cargo && item.Lack > Epsilon))
                        Add(pass, jobs, builder, new Job { To = site, Resource = cargo, Amount = view.Amount(builder, cargo), Full = true });
                    continue;
                }
                if (!Core.OnTurn(tick, builder, RetryTicks) || !Sites.AwaitsMaterials(call, site))
                    continue;
                var source = Source(pass, builder, site, flows ??= Flows.Of(pass));
                if (source is not { } found)
                    continue;
                Add(pass, jobs, builder, new Job { From = found.From, To = site, Resource = found.Good, Amount = found.Amount });
                if (moving.Contains(builder))
                    call.Commands.Remove(builder, S.Path);
                // Следующему строителю в этот тик — с учётом того, что повезёт этот.
                flows = null;
                continue;
            }
            if (cargo < 0 || builds.ContainsKey(builder) || moving.Contains(builder) || !Core.OnTurn(tick, builder, RetryTicks))
                continue;
            var store = StoreFor(pass, builder, cargo);
            if (store != 0)
                Add(pass, jobs, builder, new Job { To = store, Resource = cargo, Amount = view.Amount(builder, cargo), Full = true });
        }
    }

    /// <summary>Заводит поручение (компонент Fetch) и учитывает его в потоках этого вызова.</summary>
    private static void Add(Pass pass, Dictionary<uint, Job> jobs, uint builder, Job job)
    {
        jobs[builder] = job;
        pass.Call.Add(builder, S.Fetch).Number(L.FetchFrom, job.From).Number(L.FetchTo, job.To).Number(L.FetchResource, Cargo.Proto(job.Resource))
            .Number(L.FetchAmount, job.Amount).Flag(L.FetchFull, job.Full).Number(L.FetchMisses, 0);
        Sync(pass, jobs);
    }

    /// <summary>Потоки вызова — с нынешними поручениями (поменялись за вызов).</summary>
    private static void Sync(Pass pass, Dictionary<uint, Job> jobs) =>
        pass.Refetch([.. jobs.Select(pair => new FetchJob(pair.Key, pair.Value.From, pair.Value.To, pair.Value.Resource, pair.Value.Amount, pair.Value.Full))]);

    /// <summary>Подводит строителя к складу на длину луча; не вышло GIVE_UP раз подряд — поручение бросается (goTo). false — бросить.</summary>
    private static bool GoTo(Pass pass, uint builder, Job job, uint target, ISet<uint> moving)
    {
        var view = pass.V;
        if (moving.Contains(builder))
            return true;
        if (job.Misses > 0 && !Core.OnTurn(pass.Tick, builder, RetryTicks))
            return true;
        var radius = view.Beam(builder)?.Radius ?? 0;
        job.Misses = Beams.Seek(view, builder, target, radius) == Beams.Approach.Found ? 0 : job.Misses + 1;
        return job.Misses < GiveUp;
    }

    /// <summary>Один тик поручения: набрать у From, довезти и сгрузить на To (run). false — поручение кончено.</summary>
    private static bool Step(Pass pass, uint builder, Job job, IReadOnlyDictionary<uint, uint> builds, ISet<uint> moving)
    {
        var view = pass.V;
        var to = job.To;
        var toSite = view.IsSite(to);
        // На стройку везут, пока строитель на ней; в хранилище — пока ему не дали работу.
        var site = builds.GetValueOrDefault(builder);
        if (!view.Alive(to) || !view.HasStock(to) || (toSite ? site != to : builds.ContainsKey(builder)))
            return false;
        var have = view.Amount(builder, job.Resource);
        if (!job.Full)
        {
            var from = job.From;
            var want = job.Amount - have;
            var gone = from == 0 || !view.Alive(from) || !view.HasStock(from) || view.IsSite(from);
            if (!gone && want > Epsilon)
            {
                if (!Beams.Reaches(view, from, builder))
                    return GoTo(pass, builder, job, from, moving);
                var moved = Beams.Transfer(pass, from, builder, job.Resource, Math.Min(want, Demand.GivenBy(view, from, job.Resource)));
                if (moved > Epsilon)
                {
                    if (moving.Contains(builder))
                        pass.Call.Commands.Remove(builder, S.Path);
                    return true;
                }
            }
            // Набрал, сколько хотел, или больше не дают: едет с тем, что есть.
            if (have <= Epsilon)
                return false;
            job.Full = true;
        }
        if (have <= Epsilon)
            return false;
        if (!Beams.Reaches(view, builder, to))
            return GoTo(pass, builder, job, to, moving);
        var need = toSite ? Demand.MissingFor(view, to).Find(item => item.Good == job.Resource).Lack : double.PositiveInfinity;
        // Сгрузил всё, стройке больше не нужно или класть некуда — поручение кончено.
        return !(need <= Epsilon || Beams.Transfer(pass, builder, to, job.Resource, need) <= Epsilon || view.Amount(builder, job.Resource) <= Epsilon);
    }
}
