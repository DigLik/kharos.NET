using Base.Client.Audio;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Одно уведомление ленты (Alert в alerts.ts): вид, ключ строки hud.alerts.*, место в тайлах, час появления.</summary>
internal sealed record Alert(int Id, string Kind, string Text, double X, double Y, double At);

/// <summary>
/// Уведомления игрока — перенос createAlerts из game/alerts.ts. Раз в кадр смотрит на реплику: у своего юнита или здания
/// убыло прочности, а рядом упал чужой выстрел — «атакованы» (раз; снова — когда враги пропали из виду и напали опять);
/// в виду появился чужой юнит — «замечен противник», не чаще раза в 12 секунд; игрок узнал новое месторождение — «найдено»,
/// не чаще раза в 6 секунд, а известные при входе в мир — не новость (первый просмотр слоя — исходное знание: слой
/// приходит вместе со снимком мира, «в пути» его не бывает). Чужая ядерная ракета в воздухе — сирена всем; чужая метка
/// точки удара — первый сигнал, за WARNING секунд до удара — второй. Свой или союзный перехватчик выпустил ракету —
/// «перехвачена»; своя ядерная пропала, не долетев, — «сбита». Сигнал союзника (метка ping) — «сигнал».
/// </summary>
internal sealed class AlertWatch(LayerRef? deposits)
{
    private const double SpottedGap = 12, FoundGap = 6, HitReach = 3, HitMemory = 1.5, DownedReach = 2, Volume = 0.7, ScanGap = 0.25;

    /// <summary>Сколько секунд строка висит в ленте и метка мигает на мини-карте (ALERT_SECONDS).</summary>
    public const double Seconds = 8;

    private const int MaxAlerts = 4;

    private readonly Teams _teams = new();
    private readonly List<Alert> _alerts = [];
    private readonly Dictionary<uint, (double X, double Y, double At)> _impacts = [];
    private readonly Dictionary<uint, (double X, double Y, double ToX, double ToY)> _flights = [];
    private Dictionary<uint, double> _health = [];
    private HashSet<uint> _foes = [], _marks = [], _imminent = [], _launches = [], _chasers = [];
    private HashSet<(int, int)>? _known;
    private ClientContext? _owner;
    private ulong _lastTick;
    private double _clock, _spottedAt = double.NegativeInfinity, _foundAt = double.NegativeInfinity, _scanAt = double.NegativeInfinity;
    private ulong _layers = ulong.MaxValue;
    private bool _warned, _ready;
    private int _next;
    private double _speed = 1;

    private ComponentRef _unit = null!, _ownerRef = null!, _mark = null!, _chaser = null!, _missile = null!, _shot = null!, _life = null!, _footprint = null!;

    /// <summary>Уведомления, что ещё видны, от старых к новым.</summary>
    public IReadOnlyList<Alert> Current => _alerts;

    /// <summary>Часы уведомлений, секунды.</summary>
    public double Clock => _clock;

    private bool Init(ClientContext c)
    {
        try
        {
            _unit = c.Component("Unit");
            _ownerRef = c.Component("Owner");
            _mark = c.Component("Mark");
            _chaser = c.Component("Chaser");
            _missile = c.Component("Missile");
            _shot = c.Component("Shot");
            _life = c.Component("Health");
            _footprint = c.Component("Footprint");
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
        var tables = BaseTables.Load(name => c.TryData(name, out var bytes) ? bytes.ToArray() : null);
        _speed = tables.Buildings.GetValueOrDefault("silo")?.Warhead?.Speed ?? 1;
        return true;
    }

    private void Reset()
    {
        _alerts.Clear();
        _impacts.Clear();
        _flights.Clear();
        _health = [];
        _foes = [];
        _marks = [];
        _imminent = [];
        _launches = [];
        _chasers = [];
        _known = null;
        _layers = ulong.MaxValue;
        _warned = false;
        _scanAt = _spottedAt = _foundAt = double.NegativeInfinity;
    }

    private void Raise(ClientContext c, string kind, string text, double x, double y, string? sound = null, float volume = (float)Volume)
    {
        _alerts.Add(new Alert(_next++, kind, text, x, y, _clock));
        if (_alerts.Count > MaxAlerts)
            _alerts.RemoveAt(0);
        c.Sound(sound ?? kind, volume, 0);
    }

    private static double Num(ClientContext c, uint entity, ComponentRef component, string field) =>
        c.Get(entity, component) is { } values ? values.Number(component[field]) : double.NaN;

    public void Update(ClientContext c)
    {
        if (!_ready)
            _ready = Init(c);
        if (!_ready)
            return;
        if (!ReferenceEquals(_owner, c) || c.Tick < _lastTick)
        {
            _owner = c;
            Reset();
        }
        _lastTick = c.Tick;
        _clock += c.Dt;
        _teams.Refresh(c);

        var spotted = Foes(c);
        Marks(c);
        Chasers(c);
        Rockets(c);
        if (spotted is { } spot && _clock - _spottedAt >= SpottedGap)
        {
            _spottedAt = _clock;
            Raise(c, "spotted", "spotted", spot.X, spot.Y);
        }
        Deposits(c);
        Attacks(c);
        _alerts.RemoveAll(alert => _clock - alert.At > Seconds);
    }

    /// <summary>Чужие юниты в виду (хост шлёт только видимых); возвращает место первого нового.</summary>
    private (double X, double Y)? Foes(ClientContext c)
    {
        (double X, double Y)? spotted = null;
        var next = new HashSet<uint>();
        foreach (var entity in c.Each(_unit, _ownerRef))
        {
            if (!_teams.Foe(c, (uint)Num(c, entity, _ownerRef, "player")))
                continue;
            next.Add(entity);
            if (!_foes.Contains(entity) && spotted is null && c.Pose(entity) is { } at)
                spotted = (at.X, at.Y);
        }
        _foes = next;
        if (_foes.Count == 0)
            _warned = false;
        return spotted;
    }

    private void Marks(ClientContext c)
    {
        var next = new HashSet<uint>();
        var imminent = new HashSet<uint>();
        foreach (var entity in c.Each(_mark))
        {
            if (c.Get(entity, _mark) is not { } mark || c.Pose(entity) is not { } at)
                continue;
            next.Add(entity);
            var kind = HudParts.Str(mark, _mark, "kind");
            var player = (uint)mark.Number(_mark["player"]);
            if (kind == "ping")
            {
                if (!_marks.Contains(entity))
                    Raise(c, "ping", player == c.Player ? "ownPing" : "allyPing", at.X, at.Y);
                continue;
            }
            // Место пуска — только метка на карте; свою и союзную точку удара сиреной не встречают.
            if (kind != "strike" || player == c.Player || _teams.Allied(player))
                continue;
            var close = (mark.Number(_mark["until"]) - c.Tick) * c.TickDt <= MissileRules.Warning;
            if (close)
            {
                imminent.Add(entity);
                if (!_imminent.Contains(entity))
                    Raise(c, "nuke", "nukeImminent", at.X, at.Y, "nukeClose", 1);
            }
            else if (!_marks.Contains(entity))
                Raise(c, "nuke", "nukeIncoming", at.X, at.Y);
        }
        _marks = next;
        _imminent = imminent;
    }

    private void Chasers(ClientContext c)
    {
        var next = new HashSet<uint>();
        foreach (var entity in c.Each(_chaser))
        {
            next.Add(entity);
            if (_chasers.Contains(entity) || c.Get(entity, _chaser) is not { } chaser)
                continue;
            var player = (uint)chaser.Number(_chaser["player"]);
            if (player == c.Player || _teams.Allied(player))
                Raise(c, "intercepted", "nukeIntercepted", chaser.Number(_chaser["toX"]), chaser.Number(_chaser["toY"]));
        }
        _chasers = next;
    }

    private void Rockets(ClientContext c)
    {
        var next = new Dictionary<uint, (double X, double Y, double ToX, double ToY)>();
        var launches = new HashSet<uint>();
        foreach (var entity in c.Each(_missile))
        {
            if (c.Get(entity, _missile) is not { } m)
                continue;
            double fromX = m.Number(_missile["fromX"]), fromY = m.Number(_missile["fromY"]);
            double toX = m.Number(_missile["toX"]), toY = m.Number(_missile["toY"]);
            var player = (uint)m.Number(_missile["player"]);
            var friend = player == c.Player || _teams.Allied(player);
            // Чужая ракета приходит без точки удара (NaN или нули), только с курсом: летит слепая.
            var blind = m.Flag(_missile["blind"]) || double.IsNaN(toX) || double.IsNaN(toY) || (!friend && toX == 0 && toY == 0);
            if (blind)
            {
                launches.Add(entity);
                if (!_launches.Contains(entity))
                    Raise(c, "nuke", "nukeLaunched", fromX, fromY);
                continue;
            }
            var flight = new global::Base.Client.Missiles.Flight(fromX, fromY, toX, toY, m.Number(_missile["born"]), false);
            var (x, y, _, _, _) = global::Base.Client.Missiles.At(flight, c.Clock, c.TickDt, _speed);
            next[entity] = (x, y, toX, toY);
        }
        // Своя ракета пропала далеко от точки удара: сбита. Долетевшая пропадает в самой точке.
        foreach (var (entity, flight) in _flights)
            if (!next.ContainsKey(entity) && Math.Sqrt((flight.ToX - flight.X) * (flight.ToX - flight.X) + (flight.ToY - flight.Y) * (flight.ToY - flight.Y)) > DownedReach)
                Raise(c, "nuke", "nukeDowned", flight.X, flight.Y);
        _flights.Clear();
        foreach (var pair in next)
            _flights[pair.Key] = pair.Value;
        _launches = launches;
    }

    /// <summary>Месторождения: знакомые при входе в мир — не новость, новое — «найдено» (не чаще FoundGap).</summary>
    private void Deposits(ClientContext c)
    {
        if (deposits is null || _clock - _scanAt < ScanGap)
            return;
        var revision = Tools.Revision().Layers;
        if (_known is not null && revision == _layers)
            return;
        _scanAt = _clock;
        _layers = revision;
        var known = new HashSet<(int, int)>();
        MapItem? fresh = null;
        foreach (var item in Tools.Items(deposits, 0, 0, 0, 1e7))
        {
            var key = ((int)Math.Floor(item.X), (int)Math.Floor(item.Y));
            known.Add(key);
            if (_known is not null && !_known.Contains(key))
                fresh ??= item;
        }
        _known = known;
        if (fresh is not null && _clock - _foundAt >= FoundGap)
        {
            _foundAt = _clock;
            Raise(c, "found", "oreFound", fresh.X + KnownReserve.Size / 2.0, fresh.Y + KnownReserve.Size / 2.0);
        }
    }

    /// <summary>Атака — урон, рядом с которым недавно упал чужой выстрел; прочность падает и без врага (нет энергии).</summary>
    private void Attacks(ClientContext c)
    {
        foreach (var entity in c.Each(_shot))
            if (!_impacts.ContainsKey(entity) && c.Get(entity, _shot) is { } shot)
            {
                var player = (uint)shot.Number(_shot["player"]);
                if (player != c.Player && player != 0)
                    _impacts[entity] = (shot.Number(_shot["toX"]), shot.Number(_shot["toY"]), _clock);
            }
        foreach (var (entity, impact) in _impacts.ToArray())
            if (_clock - impact.At > HitMemory)
                _impacts.Remove(entity);

        bool ShotAt(double x, double y, double width, double height)
        {
            foreach (var impact in _impacts.Values)
            {
                var dx = Math.Max(Math.Max(x - impact.X, 0), impact.X - (x + width));
                var dy = Math.Max(Math.Max(y - impact.Y, 0), impact.Y - (y + height));
                if (Math.Sqrt(dx * dx + dy * dy) <= HitReach)
                    return true;
            }
            return false;
        }

        var next = new Dictionary<uint, double>();
        uint? hurt = null;
        foreach (var entity in c.Each(_life, _ownerRef))
        {
            if (!c.Mine(entity) || c.Get(entity, _life) is not { } life)
                continue;
            var value = life.Number(_life["value"]);
            next[entity] = value;
            if (hurt is not null || !_health.TryGetValue(entity, out var before) || value >= before - 1e-6 || c.Pose(entity) is not { } at)
                continue;
            var (width, height) = c.Get(entity, _footprint) is { } f ? (f.Number(_footprint["w"]), f.Number(_footprint["h"])) : (0.0, 0.0);
            if (ShotAt(at.X, at.Y, width, height))
                hurt = entity;
        }
        _health = next;
        if (hurt is not { } who || _warned || c.Pose(who) is not { } place)
            return;
        _warned = true;
        var building = c.Get(who, _footprint) is not null;
        var (cx, cy) = c.Get(who, _footprint) is { } size ? (place.X + size.Number(_footprint["w"]) / 2, place.Y + size.Number(_footprint["h"]) / 2) : (place.X, place.Y);
        Raise(c, "attacked", building ? "baseAttacked" : "unitsAttacked", cx, cy);
    }
}
