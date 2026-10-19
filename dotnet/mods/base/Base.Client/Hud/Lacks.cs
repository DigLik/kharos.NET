using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>
/// Чего ждут свои здания, которые из-за этого стоят (lacks.ts): каждому — первый недостающий груз. Стройка, дошедшая до
/// привезённого, — материала, а с материалами, но без кредитов на остаток цены — кредитов; производитель, чей заказ не
/// из чего начать; включённый цех без сырья; лаборатория без сырья темы; перехватчик, которому не из чего собрать ракету;
/// турель, которой нечем выстрелить, и реактор без стержней. Что выключено или без заказа — не тревога; о чужих судить
/// не по чему: их склады знает только хозяин. Над зданием по этому рисуется значок (Effects/Power.cs). Считается на
/// реплике клиента раз в <see cref="LackTicks"/> тиков. Сеть зоны клиент не знает: «есть ли в сети» перехватчика — по всем
/// своим складам.
/// </summary>
internal sealed class Lacks(Fx fx, Paint paint)
{
    /// <summary>Нехватка кредитов вместо номера груза.</summary>
    public const int Credits = -2;

    /// <summary>Раз во сколько тиков пересчитывается (LACK_TICKS).</summary>
    public const int LackTicks = 10;

    private const double Epsilon = 1e-6;

    /// <summary>Стержень реактор сжигает целиком: меньше одного — не горит (ROD).</summary>
    private const double Rod = 1;

    /// <summary>С какой доли запаса турель и реактор на исходе (AMMO_REORDER).</summary>
    public const double AmmoReorder = 0.25;

    private double _at = double.NegativeInfinity, _time = double.NegativeInfinity;
    private uint _player = uint.MaxValue;

    /// <summary>Свои здания и стройки, которые стоят: сущность → номер груза (<see cref="Icons.Goods"/>) или <see cref="Credits"/>.</summary>
    public Dictionary<uint, int> Of { get; } = [];

    /// <summary>Свои турели и реакторы, чей запас меньше четверти, но ещё не кончился (runningLow): сущность → груз.</summary>
    public Dictionary<uint, int> Low { get; } = [];

    public void Update()
    {
        var c = fx.C;
        // Раз в LACK_TICKS тиков; и раз в полсекунды экранных часов — когда партия стоит (пауза, правки без тика).
        if (_player == c.Player && Math.Abs(c.Tick - _at) < LackTicks && c.Time - _time < 0.5)
            return;
        _player = c.Player;
        _at = c.Tick;
        _time = c.Time;
        Of.Clear();
        Low.Clear();
        Sites();
        Producers();
        Workshops();
        Labs();
        Interceptors();
        Feeds();
    }

    // ===== Склад =====

    private double Amount(ComponentValues? stock, int good) => stock is null || good < 0 ? 0 : stock.Number(fx.Stock[Icons.Goods[good]]);

    private double Load(ComponentValues stock)
    {
        double load = 0;
        for (var good = 0; good < Icons.Goods.Length; good++)
            load += Amount(stock, good);
        return load;
    }

    /// <summary>Сколько этого груза ещё поместится (roomFor): что склад не принимает или предел набран — ноль.</summary>
    private double Room(uint entity, ComponentValues? stock, BuildingSpec spec, int good)
    {
        if (stock is null || good < 0)
            return 0;
        var name = Icons.Goods[good];
        var limits = fx.C.Get(entity, fx.Limits);
        if (fx.C.Has(entity, fx.Site))
        {
            if (limits is null || limits.Number(fx.Limits[name]) < 0)
                return 0;
        }
        else if (spec.Accepts.Length > 0 && Array.IndexOf(spec.Accepts, name) < 0)
            return 0;
        var limit = limits is null ? -1 : limits.Number(fx.Limits[name]);
        var left = limit < 0 ? double.PositiveInfinity : limit - Amount(stock, good);
        return Math.Max(0, Math.Min(left, stock.Number(fx.Stock["capacity"]) - Load(stock)));
    }

    /// <summary>Первый из материалов, которого на складе меньше нужного (missingFor); всё есть — −1.</summary>
    private int FirstMissing(IReadOnlyDictionary<string, double> materials, ComponentValues? stock)
    {
        foreach (var (name, amount) in materials)
        {
            var good = Array.IndexOf(Icons.Goods, name);
            if (good >= 0 && amount - Amount(stock, good) > Epsilon)
                return good;
        }
        return -1;
    }

    private void Note(uint entity, int good)
    {
        if (good >= 0)
            Of[entity] = good;
    }

    /// <summary>Своё и не выключенное (isOwn и не shutDown).</summary>
    private bool Stands(uint entity) => fx.Mine(entity) && (fx.C.Has(entity, fx.Site) || !paint.SwitchedOff(entity));

    // ===== Кто чего ждёт =====

    /// <summary>
    /// Свои стройки (не разбор): дошедшая до привезённого ждёт первый недостающий материал, а та, которой не хватит
    /// кредитов на остаток цены (кредиты раздаются по порядку закладки, sitesShortOfCredits), — кредитов.
    /// </summary>
    private void Sites()
    {
        var c = fx.C;
        var budget = fx.Credits();
        foreach (var entity in c.Each(fx.Site, fx.Owner))
        {
            if (!fx.Mine(entity) || c.Get(entity, fx.Site) is not { } site || site.Flag(fx.Site["demolish"]) || fx.BuildingOf(entity) is not { } kind)
                continue;
            var ticks = fx.SiteTicks(kind.Spec);
            var progress = site.Number(fx.Site["progress"]);
            var owed = kind.Spec.Cost * Math.Max(0, 1 - progress / ticks);
            var broke = budget < owed;
            if (!broke)
                budget -= owed;
            var stock = c.Get(entity, fx.Stock);
            if (AwaitsMaterials(kind.Spec, stock, progress, ticks))
                Note(entity, FirstMissing(kind.Spec.Materials, stock));
            else if (broke)
                Of[entity] = Credits;
        }
    }

    /// <summary>Стройка дошла до того, что позволяют привезённые материалы (awaitsMaterials).</summary>
    private bool AwaitsMaterials(BuildingSpec spec, ComponentValues? stock, double progress, double ticks)
    {
        if (spec.Materials.Count == 0 || stock is null)
            return false;
        double share = 1;
        foreach (var (name, amount) in spec.Materials)
        {
            var have = Amount(stock, Array.IndexOf(Icons.Goods, name));
            share = Math.Min(share, have >= amount - Epsilon ? 1 : have / amount);
        }
        return share < 1 && progress >= ticks * share - 1e-9;
    }

    /// <summary>Производитель с заказом, который не из чего начать.</summary>
    private void Producers()
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Producer))
        {
            if (c.Get(entity, fx.Producer) is not { } producer || producer.Number(fx.Producer["current"]) < 0 || producer.Number(fx.Producer["progress"]) > 0)
                continue;
            if (c.Has(entity, fx.Site) || c.Has(entity, fx.Converting) || !Stands(entity))
                continue;
            if (fx.NameOf((int)producer.Number(fx.Producer["current"])) is { } unit && fx.Tables.Units.TryGetValue(unit, out var spec))
                Note(entity, FirstMissing(spec.Materials, c.Get(entity, fx.Stock)));
        }
    }

    /// <summary>Включённый цех без сырья; готовому некуда лечь — это не нехватка сырья.</summary>
    private void Workshops()
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Assembly, fx.Stock))
        {
            if (c.Get(entity, fx.Assembly) is not { } assembly || !assembly.Flag(fx.Assembly["on"]) || assembly.Number(fx.Assembly["progress"]) > 0)
                continue;
            if (c.Has(entity, fx.Site) || !fx.Mine(entity) || fx.BuildingOf(entity) is not { } kind || kind.Spec.Assembles is not { } made
                || !fx.Tables.Products.TryGetValue(made, out var product))
                continue;
            var stock = c.Get(entity, fx.Stock);
            if (Room(entity, stock, kind.Spec, Array.IndexOf(Icons.Goods, made)) < product.Yield - 1e-9)
                continue;
            foreach (var (name, amount) in product.Recipe)
            {
                var good = Array.IndexOf(Icons.Goods, name);
                if (good >= 0 && Amount(stock, good) < amount - Epsilon)
                {
                    Note(entity, good);
                    break;
                }
            }
        }
    }

    /// <summary>Лаборатория без сырья темы (Research.cause = material, good — Proto груза).</summary>
    private void Labs()
    {
        foreach (var entity in fx.C.Each(fx.Research))
            if (Stands(entity) && fx.C.Get(entity, fx.Research) is { } research && (int)research.Number(fx.Research["cause"]) == Base.Shared.Rules.LabLack.Material)
                Note(entity, fx.GoodOf((int)research.Number(fx.Research["good"])));
    }

    /// <summary>Перехватчик, которому не из чего собрать ракету (interceptorLack = materials).</summary>
    private void Interceptors()
    {
        var c = fx.C;
        List<uint>? stores = null;
        foreach (var entity in c.Each(fx.Interceptor, fx.Building))
        {
            if (c.Has(entity, fx.Site) || !Stands(entity) || fx.BuildingOf(entity)?.Spec.Intercept is not { } spec || c.Get(entity, fx.Interceptor) is not { } state)
                continue;
            if (state.Number(fx.Interceptor["ticks"]) > 0 || state.Number(fx.Interceptor["missiles"]) >= spec.Capacity || c.Has(entity, fx.Off) || fx.Credits() < spec.Cost)
                continue;
            stores ??= [.. c.Each(fx.Building, fx.Stock).Where(store => fx.Mine(store) && !c.Has(store, fx.Site))];
            foreach (var (name, amount) in spec.Materials)
            {
                var good = Array.IndexOf(Icons.Goods, name);
                double have = 0;
                foreach (var store in stores)
                    have += Amount(c.Get(store, fx.Stock), good);
                if (have < amount - Epsilon)
                {
                    Note(entity, good);
                    break;
                }
            }
        }
    }

    /// <summary>Груз, которым здание кормится (feedOf): турели и склады снабжения — боеприпасы, реактор — стержни.</summary>
    private static string? FeedOf(BuildingSpec spec) => spec.Ammo || spec.Depot ? "ammo" : spec.Burns?.Good;

    /// <summary>Есть ли у турели чем выстрелить хоть одним стволом, у реактора — стержень (fed).</summary>
    private bool Fed(BuildingSpec spec, ComponentValues? stock)
    {
        if (!spec.Ammo)
            return spec.Burns is not { } burns || Amount(stock, Array.IndexOf(Icons.Goods, burns.Good)) >= Rod - Epsilon;
        var ammo = Amount(stock, Array.IndexOf(Icons.Goods, "ammo"));
        foreach (var mount in spec.Mounts)
        {
            var weapon = fx.Tables.Turrets.TryGetValue(mount.Turret, out var turret) && turret.Weapon is { } name ? fx.Tables.Weapons.GetValueOrDefault(name) : null;
            if (weapon?.Ammo is not { } cost || ammo >= cost - 1e-9)
                return true;
        }
        return false;
    }

    /// <summary>Турели без боеприпасов и реакторы без стержней — яркий знак; запас меньше четверти — бледный (runningLow).</summary>
    private void Feeds()
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Building, fx.Stock))
        {
            if (fx.BuildingOf(entity) is not { } kind || FeedOf(kind.Spec) is not { } feed || c.Has(entity, fx.Site) || !Stands(entity))
                continue;
            var stock = c.Get(entity, fx.Stock);
            var good = Array.IndexOf(Icons.Goods, feed);
            var fed = Fed(kind.Spec, stock);
            if (!fed)
            {
                Note(entity, good);
                continue;
            }
            var have = Amount(stock, good);
            var share = have / (have + Room(entity, stock, kind.Spec, good));
            if (share < AmmoReorder)
                Low[entity] = good;
        }
    }
}
