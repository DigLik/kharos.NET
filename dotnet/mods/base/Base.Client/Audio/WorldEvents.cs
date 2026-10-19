using Base.Shared.Rules;
using Base.Shared.Signals;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Звуки зданий и прочего мира — перенос game/worldSounds.ts. Петли: стройка стучит, ремонт варит, луч грузовика гудит,
/// башни жужжат, купол щита поёт, накопитель свистит, реактор пульсирует, шахта в пуске воет гидравликой, парит и ревёт
/// двигателем, ракета ревёт в полёте, трубы журчат там, докуда дотёк груз. Разовые — по сверке реплики с прошлым тиком:
/// створки встали, ракета стартовала, ядерный взрыв, щит принял удар, пробит, поднялся, ворота, достроено, разобрано, MCV
/// разворачивается, машина выехала из завода, упал или подобран груз, легло или снято покрытие, импульс радара, корабль
/// космопорта, рубильник, кузов полон или пуст, месторождение выработано, капитуляция. И уведомления игроку: машина
/// готова, изучено, улучшено, боеголовка готова, награда, груз доставлен, подарок, защита новичка кончилась, отсчёт до
/// удара, поражение.
/// Перегруз сети и сгоревшая станция — по Grid.state (сети считает хост, GridWire). Чата здесь нет (его звонит оболочка).
/// Покрытие — по слою paving, пока он читается.
/// </summary>
internal sealed class WorldEvents(Mood mood, Teams teams, Func<LayerRef?> paving)
{
    // Раз во сколько секунд пересчитываются петли: они меняются плавно, и чаще не нужно.
    private const double Period = 0.1;
    // За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана.
    private const double Hearing = 0.6;
    private const double Saturate = 2;
    private const double Quiet = 0.5;
    private const double NoticeVolume = 0.4;
    private const double WorkHold = 0.6;
    // Доли пуска шахты: створки расходятся до Doors, пар идёт с Steam, двигатель ревёт с Rise.
    private const double Doors = 0.35, Steam = 0.45, Rise = 0.6;
    private const double ServoMin = 0.01;
    private const double NukeHearing = 6;
    private const double Countdown = 10;
    // Скачок часов мира больше этого, в тиках, — новый мир или загрузка: появляющееся следующие Settle секунд — не события.
    private const long Jump = 100;
    private const double Settle = 2;

    private static readonly (string Name, double Level)[] Levels =
    [
        ("flow", 0.12), ("overload", 0.4), ("construct", 0.28), ("weld", 0.16), ("tractor", 0.1), ("servo", 0.12),
        ("reactor", 0.3), ("dome", 0.06), ("hydraulic", 0.3), ("steam", 0.4), ("rocket", 0.6), ("charge", 0.05),
    ];

    private static readonly Dictionary<string, double> Volumes = new(StringComparer.Ordinal)
    {
        ["doorsLocked"] = 0.7, ["liftoff"] = 1.1, ["nukeBlast"] = 1.6, ["shieldHit"] = 0.35, ["shieldDown"] = 0.6, ["shieldUp"] = 0.35,
        ["gate"] = 0.45, ["built"] = 0.5, ["collapse"] = 0.45, ["deploy"] = 0.5, ["rollout"] = 0.45, ["crates"] = 0.35, ["paved"] = 0.25,
        ["radar"] = 0.1, ["shipLaunch"] = 0.8, ["shipLand"] = 0.8, ["depleted"] = 0.4, ["powerOn"] = 0.35, ["powerOff"] = 0.35,
        ["cargo"] = 0.3, ["surrender"] = 0.6,
    };

    // Чаще этого, в секундах, один звук не повторяется: ворота закрываются всей стеной, дороги ложатся пачкой.
    private static readonly Dictionary<string, double> MinGap = new(StringComparer.Ordinal)
    {
        ["gate"] = 0.15, ["paved"] = 0.2, ["crates"] = 0.15, ["shieldHit"] = 0.08, ["built"] = 0.2, ["rollout"] = 0.3, ["radar"] = 15,
        ["brownout"] = 10, ["countdown"] = 0.5, ["unitReady"] = 0.5, ["researched"] = 1, ["upgraded"] = 0.5, ["gift"] = 1, ["surrender"] = 5,
        ["powerOn"] = 0.15, ["powerOff"] = 0.15, ["cargo"] = 0.2, ["chat"] = 0.15, ["chatSystem"] = 0.15, ["coins"] = 0.3, ["delivered"] = 0.5,
    };

    private readonly record struct SiloMemory(double Start, bool Ready, bool Doors);
    private readonly record struct ShieldMemory(double Hit, bool Up);
    private readonly record struct SiteMemory(bool Demolish, double Progress, double Until, (double X, double Y)? At);
    private readonly record struct TradeMemory(bool Buy, bool Flying, (double X, double Y) At);
    private readonly record struct TransferMemory(double X, double Y, uint To);
    private readonly record struct PaveMemory(int X, int Y, double Work);

    private double _clock, _since = Period, _settled;
    private long _tick = -1;

    // Что было на прошлом тике: по этому видно, что изменилось.
    private Dictionary<uint, SiloMemory> _silos = [];
    private Dictionary<uint, ShieldMemory> _shields = [];
    private Dictionary<uint, bool> _gates = [];
    private Dictionary<uint, SiteMemory> _sites = [];
    private HashSet<uint> _units = [];
    private Dictionary<uint, PaveMemory> _laying = [], _removing = [];
    private Dictionary<uint, double> _scan = [];
    private Dictionary<uint, double> _levels = [];
    private HashSet<uint> _glares = [], _chasers = [];
    private int _research = -1, _rewards = -1;
    private Dictionary<uint, TradeMemory> _trades = [];
    private Dictionary<uint, TransferMemory> _transfers = [];
    private Dictionary<uint, double> _owners = [];
    private int _doomed;
    private Dictionary<uint, bool> _spent = [], _switched = [], _loads = [];
    private Dictionary<uint, (double X, double Y)> _crates = [], _packing = [];
    private Dictionary<uint, double> _angles = [];
    private readonly List<(double X, double Y)> _servos = [];
    private double _countdown = double.PositiveInfinity;
    private readonly Dictionary<string, double> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Sum, double Side)> _sums = new(StringComparer.Ordinal);

    // Сигналы хоста: поражение и защита новичка (сигналы приходят раньше кадра мода, события — в его кадре).
    private bool _sheltered, _shelterKnown, _defeated;
    private bool _protectionOver, _lost;

    private ComponentRef? _footprint, _silo, _reveal, _chaser, _mark, _shield, _gate, _site, _building, _converting, _unit, _drop, _pave, _trade,
        _transfer, _owner, _doom, _deposit, _off, _hauler, _scanner, _producer, _player, _science, _reactor, _accumulator, _repair, _beam, _mount, _batch,
        _missile, _grid, _health;
    private KindData? _data;

    // Перегруз: перегруженные станции (место, доля прочности) — сгоревшая пропадает с прочностью у нуля; своя сеть
    // перегружена — сигнал brownout при начале.
    private Dictionary<uint, (double X, double Y, double Health)> _burning = [];
    private bool _overloaded;
    private uint? _strikeId;

    public void OnDefeated(ClientContext c, HostSignal signal)
    {
        if (signal.Read<DefeatedSignal>() is { } defeated && defeated.Player == c.Player)
            _defeated = true;
    }

    public void OnSheltered(ClientContext c, HostSignal signal)
    {
        if (signal.Read<ShelteredSignal>() is not { } sheltered)
            return;
        var now = sheltered.Players.Contains(c.Player);
        if (_shelterKnown && _sheltered && !now)
            _protectionOver = true;
        (_sheltered, _shelterKnown) = (now, true);
    }

    private void Init(ClientContext c)
    {
        if (_footprint is not null)
            return;
        _footprint = c.Component("Footprint");
        _silo = c.Component("Silo");
        _reveal = c.Component("Reveal");
        _chaser = c.Component("Chaser");
        _mark = c.Component("Mark");
        _shield = c.Component("Shield");
        _gate = c.Component("Gate");
        _site = c.Component("Site");
        _building = c.Component("Building");
        _converting = c.Component("Converting");
        _unit = c.Component("Unit");
        _drop = c.Component("Drop");
        _pave = c.Component("Pave");
        _trade = c.Component("Trade");
        _transfer = c.Component("Transfer");
        _owner = c.Component("Owner");
        _doom = c.Component("Doomed");
        _deposit = c.Component("Deposit");
        _off = c.Component("Off");
        _hauler = c.Component("Hauler");
        _scanner = c.Component("Scan");
        _producer = c.Component("Producer");
        _player = c.Component("Player");
        _science = c.Component("Science");
        _reactor = c.Component("Reactor");
        _accumulator = c.Component("Accumulator");
        _repair = c.Component("Repair");
        _beam = c.Component("Beam");
        _mount = c.Component("Mount");
        _batch = c.Component("Batch");
        _missile = c.Component("Missile");
        _grid = c.Component("Grid");
        _health = c.Component("Health");
        _data = KindData.Read(c);
    }

    // ------------------------------------------------------------------ чтение реплики

    private static double N(ClientContext c, uint entity, ComponentRef component, string field, double fallback = double.NaN) =>
        c.Get(entity, component) is { } values ? values.Number(component[field]) : fallback;

    private static double[] Col(ClientContext c, uint[] entities, ComponentRef component, string field)
    {
        var column = new double[entities.Length];
        if (entities.Length > 0)
            c.Column(entities, component, field, column);
        return column;
    }

    /// <summary>Середина сущности: с основанием — середина основания (место здания — левый верхний тайл), иначе её место.</summary>
    private (double X, double Y)? Center(ClientContext c, uint entity)
    {
        if (c.Pose(entity) is not { } pose)
            return null;
        var (x, y) = (pose.X, pose.Y);
        if (c.Get(entity, _footprint!) is { } footprint)
            (x, y) = (x + footprint.Number(_footprint!["w"]) / 2, y + footprint.Number(_footprint!["h"]) / 2);
        return (x, y);
    }

    private static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor(angle / (Math.PI * 2) + 0.5);

    // ------------------------------------------------------------------ голоса

    private void Play(ClientContext c, string name, double x, double y, double reach = Hearing, double volume = 1)
    {
        var level = (Volumes.TryGetValue(name, out var known) ? known : 0.4) * volume * c.Nearness(x, y, (float)reach);
        if (level <= 0.01 || _clock - _last.GetValueOrDefault(name, double.NegativeInfinity) < MinGap.GetValueOrDefault(name))
            return;
        _last[name] = _clock;
        c.Sound(name, (float)level, c.Side(x) * 0.8f);
    }

    private void Play(ClientContext c, string name, (double X, double Y)? at, double reach = Hearing, double volume = 1)
    {
        if (at is { } spot)
            Play(c, name, spot.X, spot.Y, reach, volume);
    }

    private void Notice(ClientContext c, string name, double volume = NoticeVolume)
    {
        if (_clock - _last.GetValueOrDefault(name, double.NegativeInfinity) < MinGap.GetValueOrDefault(name))
            return;
        _last[name] = _clock;
        c.Sound(name, (float)volume, 0);
    }

    private void Add(ClientContext c, string name, (double X, double Y)? at, double weight = 1)
    {
        if (at is not { } spot)
            return;
        var near = c.Nearness(spot.X, spot.Y, (float)Hearing) * weight;
        if (near <= 0)
            return;
        var (sum, side) = _sums.TryGetValue(name, out var known) ? known : (0.0, 0.0);
        _sums[name] = (sum + near, side + near * c.Side(spot.X));
    }

    // ------------------------------------------------------------------ кадр

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        Init(c);
        _clock += c.Dt;
        if (_tick < 0 || (long)c.Tick != _tick)
        {
            if (_tick < 0 || Math.Abs((long)c.Tick - _tick) > Jump)
                _settled = _clock + Settle;
            _tick = (long)c.Tick;
            teams.Refresh(c);
            Step(c, _clock < _settled);
        }
        _since += c.Dt;
        if (_since < Period)
            return;
        _since = 0;
        Loops(c);
    }

    private void Step(ClientContext c, bool fresh)
    {
        var tick = (double)c.Tick;
        var launchTicks = Math.Round(_data!.Launch / c.TickDt);

        // Шахты: створки, пар, двигатель, старт; своя боеголовка готова.
        var nextSilos = new Dictionary<uint, SiloMemory>();
        foreach (var entity in c.Each(_silo!))
        {
            if (c.Get(entity, _silo!) is not { } silo)
                continue;
            var start = silo.Number(_silo!["start"]);
            var ready = silo.Flag(_silo["ready"]);
            var doors = start >= 0 && (tick - start) / launchTicks >= Doors;
            nextSilos[entity] = new SiloMemory(start, ready, doors);
            if (fresh || !_silos.TryGetValue(entity, out var before))
                continue;
            var at = Center(c, entity);
            if (doors && !before.Doors)
                Play(c, "doorsLocked", at);
            if (before.Start >= 0 && start < 0 && !ready)
                Play(c, "liftoff", at, NukeHearing / 2);
            if (ready && !before.Ready && c.Mine(entity))
                Notice(c, "warheadReady");
        }
        _silos = nextSilos;

        // Ядерный взрыв: вспышка в точке удара (Reveal для всех).
        var nextGlares = new HashSet<uint>();
        var reveals = c.Each(_reveal!);
        var revealAll = Col(c, reveals, _reveal!, "all");
        for (var i = 0; i < reveals.Length; i++)
        {
            if (revealAll[i] == 0)
                continue;
            nextGlares.Add(reveals[i]);
            if (!fresh && !_glares.Contains(reveals[i]) && c.Pose(reveals[i]) is { } spot)
                Play(c, "nukeBlast", spot.X, spot.Y, NukeHearing);
        }
        _glares = nextGlares;

        // Перехватчик: старт ракеты со станка — тот же рёв, что у шахты, но короче слышен.
        var nextChasers = new HashSet<uint>();
        foreach (var entity in c.Each(_chaser!))
        {
            nextChasers.Add(entity);
            if (!fresh && !_chasers.Contains(entity) && c.Get(entity, _chaser!) is { } chaser)
                Play(c, "liftoff", chaser.Number(_chaser!["fromX"]), chaser.Number(_chaser["fromY"]), NukeHearing / 3, 0.7);
        }
        _chasers = nextChasers;

        // Отсчёт перед ударом по своим: метка удара видна тем, чьё под ней.
        var left = double.PositiveInfinity;
        var struck = false;
        var marks = c.Each(_mark!);
        _strikeId ??= Tools.StringId("strike");
        foreach (var entity in marks)
        {
            if (c.Get(entity, _mark!) is not { } mark || _strikeId is not { } id || (uint)mark.Number(_mark!["kind"]) != id)
                continue;
            left = Math.Min(left, (mark.Number(_mark["until"]) - tick) * c.TickDt);
            if (teams.Foe(c, (uint)mark.Number(_mark["player"])))
                struck = true;
        }
        mood.Struck = struck;
        var second = Math.Ceiling(left);
        if (second <= Countdown && second < _countdown)
            Notice(c, "countdown");
        _countdown = second;

        // Щиты: принял удар, пробит, поднялся.
        var nextShields = new Dictionary<uint, ShieldMemory>();
        var shields = c.Each(_shield!);
        var charges = Col(c, shields, _shield!, "charge");
        var hits = Col(c, shields, _shield!, "hit");
        for (var i = 0; i < shields.Length; i++)
        {
            var up = charges[i] > 0;
            nextShields[shields[i]] = new ShieldMemory(hits[i], up);
            if (fresh || !_shields.TryGetValue(shields[i], out var before))
                continue;
            var at = Center(c, shields[i]);
            if (hits[i] != before.Hit && up)
                Play(c, "shieldHit", at);
            if (!up && before.Up)
                Play(c, "shieldDown", at);
            if (up && !before.Up)
                Play(c, "shieldUp", at);
        }
        _shields = nextShields;

        var nextGates = new Dictionary<uint, bool>();
        var gates = c.Each(_gate!);
        var open = Col(c, gates, _gate!, "open");
        for (var i = 0; i < gates.Length; i++)
        {
            nextGates[gates[i]] = open[i] != 0;
            if (!fresh && _gates.TryGetValue(gates[i], out var before) && before != (open[i] != 0))
                Play(c, "gate", Center(c, gates[i]));
        }
        _gates = nextGates;

        // Стройки: достроено — площадка ушла, здание осталось; разобрано — ушло всё, и где оно стояло, помнится заранее.
        var nextSites = new Dictionary<uint, SiteMemory>();
        foreach (var entity in c.Each(_site!))
        {
            if (c.Get(entity, _site!) is not { } site)
                continue;
            var progress = site.Number(_site!["progress"]);
            var moved = _sites.TryGetValue(entity, out var before) && Math.Abs(before.Progress - progress) > 1e-9;
            nextSites[entity] = new SiteMemory(site.Flag(_site["demolish"]), progress, moved ? _clock + WorkHold : before.Until, Center(c, entity));
        }
        if (!fresh)
        {
            foreach (var (entity, before) in _sites)
            {
                if (nextSites.ContainsKey(entity) || before.At is null)
                    continue;
                var built = c.Alive(entity) && c.Has(entity, _building!);
                if (before.Demolish && !built)
                    Play(c, "collapse", before.At);
                else if (!before.Demolish && built)
                    Play(c, "built", before.At);
            }
        }
        _sites = nextSites;

        // MCV разворачивается или сворачивается; кончил — новое на месте старого, и встаёт с лязгом.
        var nextPacking = new Dictionary<uint, (double X, double Y)>();
        foreach (var entity in c.Each(_converting!))
        {
            if (Center(c, entity) is not { } at)
                continue;
            nextPacking[entity] = at;
            if (!fresh && !_packing.ContainsKey(entity))
                Play(c, "deploy", at);
        }
        if (!fresh)
            foreach (var (entity, at) in _packing)
                if (!c.Alive(entity) || c.Pose(entity) is null)
                    Play(c, "built", at);
        // Сворачивание кончается тем, что здание пропадает, а машина появляется: смотреть надо на прошлый кадр.
        var unpacked = _packing.Count > 0;
        _packing = nextPacking;

        // Новая своя машина у своего завода — выехала из него.
        var nextUnits = new HashSet<uint>();
        List<(double X, double Y)>? factories = null;
        foreach (var entity in c.Each(_unit!))
        {
            nextUnits.Add(entity);
            if (fresh || _units.Contains(entity) || !c.Mine(entity))
                continue;
            // Свёрнутое главное здание — тоже новая машина, но не из завода.
            if (unpacked && c.ProtoPath(c.Proto(entity)) == "units/mcv")
                continue;
            if (c.Pose(entity) is not { } position)
                continue;
            factories ??= OwnFactories(c);
            (double X, double Y)? best = null;
            var bestDistance = 5.0;
            foreach (var center in factories)
            {
                var distance = Math.Sqrt((center.X - position.X) * (center.X - position.X) + (center.Y - position.Y) * (center.Y - position.Y));
                if (distance < bestDistance)
                    (bestDistance, best) = (distance, center);
            }
            if (best is { } found)
            {
                Play(c, "rollout", found.X, found.Y);
                Notice(c, "unitReady");
            }
        }
        _units = nextUnits;

        // Груз: упал — удар ящиков; подобрали — тише.
        var nextCrates = new Dictionary<uint, (double X, double Y)>();
        foreach (var entity in c.Each(_drop!))
        {
            if (c.Pose(entity) is not { } position)
                continue;
            nextCrates[entity] = (position.X, position.Y);
            if (!fresh && !_crates.ContainsKey(entity))
                Play(c, "crates", position.X, position.Y);
        }
        if (!fresh)
            foreach (var (entity, at) in _crates)
                if (!nextCrates.ContainsKey(entity))
                    Play(c, "crates", at.X, at.Y, Hearing, Quiet);
        _crates = nextCrates;

        // Покрытие: легло — шлепок; сняли — тише. Укладка и разбор — работа строителей, сущность на тайле: кончилась
        // укладка — тайл покрыт, кончился разбор — пуст. Работу, которую отменили, не слышно.
        var nextLaying = new Dictionary<uint, PaveMemory>();
        var nextRemoving = new Dictionary<uint, PaveMemory>();
        foreach (var entity in c.Each(_pave!))
        {
            if (c.Get(entity, _pave!) is not { } pave || c.Pose(entity) is not { } position)
                continue;
            var memory = new PaveMemory((int)Math.Floor(position.X), (int)Math.Floor(position.Y), pave.Number(_pave!["work"]));
            (pave.Flag(_pave["remove"]) ? nextRemoving : nextLaying)[entity] = memory;
        }
        if (!fresh)
        {
            foreach (var (entity, at) in _laying)
                if (!nextLaying.ContainsKey(entity) && Paved(at))
                    Play(c, "paved", at.X + 0.5, at.Y + 0.5);
            foreach (var (entity, at) in _removing)
                if (!nextRemoving.ContainsKey(entity) && !Paved(at))
                    Play(c, "paved", at.X + 0.5, at.Y + 0.5, Hearing, Quiet);
        }
        _laying = nextLaying;
        _removing = nextRemoving;

        // Корабль космопорта: продажа улетает и приносит деньги, покупка садится с грузом.
        var nextTrades = new Dictionary<uint, TradeMemory>();
        foreach (var entity in c.Each(_trade!))
        {
            if (c.Get(entity, _trade!) is not { } trade || Center(c, entity) is not { } at)
                continue;
            var buy = trade.Flag(_trade!["buy"]);
            var flying = buy || trade.Number(_trade["total"]) > 0;
            nextTrades[entity] = new TradeMemory(buy, flying, at);
            if (!fresh && flying && !buy && !(_trades.TryGetValue(entity, out var before) && before.Flying))
                Play(c, "shipLaunch", at);
        }
        if (!fresh)
        {
            foreach (var (entity, before) in _trades)
            {
                if (nextTrades.ContainsKey(entity) || !before.Flying)
                    continue;
                if (before.Buy)
                {
                    Play(c, "shipLand", before.At);
                    Notice(c, "delivered");
                }
                else
                    Notice(c, "coins");
            }
        }
        _trades = nextTrades;

        // Корабль с подарком союзнику: взлетает из порта, садится у его главного здания.
        var nextTransfers = new Dictionary<uint, TransferMemory>();
        foreach (var entity in c.Each(_transfer!))
        {
            if (c.Get(entity, _transfer!) is not { } transfer)
                continue;
            nextTransfers[entity] = new TransferMemory(transfer.Number(_transfer!["x"]), transfer.Number(_transfer["y"]), (uint)transfer.Number(_transfer["to"]));
            if (!fresh && !_transfers.ContainsKey(entity))
                Play(c, "shipLaunch", Center(c, entity));
        }
        if (!fresh)
        {
            foreach (var (entity, before) in _transfers)
            {
                if (nextTransfers.ContainsKey(entity))
                    continue;
                Play(c, "shipLand", before.X, before.Y);
                if (before.To == c.Player)
                    Notice(c, "gift");
            }
        }
        _transfers = nextTransfers;

        // Чужое стало своим: подарили юниты или здания.
        var owned = c.Each(_owner!);
        var players = Col(c, owned, _owner!, "player");
        var nextOwners = new Dictionary<uint, double>(owned.Length);
        var given = false;
        for (var i = 0; i < owned.Length; i++)
        {
            nextOwners[owned[i]] = players[i];
            if (_owners.TryGetValue(owned[i], out var before) && before != players[i] && players[i] == c.Player)
                given = true;
        }
        if (!fresh && given)
            Notice(c, "gift");
        _owners = nextOwners;

        // Капитуляция: база гибнет волной.
        var doom = 0;
        (double X, double Y)? doomAt = null;
        var ownDoom = false;
        foreach (var entity in c.Each(_doom!))
        {
            // Юнит, который игрок уничтожил сам, — не капитуляция: слышен только его взрыв.
            if (c.Get(entity, _doom!) is not { } fate || fate.Flag(_doom!["scuttled"]))
                continue;
            doom++;
            if (doomAt is null && c.Pose(entity) is { } place)
                doomAt = (place.X, place.Y);
            if (c.Mine(entity))
                ownDoom = true;
        }
        if (!fresh && doom > 0 && _doomed == 0)
        {
            if (ownDoom)
                Notice(c, "surrender", NoticeVolume * 1.5);
            else if (doomAt is { } where)
                Play(c, "surrender", where.X, where.Y, Hearing * 3);
        }
        _doomed = doom;

        // Месторождение выработано.
        var nextSpent = new Dictionary<uint, bool>();
        var deposits = c.Each(_deposit!);
        var exhausted = Col(c, deposits, _deposit!, "spent");
        for (var i = 0; i < deposits.Length; i++)
        {
            nextSpent[deposits[i]] = exhausted[i] != 0;
            if (!fresh && exhausted[i] != 0 && _spent.TryGetValue(deposits[i], out var was) && !was && c.Pose(deposits[i]) is { } spot)
                Play(c, "depleted", spot.X, spot.Y);
        }
        _spent = nextSpent;

        // Станция сгорела от перегруза: пропала без взрыва, с прочностью на нуле.
        var nextBurning = new Dictionary<uint, (double X, double Y, double Health)>();
        foreach (var entity in c.Each(_grid!, _health!))
        {
            if (((int)N(c, entity, _grid!, "state", 0) & GridWire.Overload) == 0 || Center(c, entity) is not { } at || c.Get(entity, _health!) is not { } health)
                continue;
            var max = health.Number(_health!["max"]);
            nextBurning[entity] = (at.X, at.Y, max > 0 ? health.Number(_health["value"]) / max : health.Number(_health["value"]));
        }
        if (!fresh)
            foreach (var (entity, before) in _burning)
                if (!c.Alive(entity) && before.Health < 0.05)
                    Play(c, "collapse", before.X, before.Y);
        _burning = nextBurning;

        // Рубильник: здание выключили или включили.
        var switchedOff = new HashSet<uint>(c.Each(_off!));
        var nextSwitched = new Dictionary<uint, bool>();
        foreach (var entity in c.Each(_building!))
        {
            var off = switchedOff.Contains(entity);
            nextSwitched[entity] = off;
            if (fresh || !_switched.TryGetValue(entity, out var before) || before == off)
                continue;
            Play(c, off ? "powerOff" : "powerOn", Center(c, entity));
        }
        _switched = nextSwitched;

        // Свой грузовик загрузился доверху или опустел.
        var nextLoads = new Dictionary<uint, bool>();
        var haulers = c.Each(_hauler!);
        var full = Col(c, haulers, _hauler!, "full");
        for (var i = 0; i < haulers.Length; i++)
        {
            nextLoads[haulers[i]] = full[i] != 0;
            if (!fresh && _loads.TryGetValue(haulers[i], out var before) && before != (full[i] != 0) && c.Pose(haulers[i]) is { } spot)
                Play(c, "cargo", spot.X, spot.Y);
        }
        _loads = nextLoads;

        // Своя защита новичка кончилась; поражение.
        if (_protectionOver)
        {
            _protectionOver = false;
            if (!fresh)
                Notice(c, "protectionOver");
        }
        if (_defeated && !_lost)
        {
            if (!fresh)
                Notice(c, "defeat", NoticeVolume * 1.5);
            _lost = true;
        }

        // Радар: импульс.
        var nextScan = new Dictionary<uint, double>();
        var radars = c.Each(_scanner!);
        var pulses = Col(c, radars, _scanner!, "tick");
        for (var i = 0; i < radars.Length; i++)
        {
            nextScan[radars[i]] = pulses[i];
            if (!fresh && _scan.TryGetValue(radars[i], out var before) && before != pulses[i])
                Play(c, "radar", Center(c, radars[i]));
        }
        _scan = nextScan;

        // Своё здание стало на уровень выше.
        var nextLevels = new Dictionary<uint, double>();
        var buildings = c.Each(_building!, _owner!);
        var owners = Col(c, buildings, _owner!, "player");
        var levels = Col(c, buildings, _building!, "level");
        for (var i = 0; i < buildings.Length; i++)
        {
            if (owners[i] != c.Player)
                continue;
            nextLevels[buildings[i]] = levels[i];
            if (!fresh && _levels.TryGetValue(buildings[i], out var before) && levels[i] > before)
                Notice(c, "upgraded");
        }
        _levels = nextLevels;

        // Наука и награды своего счёта.
        foreach (var entity in c.Each(_player!))
        {
            if (c.Get(entity, _player!) is not { } account || (uint)account.Number(_player!["id"]) != c.Player)
                continue;
            var research = c.Get(entity, _science!) is { } science ? science.Bytes(_science!["done"]).Length / 4 : 0;
            var rewards = account.Bytes(_player["rewards"]).Length / 4;
            if (!fresh && _research >= 0 && research > _research)
            {
                Notice(c, "researched");
                mood.Triumphs++;
            }
            if (!fresh && _rewards >= 0 && rewards > _rewards)
                Notice(c, "reward");
            _research = research;
            _rewards = rewards;
        }

        Servos(c);
    }

    private List<(double X, double Y)> OwnFactories(ClientContext c)
    {
        var list = new List<(double, double)>();
        foreach (var factory in c.Each(_producer!, _building!, _owner!))
            if (c.Mine(factory) && Center(c, factory) is { } center)
                list.Add(center);
        return list;
    }

    /// <summary>Легло ли покрытие на тайле: слой paving читается, пока он есть; нет — считаем, что легло, если работа шла.</summary>
    private bool Paved(PaveMemory at)
    {
        if (paving() is not { } layer)
            return at.Work > 0;
        try
        {
            return Tools.Layer(layer, 0, 0, at.X, at.Y) != 0;
        }
        catch (ToolException)
        {
            return at.Work > 0;
        }
    }

    /// <summary>Башни, что повернулись за тик (Mount.angle против прошлого тика): жужжат серво.</summary>
    private void Servos(ClientContext c)
    {
        _servos.Clear();
        var mounts = c.Each(_mount!);
        var angles = Col(c, mounts, _mount!, "angle");
        var next = new Dictionary<uint, double>(mounts.Length);
        List<uint>? moving = null;
        for (var i = 0; i < mounts.Length; i++)
        {
            next[mounts[i]] = angles[i];
            if (_angles.TryGetValue(mounts[i], out var before) && Math.Abs(Wrap(angles[i] - before)) > ServoMin)
                (moving ??= []).Add(mounts[i]);
        }
        _angles = next;
        if (moving is null)
            return;
        var spots = new (double X, double Y, double Angle)[moving.Count];
        c.Poses(moving.ToArray(), spots);
        foreach (var spot in spots)
            if (!double.IsNaN(spot.X))
                _servos.Add((spot.X, spot.Y));
    }

    // ------------------------------------------------------------------ петли

    private void Loops(ClientContext c)
    {
        _sums.Clear();
        var tick = c.Tick + c.Alpha;

        // Перегруз: трещат перегруженные станции; своя сеть только что перегрузилась — сигнал.
        var overload = false;
        foreach (var entity in c.Each(_grid!))
        {
            if (((int)N(c, entity, _grid!, "state", 0) & GridWire.Overload) == 0 || Center(c, entity) is not { } at)
                continue;
            Add(c, "overload", at);
            if (c.Mine(entity))
                overload = true;
        }
        if (_clock >= _settled && overload && !_overloaded)
            Notice(c, "brownout");
        _overloaded = overload;
        foreach (var entity in c.Each(_reactor!))
            if (N(c, entity, _reactor!, "left", 0) > 0)
                Add(c, "reactor", Center(c, entity));
        foreach (var entity in c.Each(_accumulator!))
            if (Math.Abs(N(c, entity, _accumulator!, "flow", 0)) >= 1e-9)
                Add(c, "charge", Center(c, entity));
        foreach (var (entity, site) in _sites)
            if (_clock < site.Until && c.Has(entity, _building!))
                Add(c, "construct", site.At);
        // Ремонт: работа идёт над тем, на что смотрит ремонтник (Repair.target), — сварка в середине цели.
        foreach (var entity in c.Each(_repair!))
            if ((uint)N(c, entity, _repair!, "target", 0) is var target and not 0 && c.Alive(target))
                Add(c, "weld", Center(c, target));
        foreach (var entity in c.Each(_beam!))
            if (c.Get(entity, _beam!) is { } beam && beam.Bytes(_beam!["links"]).Length > 0 && c.Pose(entity) is { } place)
                Add(c, "tractor", (place.X, place.Y));
        foreach (var spot in _servos)
            Add(c, "servo", spot);
        foreach (var entity in c.Each(_shield!))
            if (N(c, entity, _shield!, "charge", 0) > 0)
                Add(c, "dome", Center(c, entity));
        var launchTicks = Math.Round(_data!.Launch / c.TickDt);
        foreach (var entity in c.Each(_silo!))
        {
            var start = N(c, entity, _silo!, "start", -1);
            if (start < 0)
                continue;
            var share = (tick - start) / launchTicks;
            var at = Center(c, entity);
            if (share < Doors)
                Add(c, "hydraulic", at);
            if (share >= Steam)
                Add(c, "steam", at, (share - Steam) / (1 - Steam));
            if (share >= Rise)
                Add(c, "rocket", at, Math.Pow((share - Rise) / (1 - Rise), 2));
        }
        foreach (var (_, at) in _packing)
            Add(c, "hydraulic", at, 0.6);
        // Трубы: груз в пути звучит там, докуда дотёк.
        foreach (var entity in c.Each(_batch!))
        {
            if (c.Get(entity, _batch!) is not { } batch)
                continue;
            var sent = batch.Number(_batch!["sent"]);
            var share = (tick - sent) / Math.Max(1, batch.Number(_batch["arrive"]) - sent);
            if (share < 0 || share > 1)
                continue;
            var path = batch.Bytes(_batch["path"]);
            var points = path.Length / 8;
            if (points == 0)
                continue;
            var index = Math.Min(points - 1, (int)Math.Floor(share * points));
            Add(c, "flow", (BitConverter.ToInt32(path, index * 8) + 0.5, BitConverter.ToInt32(path, index * 8 + 4) + 0.5));
        }
        foreach (var entity in c.Each(_missile!))
            if (c.Get(entity, _missile!) is { } missile)
                Add(c, "rocket", (missile.Number(_missile!["x"]), missile.Number(_missile["y"])));
        foreach (var entity in c.Each(_chaser!))
        {
            if (c.Get(entity, _chaser!) is not { } chaser)
                continue;
            var (born, arrive) = (chaser.Number(_chaser!["born"]), chaser.Number(_chaser["arrive"]));
            var share = Math.Clamp((tick - born) / Math.Max(1, arrive - born), 0, 1);
            var (fromX, fromY) = (chaser.Number(_chaser["fromX"]), chaser.Number(_chaser["fromY"]));
            Add(c, "rocket", (fromX + (chaser.Number(_chaser["toX"]) - fromX) * share, fromY + (chaser.Number(_chaser["toY"]) - fromY) * share), 0.5);
        }

        foreach (var (name, level) in Levels)
        {
            var (total, side) = _sums.TryGetValue(name, out var known) ? known : (0.0, 0.0);
            c.Audio.Loop(name, (float)(level * (1 - Math.Exp(-total / Saturate))), total > 0 ? (float)(side / total) : 0);
        }
    }
}
