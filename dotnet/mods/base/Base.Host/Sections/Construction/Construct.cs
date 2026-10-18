using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Rules;
using Base.Shared.Signals;
using Kharos.Mod.Sdk;
using MemoryPack;
using static Base.Host.Sections.Construction.Refs;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Раз в тик (construct): ремонтники (Repair) вкладывают работу во всё своё, до чего дотягиваются, — чем их больше,
/// тем быстрее. Стройку строят, здание под разбор разбирают, повреждённое — чинят, заказанное улучшение — поднимают,
/// покрытие кладут и снимают, горы срывают. Строитель сам подъезжает к своей работе (Builds); свободные сами берутся
/// за работу рядом. Кадр — ремонтники (Repair, Position, Owner); цели — побочными таблицами.
/// </summary>
internal sealed class Construct : ISystem
{
    /// <summary>На сколько тайлов вглубь основания приходится точка работы (WORK_INSET).</summary>
    private const double WorkInset = 0.4;

    /// <summary>Насколько точно юнит-ремонтник должен смотреть на цель, радианы (REPAIR_AIM).</summary>
    private const double RepairAim = 0.2;

    /// <summary>Побочные таблицы по порядку объявления.</summary>
    public const int SiteTable = 0, PaveTable = 1, DigTable = 2, HealthTable = 3, UpgradeTable = 4;

    private Column _target;
    private Column _healthValue, _healthMax;

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _target = binding.Column(S.Repair.Name, R.RepairTarget.Name);
        _healthValue = binding.Table(HealthTable).Column(S.Health.Name, S.HealthValue.Name);
        _healthMax = binding.Table(HealthTable).Column(S.Health.Name, S.HealthMax.Name);
    }

    /// <summary>Ремонтник: кто, чей, где, досягаемость, работа за тик, к чему послан, носитель, станция ли.</summary>
    private sealed record Repairer(uint Entity, uint Player, double X, double Y, double Radius, double Rate, uint Site, uint Carrier, bool Station);

    /// <summary>Цель работы.</summary>
    private sealed class Target(uint entity, Work work, bool upgrade)
    {
        public uint Entity { get; } = entity;
        public Work Work { get; } = work;
        public bool Upgrade { get; } = upgrade;
        public uint Player { get; set; }
        public bool Demolish { get; set; }
        public bool Ordered { get; set; }
    }

    /// <summary>Связь «ремонтник — цель» (RepairLink).</summary>
    private readonly record struct Link(uint From, uint To, double Rate, double FromX, double FromY, double ToX, double ToY);

    public void Run(SystemContext ctx)
    {
        var call = Call.Of(ctx);
        var entities = ctx.Entities.ToArray();
        var free = new List<uint>();
        var late = new List<uint>();
        foreach (var entity in entities)
        {
            if (!call.Has(entity, S.Unit) || !call.Has(entity, S.Builds))
                continue;
            var site = (uint)call.Num(entity, S.Builds, R.BuildsSite);
            if (Sites.WorkAt(call, site) is not { } work)
            {
                free.Add(entity);
                continue;
            }
            // Едущий за материалом к стройке не рвётся: вернётся с грузом сам.
            if (call.Has(entity, S.Path) || call.Has(entity, S.Fetch))
                continue;
            var (x, y) = call.At(entity);
            if (work.Distance(x, y) > call.Num(entity, S.Repair, S.RepairRadius))
            {
                // Не доехал или оттеснили: снова — не каждый тик и всё реже, если не подъехать.
                var misses = (int)call.Num(entity, S.Builds, R.BuildsMisses);
                if (Core.OnTurn(ctx.Tick, entity, BuildRules.RetryTicks << Math.Min(misses, BuildRules.MaxBackoff)))
                    late.Add(entity);
            }
        }
        foreach (var entity in free)
            call.Remove(entity, S.Builds);
        foreach (var builder in late)
        {
            var site = (uint)call.Num(builder, S.Builds, R.BuildsSite);
            var misses = Sites.Approach(call, builder, site) ? 0 : call.Num(builder, S.Builds, R.BuildsMisses) + 1;
            call.Set(builder, S.Builds).Number(R.BuildsMisses, misses);
        }
        Volunteer(call, ctx, entities);

        var targets = ctx.Column(_target);
        targets.Clear();
        var links = Links(call, ctx, entities);
        var done = new List<(uint Entity, double Count)>();
        var index = new Dictionary<uint, int>();
        foreach (var link in links)
        {
            targets[ctx.RowOf(link.From)] = link.To;
            // За работой юнит или турель поворачивается к ней.
            var wanted = DetMath.Atan2(link.ToY - link.FromY, link.ToX - link.FromX);
            if (Turner(call, link.From) is { } turner)
                Turn(call, link.From, turner, TurnToward(turner.Facing, wanted, turner.Turn * Core.Step));
            if (!Aimed(call, link))
                continue;
            if (index.TryGetValue(link.To, out var at))
                done[at] = (link.To, done[at].Count + link.Rate);
            else
            {
                index[link.To] = done.Count;
                done.Add((link.To, link.Rate));
            }
        }
        foreach (var (entity, count) in done)
            Apply(call, ctx, entity, count);
    }

    // ===== Добровольцы =====

    /// <summary>
    /// Свободные строители сами берутся за работу поблизости (volunteer): ближайшая своя стройка, разбор, покрытие,
    /// землеройная работа, улучшение или повреждённое не дальше WORK_RADIUS. Стройку вне зоны не берут; перегруженную
    /// электростанцию сами не чинят. Проверяют не каждый тик, а раз в RETRY_TICKS, каждый в свой.
    /// </summary>
    private void Volunteer(Call call, SystemContext ctx, uint[] entities)
    {
        var idle = new List<uint>();
        foreach (var entity in entities)
        {
            if (!call.Has(entity, S.Unit) || !Core.OnTurn(ctx.Tick, entity, BuildRules.RetryTicks) || call.Has(entity, S.Converting))
                continue;
            if (call.Has(entity, S.Fetch))
                continue;
            if (call.Has(entity, S.Builds))
            {
                // Стройка, которая ждёт материалов, строителя не держит, если он не везёт материал сам и взять его негде.
                var site = (uint)call.Num(entity, S.Builds, R.BuildsSite);
                if (!Sites.AwaitsMaterials(call, site) || (Hooks.WillFetch?.Invoke(call, entity, site) ?? false))
                    continue;
            }
            else if (call.Has(entity, S.Path))
                continue;
            idle.Add(entity);
        }
        if (idle.Count == 0)
            return;

        // type — вид здания, которое строят: такой работе нужна зона. Разбору и починке она не нужна.
        var sites = new List<(uint Entity, Work Work, uint Player, string? Type)>();
        foreach (var entity in Table(ctx, SiteTable))
        {
            if (Sites.AwaitsMaterials(call, entity) || Sites.WorkAt(call, entity) is not { } work)
                continue;
            var demolish = call.Flag(entity, S.Site, R.SiteDemolish);
            sites.Add((entity, work, call.OwnerOf(entity), demolish ? null : call.BuildingType(entity)));
        }
        foreach (var entity in Unfinished(call, ctx))
            if (Sites.WorkAt(call, entity) is { } work)
                sites.Add((entity, work, call.OwnerOf(entity), null));
        foreach (var entity in Table(ctx, DigTable))
            if (Sites.WorkAt(call, entity) is { } work)
                sites.Add((entity, work, call.OwnerOf(entity), null));
        foreach (var entity in Table(ctx, UpgradeTable))
            if (Sites.UpgradeAt(call, entity) is not null && Sites.WorkAt(call, entity) is { } work)
                sites.Add((entity, work, call.OwnerOf(entity), null));
        IReadOnlySet<uint>? overbuilt = null;
        foreach (var entity in DamagedOf(ctx))
        {
            // За едущим не гоняются, к тому, по кому бьют, не едут.
            if (!Sites.IsRepairable(call, entity) || call.Has(entity, S.Path) || Sites.UnderFire(call, entity))
                continue;
            if (call.Has(entity, S.Building) && (overbuilt ??= Overbuilt(call)).Contains(entity))
                continue;
            if (Sites.WorkAt(call, entity) is { } work)
                sites.Add((entity, work, call.OwnerOf(entity), null));
        }
        var open = new Dictionary<uint, bool>();
        foreach (var builder in idle)
        {
            var (x, y) = call.At(builder);
            var player = call.OwnerOf(builder);
            uint best = 0;
            var bestDistance = BuildRules.WorkRadius;
            foreach (var site in sites)
            {
                if (site.Player != player || site.Entity == builder)
                    continue;
                var distance = site.Work.Distance(x, y);
                if (distance > bestDistance)
                    continue;
                if (!open.TryGetValue(site.Entity, out var workable))
                    open[site.Entity] = workable = site.Type is null || !Sites.OutOfControl(player, site.Type, site.Work.X, site.Work.Y);
                if (!workable)
                    continue;
                best = site.Entity;
                bestDistance = distance;
            }
            if (best != 0)
                Sites.AssignBuilders(call, player, best, [builder], false);
        }
    }

    // ===== Связи =====

    /// <summary>
    /// Над чем сейчас работает каждый ремонтник (repairLinks): та работа, к которой послан, если дотягивается, а иначе —
    /// ближайшая из тех, до которых дотягивается и которые не стоят.
    /// </summary>
    private List<Link> Links(Call call, SystemContext ctx, uint[] entities)
    {
        var links = new List<Link>();
        var repairers = new List<Repairer>();
        foreach (var entity in entities)
        {
            // На ходу, разворачиваясь и недостроенным не работают; турель — работает и на ходу носителя.
            var carrier = CarrierOf(call, entity);
            if (call.Has(entity, S.Path) || call.Has(carrier, S.Converting) || call.Has(entity, S.Site) || call.Has(entity, S.Building))
                continue;
            var (x, y) = call.At(entity);
            var site = call.Has(entity, S.Builds) ? (uint)call.Num(entity, S.Builds, R.BuildsSite) : 0;
            repairers.Add(new Repairer(entity, call.OwnerOf(entity), x, y, call.Num(entity, S.Repair, S.RepairRadius), call.Num(entity, S.Repair, R.RepairRate, 1), site, carrier, false));
        }
        // Ремонтные станции: готовые и с энергией; центр — середина здания.
        foreach (var entity in entities)
        {
            if (!call.Has(entity, S.Building) || call.Has(entity, S.Site) || Sites.Base(call, entity) is not { } at)
                continue;
            var spec = Sites.Spec(at.Type);
            var share = (spec.Power ?? 0) < 0 ? Hooks.PowerShare?.Invoke(call, entity) ?? (call.Has(entity, R.Off) ? 0 : 1) : 1;
            if (share <= 0)
                continue;
            repairers.Add(new Repairer(entity, call.OwnerOf(entity), at.X + spec.Width / 2, at.Y + spec.Height / 2, call.Num(entity, S.Repair, S.RepairRadius),
                call.Num(entity, S.Repair, R.RepairRate, 1) * share, 0, entity, true));
        }
        if (repairers.Count == 0)
            return links;

        var targets = new List<Target>();
        foreach (var entity in Table(ctx, SiteTable))
            if (Sites.WorkAt(call, entity) is { } work)
                targets.Add(new Target(entity, work, false));
        foreach (var entity in Unfinished(call, ctx))
            if (Sites.WorkAt(call, entity) is { } work)
                targets.Add(new Target(entity, work, false));
        foreach (var entity in Table(ctx, DigTable))
            if (Sites.WorkAt(call, entity) is { } work)
                targets.Add(new Target(entity, work, false));
        foreach (var entity in DamagedOf(ctx))
            // По кому сейчас бьют, не чинят: посланный постоит рядом и возьмётся, когда бой стихнет.
            if (Sites.IsRepairable(call, entity) && !Sites.UnderFire(call, entity) && Sites.WorkAt(call, entity) is { } work)
                targets.Add(new Target(entity, work, false));
        foreach (var entity in Table(ctx, UpgradeTable))
            if (Sites.UpgradeAt(call, entity) is not null && Sites.WorkAt(call, entity) is { } work)
                targets.Add(new Target(entity, work, true));
        IReadOnlySet<uint>? overbuilt = null;
        foreach (var target in targets)
        {
            var site = call.Has(target.Entity, S.Site);
            target.Player = call.OwnerOf(target.Entity);
            target.Demolish = (site && call.Flag(target.Entity, S.Site, R.SiteDemolish)) || (call.Has(target.Entity, S.Pave) && call.Flag(target.Entity, S.Pave, S.PaveRemove));
            // Электростанцию, которой не хватило бы и целой, чинят только посланные к ней.
            target.Ordered = !site && !target.Upgrade && call.Has(target.Entity, S.Building) && (overbuilt ??= Overbuilt(call)).Contains(target.Entity);
        }
        var stalled = new Dictionary<uint, bool>();
        bool Stands(uint entity)
        {
            if (!stalled.TryGetValue(entity, out var value))
                stalled[entity] = value = IsStalled(call, entity);
            return value;
        }

        foreach (var repairer in repairers)
        {
            Target? best = null;
            var bestDistance = double.PositiveInfinity;
            foreach (var target in targets)
            {
                // Себя и свой носитель ремонтник не чинит.
                if (target.Player != repairer.Player || target.Entity == repairer.Carrier)
                    continue;
                // Станция только чинит: стройки, покрытия, разбор и улучшения — работа строителя.
                if (repairer.Station && (target.Upgrade || call.Has(target.Entity, S.Site) || call.Has(target.Entity, S.Pave) || call.Has(target.Entity, R.Dig)))
                    continue;
                if (target.Ordered && repairer.Site != target.Entity)
                    continue;
                var distance = target.Work.Distance(repairer.X, repairer.Y);
                if (distance > repairer.Radius)
                    continue;
                // Посланный к работе занят ею, остальное подождёт.
                if (repairer.Site == target.Entity)
                {
                    best = target;
                    break;
                }
                if (distance < bestDistance && !Stands(target.Entity))
                {
                    best = target;
                    bestDistance = distance;
                }
            }
            if (best is null)
                continue;
            var work = best.Work;
            var insetX = Math.Min(WorkInset, work.W / 2);
            var insetY = Math.Min(WorkInset, work.H / 2);
            links.Add(new Link(repairer.Entity, best.Entity, repairer.Rate, repairer.X, repairer.Y,
                Math.Min(work.X + work.W - insetX, Math.Max(work.X + insetX, repairer.X)),
                Math.Min(work.Y + work.H - insetY, Math.Max(work.Y + insetY, repairer.Y))));
        }
        return links;
    }

    /// <summary>
    /// Стоит ли работа, хотя до неё дотянулись (isStalled): стройка вне зоны, с юнитами на пустой площадке или без
    /// материалов; починка — без кредитов.
    /// </summary>
    private static bool IsStalled(Call call, uint entity)
    {
        // Покрытие, землеройная работа и улучшение оплачены сразу и ничего не ждут.
        if (call.Has(entity, S.Pave) || call.Has(entity, R.Dig) || Sites.UpgradeAt(call, entity) is not null)
            return false;
        // Бесплатная починка не стоит и без кредитов.
        if (!call.Has(entity, S.Site))
            return Core.Rules.RepairCost > 0 && Accounts.Credits(call, call.OwnerOf(entity)) <= 0;
        if (call.Flag(entity, S.Site, R.SiteDemolish) || Sites.Base(call, entity) is not { } at)
            return false;
        if (Sites.OutOfControl(call.OwnerOf(entity), at.Type, at.X, at.Y))
            return true;
        return Sites.IsSiteBlocked(call, entity) || Sites.AwaitsMaterials(call, entity);
    }

    // ===== Поворот =====

    /// <summary>То, что поворачивается само (turnerOf): юнит или турель — куда смотрит, скорость поворота; иначе — null.</summary>
    private readonly record struct Turning(double Facing, double Turn, double Hull, bool Turret);

    private static Turning? Turner(Call call, uint entity)
    {
        if (call.Has(entity, S.Unit))
            return Factory.Unit(call.UnitType(entity)) is { } spec ? new Turning(call.Num(entity, S.Facing, S.FacingAngle), spec.Turn, 0, false) : null;
        if (!call.Has(entity, S.Mount))
            return null;
        var turret = Core.Tables.Turrets.GetValueOrDefault(Core.ProtoName((int)call.Num(entity, S.Proto, S.ProtoId, -1), "turrets") ?? "");
        if (turret is null)
            return null;
        // Турель хранит поворот относительно носителя, а наводят её в мировых углах.
        var carrier = (uint)call.Num(entity, S.Mount, S.MountParent);
        var hull = call.Has(carrier, S.Unit) ? call.Num(carrier, S.Facing, S.FacingAngle) : 0;
        return new Turning(Wrap(hull + call.Num(entity, S.Mount, S.MountAngle)), turret.Turn, hull, true);
    }

    private static void Turn(Call call, uint entity, Turning turner, double facing)
    {
        if (facing == turner.Facing)
            return;
        if (turner.Turret)
            call.Set(entity, S.Mount).Number(S.MountAngle, Wrap(facing - turner.Hull));
        else
            call.Set(entity, S.Facing).Number(S.FacingAngle, facing);
    }

    /// <summary>Смотрит ли ремонтник на то, над чем работает (isAimed); здание смотрит во все стороны.</summary>
    private static bool Aimed(Call call, Link link)
    {
        if (Turner(call, link.From) is not { } turner)
            return true;
        return Math.Abs(Wrap(DetMath.Atan2(link.ToY - link.FromY, link.ToX - link.FromX) - turner.Facing)) <= RepairAim;
    }

    /// <summary>Угол к промежутку от −π до π (wrap).</summary>
    private static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor(angle / (Math.PI * 2) + 0.5);

    /// <summary>Куда будет смотреть поворачивающийся от facing к wanted не больше чем на maxTurn (turnToward).</summary>
    private static double TurnToward(double facing, double wanted, double maxTurn)
    {
        var off = Wrap(wanted - facing);
        return Math.Abs(off) <= maxTurn ? wanted : Wrap(facing + Math.Sign(off) * maxTurn);
    }

    /// <summary>Носитель турели или она сама (carrierOf).</summary>
    private static uint CarrierOf(Call call, uint entity) => call.Has(entity, S.Mount) && call.Num(entity, S.Mount, S.MountParent) is var parent and > 0 ? (uint)parent : entity;

    // ===== Работа =====

    /// <summary>Работа за тик над сущностью: count — сколько её вложили.</summary>
    private static void Apply(Call call, SystemContext ctx, uint entity, double count)
    {
        if (!call.Alive(entity))
            return;
        if (call.Has(entity, R.Dig))
        {
            var work = call.Num(entity, R.Dig, R.DigWork) + count;
            call.Set(entity, R.Dig).Number(R.DigWork, work);
            if (work >= Sites.WorkTicks(call.Num(entity, R.Dig, R.DigCost)))
                Earthworks.Finish(call, entity);
            return;
        }
        if (call.Has(entity, S.Pave))
        {
            var (x, y) = call.At(entity);
            var kind = call.Text(entity, S.Pave, S.PaveKind) ?? PaveRules.Road;
            if (call.Flag(entity, S.Pave, S.PaveRemove))
            {
                var left = call.Num(entity, S.Pave, R.PaveWork) - count * BuildRules.DemolishSpeed;
                call.Set(entity, S.Pave).Number(R.PaveWork, left);
                if (left > 0)
                    return;
                // Разобрали: покрытие уходит с тайла вместе с работой.
                Paving.Lay(call, (int)x, (int)y, PaveRules.None);
                call.Destroy(entity);
                return;
            }
            var work = call.Num(entity, S.Pave, R.PaveWork) + count;
            call.Set(entity, S.Pave).Number(R.PaveWork, work);
            if (work < Sites.WorkTicks(Paving.Cost(kind, (int)x, (int)y)))
                return;
            // Достроенное покрытие уходит в слой: работа исчезает, тайл — готовое покрытие хозяина (settlePave).
            Paving.Lay(call, (int)x, (int)y, PaveRules.Code(kind, call.OwnerOf(entity)));
            call.Destroy(entity);
            return;
        }
        var site = call.Has(entity, S.Site);
        if (!site && Sites.UpgradeAt(call, entity) is { } raised)
        {
            var progress = raised.Progress + count;
            if (progress < raised.Ticks)
            {
                call.Set(entity, R.Upgrade).Number(R.UpgradeProgress, progress);
                return;
            }
            call.Remove(entity, R.Upgrade);
            call.Set(entity, S.Building).Number(S.BuildingLevel, raised.Level + 1);
            Sites.ApplyLevel(call, entity, raised.Level + 1);
            return;
        }
        if (!site)
        {
            Repair(call, entity, count);
            return;
        }
        if (Sites.Base(call, entity) is not { } at)
            return;
        var player = call.OwnerOf(entity);
        if (call.Flag(entity, S.Site, R.SiteDemolish))
            Dismantle(call, entity, at.Type, player, count);
        else
            Build(call, ctx, entity, at.X, at.Y, at.Type, player, count);
    }

    /// <summary>Починка: быстрее стройки, оплачивается по мере работы; кончились кредиты — стоит.</summary>
    private static void Repair(Call call, uint entity, double count)
    {
        if (!call.Has(entity, S.Health) || Sites.WorkAt(call, entity) is not { } work || Sites.UnderFire(call, entity))
            return;
        var (value, max) = (call.Num(entity, S.Health, S.HealthValue), call.Num(entity, S.Health, S.HealthMax));
        var gain = Math.Min(max - value, count * Core.Rules.RepairSpeed / Sites.WorkTicks(work.Cost));
        var price = gain * work.Cost * Core.Rules.RepairCost;
        if (price <= 0 || Accounts.Spend(call, call.OwnerOf(entity), price))
            call.Set(entity, S.Health).Number(S.HealthValue, value + gain);
    }

    /// <summary>Разбор: быстрее стройки; отменённая стройка возвращает уплаченное по ходу, готовое — долю цены в конце.</summary>
    private static void Dismantle(Call call, uint entity, string type, uint player, double count)
    {
        var total = Sites.Ticks(type);
        var before = call.Num(entity, S.Site, R.SiteProgress);
        var progress = before - count * BuildRules.DemolishSpeed;
        var undo = call.Flag(entity, S.Site, R.SiteUndo);
        // Отменённая стройка возвращает уплаченное за снятую долю, как платили за построенную.
        if (undo)
            Accounts.Earn(call, player, (before - Math.Max(0, progress)) / total * Sites.Spec(type).Cost);
        call.Set(entity, S.Site).Number(R.SiteProgress, progress);
        if (progress > 0)
            return;
        if (!undo)
        {
            Accounts.Add(call, player, Sites.RefundOf(type, call.Num(entity, S.Building, S.BuildingLevel, 1)));
            // Оплаченное вперёд — очередь юнитов и закупка с орбиты — возвращается целиком.
            Sites.RefundQueue(call, player, entity);
            Hooks.RefundPurchase?.Invoke(call, player, entity);
        }
        // Что лежало на складе, остаётся на земле.
        Hooks.DropCargo?.Invoke(call, entity);
        call.Destroy(entity);
    }

    /// <summary>
    /// Стройка: с первым тиком работы площадка становится недостроем и занимает тайлы (пока на ней стоят — не
    /// начинают); прочность — доля построенного; платят по ходу; дальше материалов не идёт; вне зоны стоит.
    /// </summary>
    private static void Build(Call call, SystemContext ctx, uint entity, int x, int y, string type, uint player, double count)
    {
        if (Sites.OutOfControl(player, type, x, y))
            return;
        var spec = Sites.Spec(type);
        if (!call.Has(entity, S.Building))
        {
            // Выгонять пробуют не каждый тик: поиск пути недёшев.
            if (!Sites.ClearSite(call, entity, Core.OnTurn(ctx.Tick, entity, BuildRules.RetryTicks)))
                return;
            call.Add(entity, S.Building).Number(S.BuildingPhase, Tools.Count(S.Building) * 5);
            // Недострой занимает тайлы: solid меняется вместе с Add, занятость движка видит это сразу.
            call.Set(entity, S.Footprint).Flag(S.FootprintSolid, true);
            // Стройка давит брошенный под ней груз.
            ClearDrops(call, x, y, (int)spec.Width, (int)spec.Height);
            var durability = Sites.Durability(type, x, y);
            call.Add(entity, S.Health).Number(S.HealthValue, durability * BuildRules.SiteFloor).Number(S.HealthMax, durability);
            call.Add(entity, S.Sight).Number(S.SightRadius, Factory.BuildingSight(type, site: true));
        }
        var total = Sites.Ticks(type);
        var (value, max) = (call.Num(entity, S.Health, S.HealthValue), call.Num(entity, S.Health, S.HealthMax));
        // Прочность недостроенного — доля построенного: урон откатывает стройку.
        var progress = Math.Min(call.Num(entity, S.Site, R.SiteProgress), total * value / max);
        var speed = Paving.BuildSpeed(type, x, y);
        var reached = Math.Min(progress + count * speed, total * Sites.MaterialShare(call, entity));
        if (reached > progress && Accounts.Spend(call, player, (reached - progress) / total * spec.Cost))
            progress = reached;
        value = Math.Max(value, max * Math.Max(BuildRules.SiteFloor, progress / total));
        // Доли работы складываются с погрешностью: 399,99999999999966 из 400 — уже готово.
        if (progress < total - 1e-9)
        {
            call.Set(entity, S.Site).Number(R.SiteProgress, progress);
            call.Set(entity, S.Health).Number(S.HealthValue, value);
            return;
        }
        call.Set(entity, S.Health).Number(S.HealthValue, max);
        call.Remove(entity, S.Site);
        // Материалы ушли в здание; склад у готового здания свой.
        call.Remove(entity, S.Stock);
        call.Remove(entity, S.Limits);
        if (spec.Produces.Length > 0)
            call.Add(entity, S.Producer).Number(S.ProducerCurrent, -1);
        Factory.EquipBuilding(call, entity, type, player);
        call.Set(entity, S.Sight).Number(S.SightRadius, Factory.BuildingSight(type));
        Accounts.Reward(call, player, type);
        call.Commands.SignalAt(BuiltSignal.Name, MemoryPackSerializer.Serialize(new BuiltSignal { Entity = entity, Player = player, Building = type, X = x, Y = y }), x + spec.Width / 2, y + spec.Height / 2);
    }

    /// <summary>Дропы под основанием исчезают (clearDrops).</summary>
    private static void ClearDrops(Call call, int x, int y, int w, int h)
    {
        foreach (var drop in Tools.Near(x + w / 2.0, y + h / 2.0, Math.Max(w, h), S.Drop))
        {
            var (px, py) = call.At(drop);
            if (px >= x && px < x + w && py >= y && py < y + h)
                call.Destroy(drop);
        }
    }

    // ===== Таблицы =====

    private static uint[] Table(SystemContext ctx, int table) => ctx.Table(table).Entities.ToArray();

    /// <summary>Повреждённые: Health.value &lt; max (по столбцам таблицы).</summary>
    private uint[] DamagedOf(SystemContext ctx)
    {
        var table = ctx.Table(HealthTable);
        var value = table.Column(_healthValue);
        var max = table.Column(_healthMax);
        var list = new List<uint>();
        for (var row = 0; row < table.Count; row++)
            if (value[row] < max[row])
                list.Add(table.Entities[row]);
        return [.. list];
    }

    /// <summary>Покрытие в работе — укладка или разбор (paving.unfinished).</summary>
    private static List<uint> Unfinished(Call call, SystemContext ctx)
    {
        var list = new List<uint>();
        foreach (var entity in Table(ctx, PaveTable))
            if (!call.Flag(entity, S.Pave, R.PaveDone) || call.Flag(entity, S.Pave, S.PaveRemove))
                list.Add(entity);
        return list;
    }

    private static IReadOnlySet<uint> Overbuilt(Call call) => Hooks.Overbuilt?.Invoke(call) ?? new HashSet<uint>();
}
