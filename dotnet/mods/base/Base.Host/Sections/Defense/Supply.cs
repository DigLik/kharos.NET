using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Logistics;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Система supply (supply.ts): снабжение войск. У юнита магазин (Magazine); в своей сети — внутри её кругов — он сам
/// пополняется из хранилищ сети, как турель. Пустой юнит вне сети по стойке: держащий позицию стоит и ждёт, остальные сами
/// идут к ближайшей сети с боеприпасами, а наполнив магазин, возвращаются на место, откуда ушли (Tactics.away). Вне сети
/// магазины пополняет машина снабжения (UnitSpec.supplies) — со своего склада; склад машины наполняют грузовики по заявке
/// (логистика), хранилища сети и станция поставки (BuildingSpec.depot) — своим лучом, каждый тик. Авиация в кругах сети не
/// пополняется: только над своим аэродромом (BuildingSpec.rearms) или у машины снабжения.
/// <para>
/// Доступ к чужому (Н5-3): кадр — все юниты (Unit, Proto, Position, Owner), магазины, тактика и пути — побочными таблицами;
/// склады, потоки и предложения сетей — логистика (<see cref="Pass"/>), которую берёт только тик, где есть кому ехать.
/// Проход — раз в <see cref="PassTicks"/> тиков; станции поставки грузят каждый тик, связи их лучей дописываются к лучам тика.
/// </para>
/// </summary>
internal sealed class Supply : ISystem
{
    /// <summary>Раз во сколько тиков пополняются магазины (SUPPLY_TICKS).</summary>
    public const int PassTicks = 10;

    /// <summary>Раз во сколько проходов пустой юнит ищет снабжение: поиск пути дорог, а снабжения может и не быть (SEEK_PASSES).</summary>
    public const int SeekPasses = 4;

    /// <summary>То же для идущего за снабжением, который не дошёл до сети: путь оборвался или хранилище опустело (RESEEK_PASSES).</summary>
    public const int ReseekPasses = 10;

    /// <summary>Сколько боеприпасов в секунду юнит набирает в своей сети (REFILL_RATE).</summary>
    public const double RefillRate = 10;

    /// <summary>На сколько тайлов от края хранилища достаточно подойти: пополняет вся сеть, а не само хранилище (SEEK_NEAR).</summary>
    public const double SeekNear = 2;

    /// <summary>Насколько ближе края раздачи встаёт пустой, идущий к машине снабжения: она тоже может отъехать (SUPPLIER_MARGIN).</summary>
    public const double SupplierMargin = 1.5;

    /// <summary>На сколько тайлов от края аэродрома авиация ещё над ним (AIRFIELD_REACH).</summary>
    public const double AirfieldReach = 1;

    /// <summary>Сколько боеприпасов в секунду машина снабжения грузит из хранилищ своей сети (SUPPLIER_LOAD).</summary>
    public const double SupplierLoad = 10;

    /// <summary>Дальше скольких тайлов от машины снабжения станцию не ищут: луч станции короче (DEPOT_REACH).</summary>
    public const int DepotReach = 4;

    private const double Epsilon = Magazines.Epsilon;
    private const string AmmoName = "ammo";
    private const int MagazineT = 0, TacticsT = 1, PathT = 2;

    private Column _proto, _x, _y, _owner, _ammo, _resupply;
    private int[]? _suppliers;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Unit.Name, Names.Proto.Name, Names.Position.Name, Names.Owner.Name)
        .Table(Names.Magazine.Name)
        .Table(Names.Tactics.Name)
        .Table(Names.Path.Name);

    public void Bind(SystemBinding binding)
    {
        L.Load(binding.Component);
        Refs.Load(binding.Component);
        _suppliers = null;
        _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
        _x = binding.Column(Names.Position.Name, Names.Position.X);
        _y = binding.Column(Names.Position.Name, Names.Position.Y);
        _owner = binding.Column(Names.Owner.Name, Names.Owner.Player);
        _ammo = binding.Table(MagazineT).Column(Names.Magazine.Name, Names.Magazine.Ammo);
        _resupply = binding.Table(TacticsT).Column(Names.Tactics.Name, Names.Tactics.Resupply);
    }

    /// <summary>Машина снабжения в мире: где стоит, чья, на какой радиус раздаёт и с какой скоростью.</summary>
    private sealed record Machine(uint Entity, uint Player, double X, double Y, double Radius, double Rate);

    /// <summary>Готовый аэродром: где стоит, чей, какого размера и в какой он сети.</summary>
    private sealed record Airfield(uint Entity, uint Player, double X, double Y, double Width, double Height, Zones.Zone Zone);

    public void Run(SystemContext ctx)
    {
        if (!Core.Rules.UnitAmmo)
            return;
        _suppliers ??= [.. Core.Tables.Units.Where(unit => unit.Value.Supplies is not null).Select(unit => Core.UnitProto(unit.Key)).Where(proto => proto >= 0)];
        var pass = ctx.Tick % PassTicks == 0;
        var protos = ctx.Column(_proto);
        var xs = ctx.Column(_x);
        var ys = ctx.Column(_y);
        var owners = ctx.Column(_owner);
        var entities = ctx.Entities;

        // Машины снабжения: на тик с проходом и без него.
        List<(uint Entity, int Row)>? machineRows = null;
        if (_suppliers.Length > 0)
            for (var row = 0; row < ctx.Count; row++)
                if (Array.IndexOf(_suppliers, (int)protos[row]) >= 0)
                    (machineRows ??= []).Add((entities[row], row));
        if (machineRows is null && !pass)
            return;

        var call = Call.Of(ctx);
        var round = new Round(call, Cargo.Index(AmmoName));
        var machines = new List<Machine>();
        if (machineRows is not null)
        {
            machineRows.Sort((a, b) => a.Entity.CompareTo(b.Entity));
            foreach (var (entity, row) in machineRows)
            {
                // Склад (Stock) у машины обязателен.
                if (Arms.Unit((int)protos[row])?.Spec.Supplies is not { } supplies || !round.V.HasStock(entity))
                    continue;
                machines.Add(new Machine(entity, (uint)owners[row], xs[row], ys[row], supplies.Radius, supplies.Rate));
            }
            if (machines.Count > 0)
                LoadAtDepots(round, machines);
        }
        if (pass)
            Refill(ctx, round, machines);
        if (round.Linked)
            Beams.Append(round.Pass);
    }

    // ===== Станции поставки =====

    /// <summary>
    /// Каждый тик: станции поставки грузят машины снабжения, до которых дотягивается их луч (loadAtDepots). Станции ищутся по
    /// тайлам вокруг машины.
    /// </summary>
    private static void LoadAtDepots(Round round, List<Machine> machines)
    {
        var view = round.V;
        foreach (var machine in machines)
        {
            if (view.Room(machine.Entity, round.Ammo) <= Epsilon)
                continue;
            var reach = (int)Math.Ceiling(machine.Radius + DepotReach);
            var (cx, cy) = ((int)Math.Floor(machine.X), (int)Math.Floor(machine.Y));
            var side = reach * 2 + 1;
            var tiles = new int[side * side * 2];
            var at = 0;
            for (var y = cy - reach; y <= cy + reach; y++)
                for (var x = cx - reach; x <= cx + reach; x++)
                {
                    tiles[at++] = x;
                    tiles[at++] = y;
                }
            var found = new uint[side * side];
            Tools.Occupants(tiles, found);
            var seen = new HashSet<uint>();
            foreach (var depot in found)
            {
                if (depot == 0 || !seen.Add(depot))
                    continue;
                if (view.Building(depot) is not { Depot: true } || !view.IsReady(machine.Player, depot) || !Beams.Reaches(view, depot, machine.Entity))
                    continue;
                Beams.Transfer(round.Pass, depot, machine.Entity, round.Ammo);
                round.Linked = true;
            }
        }
    }

    // ===== Проход =====

    /// <summary>
    /// Раз в <see cref="PassTicks"/>: магазины юнитов в своих сетях пополняются из хранилищ сети, вне сетей — у машин снабжения,
    /// пустые идут к ближайшему снабжению, наполнившие магазин возвращаются. Груз берётся сверх резерва хранилища и сверх
    /// обещанного грузовикам — как его берут заявки.
    /// </summary>
    private void Refill(SystemContext ctx, Round round, List<Machine> machines)
    {
        var call = round.Call;
        var view = round.V;
        var ammo = round.Ammo;
        var seconds = PassTicks * Core.Step;
        // Проход — раз в PassTicks, поэтому очередь юнитов считается проходами, а не тиками, как в onTurn.
        var number = ctx.Tick / PassTicks;
        bool OnPass(uint entity, int passes) => (number + entity) % (ulong)passes == 0;

        // Магазины, тактика и пути известны таблицами кадра.
        var table = ctx.Table(MagazineT);
        var magazine = new Dictionary<uint, double>(table.Count);
        var column = table.Column(_ammo);
        for (var row = 0; row < table.Count; row++)
            magazine[table.Entities[row]] = column[row];
        table = ctx.Table(TacticsT);
        var resupply = new HashSet<uint>();
        column = table.Column(_resupply);
        for (var row = 0; row < table.Count; row++)
            if (column[row] != 0)
                resupply.Add(table.Entities[row]);
        var tactics = new HashSet<uint>(table.Entities.ToArray());
        var moving = new HashSet<uint>(ctx.Table(PathT).Entities.ToArray());

        // Машины снабжения в кругах своей сети грузятся из её хранилищ.
        foreach (var machine in machines)
        {
            var room = view.Room(machine.Entity, ammo);
            if (room <= Epsilon)
                continue;
            if (round.ZoneAt(machine.Player, machine.X, machine.Y) is { } zone)
                view.Put(machine.Entity, ammo, round.TakeFrom(zone, Math.Min(room, SupplierLoad * seconds)));
        }

        var protos = ctx.Column(_proto);
        var xs = ctx.Column(_x);
        var ys = ctx.Column(_y);
        var owners = ctx.Column(_owner);
        var entities = ctx.Entities;
        var rows = new int[ctx.Count];
        for (var row = 0; row < rows.Length; row++)
            rows[row] = row;
        var keys = entities.ToArray();
        Array.Sort(keys, rows);

        var seekers = new List<int>();
        foreach (var row in rows)
        {
            if (Arms.Unit((int)protos[row]) is not { } unit)
                continue;
            var entity = entities[row];
            var capacity = unit.Spec.Ammo ?? 0;
            if (capacity == 0)
                continue;
            if (!magazine.TryGetValue(entity, out var rounds))
            {
                call.Add(entity, S.Magazine).Number(S.MagazineAmmo, capacity);
                continue;
            }
            var (x, y, player) = (xs[row], ys[row], (uint)owners[row]);
            var lacking = rounds < capacity - Epsilon;
            // Авиацию кормит сеть только над аэродромом.
            Zones.Zone? zone = !lacking ? null : unit.Air ? round.AirfieldAt(player, x, y) : round.ZoneAt(player, x, y);
            // Вне сети магазин пополняет машина снабжения, в радиусе которой юнит стоит.
            var machine = lacking && zone is null ? round.MachineAt(machines, player, x, y) : null;
            var before = rounds;
            if (zone is not null)
                rounds += round.TakeFrom(zone, Math.Min(capacity - rounds, RefillRate * seconds));
            else if (machine is not null)
                rounds += view.Take(machine.Entity, ammo, Math.Min(capacity - rounds, machine.Rate * seconds));
            if (rounds != before)
                call.Set(entity, S.Magazine).Number(S.MagazineAmmo, rounds);
            if (resupply.Contains(entity))
            {
                // Наполнил — встаёт и возвращается, куда помнит (patrol), и снова воюет по стойке. Путь у идущего за
                // снабжением — всегда снабжения: приказ игрока снимает resupply, см. clearTactics.
                if (rounds >= capacity - Epsilon)
                {
                    call.Set(entity, S.Tactics).Flag(Refs.R.TacticsResupply, false);
                    call.Remove(entity, S.Path);
                }
                else if (zone is null && machine is null && !moving.Contains(entity) && OnPass(entity, ReseekPasses))
                    seekers.Add(row);
                continue;
            }
            if (zone is not null || machine is not null || rounds >= Magazines.CheapestShot(unit.Type) - Epsilon)
                continue;
            // Идущий по приказу идёт, куда велели: за боеприпасами он пойдёт, когда встанет.
            if (moving.Contains(entity) || (tactics.Contains(entity) && call.Text(entity, S.Tactics, Refs.R.TacticsStance) == "hold") || !OnPass(entity, SeekPasses))
                continue;
            seekers.Add(row);
        }

        // Пустые — к ближайшему хранилищу своей сети, где есть боеприпасы, или к машине снабжения с ними; авиация — к
        // аэродрому, в сети которого они есть, или к машине. Нет таких — ждут, где стоят.
        foreach (var row in seekers)
            Seek(round, machines, entities[row], (uint)owners[row], xs[row], ys[row], Arms.Unit((int)protos[row])!.Air);
    }

    /// <summary>Куда идти пустому за боеприпасами: центр места и на сколько тайлов достаточно подойти.</summary>
    private static void Seek(Round round, List<Machine> machines, uint entity, uint player, double x, double y, bool air)
    {
        var call = round.Call;
        var view = round.V;
        (double X, double Y)? best = null;
        var near = 0.0;
        var bestDistance = double.PositiveInfinity;
        foreach (var machine in machines)
        {
            if (machine.Player != player || machine.Entity == entity || view.Amount(machine.Entity, round.Ammo) <= Epsilon)
                continue;
            var distance = DetMath.Hypot(machine.X - x, machine.Y - y);
            if (distance >= bestDistance)
                continue;
            best = (machine.X, machine.Y);
            near = Math.Max(0, machine.Radius - SupplierMargin);
            bestDistance = distance;
        }
        if (air)
        {
            foreach (var airfield in round.Airfields())
            {
                if (airfield.Player != player || !round.Stocked(airfield.Zone))
                    continue;
                var center = (X: airfield.X + airfield.Width / 2, Y: airfield.Y + airfield.Height / 2);
                var distance = DetMath.Hypot(center.X - x, center.Y - y);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                best = center;
                near = Math.Min(airfield.Width, airfield.Height) / 2;
            }
        }
        else
        {
            foreach (var zone in round.Pass.ZonesOf(player))
            {
                if (!round.Stocked(zone))
                    continue;
                foreach (var source in round.Offers[zone, round.Ammo])
                {
                    if (round.LeftIn(source) <= Epsilon)
                        continue;
                    var (cx, cy) = view.Center(source);
                    var spec = view.Building(source);
                    var size = Math.Max(spec?.Width ?? 1, spec?.Height ?? 1);
                    var distance = DetMath.Hypot(cx - x, cy - y);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = (cx, cy);
                        near = size / 2 + SeekNear;
                    }
                }
            }
        }
        if (best is not { } goal)
            return;
        LeaveHome(call, entity, x, y);
        if (!call.Has(entity, S.Converting))
            Moves.Order(call, entity, (int)Math.Floor(goal.X), (int)Math.Floor(goal.Y), default, 0, near);
        if (!call.Has(entity, S.Path))
            return;
        if (!call.Has(entity, S.Tactics))
            call.Add(entity, S.Tactics);
        call.Set(entity, S.Tactics).Flag(Refs.R.TacticsResupply, true);
    }

    /// <summary>Юнит уходит из дома без приказа: тактика запоминает, откуда, чтобы вернуться (leaveHome). В патруле возвращаться некуда.</summary>
    private static void LeaveHome(Call call, uint entity, double x, double y)
    {
        if (Combat.Hooks.LeaveHome is { } hook)
        {
            hook(call, entity);
            return;
        }
        if (!call.Has(entity, S.Tactics))
            call.Add(entity, S.Tactics);
        else if (call.Flag(entity, S.Tactics, Refs.R.TacticsAway) || (call.Bytes(entity, S.Tactics, Refs.R.TacticsPatrol)?.Length ?? 0) > 0)
            return;
        call.Set(entity, S.Tactics).Flag(Refs.R.TacticsAway, true).Number(Refs.R.TacticsHomeX, Math.Floor(x)).Number(Refs.R.TacticsHomeY, Math.Floor(y));
    }

    /// <summary>Один тик снабжения: вызов, логистика мира и то, что за тик считается один раз (потоки, предложения сетей, аэродромы).</summary>
    private sealed class Round
    {
        public readonly Call Call;
        public readonly Pass Pass;
        public readonly View V;
        public readonly int Ammo;
        public bool Linked;
        private Flows? _flows;
        private Demand.OfferIndex? _offers;
        private readonly Dictionary<uint, double> _left = [];
        private List<Airfield>? _airfields;

        public Round(Call call, int ammo)
        {
            Call = call;
            Ammo = ammo;
            Pass = Pass.Of(call);
            V = Pass.V;
        }

        public Demand.OfferIndex Offers => _offers ??= new Demand.OfferIndex(Pass);

        /// <summary>Сколько ещё можно взять с хранилища в этом проходе.</summary>
        public double LeftIn(uint source)
        {
            if (!_left.TryGetValue(source, out var amount))
            {
                _flows ??= Flows.Of(Pass);
                amount = Math.Max(0, Demand.GivenBy(V, source, Ammo) - _flows.Out(source, Ammo));
                _left[source] = amount;
            }
            return amount;
        }

        public bool Stocked(Zones.Zone zone)
        {
            foreach (var source in Offers[zone, Ammo])
                if (LeftIn(source) > Epsilon)
                    return true;
            return false;
        }

        /// <summary>Сеть игрока, в кругах которой точка: из тех, где круги перекрываются, — с боеприпасами.</summary>
        public Zones.Zone? ZoneAt(uint player, double x, double y)
        {
            Zones.Zone? found = null;
            foreach (var zone in Pass.ZonesOf(player))
            {
                if (!BuildRules.InCircles(zone.Circles, x, y))
                    continue;
                if (Stocked(zone))
                    return zone;
                found ??= zone;
            }
            return found;
        }

        /// <summary>Берёт из хранилищ сети до amount боеприпасов; возвращает, сколько взято.</summary>
        public double TakeFrom(Zones.Zone zone, double amount)
        {
            var taken = 0.0;
            foreach (var source in Offers[zone, Ammo])
            {
                if (amount - taken <= Epsilon)
                    break;
                var part = V.Take(source, Ammo, Math.Min(amount - taken, LeftIn(source)));
                _left[source] = LeftIn(source) - part;
                taken += part;
            }
            return taken;
        }

        /// <summary>Ближайшая к точке машина снабжения игрока с боеприпасами, в радиусе раздачи которой точка лежит.</summary>
        public Machine? MachineAt(List<Machine> machines, uint player, double x, double y)
        {
            Machine? best = null;
            var bestDistance = double.PositiveInfinity;
            foreach (var machine in machines)
            {
                if (machine.Player != player || V.Amount(machine.Entity, Ammo) <= Epsilon)
                    continue;
                var distance = DetMath.Hypot(machine.X - x, machine.Y - y);
                if (distance <= machine.Radius && distance < bestDistance)
                {
                    best = machine;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>Готовые аэродромы всех игроков (airfieldsOf).</summary>
        public List<Airfield> Airfields()
        {
            if (_airfields is not null)
                return _airfields;
            _airfields = [];
            var buildings = Core.All(S.Building, S.Owner);
            var protos = Tools.Column(buildings, S.Proto, S.ProtoId);
            for (var i = 0; i < buildings.Length; i++)
            {
                var entity = buildings[i];
                if (Arms.Building((int)protos[i]) is not { Spec.Rearms: true } arms || !V.IsReady(V.Owner(entity), entity) || Zones.NetworkOf(entity) is not { } zone)
                    continue;
                var (x, y) = V.At(entity);
                _airfields.Add(new Airfield(entity, V.Owner(entity), x, y, arms.Spec.Width, arms.Spec.Height, zone));
            }
            return _airfields;
        }

        /// <summary>Сеть аэродрома игрока, над которым точка; нет — null (airfieldAt).</summary>
        public Zones.Zone? AirfieldAt(uint player, double x, double y)
        {
            foreach (var airfield in Airfields())
                if (airfield.Player == player && BuildRules.LinkGap(x, y, airfield.X, airfield.Y, airfield.Width, airfield.Height) <= AirfieldReach)
                    return airfield.Zone;
            return null;
        }
    }
}
