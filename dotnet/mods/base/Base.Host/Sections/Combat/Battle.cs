using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Combat.Refs;

namespace Base.Host.Sections.Combat;

/// <summary>Стойка юнита (Stance в tactics.ts): без тактики — оборона.</summary>
internal enum Stance
{
    Defensive,
    Aggressive,
    Hold,
    Passive,
}

/// <summary>
/// Бой одного тика (<see cref="Fight"/>): снимок мира из таблиц кадра, вся работа боя над ним и запись итога. Экземпляр
/// живёт с системой и переиспользует память; между тиками ничего не помнит (всё собирается заново).
/// </summary>
internal sealed partial class Battle
{
    // ===== Константы combat.ts =====

    /// <summary>Раз во сколько тиков свободный высматривает врага, а гонящийся прокладывает путь заново.</summary>
    private const int ScanTicks = 5;
    /// <summary>На столько тайлов дальше считается безобидная цель.</summary>
    private const double Harmless = 1000;
    private const int ChaseTicks = 20;
    private const double ChaseMargin = 1.5;
    /// <summary>Надбавка дальности оружию на здании (TOWER_RANGE).</summary>
    public const double TowerRange = 2;
    private const int ChaseSearches = 16;
    private const uint ChaseTiles = 15000;
    /// <summary>Норма поиска путей погони на тик (pool инструмента Plan).</summary>
    private const uint ChasePool = 0xC0BA7;
    private const int ShooterStride = 17;
    private const int StuckLimit = 3;
    private const double Aim = 0.12;
    private const int BeamTicks = 3;
    private const double ChainRange = 2.5, ChainDecay = 0.6;
    private const double Overfly = 1.5;
    private const double SplashEdge = 0.5;
    private const double SlotMargin = 0.5, SlotReach = 0.75, SlotReplan = 4;

    /// <summary>Ближе стольких тайлов к точке своего места гонящийся считается на ней: доезжать незачем.</summary>
    private const double SlotSnap = 0.05;
    private const int SlotPatience = 10;
    /// <summary>Учебный противник (TRAINING_PLAYER).</summary>
    public const uint TrainingPlayer = 9999;

    // ===== Столбцы =====

    private Column _target, _chase, _cooldown, _stuck, _ordered, _sx, _sy;
    private Column _value, _hit, _mx, _my, _owner;
    private Column _unitProto, _unitFacing;
    private Column _buildingProto, _buildingW, _buildingH, _buildingOpen;
    private Column _mountParent, _mountAngle, _mountProto;
    private Column _shotPlayer, _shotSource, _shotTarget, _shotFromX, _shotFromY, _shotToX, _shotToY, _shotAge, _shotLife, _shotBlocked, _shotAir,
        _shotX, _shotY, _shotProto;
    private Column _blastAge, _blastLife;
    private Column _burstRadius, _burstDamage, _burstCore, _burstX, _burstY;
    private Column _doomedLeft;
    private Column _pathGoalX, _pathGoalY, _pathWait, _pathSlot;
    private Column _stance, _away, _homeX, _homeY, _resupply;
    private Column _playerId, _playerSafe, _teamHead;

    public void Bind(SystemBinding binding)
    {
        _target = binding.Column(Names.Armed.Name, Names.Armed.Target);
        _chase = binding.Column(Names.Armed.Name, Names.Armed.Chase);
        _cooldown = binding.Column(Names.Armed.Name, Names.Armed.Cooldown);
        _stuck = binding.Column(Names.Armed.Name, Names.Armed.Stuck);
        _ordered = binding.Column(Names.Armed.Name, Names.Armed.Ordered);
        _sx = binding.Column(Names.Position.Name, Names.Position.X);
        _sy = binding.Column(Names.Position.Name, Names.Position.Y);
        PartBinding T(int i) => binding.Table(i);
        _value = T(Fight.MarksT).Column(Names.Health.Name, Names.Health.Value);
        _hit = T(Fight.MarksT).Column(Names.Health.Name, Names.Health.Hit);
        _mx = T(Fight.MarksT).Column(Names.Position.Name, Names.Position.X);
        _my = T(Fight.MarksT).Column(Names.Position.Name, Names.Position.Y);
        _owner = T(Fight.MarksT).Column(Names.Owner.Name, Names.Owner.Player);
        _unitProto = T(Fight.UnitsT).Column(Names.Proto.Name, Names.Proto.Id);
        _unitFacing = T(Fight.UnitsT).Column(Names.Facing.Name, Names.Facing.Angle);
        _buildingProto = T(Fight.BuildingsT).Column(Names.Proto.Name, Names.Proto.Id);
        _buildingW = T(Fight.BuildingsT).Column(Names.Footprint.Name, Names.Footprint.W);
        _buildingH = T(Fight.BuildingsT).Column(Names.Footprint.Name, Names.Footprint.H);
        _buildingOpen = T(Fight.BuildingsT).Column(Names.Footprint.Name, Names.Footprint.Open);
        _mountParent = T(Fight.MountsT).Column(Names.Mount.Name, Names.Mount.Parent);
        _mountAngle = T(Fight.MountsT).Column(Names.Mount.Name, Names.Mount.Angle);
        _mountProto = T(Fight.MountsT).Column(Names.Proto.Name, Names.Proto.Id);
        var shots = T(Fight.ShotsT);
        _shotPlayer = shots.Column(Names.Shot.Name, Names.Shot.Player);
        _shotSource = shots.Column(Names.Shot.Name, Names.Shot.Source);
        _shotTarget = shots.Column(Names.Shot.Name, Names.Shot.Target);
        _shotFromX = shots.Column(Names.Shot.Name, Names.Shot.FromX);
        _shotFromY = shots.Column(Names.Shot.Name, Names.Shot.FromY);
        _shotToX = shots.Column(Names.Shot.Name, Names.Shot.ToX);
        _shotToY = shots.Column(Names.Shot.Name, Names.Shot.ToY);
        _shotAge = shots.Column(Names.Shot.Name, Names.Shot.Age);
        _shotLife = shots.Column(Names.Shot.Name, Names.Shot.Life);
        _shotBlocked = shots.Column(Names.Shot.Name, Names.Shot.Blocked);
        _shotAir = shots.Column(Names.Shot.Name, Names.Shot.Air);
        _shotX = shots.Column(Names.Position.Name, Names.Position.X);
        _shotY = shots.Column(Names.Position.Name, Names.Position.Y);
        _shotProto = shots.Column(Names.Proto.Name, Names.Proto.Id);
        _blastAge = T(Fight.BlastsT).Column(Names.Blast.Name, Names.Blast.Age);
        _blastLife = T(Fight.BlastsT).Column(Names.Blast.Name, Names.Blast.Life);
        _burstRadius = T(Fight.BurstsT).Column(Names.Burst.Name, Names.Burst.Radius);
        _burstDamage = T(Fight.BurstsT).Column(Names.Burst.Name, Names.Burst.Damage);
        _burstCore = T(Fight.BurstsT).Column(Names.Burst.Name, Names.Burst.Core);
        _burstX = T(Fight.BurstsT).Column(Names.Position.Name, Names.Position.X);
        _burstY = T(Fight.BurstsT).Column(Names.Position.Name, Names.Position.Y);
        _doomedLeft = T(Fight.DoomedT).Column(Names.Doomed.Name, Names.Doomed.Left);
        _pathGoalX = T(Fight.PathsT).Column(Names.Path.Name, Names.Path.GoalX);
        _pathGoalY = T(Fight.PathsT).Column(Names.Path.Name, Names.Path.GoalY);
        _pathWait = T(Fight.PathsT).Column(Names.Path.Name, Names.Path.Wait);
        _pathSlot = T(Fight.PathsT).Column(Names.Path.Name, Names.Path.Slot);
        _stance = T(Fight.TacticsT).Column(Names.Tactics.Name, Names.Tactics.Stance);
        _away = T(Fight.TacticsT).Column(Names.Tactics.Name, Names.Tactics.Away);
        _homeX = T(Fight.TacticsT).Column(Names.Tactics.Name, Names.Tactics.HomeX);
        _homeY = T(Fight.TacticsT).Column(Names.Tactics.Name, Names.Tactics.HomeY);
        _resupply = T(Fight.TacticsT).Column(Names.Tactics.Name, Names.Tactics.Resupply);
        _playerId = T(Fight.PlayersT).Column(Names.Player.Name, Names.Player.Id);
        _playerSafe = T(Fight.PlayersT).Column(Names.Player.Name, Names.Player.Safe);
        _teamHead = T(Fight.PlayersT).Column(Names.Team.Name, Names.Team.Head);
    }

    // ===== Снимок тика =====

    private Call _call = null!;
    private ulong _tick;
    private double _step;

    /// <summary>Метки по номерам и по порядку (юниты, за ними здания — как collectMarks).</summary>
    private readonly List<Mark> _pool = [];
    private readonly List<Mark> _marks = [];
    private readonly EntityIndex _markOf = new();
    private readonly MarkGrid _grid = new();
    private readonly List<Mark> _dead = [];

    /// <summary>Стены по тайлам: на линии огня принимают выстрел.</summary>
    private readonly Dictionary<long, Mark> _walls = [];

    /// <summary>Игроки под защитой новичка и головы союзов.</summary>
    private readonly HashSet<uint> _safe = [];
    private readonly Dictionary<uint, double> _heads = [];

    /// <summary>Стрелки: строки кадра, по возрастанию номеров.</summary>
    private uint[] _armEntity = [];
    private double[] _armTarget = [], _armCooldown = [], _armStuck = [], _armX = [], _armY = [];
    private bool[] _armChase = [], _armOrdered = [];
    private int[] _shooters = [];
    private int _armCount;
    private readonly EntityIndex _armRow = new();

    /// <summary>Турели: носитель, поворот, вид; турели носителя по возрастанию номеров.</summary>
    private readonly EntityIndex _mountRow = new();
    private uint[] _mountParentOf = [];
    private double[] _mountAngleOf = [];
    private int[] _mountProtoOf = [];
    private bool[] _mountTurned = [];
    private uint[] _mountEntity = [];
    private int _mountCount;
    private readonly Dictionary<uint, List<uint>> _turrets = [];
    private readonly Stack<List<uint>> _freeLists = [];

    /// <summary>Пути (Path): цель, ожидание, к месту ли у цели. Свои снятия и новые — поверх.</summary>
    private readonly Dictionary<uint, PathState> _paths = [];

    private readonly HashSet<uint> _converting = [], _off = [];

    /// <summary>Тактика (Tactics): стойка, поход, место, за боеприпасами ли; патруль — лениво.</summary>
    private readonly Dictionary<uint, TacticsState> _tactics = [];
    private bool _patrolsRead;
    private readonly Dictionary<double, Stance> _stances = [];

    private sealed class PathState
    {
        public int GoalX, GoalY;
        public double Wait;
        public bool Slot;
    }

    private sealed class TacticsState
    {
        public Stance Stance;
        public bool Away, Resupply;
        public double HomeX, HomeY;
        public int Patrol;
    }

    public void Run(SystemContext ctx)
    {
        _call = Call.Of(ctx);
        _tick = ctx.Tick;
        _step = Core.Step;
        Read(ctx);
        _shields = Hooks.Shields?.Invoke(_call);
        if (_shields is { Any: false })
            _shields = null;
        Age(ctx);
        Fly(ctx);
        Shoot();
        Doom(ctx);
        Bursts(ctx);
        Swamp();
        Die();
        Write(ctx);
    }

    // ===== Чтение =====

    private void Read(SystemContext ctx)
    {
        _used = 0;
        _markOf.Begin();
        _armRow.Begin();
        _mountRow.Begin();
        _grid.Clear();
        _marks.Clear();
        _dead.Clear();
        _walls.Clear();
        _safe.Clear();
        _heads.Clear();
        foreach (var list in _turrets.Values)
        {
            list.Clear();
            _freeLists.Push(list);
        }
        _turrets.Clear();
        _paths.Clear();
        _converting.Clear();
        _off.Clear();
        _tactics.Clear();
        _patrolsRead = false;
        _stances.Clear();
        _blasts.Clear();
        _gone.Clear();
        _pursuers.Clear();
        _slots.Clear();
        _chasing.Clear();
        _walkable.Clear();
        _searches = 0;

        var players = ctx.Table(Fight.PlayersT);
        var ids = players.Column(_playerId);
        var safe = players.Column(_playerSafe);
        var heads = players.Column(_teamHead);
        for (var row = 0; row < players.Count; row++)
        {
            var id = (uint)ids[row];
            if (safe[row] > _tick)
                _safe.Add(id);
            _heads[id] = heads[row];
        }

        // Метки: юниты, потом здания, каждые по возрастанию номеров.
        var table = ctx.Table(Fight.MarksT);
        var units = ctx.Table(Fight.UnitsT);
        var buildings = ctx.Table(Fight.BuildingsT);
        var values = table.Column(_value);
        var xs = table.Column(_mx);
        var ys = table.Column(_my);
        var owners = table.Column(_owner);
        var unitProto = units.Column(_unitProto);
        var unitFacing = units.Column(_unitFacing);
        var buildingProto = buildings.Column(_buildingProto);
        var buildingW = buildings.Column(_buildingW);
        var buildingH = buildings.Column(_buildingH);
        var buildingOpen = buildings.Column(_buildingOpen);
        var order = new List<(uint Entity, int Row, int Kind)>(table.Count);
        for (var row = 0; row < table.Count; row++)
        {
            var entity = table.Entities[row];
            if (units.RowOf(entity) >= 0)
                order.Add((entity, row, 0));
            else if (buildings.RowOf(entity) >= 0)
                order.Add((entity, row, 1));
        }
        order.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Entity.CompareTo(b.Entity));
        foreach (var (entity, row, kind) in order)
        {
            var mark = NewMark();
            mark.Entity = entity;
            mark.Player = (uint)owners[row];
            mark.Value = values[row];
            if (kind == 0)
            {
                var at = units.RowOf(entity);
                var arms = Arms.Unit((int)unitProto[at]);
                if (arms is null)
                    continue;
                mark.Proto = (int)unitProto[at];
                mark.Unit = arms;
                mark.X = mark.Left = xs[row];
                mark.Y = mark.Top = ys[row];
                mark.Radius = arms.Spec.Radius;
                mark.Air = arms.Air;
                mark.Armor = arms.Armor;
                mark.Hp = arms.Spec.Hp;
                mark.Facing = unitFacing[at];
            }
            else
            {
                var at = buildings.RowOf(entity);
                var arms = Arms.Building((int)buildingProto[at]);
                if (arms is null)
                    continue;
                mark.Proto = (int)buildingProto[at];
                mark.Building = arms;
                mark.Left = xs[row];
                mark.Top = ys[row];
                mark.Width = buildingW[at];
                mark.Height = buildingH[at];
                mark.X = mark.Left + mark.Width / 2;
                mark.Y = mark.Top + mark.Height / 2;
                mark.Armor = Arms.BuildingArmor;
                mark.Hp = arms.Hp;
                mark.Wall = arms.Wall || (arms.Gate && buildingOpen[at] == 0);
                if (mark.Wall)
                    for (var ty = (int)Math.Floor(mark.Top); ty < mark.Top + mark.Height; ty++)
                        for (var tx = (int)Math.Floor(mark.Left); tx < mark.Left + mark.Width; tx++)
                            _walls[TileKey(tx, ty)] = mark;
            }
            _markOf.Set(entity, _marks.Count);
            _marks.Add(mark);
            _used++;
        }
        foreach (var mark in _marks)
            _grid.Put(mark);

        // Стрелки.
        var count = ctx.Count;
        Grow(ref _armEntity, count);
        Grow(ref _armTarget, count);
        Grow(ref _armCooldown, count);
        Grow(ref _armStuck, count);
        Grow(ref _armX, count);
        Grow(ref _armY, count);
        Grow(ref _armChase, count);
        Grow(ref _armOrdered, count);
        Grow(ref _shooters, count);
        _armCount = count;
        var target = ctx.Column(_target);
        var chase = ctx.Column(_chase);
        var cooldown = ctx.Column(_cooldown);
        var stuck = ctx.Column(_stuck);
        var ordered = ctx.Column(_ordered);
        var sx = ctx.Column(_sx);
        var sy = ctx.Column(_sy);
        for (var row = 0; row < count; row++)
        {
            var entity = ctx.Entities[row];
            _armEntity[row] = entity;
            _armTarget[row] = target[row];
            _armChase[row] = chase[row] != 0;
            _armCooldown[row] = cooldown[row];
            _armStuck[row] = stuck[row];
            _armOrdered[row] = ordered[row] != 0;
            _armX[row] = sx[row];
            _armY[row] = sy[row];
            _armRow.Set(entity, row);
            _shooters[row] = row;
        }
        Array.Sort(_shooters, 0, count, Comparer<int>.Create((a, b) => _armEntity[a].CompareTo(_armEntity[b])));

        // Турели.
        var mounts = ctx.Table(Fight.MountsT);
        var parents = mounts.Column(_mountParent);
        var angles = mounts.Column(_mountAngle);
        var protos = mounts.Column(_mountProto);
        _mountCount = mounts.Count;
        Grow(ref _mountParentOf, _mountCount);
        Grow(ref _mountAngleOf, _mountCount);
        Grow(ref _mountProtoOf, _mountCount);
        Grow(ref _mountTurned, _mountCount);
        Grow(ref _mountEntity, _mountCount);
        var byEntity = new int[_mountCount];
        for (var row = 0; row < _mountCount; row++)
        {
            var entity = mounts.Entities[row];
            _mountEntity[row] = entity;
            _mountParentOf[row] = parents[row] > 0 ? (uint)parents[row] : 0;
            _mountAngleOf[row] = angles[row];
            _mountProtoOf[row] = (int)protos[row];
            _mountTurned[row] = false;
            _mountRow.Set(entity, row);
            byEntity[row] = row;
        }
        Array.Sort(byEntity, (a, b) => _mountEntity[a].CompareTo(_mountEntity[b]));
        foreach (var row in byEntity)
        {
            var parent = _mountParentOf[row];
            if (parent == 0)
                continue;
            if (!_turrets.TryGetValue(parent, out var list))
                _turrets[parent] = list = _freeLists.Count > 0 ? _freeLists.Pop() : [];
            list.Add(_mountEntity[row]);
        }

        var paths = ctx.Table(Fight.PathsT);
        var goalX = paths.Column(_pathGoalX);
        var goalY = paths.Column(_pathGoalY);
        var wait = paths.Column(_pathWait);
        var slot = paths.Column(_pathSlot);
        for (var row = 0; row < paths.Count; row++)
            _paths[paths.Entities[row]] = new PathState { GoalX = (int)goalX[row], GoalY = (int)goalY[row], Wait = wait[row], Slot = slot[row] != 0 };
        foreach (var entity in ctx.Table(Fight.ConvertingT).Entities)
            _converting.Add(entity);
        foreach (var entity in ctx.Table(Fight.OffT).Entities)
            _off.Add(entity);

        var tactics = ctx.Table(Fight.TacticsT);
        var stance = tactics.Column(_stance);
        var away = tactics.Column(_away);
        var homeX = tactics.Column(_homeX);
        var homeY = tactics.Column(_homeY);
        var resupply = tactics.Column(_resupply);
        for (var row = 0; row < tactics.Count; row++)
            _tactics[tactics.Entities[row]] = new TacticsState
            {
                Stance = StanceOf(stance[row]), Away = away[row] != 0, HomeX = homeX[row], HomeY = homeY[row], Resupply = resupply[row] != 0, Patrol = -1,
            };

        ReadShots(ctx);
        ReadBlasts(ctx);
    }

    private Mark NewMark()
    {
        if (_used >= _pool.Count)
            _pool.Add(new Mark());
        var mark = _pool[_used];
        mark.Entity = 0;
        mark.Player = 0;
        mark.X = mark.Y = mark.Left = mark.Top = mark.Width = mark.Height = mark.Radius = 0;
        mark.Air = mark.Wall = mark.Hit = mark.Dead = mark.Turned = false;
        mark.Armor = 0;
        mark.Hp = 1;
        mark.Value = 1;
        mark.Arms = -1;
        mark.Seen = 0;
        mark.Unit = null;
        mark.Building = null;
        mark.Facing = 0;
        mark.Proto = -1;
        return mark;
    }

    private int _used;

    private static void Grow<T>(ref T[] array, int count)
    {
        if (array.Length < count)
            array = new T[Math.Max(count, array.Length * 2)];
    }

    private static long TileKey(int x, int y) => ((long)x << 32) | (uint)y;

    /// <summary>Стойка по номеру строки (aggressive, defensive, hold, passive).</summary>
    private Stance StanceOf(double id)
    {
        if (_stances.TryGetValue(id, out var stance))
            return stance;
        stance = (id > 0 ? Tools.Text((uint)id) : "") switch
        {
            "aggressive" => Stance.Aggressive,
            "hold" => Stance.Hold,
            "passive" => Stance.Passive,
            _ => Stance.Defensive,
        };
        _stances[id] = stance;
        return stance;
    }

    // ===== Мир глазами боя =====

    private Mark? MarkOf(double entity) => entity > 0 && _markOf.Get((uint)entity) is var at and >= 0 ? _marks[at] : null;

    private Mark? MarkOf(uint entity) => _markOf.Get(entity) is var at and >= 0 ? _marks[at] : null;

    /// <summary>Носитель турели или сама сущность (carrierOf).</summary>
    private uint CarrierOf(uint entity) => _mountRow.Get(entity) is var row and >= 0 && _mountParentOf[row] != 0 ? _mountParentOf[row] : entity;

    private static readonly List<uint> NoTurrets = [];

    /// <summary>Турели носителя (turretsOf).</summary>
    private List<uint> TurretsOf(uint entity) => _turrets.GetValueOrDefault(entity) ?? NoTurrets;

    /// <summary>Оружие стрелка или турели (weaponOf); безоружен — null.</summary>
    private Weapon? GunOf(uint entity)
    {
        if (_mountRow.Get(entity) is var row and >= 0)
            return Arms.Turret(_mountProtoOf[row])?.Weapon;
        return MarkOf(entity)?.Unit?.Weapon;
    }

    private bool HasPath(uint entity) => _paths.ContainsKey(entity);

    private void RemovePath(uint entity)
    {
        if (_paths.Remove(entity))
            _call.Commands.Remove(entity, S.Path);
    }

    private TacticsState? TacticsOf(uint entity) => _tactics.GetValueOrDefault(entity);

    /// <summary>Сколько точек в патруле юнита (лениво: байты патрулей — одним вызовом на тик).</summary>
    private int PatrolOf(uint entity)
    {
        if (!_tactics.TryGetValue(entity, out var tactics))
            return 0;
        if (!_patrolsRead)
        {
            _patrolsRead = true;
            var entities = _tactics.Keys.ToArray();
            var patrols = Tools.BytesMany(entities, S.Tactics, R.TacticsPatrol);
            for (var i = 0; i < entities.Length; i++)
                if (_tactics[entities[i]].Patrol < 0)
                    _tactics[entities[i]].Patrol = (patrols[i]?.Length ?? 0) / 16;
        }
        return Math.Max(0, tactics.Patrol);
    }

    private Stance StanceOf(uint entity) => _tactics.GetValueOrDefault(entity)?.Stance ?? Stance.Defensive;

    /// <summary>Враги ли игроки (hostile): игрок 0 ни с кем не воюет.</summary>
    public static bool Hostile(uint a, uint b) => a != b && a != 0 && b != 0;

    /// <summary>Союзники ли (одинаковый Team.head ≥ 0).</summary>
    private bool Allied(uint a, uint b) =>
        a != b && _heads.TryGetValue(a, out var head) && head >= 0 && _heads.TryGetValue(b, out var other) && other == head;

    /// <summary>Может ли оружие игрока player попасть в метку (canHit).</summary>
    private bool CanHit(Weapon weapon, uint player, Mark mark) =>
        Hostile(player, mark.Player) && !Allied(player, mark.Player) && !_safe.Contains(player) && !_safe.Contains(mark.Player)
        && (mark.Air ? weapon.Air : !weapon.OnlyAir) && !mark.Dead;

    /// <summary>Может ли метка навредить такому стрелку (threatens): её оружие или турелей бьёт по нему.</summary>
    private bool Threatens(Mark mark, bool air)
    {
        if (mark.Arms < 0)
        {
            mark.Arms = 0;
            Arm(mark, mark.Entity);
            foreach (var turret in TurretsOf(mark.Entity))
                Arm(mark, turret);
        }
        return (mark.Arms & (air ? 2 : 1)) != 0;
    }

    private void Arm(Mark mark, uint gunner)
    {
        if (GunOf(gunner) is not { } weapon || !_armRow.Has(gunner))
            return;
        mark.Arms |= (weapon.OnlyAir ? 0 : 1) | (weapon.Air ? 2 : 0);
    }

    /// <summary>
    /// Уходит из дома в погоню (leaveHome): тактика запоминает, откуда ушёл. В патруле возвращаться некуда. Запись — хук
    /// раздела Tactics, если он есть; кэш тактики тика обновляется в любом случае.
    /// </summary>
    private void LeaveHome(uint entity)
    {
        var tactics = TacticsOf(entity);
        if (tactics is not null && (tactics.Away || PatrolOf(entity) > 0))
            return;
        var mark = MarkOf(entity);
        var (x, y) = mark is not null ? (mark.X, mark.Y) : _call.At(entity);
        var (homeX, homeY) = (Math.Floor(x), Math.Floor(y));
        if (Hooks.LeaveHome is { } hook)
            hook(_call, entity);
        else if (tactics is null)
            _call.Add(entity, S.Tactics).Flag(R.TacticsAway, true).Number(R.TacticsHomeX, homeX).Number(R.TacticsHomeY, homeY);
        else
            _call.Set(entity, S.Tactics).Flag(R.TacticsAway, true).Number(R.TacticsHomeX, homeX).Number(R.TacticsHomeY, homeY);
        if (tactics is null)
        {
            _tactics[entity] = new TacticsState { Stance = Stance.Defensive, Away = true, HomeX = homeX, HomeY = homeY };
            return;
        }
        tactics.Away = true;
        tactics.HomeX = homeX;
        tactics.HomeY = homeY;
    }

    /// <summary>Гонится ли носитель или одна из его турелей за целью (inChase).</summary>
    private bool InChase(uint carrier)
    {
        if (Chasing(carrier))
            return true;
        foreach (var turret in TurretsOf(carrier))
            if (Chasing(turret))
                return true;
        return false;
    }

    private bool Chasing(uint gunner) => _armRow.Get(gunner) is var row and >= 0 && _armChase[row] && _armTarget[row] != 0;

    // ===== Запись =====

    private void Write(SystemContext ctx)
    {
        var target = ctx.Column(_target);
        var chase = ctx.Column(_chase);
        var cooldown = ctx.Column(_cooldown);
        var stuck = ctx.Column(_stuck);
        var ordered = ctx.Column(_ordered);
        for (var row = 0; row < _armCount; row++)
        {
            target[row] = _armTarget[row];
            chase[row] = _armChase[row] ? 1 : 0;
            cooldown[row] = _armCooldown[row];
            stuck[row] = _armStuck[row];
            ordered[row] = _armOrdered[row] ? 1 : 0;
        }
        WriteShots(ctx);
        WriteBlasts(ctx);
        var commands = _call.Commands;
        foreach (var mark in _marks)
        {
            if (mark.Dead)
                continue;
            if (mark.Hit)
                _call.Set(mark.Entity, S.Health).Number(S.HealthValue, mark.Value).Number(R.HealthHit, _tick);
            if (mark.Turned)
                commands.Set(mark.Entity, S.Facing).Number(S.FacingAngle, mark.Facing);
        }
        for (var row = 0; row < _mountCount; row++)
        {
            if (!_mountTurned[row])
                continue;
            var turret = _mountEntity[row];
            if (MarkOf(_mountParentOf[row]) is { Dead: true })
                continue;
            commands.Set(turret, S.Mount).Number(S.MountAngle, _mountAngleOf[row]);
            commands.Set(turret, S.Facing).Number(S.FacingAngle, Wrap(HullFacing(_mountParentOf[row]) + _mountAngleOf[row]));
        }
        _used = 0;
    }

    // ===== Углы =====

    private const double Turn = Math.PI * 2;

    /// <summary>Угол в −π..π (wrap в common.ts).</summary>
    public static double Wrap(double angle) => angle - Turn * Math.Floor(angle / Turn + 0.5);

    /// <summary>Поворот от facing к wanted не больше maxTurn (turnToward).</summary>
    public static double TurnToward(double facing, double wanted, double maxTurn)
    {
        var off = Wrap(wanted - facing);
        return Math.Abs(off) <= maxTurn ? wanted : Wrap(facing + Math.Sign(off) * maxTurn);
    }

    /// <summary>Куда смотрит носитель: у юнита — его Facing, у здания — 0.</summary>
    private double HullFacing(uint carrier) => MarkOf(carrier) is { Unit: not null } hull ? hull.Facing : 0;
}
