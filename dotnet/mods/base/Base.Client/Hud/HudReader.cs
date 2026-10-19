using System.Buffers.Binary;
using System.Text.Json;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Компоненты схемы, что читает HUD, — по имени при первом кадре (нет в схеме — null).</summary>
internal sealed class HudParts(ClientContext c)
{
    private ComponentRef? Get(string name)
    {
        try
        {
            return c.Component(name);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public readonly ComponentRef? Owner = c.Component("Owner"), Position = c.Component("Position"), Proto = c.Component("Proto");
    public ComponentRef? Unit => _unit ??= Get("Unit");
    public ComponentRef? Building => _building ??= Get("Building");
    public ComponentRef? Site => _site ??= Get("Site");
    public ComponentRef? Health => _health ??= Get("Health");
    public ComponentRef? Stock => _stock ??= Get("Stock");
    public ComponentRef? Wanted => _wanted ??= Get("Wanted");
    public ComponentRef? Hauler => _hauler ??= Get("Hauler");
    public ComponentRef? Carry => _carry ??= Get("Carry");
    public ComponentRef? Tactics => _tactics ??= Get("Tactics");
    public ComponentRef? Magazine => _magazine ??= Get("Magazine");
    public ComponentRef? Producer => _producer ??= Get("Producer");
    public ComponentRef? Converting => _converting ??= Get("Converting");
    public ComponentRef? Deploys => _deploys ??= Get("Deploys");
    public ComponentRef? Assembly => _assembly ??= Get("Assembly");
    public ComponentRef? Off => _off ??= Get("Off");
    public ComponentRef? Busy => _busy ??= Get("Busy");
    public ComponentRef? Accumulator => _accumulator ??= Get("Accumulator");
    public ComponentRef? Reactor => _reactor ??= Get("Reactor");
    public ComponentRef? Upgrade => _upgrade ??= Get("Upgrade");
    public ComponentRef? Research => _research ??= Get("Research");
    public ComponentRef? Science => _science ??= Get("Science");
    public ComponentRef? Silo => _silo ??= Get("Silo");
    public ComponentRef? Interceptor => _interceptor ??= Get("Interceptor");
    public ComponentRef? Shield => _shield ??= Get("Shield");
    public ComponentRef? Player => _player ??= Get("Player");
    public ComponentRef? Team => _team ??= Get("Team");
    public ComponentRef? Mount => _mount ??= Get("Mount");
    public ComponentRef? Doomed => _doomed ??= Get("Doomed");
    public ComponentRef? Deposit => _deposit ??= Get("Deposit");
    public ComponentRef? Mark => _mark ??= Get("Mark");
    public ComponentRef? Missile => _missile ??= Get("Missile");
    public ComponentRef? Sight => _sight ??= Get("Sight");

    private ComponentRef? _unit, _building, _site, _health, _stock, _wanted, _hauler, _carry, _tactics, _magazine, _producer, _converting, _deploys,
        _assembly, _off, _busy, _accumulator, _reactor, _upgrade, _research, _science, _silo, _interceptor, _shield, _player, _team, _mount, _doomed,
        _deposit, _mark, _missile, _sight;

    /// <summary>Число поля; нет компонента или поля — fallback.</summary>
    public static double Num(ComponentValues? values, ComponentRef? component, string field, double fallback = 0) =>
        values is not null && component?.Find(field) is { } f ? values.Number(f) : fallback;

    /// <summary>Строковое поле: номер в строках мира → текст; 0 — значение по умолчанию поля.</summary>
    public static string Str(ComponentValues? values, ComponentRef? component, string field)
    {
        if (values is null || component?.Find(field) is not { } f)
            return "";
        var id = (uint)values.Number(f);
        if (id == 0)
            return f.DefaultText ?? "";
        try
        {
            return Tools.Text(id);
        }
        catch (ToolException)
        {
            return f.DefaultText ?? "";
        }
    }

    /// <summary>Байты поля как u32 подряд.</summary>
    public static List<uint> U32s(ComponentValues? values, ComponentRef? component, string field)
    {
        var list = new List<uint>();
        if (values is null || component?.Find(field) is not { } f)
            return list;
        var bytes = values.Bytes(f);
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            list.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
        return list;
    }
}

/// <summary>
/// Собирает <see cref="HudState"/> из реплики и выделения — перенос readHud из game/hud.ts. Хост клиенту запросов не
/// отвечает, поэтому всё считается здесь по компонентам и данным (BaseTables): энергия — одной сетью по своим готовым
/// зданиям (TS делит по сетям), запас — по хранилищам.
/// </summary>
internal sealed class HudReader
{
    private readonly ClientContext _c;
    private readonly HudParts _p;
    private readonly BaseTables _tables;
    private readonly RulesSpec _rules;
    private readonly string[] _goods;
    private readonly Dictionary<int, string?> _paths = [];

    private readonly LayerRef? _deposits;

    public HudReader(ClientContext c, HudParts parts, BaseTables tables, RulesSpec rules, LayerRef? deposits)
    {
        _deposits = deposits;
        _c = c;
        _p = parts;
        _tables = tables;
        _rules = rules;
        _goods = [.. tables.Goods];
    }

    public BaseTables Tables => _tables;

    /// <summary>Грузы по порядку столбцов Stock (GOODS).</summary>
    public IReadOnlyList<string> Goods => _goods;

    /// <summary>«Раздел/имя» вида по номеру (кэш).</summary>
    public string? PathOf(int proto)
    {
        if (proto < 0)
            return null;
        if (!_paths.TryGetValue(proto, out var path))
            _paths[proto] = path = _c.ProtoPath(proto);
        return path;
    }

    /// <summary>Имя вида без раздела, если вид из раздела section («units»); иначе null.</summary>
    public string? NameOf(int proto, string section) =>
        PathOf(proto) is { } path && path.StartsWith(section + "/", StringComparison.Ordinal) ? path[(section.Length + 1)..] : null;

    public string? UnitType(uint entity) => _c.Has(entity, _p.Unit!) ? NameOf(_c.Proto(entity), "units") : null;

    public string? BuildingType(uint entity) =>
        (_p.Building is { } b && _c.Has(entity, b)) || (_p.Site is { } s && _c.Has(entity, s)) ? NameOf(_c.Proto(entity), "buildings") : null;

    private ComponentValues? Get(uint entity, ComponentRef? component) => component is null ? null : _c.Get(entity, component);

    private bool Has(uint entity, ComponentRef? component) => component is not null && _c.Has(entity, component);

    /// <summary>Склад: груз по столбцам Stock.</summary>
    public Dictionary<string, double> Items(ComponentValues? stock)
    {
        var items = new Dictionary<string, double>();
        if (stock is null)
            return items;
        foreach (var good in _goods)
            if (_p.Stock!.Find(good) is { } field && stock.Number(field) is var amount && amount > 0)
                items[good] = amount;
        return items;
    }

    /// <summary>Груз списком в порядке GOODS, без нулей; доли — вниз (stacksOf).</summary>
    public List<Stack> Stacks(IReadOnlyDictionary<string, double> amounts) =>
        [.. _goods.Where(good => amounts.GetValueOrDefault(good) >= 1).Select(good => new Stack(good, Math.Floor(amounts[good])))];

    private static List<Stack> Exact(IReadOnlyDictionary<string, double> amounts) => [.. amounts.Where(pair => pair.Value != 0).Select(pair => new Stack(pair.Key, pair.Value))];

    private static double Round(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    /// <summary>Срок для интерфейса: «м:сс», от часа — «ч:мм:сс» (clockOf).</summary>
    public static string ClockOf(double seconds)
    {
        var whole = (long)Math.Floor(seconds + 0.5);
        return whole < 3600 ? $"{whole / 60}:{whole % 60:00}" : $"{whole / 3600}:{whole % 3600 / 60:00}:{whole % 60:00}";
    }

    /// <summary>Свой счёт: сущность Player.</summary>
    public ComponentValues? Account()
    {
        if (_p.Player is not { } player)
            return null;
        foreach (var entity in _c.Each(player))
            if (_c.Get(entity, player) is { } values && HudParts.Num(values, player, "id") == _c.Player)
                return values;
        return null;
    }

    /// <summary>Сущность счёта своего игрока (Science на ней же).</summary>
    private uint AccountEntity()
    {
        if (_p.Player is not { } player)
            return 0;
        foreach (var entity in _c.Each(player))
            if (HudParts.Num(_c.Get(entity, player), player, "id") == _c.Player)
                return entity;
        return 0;
    }

    /// <summary>Открытые темы, план и готовность по темам (Science).</summary>
    public (HashSet<string> Done, List<string> Plan, Dictionary<string, double> Progress) Science()
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<string>();
        var progress = new Dictionary<string, double>(StringComparer.Ordinal);
        var account = AccountEntity();
        if (account == 0 || Get(account, _p.Science) is not { } science)
            return (done, plan, progress);
        string? Topic(int proto) => PathOf(proto) is { } path ? path[(path.IndexOf('/') + 1)..] : null;
        foreach (var proto in HudParts.U32s(science, _p.Science, "done"))
            if (Topic((int)proto) is { } topic)
                done.Add(topic);
        foreach (var proto in HudParts.U32s(science, _p.Science, "plan"))
            if (Topic((int)proto) is { } topic)
                plan.Add(topic);
        if (_p.Science!.Find("progress") is { } field)
        {
            var bytes = science.Bytes(field);
            for (var at = 0; at + 12 <= bytes.Length; at += 12)
                if (Topic(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at))) is { } topic)
                    progress[topic] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 4));
        }
        return (done, plan, progress);
    }

    /// <summary>Союзы: глава союза каждого игрока по Team (−1 — ни с кем).</summary>
    public Dictionary<uint, long> Heads()
    {
        var heads = new Dictionary<uint, long>();
        if (_p.Team is not { } team)
            return heads;
        foreach (var entity in _c.Each(team))
            if (_c.Get(entity, team) is { } values && HudParts.Num(values, team, "player") is var player and >= 1)
                heads[(uint)player] = (long)HudParts.Num(values, team, "head", -1);
        return heads;
    }

    /// <summary>Союзник ли игрок other своему игроку.</summary>
    public static bool Allied(Dictionary<uint, long> heads, uint player, uint other) =>
        other != 0 && other != player && heads.TryGetValue(player, out var head) && head >= 0 && heads.TryGetValue(other, out var theirs) && theirs == head;

    /// <summary>Готовое ли здание (не стройка).</summary>
    private bool Ready(uint entity) => Has(entity, _p.Building) && !Has(entity, _p.Site);

    /// <summary>
    /// Хозяйство своих готовых зданий одной сетью (economyOfZone в income.ts): выработка (повреждённые дают меньше),
    /// спрос (без дела — треть), полная нагрузка, накопители, доход генераторов и подмога главного здания.
    /// </summary>
    public (GridState Grid, double Income, double Crowd) Economy(double credits, IEnumerable<uint> buildings)
    {
        double produced = 0, demand = 0, peak = 0, stored = 0, spare = 0, income = 0, crowd = 0, powered = 0;
        var list = buildings.ToList();
        var core = list.Any(entity => BuildingType(entity) == BuildRules.Core);
        var step = _c.TickDt > 0 ? _c.TickDt : 0.05;
        foreach (var entity in list)
        {
            if (BuildingType(entity) is not { } type || _tables.Buildings.GetValueOrDefault(type) is not { } spec)
                continue;
            if (spec.Battery is { } battery && Get(entity, _p.Accumulator) is { } cell)
            {
                var flow = HudParts.Num(cell, _p.Accumulator, "flow");
                if (flow > 0)
                    stored += flow;
                spare += Math.Max(0, Math.Min(battery.Rate - Math.Max(0, flow), HudParts.Num(cell, _p.Accumulator, "charge") / step));
            }
            var power = spec.Power ?? 0;
            if (spec.Burns is not null && !(HudParts.Num(Get(entity, _p.Reactor), _p.Reactor, "left") > 0 && !Has(entity, _p.Off)))
                power = 0;
            if (power > 0 || (spec.Scan ?? 0) != 0)
                power *= BuildRules.LevelFactor(HudParts.Num(Get(entity, _p.Building), _p.Building, "level", 1));
            var earns = core || type == BuildRules.Core || (spec.Income ?? 0) == 0 ? spec.Income ?? 0 : 0;
            var off = Has(entity, _p.Assembly) ? HudParts.Num(Get(entity, _p.Assembly), _p.Assembly, "on") == 0 : Has(entity, _p.Off);
            if (power < 0 && off)
                continue;
            if (power > 0)
            {
                var health = Get(entity, _p.Health);
                var share = health is null ? 1 : HudParts.Num(health, _p.Health, "value", 1) / Math.Max(1e-9, HudParts.Num(health, _p.Health, "max", 1));
                // Прочность хоста — доля (TS); у схемы .NET — value из max.
                produced += power * Math.Min(1, share);
            }
            if (power < 0)
            {
                var norms = spec.Crowding ? ++crowd : 1;
                peak -= power * norms;
                demand -= (PowerRules.AlwaysBusy(spec) || Has(entity, _p.Busy) ? power : PowerRules.IdlePower(power)) * norms;
                powered += earns;
            }
            else
                income += earns;
        }
        produced += stored;
        if (powered != 0)
            income += powered * Math.Min(1, demand > 0 ? produced / demand : 1);
        if (core && credits < PowerRules.ReliefBelow)
            income += PowerRules.ReliefIncome - (_tables.Buildings.GetValueOrDefault(BuildRules.Core)?.Income ?? 0);
        var grid = new GridState(
            Math.Floor(produced * 10) / 10,
            Math.Ceiling(demand * 10) / 10,
            Math.Ceiling(peak * 10) / 10,
            Math.Round(stored * 100, MidpointRounding.AwayFromZero) / 100,
            PowerRules.OverloadOf(produced, peak, spare) > 0);
        return (grid, income, crowd);
    }

    /// <summary>Состояние HUD на этот кадр; selection — выделенные, players — ростер с союзами.</summary>
    public HudState Read(uint[] selection, HudModes modes)
    {
        var c = _c;
        var state = new HudState();
        var account = Account();
        var credits = Math.Floor(HudParts.Num(account, _p.Player, "credits"));
        state.Credits = credits;
        var hour = c.Shared.TryGetValue(Shared.HourKey, out var h) && h is double value ? value : Weather.HourAt(c.Clock);
        // Минуты — десятками, как в TS: интерфейс перерисовывается, только когда состояние изменилось.
        state.Clock = $"{(int)Math.Floor(hour):00}:{(int)Math.Floor(hour % 1 * 6)}0";
        state.Storm = c.Atmosphere.Precipitation > 0.05f;

        // Свои готовые здания: энергия, доход, запас.
        var own = new List<uint>();
        if (_p.Building is { } building)
            foreach (var entity in c.Each(building, _p.Owner!))
                if (c.Mine(entity) && !Has(entity, _p.Site))
                    own.Add(entity);
        var (grid, income, crowd) = Economy(credits, own);
        state.Grid = own.Count > 0 ? grid : null;
        state.Income = Round(income);
        var stock = new Dictionary<string, double>();
        double capacity = 0;
        foreach (var entity in own)
        {
            if (BuildingType(entity) is not { } type || _tables.Buildings.GetValueOrDefault(type) is not { Stores: true })
                continue;
            var values = Get(entity, _p.Stock);
            capacity += HudParts.Num(values, _p.Stock, "capacity");
            foreach (var (good, amount) in Items(values))
                stock[good] = stock.GetValueOrDefault(good) + amount;
        }
        state.Stock = capacity > 0 ? (Stacks(stock), capacity) : null;

        // Игроки: ростер, союзы — по Team.
        var heads = Heads();
        foreach (var row in c.Roster())
            state.Players.Add(new PlayerRow(row.Player, row.Nick, row.Player == c.Player, row.Online, Allied(heads, c.Player, row.Player), heads.GetValueOrDefault(row.Player, -1)));

        var science = Science();
        var current = science.Plan.FirstOrDefault(topic => !science.Done.Contains(topic));
        state.Current = current is null ? null : (current, Round(science.Progress.GetValueOrDefault(current)));
        ReadSelection(state, selection, own, credits, crowd, science.Done, current, science.Progress, modes);
        return state;
    }

    private void ReadSelection(HudState state, uint[] selection, List<uint> own, double credits, double crowd, HashSet<string> done, string? topic,
        Dictionary<string, double> progress, HudModes modes)
    {
        var c = _c;
        var step = c.TickDt > 0 ? c.TickDt : 0.05;
        var counts = new Dictionary<string, int>();
        double unitHealth = 0, filled = 0;
        int unitCount = 0, armed = 0, magazines = 0, empty = 0, buildingCount = 0;
        string? firstKind = null;
        var producers = new List<uint>();
        var upgradeMaterials = new Dictionary<string, double>();
        var tacticsUnits = new List<uint>();
        string? stance = null;
        var stanceMixed = false;
        var patrolling = 0;
        var haulUnits = new List<uint>();
        TruckJob? job = null;
        IReadOnlyList<string>? filter = null;
        var cargoItems = new Dictionary<string, double>();
        double cargoCapacity = 0;
        int bound = 0, busy = 0;
        var gift = new List<uint>();
        var mine = 0;
        foreach (var entity in selection)
        {
            if (!c.Alive(entity))
                continue;
            var mineEntity = c.Mine(entity);
            if (mineEntity)
                mine++;
            var unitType = UnitType(entity);
            if (unitType is not null)
            {
                var spec = _tables.Units.GetValueOrDefault(unitType);
                counts[unitType] = counts.GetValueOrDefault(unitType) + 1;
                var health = Get(entity, _p.Health);
                unitHealth += health is null ? 1 : HudParts.Num(health, _p.Health, "value", 1) / Math.Max(1e-9, HudParts.Num(health, _p.Health, "max", 1));
                unitCount++;
                if (spec?.Ammo is > 0 and var magazine && _rules.UnitAmmo && mineEntity && Get(entity, _p.Magazine) is { } ammo)
                {
                    magazines++;
                    var have = HudParts.Num(ammo, _p.Magazine, "ammo");
                    filled += Math.Min(1, have / magazine);
                    if (have < 1e-6)
                        empty++;
                }
                if (spec is not null && CanFight(spec))
                {
                    armed++;
                    var mineStance = HudParts.Str(Get(entity, _p.Tactics), _p.Tactics, "stance");
                    if (mineStance.Length == 0)
                        mineStance = "defensive";
                    if (tacticsUnits.Count == 0)
                        stance = mineStance;
                    else if (stance != mineStance)
                        stanceMixed = true;
                    tacticsUnits.Add(entity);
                    if (_p.Tactics?.Find("patrol") is { } patrol && Get(entity, _p.Tactics) is { } tactics && tactics.Bytes(patrol).Length > 0)
                        patrolling++;
                }
                // Кого можно отдать союзнику или уничтожить: свои юниты, не турели на носителе и не обречённые.
                if (mineEntity && !Has(entity, _p.Mount) && !Has(entity, _p.Doomed))
                    gift.Add(entity);
                if (Get(entity, _p.Hauler) is { } hauler)
                {
                    var stock = Get(entity, _p.Stock);
                    foreach (var (good, amount) in Items(stock))
                        cargoItems[good] = cargoItems.GetValueOrDefault(good) + amount;
                    cargoCapacity += HudParts.Num(stock, _p.Stock, "capacity");
                    var jobOf = JobOf(entity, hauler);
                    if (HudParts.Num(hauler, _p.Hauler, "mine") != 0)
                        bound++;
                    else if (jobOf.Kind != "auto")
                        busy++;
                    filter ??= FilterOf(hauler);
                    job = job is null ? jobOf : job.Kind != jobOf.Kind ? new TruckJob("mixed", []) : job.Kind == "carry" ? job with { Left = job.Left + jobOf.Left } : job;
                    haulUnits.Add(entity);
                }
            }
            var buildingType = BuildingType(entity);
            if (buildingType is not null)
                state.Building = buildingType;
            if (buildingType is not null && _tables.Buildings.GetValueOrDefault(buildingType) is { } bspec)
                ReadBuilding(state, entity, buildingType, bspec, mineEntity, own, credits, done, topic, progress, upgradeMaterials, step, modes);
            if (buildingType is not null)
            {
                buildingCount++;
                firstKind ??= buildingType;
                state.Mixed |= firstKind != buildingType;
            }
            // MCV — тоже производитель: заказывать в нём нельзя, но разворот и очередь главного здания — его.
            if (Has(entity, _p.Producer) && !Has(entity, _p.Site) && mineEntity)
                producers.Add(entity);
        }
        state.Buildings = buildingCount;
        state.Groupable = mine > 0;
        state.Units = [.. _tables.Units.Keys.Where(counts.ContainsKey).Select(type => (type, counts[type]))];
        if (unitCount > 0)
            state.Army = (armed, Round(unitHealth / unitCount), magazines > 0 ? Round(filled / magazines) : null, empty);
        if (tacticsUnits.Count > 0)
            state.Tactics = new TacticsState([.. tacticsUnits], stanceMixed ? null : stance, patrolling, modes.Patrolling);
        if (haulUnits.Count > 0)
        {
            state.Cargo = new CargoState(Stacks(cargoItems), cargoCapacity, bound, busy);
            state.Haul = new HaulState([.. haulUnits], filter ?? [], job ?? new TruckJob("auto", []), modes.Routing, modes.Serving, modes.Carrying);
        }
        state.Gift = gift.Count > 0 ? [.. gift] : null;
        state.Relief = state.Building == BuildRules.Core && buildingCount == 1 && state.Site is null && credits < PowerRules.ReliefBelow && selection.All(c.Mine);
        if (state.Level?.Next is { } next)
        {
            var lack = next.Lack;
            if (credits < next.Cost && lack != "research")
                lack = "credits";
            state.Level = state.Level with { Next = next with { Materials = Exact(upgradeMaterials), Lack = lack } };
        }
        if (counts.ContainsKey("builder"))
            state.Construction = ConstructionOf(own, credits, crowd, done, modes);
        ReadProduction(state, producers, credits, done, modes);
    }

    /// <summary>Вооружён ли вид (canFight): своё оружие или на турелях.</summary>
    private bool CanFight(Base.Shared.Data.UnitSpec spec) =>
        spec.Weapon is not null || spec.Mounts.Any(mount => _tables.Turrets.GetValueOrDefault(mount.Turret)?.Weapon is not null);

    /// <summary>Фильтр грузовика: маска по порядку столбцов Stock.</summary>
    private List<string> FilterOf(ComponentValues hauler)
    {
        var mask = (long)HudParts.Num(hauler, _p.Hauler, "filter");
        var list = new List<string>();
        for (var i = 0; i < _goods.Length && i < 62; i++)
            if ((mask & (1L << i)) != 0)
                list.Add(_goods[i]);
        return list;
    }

    /// <summary>Чем занят грузовик (jobOf).</summary>
    private TruckJob JobOf(uint entity, ComponentValues hauler)
    {
        string Named(uint target) => BuildingType(target) ?? "";
        List<string> Names(IEnumerable<uint> list) => [.. list.Select(Named).Where(name => name.Length > 0)];
        if (Get(entity, _p.Carry) is { } carry)
        {
            var from = (uint)HudParts.Num(carry, _p.Carry, "from");
            var to = (uint)HudParts.Num(carry, _p.Carry, "to");
            var resource = NameOfGood((int)HudParts.Num(carry, _p.Carry, "resource", -1));
            return new TruckJob("carry", Names(from != 0 ? [from, to] : [to]), resource, Math.Ceiling(HudParts.Num(carry, _p.Carry, "left") - 1e-6));
        }
        if (HudParts.U32s(hauler, _p.Hauler, "route") is { Count: > 0 } route)
            return new TruckJob("route", Names(route));
        if (HudParts.U32s(hauler, _p.Hauler, "serve") is { Count: > 0 } serve)
            return new TruckJob("serve", Names(serve));
        if ((uint)HudParts.Num(hauler, _p.Hauler, "mine") is var mine and not 0)
            return new TruckJob("mine", Names([mine]));
        if ((uint)HudParts.Num(hauler, _p.Hauler, "supply") is var supply and not 0)
            return new TruckJob("supply", Names([supply]));
        if (HudParts.Num(hauler, _p.Hauler, "pickup") != 0)
            return new TruckJob("pickup", []);
        return new TruckJob("auto", []);
    }

    /// <summary>Груз по номеру Proto (ресурс, изделие, руда).</summary>
    private string? NameOfGood(int proto) => PathOf(proto) is { } path ? path[(path.IndexOf('/') + 1)..] : null;

    private void ReadBuilding(HudState state, uint entity, string type, BuildingSpec spec, bool mine, List<uint> own, double credits, HashSet<string> done,
        string? topic, Dictionary<string, double> progress, Dictionary<string, double> upgradeMaterials, double step, HudModes modes)
    {
        var c = _c;
        var ready = Ready(entity);
        var buildingValues = Get(entity, _p.Building);
        var level = (int)HudParts.Num(buildingValues, _p.Building, "level", 1);
        var stock = Get(entity, _p.Stock);
        if (spec.Extract && c.Pose(entity) is { } at && KnownReserve.Spot(c, _deposits, at.X + 0.5, at.Y + 0.5) is { } spot)
            state.Deposit = (spot.Kind, Math.Floor(spot.Left ?? 0));
        if (((spec.Power ?? 0) > 0 || spec.Battery is not null) && mine && ready)
            state.Power = state.Grid;
        if (spec.Battery is { } battery && Get(entity, _p.Accumulator) is { } cell)
            state.Battery = (Math.Floor(HudParts.Num(cell, _p.Accumulator, "charge")), battery.Capacity, Round(HudParts.Num(cell, _p.Accumulator, "flow")));
        // Буфер производителя — не склад: показываем его, только пока там лежат материалы заказа.
        var buffer = spec.Inventory is null;
        var items = Items(stock);
        if (ready && stock is not null && !spec.Ammo && !(buffer && items.Count == 0))
        {
            var accepts = spec.Accepts;
            var slots = accepts.Length > 0 && accepts.All(spec.Limits.ContainsKey)
                ? accepts.Select(good => (good, Math.Floor(items.GetValueOrDefault(good)), spec.Limits[good])).ToList()
                : null;
            state.Stored = new StoredState(Stacks(items), HudParts.Num(stock, _p.Stock, "capacity"), spec.Stores, buffer, slots);
        }
        if (ready && stock is not null && spec.Stores && mine)
        {
            var cap = HudParts.Num(stock, _p.Stock, "capacity");
            state.Reserve = state.Reserve is { } reserve
                ? reserve with { Buildings = [.. reserve.Buildings, entity], Capacity = Math.Min(reserve.Capacity, cap) }
                : new ReserveState([entity], HudParts.Num(stock, _p.Stock, "reserve"), cap);
        }
        if (ready && stock is not null && mine && Get(entity, _p.Wanted) is { } wanted)
        {
            var cap = HudParts.Num(stock, _p.Stock, "capacity");
            if (state.Request is { } request)
                state.Request = request with { Buildings = [.. request.Buildings, entity], Capacity = Math.Min(request.Capacity, cap) };
            else
            {
                var order = new Dictionary<string, double>();
                var have = new Dictionary<string, double>();
                foreach (var good in _goods)
                    if (_p.Wanted!.Find(good) is { } field && wanted.Number(field) is var amount && amount > 0)
                    {
                        order[good] = amount;
                        have[good] = Math.Floor(items.GetValueOrDefault(good));
                    }
                state.Request = new RequestState([entity], order, have, cap);
            }
        }
        if (ready && spec.Refines is { } ore)
            state.Refinery = (ore, 1.5 * BuildRules.LevelFactor(level));
        if (ready && mine && BuildRules.LevelGain(spec) is { } gain)
        {
            var levelState = state.Level ?? new LevelState(level, BuildRules.MaxLevel, gain, BuildRules.LevelFactor(level), null, null);
            if (Get(entity, _p.Upgrade) is { } upgrade)
            {
                var ticks = BuildRules.UpgradeTicks(spec, level, step, HudParts.Num(upgrade, _p.Upgrade, "scale", 1));
                var raising = levelState.Raising ?? ([], Round(Math.Min(1, HudParts.Num(upgrade, _p.Upgrade, "progress") / Math.Max(1, ticks))));
                levelState = levelState with { Raising = ([.. raising.Buildings, entity], raising.Progress) };
            }
            else if (level < BuildRules.MaxLevel)
            {
                var (cost, materials) = BuildRules.UpgradeCost(spec, level, _rules.UpgradeScale);
                var levelTopic = ResearchRules.LevelTopic(level);
                var next = levelState.Next ?? new LevelNext([], level + 1, BuildRules.LevelFactor(level + 1), 0, [], null, levelTopic);
                foreach (var (good, amount) in materials)
                    upgradeMaterials[good] = upgradeMaterials.GetValueOrDefault(good) + amount;
                var lack = next.Lack;
                if (_rules.TechTree && !done.Contains(levelTopic))
                    lack = "research";
                levelState = levelState with { Next = next with { Buildings = [.. next.Buildings, entity], Cost = next.Cost + cost, Lack = lack } };
            }
            state.Level = levelState;
        }
        if (ready && mine && spec.Researches)
        {
            var running = Get(entity, _p.Research);
            var lab = state.Research ?? new LabState(0, 0, null, topic, topic is null ? 0 : Round(progress.GetValueOrDefault(topic)));
            var lack = HudParts.Str(running, _p.Research, "lack");
            state.Research = lab with { Labs = lab.Labs + 1, Speed = Round(lab.Speed + HudParts.Num(running, _p.Research, "speed")), Lack = lab.Lack ?? (lack.Length > 0 ? lack : null) };
        }
        if (state.Silo is null && spec.Warhead is { } warhead && ready && mine && Get(entity, _p.Silo) is { } silo)
        {
            var start = HudParts.Num(silo, _p.Silo, "start", -1);
            var ticks = HudParts.Num(silo, _p.Silo, "ticks");
            var stage = start >= 0 ? "launching" : HudParts.Num(silo, _p.Silo, "ready") != 0 ? "ready" : ticks > 0 ? "assembling" : "empty";
            var left = start >= 0 ? Math.Max(0, start - c.Tick) * step : 0;
            state.Silo = new SiloState(entity, stage, ticks > 0 ? Round(Math.Min(1, HudParts.Num(silo, _p.Silo, "progress") / ticks)) : 0,
                stage == "launching" ? Math.Ceiling(left) : warhead.Launch, warhead.Cost, Exact(warhead.Materials), ClockOf(warhead.Seconds),
                stage == "empty" && credits < warhead.Cost ? "credits" : null, modes.Aiming == entity);
        }
        if (state.Interceptor is null && spec.Intercept is { } guard && ready && mine && Get(entity, _p.Interceptor) is { } charge)
        {
            var ticks = HudParts.Num(charge, _p.Interceptor, "ticks");
            state.Interceptor = (HudParts.Num(charge, _p.Interceptor, "missiles"), guard.Capacity, ticks > 0,
                ticks > 0 ? Round(Math.Min(1, HudParts.Num(charge, _p.Interceptor, "progress") / ticks)) : 0, guard.Radius);
        }
        if (ready && spec.Ammo && stock is not null)
            state.Ammo = (Math.Floor(items.GetValueOrDefault("ammo")), HudParts.Num(stock, _p.Stock, "capacity"));
        if (ready && spec.Burns is { } burns && stock is not null && mine)
            state.Rods = (Math.Floor(items.GetValueOrDefault(burns.Good)), HudParts.Num(stock, _p.Stock, "capacity"),
                HudParts.Num(Get(entity, _p.Reactor), _p.Reactor, "left") > 0 && !Has(entity, _p.Off));
        if (ready && mine && PowerRules.Switchable(spec) && !Has(entity, _p.Assembly))
            state.Switchable = state.Switchable is { } on
                ? on with { Buildings = [.. on.Buildings, entity], On = on.On || !Has(entity, _p.Off) }
                : new SwitchState([entity], !Has(entity, _p.Off));
        if (ready && mine && Get(entity, _p.Assembly) is { } assembly && spec.Assembles is { } recipe && _tables.Products.GetValueOrDefault(recipe) is { } product)
        {
            var seconds = product.Recipe.Values.Sum();
            var have = state.Stock?.Items.FirstOrDefault(stack => stack.Resource == recipe).Amount ?? 0;
            state.Assembly = new AssemblyState(entity, recipe, HudParts.Num(assembly, _p.Assembly, "on") != 0,
                Round(Math.Min(1, HudParts.Num(assembly, _p.Assembly, "progress") / Math.Max(1e-9, seconds))), have, Exact(product.Recipe), product.Yield, seconds);
        }
        var health = Get(entity, _p.Health);
        var value = HudParts.Num(health, _p.Health, "value", 1);
        var max = HudParts.Num(health, _p.Health, "max", 1);
        var site = Get(entity, _p.Site);
        var demolishing = HudParts.Num(site, _p.Site, "demolish") != 0;
        var undo = HudParts.Num(site, _p.Site, "undo") != 0;
        // У строящегося прочность — та же доля построенного, что и полоса стройки.
        if (health is not null && value < max && !(site is not null && (!demolishing || undo)))
        {
            state.Health = Round(value / max);
            state.Repair = BuildRules.RepairCostOf(spec.Cost, value, _rules.RepairCost, max);
        }
        if (spec.Shield is { } dome && Get(entity, _p.Shield) is { } shield)
            state.Shield = (Math.Floor(HudParts.Num(shield, _p.Shield, "charge")), dome.Capacity, Math.Max(0, Math.Ceiling((HudParts.Num(shield, _p.Shield, "down") - c.Tick) * step)));
        if (site is not null)
        {
            var ticks = BuildRules.SiteTicks(spec, step);
            state.Site = new SiteState(entity, Has(entity, _p.Building), Round(Math.Min(1, HudParts.Num(site, _p.Site, "progress") / Math.Max(1, ticks))), demolishing, undo);
        }
        // Разобрать можно своё готовое здание, кроме главного и превращающегося (canDemolish).
        if (ready && mine && type != BuildRules.Core && !Has(entity, _p.Converting))
        {
            var refund = BuildRules.RefundOf(spec, level);
            state.Demolish = state.Demolish is { } demolish
                ? demolish with { Buildings = [.. demolish.Buildings, entity], Refund = demolish.Refund + refund }
                : new DemolishState([entity], refund);
        }
    }

    /// <summary>Строитель среди выбранного: открытые здания и инструменты (construction в readHud).</summary>
    private ConstructionState ConstructionOf(List<uint> own, double credits, double crowd, HashSet<string> done, HudModes modes)
    {
        var readyTypes = new HashSet<string>(own.Select(BuildingType).OfType<string>(), StringComparer.Ordinal);
        bool Researched(string? topic) => topic is null || done.Contains(topic);
        var options = new List<BuildOption>();
        foreach (var (type, spec) in _tables.Buildings.Where(pair => pair.Value.Menu is not null).OrderBy(pair => pair.Value.Menu))
        {
            if (!BuildRules.Unlocked(spec, _rules.TechTree, readyTypes.Contains, Researched))
                continue;
            options.Add(new BuildOption(type, spec.Cost, credits >= spec.Cost, PowerRules.PowerOf(spec, crowd), PowerRules.IdlePowerOf(spec, crowd), Exact(spec.Materials)));
        }
        (string Tool, string? Topic)[] tools =
        [
            ("foundation", PaveRules.FoundationTopic), ("road", PaveRules.RoadTopic), ("bridge", PaveRules.BridgeTopic), ("peak", "earthworks"), ("cliff", "earthworks"),
            ("pipe", _tables.Buildings.GetValueOrDefault("pipe")?.Research), ("well", _tables.Buildings.GetValueOrDefault("well")?.Research),
        ];
        var open = tools.Where(tool => !_rules.TechTree || Researched(tool.Topic)).Select(tool => tool.Tool).ToList();
        return new ConstructionState(modes.Placing, modes.Paving, options, open);
    }

    /// <summary>Производство и превращение (production, conversion в readHud).</summary>
    private void ReadProduction(HudState state, List<uint> producers, double credits, HashSet<string> done, HudModes modes)
    {
        var c = _c;
        if (producers.Count == 0 || _p.Producer is not { } producerRef)
            return;
        var type = BuildingType(producers[0]);
        if (producers.Count > 1 && producers.Any(entity => BuildingType(entity) != type || type is null))
            return;
        List<int> QueueOf(uint entity) => c.Get(entity, producerRef) is { } values ? BaseState.Queue(values, producerRef) : [];
        var entity = producers.Aggregate((best, next) => QueueOf(next).Count < QueueOf(best).Count ? next : best);
        var busiest = producers.Aggregate((best, next) => QueueOf(next).Count > QueueOf(best).Count ? next : best);
        var isUnit = Has(entity, _p.Unit);
        var converting = Get(entity, _p.Converting);
        if (producers.Count == 1 && (isUnit || type == BuildRules.Core))
        {
            var total = HudParts.Num(converting, _p.Converting, "total");
            var progress = converting is not null && total > 0 ? Round(1 - HudParts.Num(converting, _p.Converting, "left") / total) : (double?)null;
            state.Conversion = new ConversionState(isUnit ? "deploy" : "pack", entity, converting is null, progress, modes.Deploying == entity,
                converting is null && Has(entity, _p.Deploys), isUnit && (converting is not null || Has(entity, _p.Deploys)));
        }
        var producer = c.Get(busiest, producerRef)!;
        var queue = BaseState.Queue(producer, producerRef);
        var first = queue.Count > 0 ? NameOf(queue[0], "units") : null;
        var firstSpec = first is null ? null : _tables.Units.GetValueOrDefault(first);
        var buildTicks = firstSpec is null ? 1 : Math.Max(1, Math.Floor(firstSpec.BuildTime / (c.TickDt > 0 ? c.TickDt : 0.05) + 0.5));
        var readyTypes = new HashSet<string>(StringComparer.Ordinal);
        if (_p.Building is { } building)
            foreach (var own in c.Each(building, _p.Owner!))
                if (c.Mine(own) && !Has(own, _p.Site) && BuildingType(own) is { } ownType)
                    readyTypes.Add(ownType);
        var produces = isUnit || type is null || Has(entity, _p.Site) ? [] : _tables.Buildings.GetValueOrDefault(type)?.Produces ?? [];
        var options = new List<ProduceOption>();
        foreach (var unit in produces)
        {
            if (_tables.Units.GetValueOrDefault(unit) is not { } spec)
                continue;
            var missing = _rules.TechTree ? spec.Requires.Where(need => !readyTypes.Contains(need)).ToList() : [];
            var research = _rules.TechTree && spec.Research is { } topic && !done.Contains(topic) ? topic : null;
            options.Add(new ProduceOption(unit, spec.Cost, credits >= spec.Cost, Exact(spec.Materials), missing, research, false));
        }
        state.Production = new ProductionState(entity, busiest, producers.Count, queue,
            queue.Count > 0 ? Round(Math.Min(1, HudParts.Num(producer, producerRef, "progress") / buildTicks)) : 0,
            QueueOf(entity).Count >= 5, !isUnit, HudParts.Num(producer, producerRef, "rally") != 0, options, null);
    }
}

/// <summary>
/// Режимы наведения, что ведёт управление (К2: Controls/Placing): закладка, протяжка покрытия, маршрут, обслуживание,
/// патруль, наводка шахты, разворот MCV. HUD читает и пишет их через c.Shared под ключами <see cref="HudShared"/>.
/// </summary>
internal sealed record HudModes(string? Placing, (string Tool, double Cost, int Tiles)? Paving, int? Routing, int? Serving, bool Carrying, bool Patrolling,
    uint? Aiming, uint? Deploying);

/// <summary>
/// Ключи c.Shared, через которые HUD говорит с управлением и окном исследований. Закладка, протяжка и разворот MCV —
/// режимы управления (<see cref="Modes"/>, К2); окно исследований — <see cref="ResearchSection"/> (К4). Набор маршрута,
/// обслуживания, ручная перевозка, патруль и наводка шахты — свои ключи HUD: их подхватит управление.
/// </summary>
internal static class HudShared
{
    /// <summary>Набор маршрута и обслуживания (int — сколько набрано), ручная перевозка («source»).</summary>
    public const string Route = "hud.route", Serve = "hud.serve", Carry = "hud.carry";

    /// <summary>Выбор точки патруля (bool).</summary>
    public const string Patrol = "hud.patrol";

    /// <summary>Шахта, которую наводят (uint).</summary>
    public const string Aim = "hud.aim";

    /// <summary>Открыто ли окно исследований (bool) — его рисует К4.</summary>
    public const string Tech = ResearchSection.OpenKey;

    public static HudModes Read(ClientContext c)
    {
        int? Count(string key) => c.Shared.TryGetValue(key, out var value) ? value switch { int n => n, uint[] list => list.Length, _ => null } : null;
        return new HudModes(Modes.Placing(c), Modes.Paving(c) is { } tool ? (tool, 0, 0) : null, Count(Route), Count(Serve),
            c.Shared.TryGetValue(Carry, out var carry) && carry is not null, c.Shared.TryGetValue(Patrol, out var patrol) && patrol is true,
            c.Shared.TryGetValue(Aim, out var aim) && aim is uint silo ? silo : null, Modes.Deploying(c));
    }

    /// <summary>Пишет ключ: null или false — снять.</summary>
    public static void Set(ClientContext c, string key, object? value)
    {
        if (value is null or false)
            c.Shared.Remove(key);
        else
            c.Shared[key] = value;
    }
}

/// <summary>Правила партии: rules.json (default) поверх — rules записи партии в Settings, если есть.</summary>
internal static class HudRules
{
    public static RulesSpec Of(ClientContext c, BaseTables tables)
    {
        var rules = tables.Rules;
        try
        {
            using var document = JsonDocument.Parse(c.Settings());
            if (document.RootElement.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("rules", out var custom) && custom.ValueKind == JsonValueKind.Object)
            {
                if (custom.TryGetProperty("techTree", out var tech) && tech.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    rules.TechTree = tech.GetBoolean();
                if (custom.TryGetProperty("unitAmmo", out var ammo) && ammo.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    rules.UnitAmmo = ammo.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }
        return rules;
    }
}
