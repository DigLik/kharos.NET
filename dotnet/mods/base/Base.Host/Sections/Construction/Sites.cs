using Base.Host.Kernel;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Construction.Refs;

namespace Base.Host.Sections.Construction;

/// <summary>То, над чем работают строители (Work в construction.ts): основание в тайлах (у юнита — точка и радиус), цена.</summary>
internal readonly record struct Work(double X, double Y, double W, double H, double Radius, double Cost)
{
    /// <summary>Расстояние от точки до края работы; внутри основания — ноль (distanceTo).</summary>
    public double Distance(double x, double y) => Math.Max(0, BuildRules.RectDistance(X, Y, W, H, x, y) - Radius);
}

/// <summary>
/// Стройки (construction.ts, buildings.ts): закладка, строители, отмена, разбор, кредиты по ходу, материалы, зона.
/// Площадка — Position, Site, Owner, Proto «buildings/вид», Footprint без solid (тайлов не занимает), материалы — Stock
/// с Limits. С первым тиком работы — Building, Health, Sight стройки и Footprint.solid: недострой занимает тайлы и видит.
/// </summary>
internal static class Sites
{
    /// <summary>На сколько тайлов ближе своего радиуса встаёт ремонтник (APPROACH_MARGIN).</summary>
    private const double ApproachMargin = 0.5;

    // ===== Виды и правила =====

    public static BuildingSpec Spec(string type) => Factory.Building(type)!;

    /// <summary>Тиков работы одного строителя на здание (siteTicks).</summary>
    public static int Ticks(string type) => BuildRules.SiteTicks(Spec(type), Core.Step);

    /// <summary>Тиков работы на то, что стоит cost (workTicks).</summary>
    public static int WorkTicks(double cost) => BuildRules.WorkTicks(cost, Core.Step);

    /// <summary>Лево-верх и вид стройки или здания; не здание — null.</summary>
    public static (int X, int Y, string Type)? Base(Call call, uint entity)
    {
        if (call.BuildingType(entity) is not { } type || !call.Has(entity, S.Position))
            return null;
        var (x, y) = call.At(entity);
        return ((int)x, (int)y, type);
    }

    /// <summary>Попадает ли центр основания в зону игрока (inControl).</summary>
    public static bool InControl(uint player, string type, double x, double y)
    {
        var spec = Spec(type);
        return BuildRules.InCircles(Zones.CirclesOf(player), x + spec.Width / 2, y + spec.Height / 2);
    }

    /// <summary>Стоит ли стройка здесь без зоны (outOfControl): здание её требует, а в зоне игрока его нет.</summary>
    public static bool OutOfControl(uint player, string type, double x, double y) => BuildRules.NeedsZone(Spec(type)) && !InControl(player, type, x, y);

    /// <summary>Виды готовых зданий игрока (для дерева технологий).</summary>
    public static HashSet<string> ReadyTypes(uint player)
    {
        var entities = Core.All(S.Building, S.Owner);
        var values = new double[entities.Length * 2];
        var has = new bool[entities.Length * 2];
        Tools.Columns(entities, [(S.Owner, S.OwnerPlayer), (S.Proto, S.ProtoId)], values, has);
        var site = new bool[entities.Length];
        Tools.Has(entities, S.Site, site);
        var types = new HashSet<string>();
        for (var i = 0; i < entities.Length; i++)
            if (!site[i] && values[i] == player && Core.ProtoName((int)values[entities.Length + i], "buildings") is { } type)
                types.Add(type);
        return types;
    }

    /// <summary>Открыто ли игроку здание (isUnlocked): строится строителями, нужные здания стоят готовыми, тема открыта.</summary>
    public static bool Unlocked(Call call, uint player, string type)
    {
        if (Factory.Building(type) is not { } spec)
            return false;
        HashSet<string>? ready = null;
        return BuildRules.Unlocked(spec, Core.Rules.TechTree, need => (ready ??= ReadyTypes(player)).Contains(need), topic => Accounts.Researched(call, player, topic));
    }

    /// <summary>
    /// Может ли игрок заложить здание (canBuild): вид строится строителями, место годится, лежит в своей зоне и не задевает
    /// чужую; шахта — на месторождении, где что-то осталось.
    /// </summary>
    public static bool CanBuild(Call call, uint player, string type, int x, int y)
    {
        if (!Unlocked(call, player, type) || !Placement.CanPlace(call, type, x, y))
            return false;
        var spec = Spec(type);
        if (Zones.InForeignZone(player, x, y, (int)spec.Width, (int)spec.Height))
            return false;
        if (spec.Extract && ReserveLeft(call, x, y) <= 0)
            return false;
        return !OutOfControl(player, type, x, y);
    }

    /// <summary>Сколько осталось в месторождении левым верхним тайлом (x, y): запас без добытого (reserveLeft).</summary>
    public static double ReserveLeft(Call call, int x, int y)
    {
        if (S.Deposits is not { } layer)
            return 0;
        double reserve = -1;
        foreach (var item in Tools.Items(layer, 0, x, y, 0.5))
            if (item.X == x && item.Y == y)
                reserve = item.Fields.Length > 1 ? item.Fields[1] : 0;
        if (reserve < 0)
            return 0;
        double mined = 0;
        foreach (var entity in Tools.Near(x, y, 0.5, S.Deposit))
            if (call.At(entity) == (x, y))
                mined = call.Num(entity, S.Deposit, S.DepositMined);
        return Math.Max(0, reserve - mined);
    }

    // ===== Площадки =====

    /// <summary>Стройка (Site), чьё основание накрывает тайл (siteAt): и площадка, и недострой, и разбор; нет — 0.</summary>
    public static uint SiteAt(Call call, int x, int y)
    {
        foreach (var entity in Tools.Near(x + 0.5, y + 0.5, 6, S.Site))
            if (Covers(call, entity, x, y))
                return entity;
        if (call.Writes)
            foreach (var born in call.Stage.Spawned)
                if (Covers(call, born, x, y))
                    return born;
        return 0;
    }

    private static bool Covers(Call call, uint entity, int x, int y)
    {
        if (!call.Has(entity, S.Site) || Base(call, entity) is not { } at)
            return false;
        var spec = Spec(at.Type);
        return x >= at.X && x < at.X + spec.Width && y >= at.Y && y < at.Y + spec.Height;
    }

    /// <summary>
    /// Площадка под здание без проверок: на тайлы не встаёт, но место закреплено; материалы — склад под них (материалы
    /// привезут грузовики). Её закладывают <see cref="OrderBuild"/>, протяжки стен и труб.
    /// </summary>
    public static uint PlaceSite(Call call, uint player, string type, int x, int y)
    {
        var spec = Spec(type);
        var site = call.Spawn();
        call.Add(site, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
        call.Add(site, S.Site);
        call.Add(site, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(site, S.Proto).Number(S.ProtoId, Core.BuildingProto(type));
        call.Add(site, S.Footprint).Number(S.FootprintW, spec.Width).Number(S.FootprintH, spec.Height).Flag(S.FootprintSolid, false).Flag(S.FootprintOpen, false);
        // Площадка не видит: обзор у стройки появляется с первым тиком работы, когда она становится зданием.
        if (spec.Materials.Count > 0)
        {
            call.Add(site, S.Stock).Number(S.StockCapacity, spec.Materials.Values.Sum());
            // Площадка принимает только свои материалы и не больше нужного.
            var limits = call.Add(site, S.Limits);
            foreach (var good in Core.Tables.Goods)
                if (S.Limits.Find(good) is { } field)
                    limits.Number(field, spec.Materials.GetValueOrDefault(good));
        }
        return site;
    }

    /// <summary>
    /// Закладывает площадку и посылает к ней строителей (orderBuild): свои юниты уходят с площадки сразу. Заложить можно
    /// и без кредитов: платят по ходу стройки. Возвращает площадку; не вышло — 0.
    /// </summary>
    public static uint OrderBuild(Call call, uint player, string type, int x, int y, IReadOnlyList<uint> builders)
    {
        if (!CanBuild(call, player, type, x, y))
            return 0;
        var site = PlaceSite(call, player, type, x, y);
        ClearSite(call, site, true);
        AssignBuilders(call, player, site, builders);
        return site;
    }

    /// <summary>
    /// Освобождает основание площадки и говорит, свободно ли оно (clearSite): свои стоящие с evict уходят, едущих не
    /// трогают, чужих ждут.
    /// </summary>
    public static bool ClearSite(Call call, uint site, bool evict)
    {
        if (Base(call, site) is not { } at)
            return true;
        var spec = Spec(at.Type);
        return Moves.Clear(call, call.OwnerOf(site), at.X, at.Y, (int)spec.Width, (int)spec.Height, evict);
    }

    /// <summary>Стоят ли на ещё не начатой площадке юниты (isSiteBlocked).</summary>
    public static bool IsSiteBlocked(Call call, uint site)
    {
        if (!call.Has(site, S.Site) || call.Has(site, S.Building) || Base(call, site) is not { } at)
            return false;
        var spec = Spec(at.Type);
        return Moves.UnitsIn(at.X, at.Y, (int)spec.Width, (int)spec.Height).Length > 0;
    }

    private static bool IsOwnSite(Call call, uint player, uint site) => call.Has(site, S.Site) && call.IsOwn(player, site);

    /// <summary>Сколько игрок ещё доплатит за свою стройку: остаток цены по ходу (разбор не платит).</summary>
    private static double Owed(Call call, uint site, string type)
    {
        var spec = Spec(type);
        return spec.Cost * Math.Max(0, 1 - call.Num(site, S.Site, R.SiteProgress) / Ticks(type));
    }

    /// <summary>Стройки игрока по порядку закладки (не разбор): сущность и вид.</summary>
    private static List<(uint Entity, string Type)> OwnSites(Call call, uint player)
    {
        var list = new List<(uint, string)>();
        foreach (var entity in Core.All(S.Site, S.Owner))
            if (call.OwnerOf(entity) == player && !call.Flag(entity, S.Site, R.SiteDemolish) && call.BuildingType(entity) is { } type)
                list.Add((entity, type));
        return list;
    }

    /// <summary>Свободные кредиты (freeCredits): на счету за вычетом того, что игрок доплатит за свои стройки.</summary>
    public static double FreeCredits(Call call, uint player)
    {
        double owed = 0;
        foreach (var (entity, type) in OwnSites(call, player))
            owed += Owed(call, entity, type);
        return Accounts.Credits(call, player) - owed;
    }

    /// <summary>Каким стройкам не хватит кредитов на счету: кредиты раздаются по порядку закладки (sitesShortOfCredits).</summary>
    public static List<uint> ShortOfCredits(Call call, uint player)
    {
        var shortOf = new List<uint>();
        var budget = Accounts.Credits(call, player);
        foreach (var (entity, type) in OwnSites(call, player))
        {
            var left = Owed(call, entity, type);
            if (budget >= left)
                budget -= left;
            else
                shortOf.Add(entity);
        }
        return shortOf;
    }

    // ===== Материалы =====

    /// <summary>Какую долю стройки позволяют привезённые материалы, 0…1 (materialShare); без материалов — 1.</summary>
    public static double MaterialShare(Call call, uint site)
    {
        if (call.BuildingType(site) is not { } type || !call.Has(site, S.Site) || !call.Has(site, S.Stock))
            return 1;
        var materials = Spec(type).Materials;
        if (materials.Count == 0)
            return 1;
        double share = 1;
        foreach (var (good, amount) in materials)
        {
            var have = S.Stock.Find(good) is { } field ? call.Num(site, S.Stock, field) : 0;
            // Недостача на крошку (луч переносит груз долями) считается привезённой.
            share = Math.Min(share, have >= amount - BuildRules.MaterialEpsilon ? 1 : have / amount);
        }
        return share;
    }

    /// <summary>Стройка дошла до того, что позволяют материалы, и ждёт подвоза (awaitsMaterials).</summary>
    public static bool AwaitsMaterials(Call call, uint site)
    {
        if (!call.Has(site, S.Site) || call.Flag(site, S.Site, R.SiteDemolish) || call.BuildingType(site) is not { } type)
            return false;
        var share = MaterialShare(call, site);
        return share < 1 && call.Num(site, S.Site, R.SiteProgress) >= Ticks(type) * share - 1e-9;
    }

    // ===== Работа =====

    /// <summary>Попадали ли по этому в последние repairPause секунд (underFire).</summary>
    public static bool UnderFire(Call call, uint entity)
    {
        if (!call.Has(entity, S.Health))
            return false;
        var hit = call.Num(entity, S.Health, R.HealthHit, -1);
        return hit >= 0 && call.Tick - hit < Core.Rules.RepairPause / Core.Step;
    }

    /// <summary>Чинится ли: повреждённое готовое здание или юнит с Health.repairable (isRepairable).</summary>
    public static bool IsRepairable(Call call, uint entity)
    {
        if (Core.Rules.RepairSpeed <= 0 || !call.Has(entity, S.Health) || !call.Flag(entity, S.Health, S.HealthRepairable))
            return false;
        return call.Num(entity, S.Health, S.HealthValue) < call.Num(entity, S.Health, S.HealthMax) && !call.Has(entity, S.Site) && !call.Has(entity, S.Converting);
    }

    /// <summary>Может ли игрок послать чинить (canRepair).</summary>
    public static bool CanRepair(Call call, uint player, uint entity) => call.IsOwn(player, entity) && IsRepairable(call, entity);

    /// <summary>Улучшение в работе (upgradeAt): прогресс, сколько тиков надо; повреждённое сначала чинят, разбор не улучшают.</summary>
    public static (double Progress, int Ticks, double Level, string Type)? UpgradeAt(Call call, uint entity)
    {
        if (!call.Has(entity, R.Upgrade) || !call.Has(entity, S.Building) || call.Has(entity, S.Site) || IsRepairable(call, entity))
            return null;
        if (call.BuildingType(entity) is not { } type)
            return null;
        var level = call.Num(entity, S.Building, S.BuildingLevel, 1);
        var scale = call.Num(entity, R.Upgrade, R.UpgradeScale, 1);
        return (call.Num(entity, R.Upgrade, R.UpgradeProgress), BuildRules.UpgradeTicks(Spec(type), level, Core.Step, scale), level, type);
    }

    /// <summary>Над чем здесь работают строители (workAt): стройка, разбор, починка, улучшение, покрытие, землеройная работа.</summary>
    public static Work? WorkAt(Call call, uint entity)
    {
        if (!call.Alive(entity) || !call.Has(entity, S.Position))
            return null;
        var (x, y) = call.At(entity);
        if (call.Has(entity, S.Pave))
        {
            if (call.Flag(entity, S.Pave, R.PaveDone) && !call.Flag(entity, S.Pave, S.PaveRemove))
                return null;
            return new Work(x, y, 1, 1, 0, Paving.Cost(call.Text(entity, S.Pave, S.PaveKind) ?? PaveRules.Road, (int)x, (int)y));
        }
        if (call.Has(entity, R.Dig))
            return Earthworks.Area(call, entity) is { } area ? new Work(area.X, area.Y, area.W, area.H, 0, call.Num(entity, R.Dig, R.DigCost)) : null;
        var site = call.Has(entity, S.Site);
        var repair = !site && IsRepairable(call, entity);
        if (repair && call.Has(entity, S.Unit))
            return Factory.Unit(call.UnitType(entity)) is { } unit ? new Work(x, y, 0, 0, call.Num(entity, S.Body, S.BodyRadius, unit.Radius), unit.Cost) : null;
        var raised = !site && !repair ? UpgradeAt(call, entity) : null;
        if (!site && !repair && raised is null)
            return null;
        if (call.BuildingType(entity) is not { } type)
            return null;
        var spec = Spec(type);
        var cost = raised is { } up ? BuildRules.UpgradeCost(spec, up.Level, call.Num(entity, R.Upgrade, R.UpgradeScale, 1)).Credits : spec.Cost;
        return new Work(x, y, spec.Width, spec.Height, 0, cost);
    }

    /// <summary>
    /// Подводит ремонтника к работе ровно настолько, чтобы она попала в его радиус (approach): на ближайший свободный
    /// тайл, откуда он дотягивается; claimed — тайлы, уже розданные этим приказом. Возвращает, доведёт ли путь до работы.
    /// </summary>
    public static bool Approach(Call call, uint builder, uint site, HashSet<(int, int)>? claimed = null)
    {
        claimed ??= [];
        if (!call.Has(builder, S.Repair) || WorkAt(call, site) is not { } work)
            return false;
        var (px, py) = call.At(builder);
        var radius = call.Num(builder, S.Repair, S.RepairRadius);
        if (work.Distance(px, py) <= radius)
            return true;
        var reach = Math.Max(0, radius - ApproachMargin);
        var own = call.Num(builder, S.Body, S.BodyRadius, Factory.Unit(call.UnitType(builder))?.Radius ?? 0.5);
        var span = (int)Math.Ceiling(reach + work.Radius);
        var (left, top) = ((int)Math.Floor(work.X) - span, (int)Math.Floor(work.Y) - span);
        var (right, bottom) = ((int)Math.Floor(work.X + work.W) + span, (int)Math.Floor(work.Y + work.H) + span);
        var (w, h) = (right - left + 1, bottom - top + 1);
        var taken = Standing(call, builder, own, left, top, right, bottom);
        var xy = new int[w * h * 2];
        for (var i = 0; i < w * h; i++)
            (xy[i * 2], xy[i * 2 + 1]) = (left + i % w, top + i / w);
        var walkable = new bool[w * h];
        Tools.Walkable(xy, walkable);
        var sites = SitesIn(call, left, top, right, bottom);
        (int X, int Y)? best = null;
        var bestDistance = double.PositiveInfinity;
        for (var ty = top; ty <= bottom; ty++)
            for (var tx = left; tx <= right; tx++)
            {
                var distance = BuildRules.Hypot(tx + 0.5 - px, ty + 0.5 - py);
                if (distance >= bestDistance)
                    continue;
                // Само основание займёт здание.
                var edge = work.Distance(tx + 0.5, ty + 0.5);
                if (edge > reach || (work.W > 0 && edge == 0))
                    continue;
                if (claimed.Contains((tx, ty)) || taken.Contains((tx, ty)) || !walkable[(ty - top) * w + tx - left])
                    continue;
                // На площадке не встают: оттуда строителя попросят, как только там начнут строить.
                if (sites.Any(rect => tx >= rect.X && tx < rect.X + rect.W && ty >= rect.Y && ty < rect.Y + rect.H))
                    continue;
                best = (tx, ty);
                bestDistance = distance;
            }
        if (best is not { } goal)
            return false;
        claimed.Add(goal);
        var status = Moves.Order(call, builder, goal.X, goal.Y);
        if (status == PlanStatus.None)
            return false;
        if (status != PlanStatus.Found)
            return true;
        var points = call.Bytes(builder, S.Path, S.PathPoints);
        if (points is not { Length: >= 16 })
            return false;
        var lastX = BitConverter.ToDouble(points, points.Length - 16);
        var lastY = BitConverter.ToDouble(points, points.Length - 8);
        return work.Distance(lastX, lastY) <= radius;
    }

    /// <summary>Тайлы под стоящими наземными юнитами (standingUnits) в прямоугольнике: занят, если корпус radius задел бы стоящего.</summary>
    private static HashSet<(int, int)> Standing(Call call, uint self, double radius, int left, int top, int right, int bottom)
    {
        var tiles = new HashSet<(int, int)>();
        var (cx, cy) = ((left + right + 1) / 2.0, (top + bottom + 1) / 2.0);
        var around = BuildRules.Hypot(right - left + 1, bottom - top + 1) / 2 + 3;
        foreach (var unit in Tools.Near(cx, cy, around, S.Unit))
        {
            if (unit == self || call.Has(unit, S.Path) || !call.Alive(unit))
                continue;
            var spec = Factory.Unit(call.UnitType(unit));
            if (spec is null || spec.Kind == "air")
                continue;
            var (ux, uy) = call.At(unit);
            var reach = call.Num(unit, S.Body, S.BodyRadius, spec.Radius) + radius;
            for (var ty = (int)Math.Floor(uy - reach); ty <= (int)Math.Floor(uy + reach); ty++)
            {
                var dy = ty + 0.5 - uy;
                for (var tx = (int)Math.Floor(ux - reach); tx <= (int)Math.Floor(ux + reach); tx++)
                {
                    var dx = tx + 0.5 - ux;
                    if (dx * dx + dy * dy < reach * reach)
                        tiles.Add((tx, ty));
                }
            }
        }
        return tiles;
    }

    /// <summary>Основания строек, задевающих прямоугольник.</summary>
    private static List<(int X, int Y, int W, int H)> SitesIn(Call call, int left, int top, int right, int bottom)
    {
        var list = new List<(int, int, int, int)>();
        var (cx, cy) = ((left + right + 1) / 2.0, (top + bottom + 1) / 2.0);
        var around = BuildRules.Hypot(right - left + 1, bottom - top + 1) / 2 + 6;
        IEnumerable<uint> sites = Tools.Near(cx, cy, around, S.Site);
        if (call.Writes)
            sites = sites.Concat(call.Stage.Spawned);
        foreach (var site in sites)
            if (call.Has(site, S.Site) && Base(call, site) is { } at)
                list.Add((at.X, at.Y, (int)Spec(at.Type).Width, (int)Spec(at.Type).Height));
        return list;
    }

    /// <summary>
    /// Посылает строителей игрока на его стройку, разбор, к повреждённому — чинить (assignBuilders). Юниты без Repair и
    /// чужие выбрасываются; сам себя ремонтник не чинит. ordered — послан игроком.
    /// </summary>
    public static bool AssignBuilders(Call call, uint player, uint site, IReadOnlyList<uint> units, bool ordered = true)
    {
        if (!call.IsOwn(player, site) || WorkAt(call, site) is null)
            return false;
        var builders = new List<uint>();
        foreach (var unit in units)
            if (!builders.Contains(unit) && unit != site && call.Has(unit, S.Unit) && call.Has(unit, S.Repair) && call.IsOwn(player, unit) && !call.Has(unit, S.Converting))
                builders.Add(unit);
        var claimed = new HashSet<(int, int)>();
        foreach (var builder in builders)
        {
            // Новая работа снимает и поручение везти материал на прежнюю: груз остаётся в кузове.
            call.Remove(builder, S.Fetch);
            call.Add(builder, S.Builds).Number(R.BuildsSite, site).Flag(R.BuildsOrdered, ordered).Number(R.BuildsMisses, 0);
            Approach(call, builder, site, claimed);
        }
        return builders.Count > 0;
    }

    /// <summary>Строитель на работе, которую дал ему игрок, или с приказами в очереди (isBusyBuilder).</summary>
    public static bool IsBusyBuilder(Call call, uint entity) =>
        (call.Has(entity, S.Builds) && call.Flag(entity, S.Builds, R.BuildsOrdered)) || Commands.QueueOf(call, entity).Length > 0;

    // ===== Отмена и разбор =====

    /// <summary>
    /// Отменяет стройку (cancelBuild): площадка без начатой работы исчезает сразу; недострой разбирают (undo), уплаченное
    /// возвращается по ходу; отмена разбора готового оставляет его целым; разбор отменённой стройки не отменить.
    /// </summary>
    public static bool CancelBuild(Call call, uint player, uint site)
    {
        if (!IsOwnSite(call, player, site) || call.Flag(site, S.Site, R.SiteUndo))
            return false;
        if (call.Flag(site, S.Site, R.SiteDemolish))
        {
            call.Remove(site, S.Site);
            if (call.BuildingType(site) is { } built)
                call.Set(site, S.Sight).Number(S.SightRadius, Factory.BuildingSight(built));
            // Ворота снова ходят по своему флагу.
            if (call.Has(site, S.Gate))
                call.Set(site, S.Footprint).Flag(S.FootprintOpen, call.Flag(site, S.Gate, S.GateOpen));
            return true;
        }
        if (call.Has(site, S.Building))
        {
            call.Set(site, S.Site).Flag(R.SiteDemolish, true).Flag(R.SiteUndo, true);
            return true;
        }
        // Привезённые материалы остаются на земле.
        Hooks.DropCargo?.Invoke(call, site);
        call.Destroy(site);
        return true;
    }

    /// <summary>Убирает стройку сразу, без разбора: возвращается цена построенной доли (scrapSite).</summary>
    public static bool ScrapSite(Call call, uint player, uint site)
    {
        if (!IsOwnSite(call, player, site) || call.BuildingType(site) is not { } type)
            return false;
        if (!call.Flag(site, S.Site, R.SiteDemolish))
            Accounts.Add(call, player, Math.Floor(Spec(type).Cost * Math.Min(1, call.Num(site, S.Site, R.SiteProgress) / Ticks(type))));
        Hooks.DropCargo?.Invoke(call, site);
        call.Destroy(site);
        return true;
    }

    /// <summary>Сколько вернёт разбор здания этого вида и уровня (refundOf).</summary>
    public static double RefundOf(string type, double level = 1) => BuildRules.RefundOf(Spec(type), level);

    /// <summary>Может ли игрок разобрать здание (canDemolish): своё, готовое, не главное.</summary>
    public static bool CanDemolish(Call call, uint player, uint building)
    {
        if (!call.Has(building, S.Building) || call.BuildingType(building) is not { } type || type == BuildRules.Core)
            return false;
        return call.IsOwn(player, building) && !call.Has(building, S.Site) && !call.Has(building, S.Converting);
    }

    /// <summary>
    /// Назначает готовое здание под разбор и посылает строителей (demolish): разбирают, как строят, только быстрее; часть
    /// цены возвращается в конце. Здание с этого момента не работает и зону не расширяет.
    /// </summary>
    public static bool Demolish(Call call, uint player, uint building, IReadOnlyList<uint> builders)
    {
        if (!CanDemolish(call, player, building))
            return false;
        // Недоделанное улучшение разбор отменяет: его цена возвращается целиком.
        Hooks.CancelUpgrade?.Invoke(call, player, building);
        var type = call.BuildingType(building)!;
        call.Add(building, S.Site).Number(R.SiteProgress, Ticks(type)).Flag(R.SiteDemolish, true).Flag(R.SiteUndo, false);
        // Под разбором здание видит, как стройка.
        call.Set(building, S.Sight).Number(S.SightRadius, Factory.BuildingSight(type, site: true));
        // Ворота под разбором — уже не ворота.
        if (call.Has(building, S.Gate))
            call.Set(building, S.Footprint).Flag(S.FootprintOpen, false);
        AssignBuilders(call, player, building, builders);
        return true;
    }

    /// <summary>Очередь производства — назад кредитами, начатый заказ — и материалами на склад (refundQueue).</summary>
    public static void RefundQueue(Call call, uint player, uint entity)
    {
        if (!call.Has(entity, S.Producer))
            return;
        var queue = Kernel.Production.Queue(call, entity);
        if (queue.Count == 0)
            return;
        if (call.Num(entity, S.Producer, S.ProducerProgress) > 0 && call.Has(entity, S.Stock) && Factory.Unit(queue[0]) is { } first)
        {
            var writer = call.Set(entity, S.Stock);
            foreach (var (good, amount) in first.Materials)
                if (S.Stock.Find(good) is { } field)
                    writer.Number(field, call.Num(entity, S.Stock, field) + amount);
        }
        foreach (var unit in queue)
            Accounts.Add(call, player, Factory.Unit(unit)?.Cost ?? 0);
        Kernel.Production.SetQueue(call, entity, [], 0);
    }

    // ===== Стены =====

    /// <summary>Какие тайлы протянутой стены или ворот можно заложить (wallStroke); кредиты не считаются.</summary>
    public static bool[] WallStroke(Call call, uint player, ReadOnlySpan<int> tiles, string type = "wall")
    {
        var taken = new HashSet<(int, int)>();
        var allowed = new bool[tiles.Length / 2];
        for (var i = 0; i < allowed.Length; i++)
        {
            var (x, y) = (tiles[i * 2], tiles[i * 2 + 1]);
            var ok = !taken.Contains((x, y)) && CanBuild(call, player, type, x, y);
            if (ok)
                taken.Add((x, y));
            allowed[i] = ok;
        }
        return allowed;
    }

    /// <summary>Закладывает протянутую стену или ворота и посылает строителей к первой секции (orderWalls); возвращает, сколько заложено.</summary>
    public static int OrderWalls(Call call, uint player, int[] tiles, IReadOnlyList<uint> builders, string type = "wall")
    {
        var allowed = WallStroke(call, player, tiles.AsSpan(0, Math.Min(tiles.Length & ~1, PaveRules.Limit * 2)), type);
        uint first = 0;
        var count = 0;
        for (var i = 0; i < allowed.Length; i++)
        {
            if (!allowed[i])
                continue;
            var site = OrderBuild(call, player, type, tiles[i * 2], tiles[i * 2 + 1], []);
            if (site == 0)
                break;
            if (first == 0)
                first = site;
            count++;
        }
        if (first != 0)
            AssignBuilders(call, player, first, builders);
        return count;
    }

    /// <summary>Первая своя работа на тайлах (workIn): стройка, разбор, покрытие в работе, землеройная работа; нет — 0.</summary>
    public static uint WorkIn(Call call, uint player, ReadOnlySpan<int> tiles)
    {
        for (var i = 0; i + 1 < tiles.Length; i += 2)
        {
            var (x, y) = (tiles[i], tiles[i + 1]);
            var site = SiteAt(call, x, y);
            if (site != 0 && call.IsOwn(player, site))
                return site;
            var paved = Paving.At(call, x, y);
            if (paved != 0 && (!call.Flag(paved, S.Pave, R.PaveDone) || call.Flag(paved, S.Pave, S.PaveRemove)) && call.IsOwn(player, paved))
                return paved;
            var dig = Earthworks.At(call, x, y);
            if (dig != 0 && call.IsOwn(player, dig))
                return dig;
        }
        return 0;
    }

    // ===== Готовое здание =====

    /// <summary>Доля прочности на этом месте (durabilityOf): оборона на песке без фундамента — SAND_DURABILITY.</summary>
    public static double Durability(string type, int x, int y)
    {
        var spec = Spec(type);
        if (!spec.Defense)
            return 1;
        var (w, h) = ((int)spec.Width, (int)spec.Height);
        for (var ty = y; ty < y + h; ty++)
            for (var tx = x; tx < x + w; tx++)
                if (Paving.Sand(tx, ty) && !PaveRules.IsFoundation(Paving.Laid(tx, ty)))
                    return BuildRules.SandDurability;
        return 1;
    }

    /// <summary>Склад под уровень здания (applyLevel): хранилище и трюм космопорта с уровнем вмещают больше.</summary>
    public static void ApplyLevel(Call call, uint entity, double level)
    {
        if (call.BuildingType(entity) is not { } type || !call.Has(entity, S.Stock))
            return;
        var spec = Spec(type);
        if ((spec.Stores || spec.Trades) && spec.Inventory is { } room)
            call.Set(entity, S.Stock).Number(S.StockCapacity, BuildRules.Round(room * BuildRules.LevelFactor(level)));
    }
}
