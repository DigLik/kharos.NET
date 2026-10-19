using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Звуки боя — перенос game/soundscape.ts: каждый новый выстрел и взрыв в реплике получает голос; громкость — по тому,
/// насколько он далеко от середины экрана, сторона — по тому, левее он или правее. Лучевое оружие бьёт сразу — его
/// попадание звучит само. Погибшее (пропало в тот же тик, что взрыв рядом) скрежещет или обваливается. Лимит голосов —
/// MinGap: сотня винтовок звучит как одна очередь. Заодно считает обстановку для фона (<see cref="Mood"/>): тревогу —
/// противники в виду и вражеская метка удара — и бой — частоту выстрелов своих и чужих; успех — отбитая атака. Тряска
/// камеры от взрыва — дело камеры.
/// </summary>
internal sealed class Battle(Mood mood, Teams teams)
{
    private readonly record struct ShotSound(string Name, double Volume);

    private static readonly Dictionary<string, ShotSound> Shots = new(StringComparer.Ordinal)
    {
        ["rifle"] = new("rifle", 0.35), ["machinegun"] = new("machinegun", 0.25), ["cannon"] = new("cannon", 0.8),
        ["artillery"] = new("artillery", 1), ["bomb"] = new("bombFall", 0.5), ["launcher"] = new("launcher", 0.45),
        ["laser"] = new("laser", 0.35), ["arc"] = new("arc", 0.5), ["flame"] = new("flame", 0.4), ["flak"] = new("flak", 0.45),
        ["coreGun"] = new("laser", 0.6),
    };

    // Лучевое оружие бьёт сразу и без взрыва: попадание звучит само — лазер прожигает, огонь потрескивает, разряд щёлкает.
    private static readonly Dictionary<string, string> HitSounds = new(StringComparer.Ordinal)
    {
        ["laser"] = "sizzle", ["coreGun"] = "sizzle", ["flame"] = "burn", ["arc"] = "zap",
    };

    private static readonly Dictionary<string, double> MinGap = new(StringComparer.Ordinal)
    {
        ["rifle"] = 0.04, ["machinegun"] = 0.035, ["cannon"] = 0.08, ["launcher"] = 0.06, ["laser"] = 0.06, ["arc"] = 0.08, ["blast"] = 0.05,
        ["bigBlast"] = 0.1, ["flame"] = 0.12, ["flak"] = 0.06, ["artillery"] = 0.1, ["bombFall"] = 0.15, ["ricochet"] = 0.05, ["wreck"] = 0.1,
        ["collapse"] = 0.2, ["sizzle"] = 0.06, ["burn"] = 0.15, ["zap"] = 0.05,
    };

    private const double BeamHitVolume = 0.3;
    // Взрыв меньше SmallBlast — попадание пули, оно щёлкает тихо; от BigBlast — гибель техники или здания.
    private const double SmallBlast = 0.3, HitVolume = 0.12, BigBlast = 1.3;
    // Погибший — то, что пропало в кадр взрыва не дальше этого от него, в тайлах, или в его размер.
    private const double DeathReach = 1;
    private const double WreckVolume = 0.6, CollapseVolume = 0.8;
    private const double BlastVolume = 1, BlastVolumePerSize = 0.6;
    // За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана.
    private const double Hearing = 0.8;
    // Сколько противников в виду — уже сильная тревога: при стольких она на двух третях.
    private const double ThreatFoes = 6;
    // Бой: за столько секунд счёт выстрелов тает на две трети; при стольких выстрелах в памяти бой на двух третях.
    private const double ShotsMemory = 3, BattleShots = 12;
    // Атака отбита: был бой, а потом столько секунд ни противника в виду, ни выстрелов.
    private const double RepelledQuiet = 12;

    private enum Body { Building, Machine, Soldier }

    private readonly record struct Spot(double X, double Y, Body Kind);

    private readonly Dictionary<string, double> _last = new(StringComparer.Ordinal);
    private Dictionary<uint, double> _shots = [], _blasts = [];
    private Dictionary<uint, Spot> _bodies = [];
    private ComponentRef? _shot, _blast, _unit, _building, _owner, _footprint;
    private KindData? _data;
    private double _clock, _recent, _quietSince;
    private ulong _tick = ulong.MaxValue;
    private int _foes;
    private bool _fought;
    private readonly Dictionary<int, string?> _weapons = [];

    private void Play(ClientContext c, string name, double volume, double x, double y)
    {
        var level = volume * c.Nearness(x, y, (float)Hearing);
        if (level <= 0.01 || _clock - _last.GetValueOrDefault(name, double.NegativeInfinity) < MinGap.GetValueOrDefault(name))
            return;
        _last[name] = _clock;
        c.Sound(name, (float)level, c.Side(x) * 0.8f);
    }

    private string? WeaponOf(ClientContext c, uint entity)
    {
        var proto = c.Proto(entity);
        if (!_weapons.TryGetValue(proto, out var name))
        {
            var path = proto >= 0 ? c.ProtoPath(proto) : null;
            _weapons[proto] = name = path is not null && path.StartsWith("weapons/", StringComparison.Ordinal) ? path[8..] : null;
        }
        return name;
    }

    private static double[] Col(ClientContext c, uint[] entities, ComponentRef component, string field)
    {
        var column = new double[entities.Length];
        if (entities.Length > 0)
            c.Column(entities, component, field, column);
        return column;
    }

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        if (_shot is null)
        {
            _shot = c.Component("Shot");
            _blast = c.Component("Blast");
            _unit = c.Component("Unit");
            _building = c.Component("Building");
            _owner = c.Component("Owner");
            _footprint = c.Component("Footprint");
            _data = KindData.Read(c);
        }
        _clock += c.Dt;
        _recent *= Math.Exp(-c.Dt / ShotsMemory);
        if (c.Tick != _tick)
        {
            _tick = c.Tick;
            Tick(c);
        }

        // Тревога — противник в виду (хост шлёт только видимое), а летящая на нас ракета — тревога целиком.
        mood.Threat = mood.Struck ? 1 : 1 - Math.Exp(-_foes / ThreatFoes);
        mood.Battle = 1 - Math.Exp(-_recent / BattleShots);

        // Успех: отбита атака (открытая тема — в WorldEvents).
        if (mood.Battle > 0.5 && _foes > 0)
            _fought = true;
        if (_foes > 0 || mood.Battle > 0.1)
            _quietSince = _clock;
        else if (_fought && _clock - _quietSince >= RepelledQuiet)
        {
            _fought = false;
            mood.Triumphs++;
        }
    }

    /// <summary>Что нового в реплике на этом тике: выстрелы, взрывы, погибшие и противники в виду.</summary>
    private void Tick(ClientContext c)
    {
        // Выстрелы: новый — то, чего не было, или моложе прежнего с тем же номером. Перескоки разряда — те же выстрелы
        // в тот же миг: их глушит MinGap.
        var shots = c.Each(_shot!);
        var ages = Col(c, shots, _shot!, "age");
        var nextShots = new Dictionary<uint, double>(shots.Length);
        if (shots.Length > 0)
        {
            var players = Col(c, shots, _shot!, "player");
            var fromX = Col(c, shots, _shot!, "fromX");
            var fromY = Col(c, shots, _shot!, "fromY");
            var toX = Col(c, shots, _shot!, "toX");
            var toY = Col(c, shots, _shot!, "toY");
            var blocked = Col(c, shots, _shot!, "blocked");
            for (var i = 0; i < shots.Length; i++)
            {
                nextShots[shots[i]] = ages[i];
                if (_shots.TryGetValue(shots[i], out var age) && ages[i] >= age)
                    continue;
                var player = (uint)players[i];
                if (player == c.Player || teams.Foe(c, player))
                    _recent++;
                if (WeaponOf(c, shots[i]) is not { } weapon || !Shots.TryGetValue(weapon, out var sound))
                    continue;
                Play(c, sound.Name, sound.Volume, fromX[i], fromY[i]);
                if (HitSounds.TryGetValue(weapon, out var hit) && blocked[i] == 0)
                    Play(c, hit, BeamHitVolume, toX[i], toY[i]);
            }
        }
        _shots = nextShots;

        // Погибшее пропадает в тот же тик, что появляется взрыв на его месте.
        var nextBodies = new Dictionary<uint, Spot>();
        var units = c.Each(_unit!, _owner!);
        var spots = new (double X, double Y, double Angle)[units.Length];
        if (units.Length > 0)
            c.Poses(units, spots);
        var owners = Col(c, units, _owner!, "player");
        var foes = 0;
        for (var i = 0; i < units.Length; i++)
        {
            if (teams.Foe(c, (uint)owners[i]))
                foes++;
            if (double.IsNaN(spots[i].X))
                continue;
            var infantry = c.ProtoPath(c.Proto(units[i])) is { } path && _data!.UnitKinds.GetValueOrDefault(path) == "infantry";
            nextBodies[units[i]] = new Spot(spots[i].X, spots[i].Y, infantry ? Body.Soldier : Body.Machine);
        }
        _foes = foes;
        var buildings = c.Each(_building!, _footprint!);
        if (buildings.Length > 0)
        {
            var places = new (double X, double Y, double Angle)[buildings.Length];
            c.Poses(buildings, places);
            var widths = Col(c, buildings, _footprint!, "w");
            var heights = Col(c, buildings, _footprint!, "h");
            for (var i = 0; i < buildings.Length; i++)
                if (!double.IsNaN(places[i].X))
                    nextBodies[buildings[i]] = new Spot(places[i].X + widths[i] / 2, places[i].Y + heights[i] / 2, Body.Building);
        }
        var fallen = new List<Spot>();
        foreach (var (entity, body) in _bodies)
            if (!nextBodies.ContainsKey(entity))
                fallen.Add(body);
        _bodies = nextBodies;

        var blasts = c.Each(_blast!);
        var nextBlasts = new Dictionary<uint, double>(blasts.Length);
        if (blasts.Length > 0)
        {
            var blastAges = Col(c, blasts, _blast!, "age");
            var sizes = Col(c, blasts, _blast!, "size");
            var places = new (double X, double Y, double Angle)[blasts.Length];
            c.Poses(blasts, places);
            for (var i = 0; i < blasts.Length; i++)
            {
                nextBlasts[blasts[i]] = blastAges[i];
                if (_blasts.TryGetValue(blasts[i], out var age) && blastAges[i] >= age)
                    continue;
                var (x, y, size) = (places[i].X, places[i].Y, sizes[i]);
                if (double.IsNaN(x))
                    continue;
                if (size < SmallBlast)
                {
                    Play(c, "ricochet", HitVolume, x, y);
                    continue;
                }
                Play(c, size >= BigBlast ? "bigBlast" : "blast", BlastVolume + size * BlastVolumePerSize, x, y);
                var reach = Math.Max(DeathReach, size);
                foreach (var body in fallen)
                {
                    if (Math.Sqrt((body.X - x) * (body.X - x) + (body.Y - y) * (body.Y - y)) > reach)
                        continue;
                    if (body.Kind == Body.Building)
                        Play(c, "collapse", CollapseVolume + size * 0.2, x, y);
                    else if (body.Kind == Body.Machine)
                        Play(c, "wreck", WreckVolume, x, y);
                    break;
                }
            }
        }
        _blasts = nextBlasts;
    }
}
