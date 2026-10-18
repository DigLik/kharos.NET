using System.Buffers.Binary;
using Kharos.Engine.Numerics;
using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

// Ход юнитов (movement.ts, crates/sim/src/systems/movement.rs): к точке пути с объездом соседей, поворот со своей
// скоростью, скольжение вдоль стен, расталкивание; дошедшие и застрявшие ищут путь заново. Тот же порядок и та же
// арифметика (DetMath вместо Math.* JS).
public sealed partial class Navigation
{
    /// <summary>Сколько тиков юнит может не подбираться к точке пути ближе, прежде чем проложить путь заново.</summary>
    private const double WaitTicks = 30;
    /// <summary>Сколько тиков подряд ехавший напрямую может упираться в местность или здание, прежде чем попросить путь.</summary>
    private const double SlideTicks = 4;
    /// <summary>На сколько тайлов надо подобраться к точке ближе прежнего, чтобы это считалось продвижением.</summary>
    private const double Progress = 0.05;
    private const double MaxTries = JamTries;
    /// <summary>Ближе скольких тайлов к цели юнит, исчерпав попытки, сдаётся и встаёт.</summary>
    private const double GiveUpReach = 4;
    /// <summary>Промежуточная точка засчитывается, когда юнит подошёл к ней на столько тайлов.</summary>
    private const double Reached = 0.6;
    /// <summary>Сколько тайлов сверх радиусов до занятой последней точки — «приехал».</summary>
    private const double Settle = 1;
    private const double OrbitMargin = 1.3;
    /// <summary>На сколько секунд вперёд юнит предвидит сближение с соседями.</summary>
    private const double Horizon = 1.2;
    private const double Aimed = 0.05;
    private const double Gap = 0.1;
    private const double Avoidance = 2;
    private const int Separations = 2;
    /// <summary>Сколько юнитов за тик прокладывают путь заново.</summary>
    private const int LostSearches = 8;
    /// <summary>Сколько тайлов за тик вместе осматривают перепрокладывающие путь.</summary>
    private const double LostTiles = 8000;
    private const ulong RoadLookTicks = 20;
    private const double Sense = 2.5;
    private const double Cell = 2;
    private const double Drift = 1;
    private const double Large = 0.9;
    /// <summary>Сколько тайлов за тик осматривают поиски пути упёршихся юнитов, ехавших напрямую.</summary>
    private const double PlanTiles = 20000;

    // Тела тика: столбцами, номер — порядок строк Body.
    private int _n;
    private uint[] _entity = [];
    private double[] _x = [], _y = [], _radius = [], _mass = [], _vx = [], _vy = [], _owner = [], _facing = [], _facing0 = [];
    private bool[] _air = [], _foot = [], _moving = [], _rooted = [], _hasFacing = [];
    private readonly Dictionary<uint, int> _index = [];
    private readonly Dictionary<long, int> _cellSlot = [];
    private int[] _cellStart = [], _cellCount = [], _cellOf = [], _members = [], _slotX = [], _slotY = [];
    /// <summary>Ячейки прямоугольником: номер ячейки + 1 (0 — пусто) по (cy − top)·w + (cx − left); null — слишком велик.</summary>
    private int[] _flat = [];
    private bool _useFlat;
    private int _flatLeft, _flatTop, _flatW, _flatH;
    private const long MaxFlatCells = 1 << 22;
    private readonly List<int> _large = [];
    private readonly List<int> _neighbours = [];
    private readonly Dictionary<double, double> _heads = [];
    /// <summary>
    /// Места тел на начало прошлого хода: скорость за тик (Unit.prevX/prevY в TS). В мире их нет — в сохранение их
    /// кладёт тот, кто сохраняет (<see cref="SaveMotion"/>); без них после загрузки первый тик соседи кажутся стоящими.
    /// </summary>
    private Dictionary<uint, (double X, double Y)> _prev = [], _prevNext = [];
    private Route[] _routes = [];
    private double[] _arena = [];

    /// <summary>Память хода для сохранения: count:u32 (entity:u32 x:f64 y:f64)×count по возрастанию номеров.</summary>
    public byte[] SaveMotion()
    {
        var entities = _prev.Keys.Order().ToArray();
        var bytes = new byte[4 + entities.Length * 20];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entities.Length);
        for (var i = 0; i < entities.Length; i++)
        {
            var at = bytes.AsSpan(4 + i * 20);
            var (x, y) = _prev[entities[i]];
            BinaryPrimitives.WriteUInt32LittleEndian(at, entities[i]);
            BinaryPrimitives.WriteDoubleLittleEndian(at[4..], x);
            BinaryPrimitives.WriteDoubleLittleEndian(at[12..], y);
        }
        return bytes;
    }

    /// <summary>Память хода из <see cref="SaveMotion"/>; обрыв — <see cref="FormatException"/>.</summary>
    public void LoadMotion(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
            throw new FormatException("память хода обрывается");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if ((ulong)bytes.Length != 4 + (ulong)count * 20)
            throw new FormatException("память хода не той длины");
        _prev.Clear();
        for (var i = 0; i < (int)count; i++)
        {
            var at = bytes[(4 + i * 20)..];
            _prev[BinaryPrimitives.ReadUInt32LittleEndian(at)] = (BinaryPrimitives.ReadDoubleLittleEndian(at[4..]), BinaryPrimitives.ReadDoubleLittleEndian(at[12..]));
        }
    }

    /// <summary>Путь идущего, как он лежит в Path: меняется на месте, пишется обратно в конце.</summary>
    private struct Route
    {
        public int Index;
        public uint Entity;
        public double GoalX, GoalY, Wait, Tries, Near, Best;
        public bool Roads, Direct, Stuck;
        public int Start, Count;
        public bool PointsChanged, Walled, Slid;
        public Chassis Chassis;
        public double Speed, Turn;
    }

    private void Grow(int n)
    {
        if (_entity.Length >= n)
            return;
        var size = Math.Max(n, _entity.Length * 2);
        Array.Resize(ref _entity, size);
        Array.Resize(ref _x, size);
        Array.Resize(ref _y, size);
        Array.Resize(ref _radius, size);
        Array.Resize(ref _mass, size);
        Array.Resize(ref _vx, size);
        Array.Resize(ref _vy, size);
        Array.Resize(ref _owner, size);
        Array.Resize(ref _facing, size);
        Array.Resize(ref _facing0, size);
        Array.Resize(ref _air, size);
        Array.Resize(ref _foot, size);
        Array.Resize(ref _moving, size);
        Array.Resize(ref _rooted, size);
        Array.Resize(ref _hasFacing, size);
        Array.Resize(ref _cellOf, size);
        Array.Resize(ref _members, size);
    }

    /// <summary>Союзны ли игроки: одинаковый Team.head ≥ 0 (allied в allies.ts).</summary>
    private bool Allied(double a, double b) =>
        a != b && _heads.TryGetValue(a, out var head) && head >= 0 && _heads.TryGetValue(b, out var other) && head == other;

    /// <summary>Сдвигается ли тело, когда в него въехал by: своё — да, стоящее чужое — нет, как и разворачивающееся.</summary>
    private bool Yields(int body, int by) => !_rooted[body] && (_moving[body] || _owner[body] == _owner[by] || Allied(_owner[body], _owner[by]));

    /// <summary>Соседи тела, чьи края ближе reach тайлов к его краю (<c>gather</c>): в <see cref="_neighbours"/>.</summary>
    private void Gather(int me, double reach)
    {
        _neighbours.Clear();
        var (x, y) = (_x[me], _y[me]);
        var far = _radius[me] + Large + reach + Drift;
        var right = PathSearch.Floor((x + far) / Cell);
        var bottom = PathSearch.Floor((y + far) / Cell);
        var top = PathSearch.Floor((y - far) / Cell);
        var left = PathSearch.Floor((x - far) / Cell);
        if (_useFlat)
        {
            // Те же ячейки в том же порядке, только без словаря: за краем прямоугольника тел нет.
            var (fromY, toY) = (Math.Max(top, _flatTop), Math.Min(bottom, _flatTop + _flatH - 1));
            var (fromX, toX) = (Math.Max(left, _flatLeft), Math.Min(right, _flatLeft + _flatW - 1));
            for (var cy = fromY; cy <= toY; cy++)
            {
                var row = (cy - _flatTop) * _flatW - _flatLeft;
                for (var cx = fromX; cx <= toX; cx++)
                {
                    var slot = _flat[row + cx] - 1;
                    if (slot < 0)
                        continue;
                    var start = _cellStart[slot];
                    for (var at = start; at < start + _cellCount[slot]; at++)
                        Check(me, _members[at], reach);
                }
            }
        }
        else
        {
            for (var cy = top; cy <= bottom; cy++)
                for (var cx = left; cx <= right; cx++)
                {
                    if (!_cellSlot.TryGetValue(Key(cx, cy), out var slot))
                        continue;
                    var start = _cellStart[slot];
                    for (var at = start; at < start + _cellCount[slot]; at++)
                        Check(me, _members[at], reach);
                }
        }
        foreach (var other in _large)
            Check(me, other, reach);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Check(int me, int other, double reach)
    {
        if (other == me || _air[other] != _air[me])
            return;
        var far = _radius[me] + _radius[other] + reach;
        var (offsetX, offsetY) = (_x[other] - _x[me], _y[other] - _y[me]);
        if (offsetX * offsetX + offsetY * offsetY < far * far)
            _neighbours.Add(other);
    }

    /// <summary>Куда ехать, чтобы не столкнуться (<c>steer</c>): к точке, отведённый от предвиденных сближений.</summary>
    private double Steer(int me, double dirX, double dirY, double speed)
    {
        Gather(me, Sense);
        double awayX = 0, awayY = 0;
        var (ownX, ownY) = (dirX * speed, dirY * speed);
        foreach (var other in _neighbours)
        {
            var (offsetX, offsetY) = (_x[other] - _x[me], _y[other] - _y[me]);
            var (relativeX, relativeY) = (ownX - _vx[other], ownY - _vy[other]);
            var closing = relativeX * relativeX + relativeY * relativeY;
            var reach = _radius[me] + _radius[other] + Gap;
            var when = closing > 1e-6 ? (offsetX * relativeX + offsetY * relativeY) / closing : 0;
            if (when <= 0 || when > Horizon)
                continue;
            var (missX, missY) = (offsetX - relativeX * when, offsetY - relativeY * when);
            var miss = DetMath.Hypot(missX, missY);
            if (miss >= reach)
                continue;
            var length = Math.Sqrt(closing);
            var sideX = miss > 1e-3 ? -missX / miss : -relativeY / length;
            var sideY = miss > 1e-3 ? -missY / miss : relativeX / length;
            var urgency = (1 - when / Horizon) * ((reach - miss) / reach);
            awayX += sideX * urgency;
            awayY += sideY * urgency;
        }
        return DetMath.Atan2(dirY + awayY * Avoidance, dirX + awayX * Avoidance);
    }

    /// <summary>Можно ли телу встать в точку: местность пускает, или оно уже внутри здания и выходит из него.</summary>
    private bool Open(int body, double x, double y, bool inside) => inside || CanStand(_air[body], PathSearch.Floor(x), PathSearch.Floor(y), _foot[body]);

    /// <summary>Собирает тела тика: места, корпуса, скорости за прошлый тик, сетка соседей.</summary>
    private void GatherBodies(double step)
    {
        var bodies = World.Store(Builtin.Body);
        var entities = bodies.Entities;
        var n = entities.Length;
        Grow(n);
        _index.Clear();
        _large.Clear();
        _cellSlot.Clear();
        _prevNext.Clear();
        var positions = World.Store(Builtin.Position);
        var facings = World.Store(Builtin.Facing);
        var owners = World.Store(Builtin.Owner);
        var movers = World.Store(Builtin.Mover);
        var paths = World.Store(Builtin.Path);
        var radii = bodies.RawColumn(F.BodyRadius);
        var masses = bodies.RawColumn(F.BodyMass);
        var rooted = bodies.RawColumn(F.BodyRooted);
        var count = 0;
        var slots = 0;
        for (var row = 0; row < n; row++)
        {
            var entity = entities[row];
            var at = positions.RowOf(entity);
            if (at < 0)
                continue;
            var i = count++;
            var (x, y) = (positions.RawColumn(0)[at], positions.RawColumn(1)[at]);
            var radius = radii[row];
            var mover = movers.RowOf(entity);
            var chassis = mover < 0 ? Movement.Chassis.Ground : Chassis.Of(World, movers.RawColumn(F.MoverChassis)[mover]);
            var speed = mover < 0 ? 0 : movers.RawColumn(F.MoverSpeed)[mover];
            // Скорость за прошлый тик — по тому, где тело было тогда. Телепорт скоростью не считается.
            double vx = 0, vy = 0;
            if (_prev.TryGetValue(entity, out var before))
            {
                (vx, vy) = ((x - before.X) / step, (y - before.Y) / step);
                if (DetMath.Hypot(vx, vy) > speed * 1.5)
                    (vx, vy) = (0, 0);
            }
            _prevNext[entity] = (x, y);
            _entity[i] = entity;
            (_x[i], _y[i], _radius[i]) = (x, y, radius);
            _mass[i] = masses[row] > 0 ? masses[row] : radius * radius;
            (_vx[i], _vy[i]) = (vx, vy);
            _air[i] = chassis.Air;
            _foot[i] = chassis.Foot;
            _moving[i] = paths.Contains(entity);
            _rooted[i] = rooted[row] != 0;
            var owner = owners.RowOf(entity);
            _owner[i] = owner < 0 ? 0 : owners.RawColumn(0)[owner];
            var facing = facings.RowOf(entity);
            _hasFacing[i] = facing >= 0;
            _facing[i] = _facing0[i] = facing < 0 ? 0 : facings.RawColumn(0)[facing];
            _index[entity] = i;
            if (radius > Large)
            {
                _large.Add(i);
                _cellOf[i] = -1;
                continue;
            }
            var (cellX, cellY) = (PathSearch.Floor(x / Cell), PathSearch.Floor(y / Cell));
            var key = Key(cellX, cellY);
            if (!_cellSlot.TryGetValue(key, out var slot))
            {
                slot = slots++;
                _cellSlot[key] = slot;
                if (_cellCount.Length < slots)
                {
                    Array.Resize(ref _cellCount, Math.Max(slots, _cellCount.Length * 2));
                    Array.Resize(ref _cellStart, _cellCount.Length);
                    Array.Resize(ref _slotX, _cellCount.Length);
                    Array.Resize(ref _slotY, _cellCount.Length);
                }
                _cellCount[slot] = 0;
                (_slotX[slot], _slotY[slot]) = (cellX, cellY);
            }
            _cellOf[i] = slot;
            _cellCount[slot]++;
        }
        _n = count;
        (_prev, _prevNext) = (_prevNext, _prev);
        // Ячейки — подряд, внутри — в порядке тел.
        var start = 0;
        for (var slot = 0; slot < slots; slot++)
        {
            _cellStart[slot] = start;
            start += _cellCount[slot];
            _cellCount[slot] = 0;
        }
        for (var i = 0; i < count; i++)
        {
            var slot = _cellOf[i];
            if (slot < 0)
                continue;
            _members[_cellStart[slot] + _cellCount[slot]++] = i;
        }
        // Прямоугольник ячеек для поиска соседей без словаря.
        _useFlat = false;
        if (slots > 0)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var slot = 0; slot < slots; slot++)
            {
                (minX, maxX) = (Math.Min(minX, _slotX[slot]), Math.Max(maxX, _slotX[slot]));
                (minY, maxY) = (Math.Min(minY, _slotY[slot]), Math.Max(maxY, _slotY[slot]));
            }
            var area = ((long)maxX - minX + 1) * ((long)maxY - minY + 1);
            if (area <= MaxFlatCells)
            {
                (_flatLeft, _flatTop, _flatW, _flatH) = (minX, minY, maxX - minX + 1, maxY - minY + 1);
                if (_flat.Length < area)
                    _flat = new int[Math.Max(area, _flat.Length * 2)];
                else
                    Array.Clear(_flat, 0, (int)area);
                for (var slot = 0; slot < slots; slot++)
                    _flat[(_slotY[slot] - minY) * _flatW + (_slotX[slot] - minX)] = slot + 1;
                _useFlat = true;
            }
        }
        _heads.Clear();
        var teams = World.Store(Builtin.Team);
        var players = teams.RawColumn(F.TeamPlayer);
        var heads = teams.RawColumn(F.TeamHead);
        for (var row = 0; row < teams.Count; row++)
            _heads[players[row]] = heads[row];
    }

    /// <summary>Пути идущих: Path с Mover у тел, по порядку строк Path; точки — в общий запас.</summary>
    private int GatherRoutes()
    {
        var paths = World.Store(Builtin.Path);
        var entities = paths.Entities;
        var movers = World.Store(Builtin.Mover);
        if (_routes.Length < entities.Length)
            _routes = new Route[Math.Max(entities.Length, _routes.Length * 2)];
        var total = 0;
        for (var row = 0; row < entities.Length; row++)
            total += paths.BytesAt(F.PathPointsSlot, row).Length / 8;
        if (_arena.Length < total)
            _arena = new double[Math.Max(total, _arena.Length * 2)];
        var count = 0;
        var used = 0;
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            var mover = movers.RowOf(entity);
            if (mover < 0 || !_index.TryGetValue(entity, out var index))
                continue;
            var bytes = paths.BytesAt(F.PathPointsSlot, row);
            var points = bytes.Length / 8 / 2 * 2;
            // Путь без точек ждёт планировщика: юнит стоит.
            if (points < 2)
                continue;
            for (var at = 0; at < points; at++)
                _arena[used + at] = BinaryPrimitives.ReadDoubleLittleEndian(bytes[(at * 8)..]);
            ref var route = ref _routes[count++];
            route = new Route
            {
                Index = index,
                Entity = entity,
                GoalX = paths.RawColumn(F.PathGoalX.Slot)[row],
                GoalY = paths.RawColumn(F.PathGoalY.Slot)[row],
                Wait = paths.RawColumn(F.PathWait.Slot)[row],
                Tries = paths.RawColumn(F.PathTries)[row],
                Near = paths.RawColumn(F.PathNear.Slot)[row],
                Best = paths.RawColumn(F.PathBest.Slot)[row],
                Roads = paths.RawColumn(F.PathRoads.Slot)[row] != 0,
                Direct = paths.RawColumn(F.PathDirect.Slot)[row] != 0,
                Stuck = paths.RawColumn(F.PathStuck.Slot)[row] != 0,
                Start = used,
                Count = points,
                Chassis = Chassis.Of(World, movers.RawColumn(F.MoverChassis)[mover]),
                Speed = movers.RawColumn(F.MoverSpeed)[mover],
                Turn = movers.RawColumn(F.MoverTurn)[mover],
            };
            used += points;
        }
        return count;
    }

    private readonly List<int> _pushed = [];
    private readonly HashSet<int> _inPushed = [];
    private readonly List<uint> _stopped = [];
    private readonly List<(uint Entity, int X, int Y, double Near, int BlockedX, int BlockedY, bool Blocked)> _further = [];
    private readonly List<(uint Entity, int Index, int X, int Y, double Tries, double Near)> _lost = [];
    private readonly List<(uint Entity, int X, int Y, double Near)> _roadward = [];

    /// <summary>
    /// Раз в тик (<c>moveUnits</c>): двигает юнитов по их путям. Юнит едет к точке пути, предвидя сближение с соседями,
    /// поворачивает со своей скоростью, едет туда, куда смотрит; в стену не упирается, а скользит вдоль; зашедших друг в
    /// друга расталкивает. Летающие летят по прямой и мешают только друг другу.
    /// </summary>
    public void MoveUnits(ulong tick, double step)
    {
        GatherBodies(step);
        var routes = GatherRoutes();
        _roadward.Clear();
        for (var m = 0; m < routes; m++)
        {
            ref var route = ref _routes[m];
            var me = route.Index;
            var entity = route.Entity;
            var (speed, turn, radius) = (route.Speed, route.Turn, _radius[me]);
            // Путь проложен без дороги, а она показалась в обзоре — юнит прикинет путь заново.
            if (!route.Roads && route.Chassis.Fastest > 1 && (tick + entity) % RoadLookTicks == 0 && RoadInSight(entity))
                _roadward.Add((entity, (int)route.GoalX, (int)route.GoalY, route.Near));
            var (x, y) = (_x[me], _y[me]);
            // Юнит, под которым выросло здание, выходит из него: внутри здания тайлы ему не преграда.
            var inside = !CanStand(_air[me], PathSearch.Floor(x), PathSearch.Floor(y), _foot[me]);
            if (_rooted[me])
                continue;
            var (px, py) = (_arena[route.Start], _arena[route.Start + 1]);
            var (dx, dy) = (px - x, py - y);
            var distance = DetMath.Hypot(dx, dy);
            var facing = _facing[me];
            var wanted = distance != 0 ? DetMath.Atan2(dy, dx) : facing;
            var last = route.Count == 2;
            // К последней точке юнит подъезжает прямо: объезжая соседей у самой цели, он вертелся бы вокруг неё.
            var heading = distance != 0 && !(last && distance < radius + Settle) ? Steer(me, dx / distance, dy / distance, speed) : wanted;
            facing = TurnToward(facing, heading, turn * step);
            _facing[me] = facing;
            var askew = Math.Abs(Wrap(heading - facing));
            var moved = speed * TerrainSpeed(route.Chassis, radius, PathSearch.Floor(x), PathSearch.Floor(y)) * step * Math.Max(0, DetMath.Cos(askew));
            var off = Math.Abs(Wrap(wanted - facing));
            var avoiding = Math.Abs(Wrap(heading - wanted)) > Aimed;
            if (!avoiding && distance < 2 * speed / turn * DetMath.Sin(Math.Min(off, Math.PI / 2)) * OrbitMargin)
                moved = 0;
            if (moved == 0)
                continue;
            // Глядя точно на точку пути, юнит приходит в неё ровно, без перелёта.
            if (facing == wanted && moved >= distance)
            {
                if (Open(me, px, py, inside))
                    (_x[me], _y[me]) = (px, py);
                else
                    route.Walled = true;
                continue;
            }
            var (stepX, stepY) = (DetMath.Cos(facing) * moved, DetMath.Sin(facing) * moved);
            if (Open(me, x + stepX, y + stepY, inside))
            {
                (_x[me], _y[me]) = (x + stepX, y + stepY);
                continue;
            }
            // Впереди непроходимое — скользит вдоль него: по той оси, по которой можно и которая ближе к точке.
            var alongX = Open(me, x + stepX, y, inside) ? stepX * dx : double.NegativeInfinity;
            var alongY = Open(me, x, y + stepY, inside) ? stepY * dy : double.NegativeInfinity;
            if (alongX > 0 && alongX >= alongY)
                _x[me] = x + stepX;
            else if (alongY > 0)
                _y[me] = y + stepY;
            else
                route.Walled = true;
            route.Slid = true;
        }

        Separate(routes);

        _stopped.Clear();
        _lost.Clear();
        _further.Clear();
        for (var m = 0; m < routes; m++)
        {
            ref var route = ref _routes[m];
            var me = route.Index;
            var entity = route.Entity;
            var (x, y, radius, air) = (_x[me], _y[me], _radius[me], _air[me]);
            var (px, py) = (_arena[route.Start], _arena[route.Start + 1]);
            var left = DetMath.Hypot(px - x, py - y);
            var last = route.Count == 2;
            var blocker = -1;
            if (left < radius + Large + Settle)
            {
                // Кто стоит в точке, не давая встать в неё: тот, чей край ближе края юнита.
                Gather(me, Sense);
                foreach (var other in _neighbours)
                    if (DetMath.Hypot(_x[other] - px, _y[other] - py) < radius + _radius[other])
                    {
                        blocker = other;
                        break;
                    }
            }
            // Промежуточную точку занял сосед, а юнит уже рядом: точка пропускается, если следующая видна по прямой.
            if (blocker >= 0 && !last && left < radius + _radius[blocker] + Settle)
            {
                var clear = air || PathSearch.IsClear(new StandGrid(this, air, _foot[me]), x, y, _arena[route.Start + 2], _arena[route.Start + 3]);
                if (clear)
                {
                    Drop(ref route);
                    route.Best = -1;
                    route.Wait = 0;
                    continue;
                }
            }
            // Последнюю точку занял тот, кого не сдвинуть, или свой, что едет туда же: ближе не подъехать.
            var settled = blocker >= 0 && last && left < radius + _radius[blocker] + Settle && (!Yields(blocker, me) || (_moving[blocker] && _entity[blocker] < entity));
            if (left == 0 || settled || (!last && left < Reached))
            {
                Drop(ref route);
                route.Tries = 0;
                route.Best = -1;
                route.Wait = 0;
                if (route.Count > 0)
                    continue;
                // Путь к далёкой цели кончается раньше неё: тогда это не приезд, и юнит ищет путь дальше отсюда.
                var (gx, gy) = (x - route.GoalX - 0.5, y - route.GoalY - 0.5);
                var reached = route.Near != 0
                    ? gx * gx + gy * gy <= (route.Near + 1) * (route.Near + 1)
                    : (Math.Floor(x) == route.GoalX && Math.Floor(y) == route.GoalY) || (settled && DetMath.Hypot(gx, gy) < radius + Settle + 1);
                if (reached || air)
                {
                    _stopped.Add(entity);
                }
                else
                {
                    // Вернуть точку: пока путь ищется (а норма тика могла кончиться), юнит едет к ней же.
                    route.Start -= 2;
                    _arena[route.Start] = x;
                    _arena[route.Start + 1] = y;
                    route.Count = 2;
                    _further.Add(settled
                        ? (entity, (int)route.GoalX, (int)route.GoalY, route.Near, PathSearch.Floor(_x[blocker]), PathSearch.Floor(_y[blocker]), true)
                        : (entity, (int)route.GoalX, (int)route.GoalY, route.Near, 0, 0, false));
                }
                continue;
            }
            // Ехал напрямую и упёрся в местность или здание: путь нужен сразу, продвижение вдоль стены не в счёт.
            if (route.Direct && (route.Slid || route.Walled))
            {
                if (++route.Wait >= SlideTicks)
                {
                    route.Stuck = true;
                    route.Wait = 0;
                    route.Best = -1;
                }
                continue;
            }
            if (route.Best < 0 || left < route.Best - Progress)
            {
                route.Best = left;
                route.Wait = 0;
            }
            else if (++route.Wait >= WaitTicks)
            {
                GiveUp(ref route, x, y);
                route.Wait = 0;
                route.Best = -1;
            }
        }

        // Запись: места, повороты, пути.
        var positionX = World.Schema[Builtin.Position].Fields[0].Index;
        var positionY = World.Schema[Builtin.Position].Fields[1].Index;
        for (var i = 0; i < _n; i++)
        {
            World.SetNumber(_entity[i], Builtin.Position, positionX, _x[i]);
            World.SetNumber(_entity[i], Builtin.Position, positionY, _y[i]);
            if (_hasFacing[i] && BitConverter.DoubleToInt64Bits(_facing[i]) != BitConverter.DoubleToInt64Bits(_facing0[i]))
                World.SetNumber(_entity[i], Builtin.Facing, 0, _facing[i]);
        }
        for (var m = 0; m < routes; m++)
        {
            ref var route = ref _routes[m];
            var entity = route.Entity;
            World.SetNumber(entity, Builtin.Path, F.PathWait.Index, route.Wait);
            World.SetNumber(entity, Builtin.Path, F.PathTriesField.Index, route.Tries);
            World.SetNumber(entity, Builtin.Path, F.PathBest.Index, route.Best);
            World.SetNumber(entity, Builtin.Path, F.PathStuck.Index, route.Stuck ? 1 : 0);
            if (route.PointsChanged)
                World.SetBytes(entity, Builtin.Path, F.PathPoints.Index, PointBytes(_arena, route.Start, route.Count));
        }
        foreach (var entity in _stopped)
            World.Remove(entity, Builtin.Path);

        var searchedBefore = Search.Searched;
        // Новые поиски начинаются, пока не вышла норма; все вместе — не больше одного обычного поиска сверх неё.
        var previous = Search.OpenQuota(LostTiles + PathSearch.SearchLimit);
        try
        {
            bool Over() => Search.Searched - searchedBefore >= LostTiles;
            foreach (var (entity, x, y, near, blockedX, blockedY, blocked) in _further)
            {
                if (Over())
                    continue;
                var unit = Unit(entity);
                var (fromX, fromY) = (PathSearch.Floor(unit.X), PathSearch.Floor(unit.Y));
                Order(entity, x, y, null, 0, near);
                // Ближе к цели не подойти — встаёт: иначе искал бы на месте каждый тик.
                if (ReadPoints(entity, _centers) && _centers.Count >= 2)
                {
                    var (endX, endY) = (PathSearch.Floor(_centers[^2]), PathSearch.Floor(_centers[^1]));
                    if ((endX == fromX && endY == fromY) || (blocked && endX == blockedX && endY == blockedY))
                        World.Remove(entity, Builtin.Path);
                }
            }
            foreach (var (entity, x, y, near) in _roadward)
            {
                if (Over())
                    break;
                if (!World.Has(entity, Builtin.Path))
                    continue;
                Order(entity, x, y, null, 0, near);
            }
            foreach (var (entity, me, x, y, tries, near) in _lost)
            {
                if (Over())
                    break;
                var result = Order(entity, x, y, null, tries, near);
                if (!World.Has(entity, Builtin.Path) || result == Moved.Set || Search.Cut)
                    continue;
                // Нового пути нет — обступили со всех сторон. Обступили стоящие без приказа — значит, доехал.
                Gather(me, Gap);
                if (tries >= MaxTries && _neighbours.Count > 0 && _neighbours.TrueForAll(other => !_moving[other]))
                    World.Remove(entity, Builtin.Path);
            }
        }
        finally
        {
            Search.CloseQuota(previous);
        }
    }

    /// <summary>Убирает первую точку пути.</summary>
    private static void Drop(ref Route route)
    {
        route.Start += 2;
        route.Count -= 2;
        route.PointsChanged = true;
    }

    /// <summary>Юнит не может идти дальше: прокладывает путь заново или, если уже пробовал, встаёт (<c>giveUp</c>).</summary>
    private void GiveUp(ref Route route, double x, double y)
    {
        var close = DetMath.Hypot(x - route.GoalX - 0.5, y - route.GoalY - 0.5) <= route.Near + GiveUpReach;
        // Ехал напрямую: упёрся — путь ему проложит planPaths; мешают только юниты — затор после MAX_TRIES ожиданий.
        if (route.Direct)
        {
            route.Wait = 0;
            if (route.Walled || ++route.Tries >= MaxTries)
                route.Stuck = true;
        }
        else if (route.Tries >= MaxTries && close)
        {
            _stopped.Add(route.Entity);
        }
        else if (_lost.Count < LostSearches)
        {
            _lost.Add((route.Entity, route.Index, (int)route.GoalX, (int)route.GoalY, Math.Min(route.Tries + 1, MaxTries), route.Near));
        }
    }

    /// <summary>
    /// Расталкивание: зашедших друг в друга разводят. Начинают те, кто ехал, — стоящие, которых сдвинули, продолжают
    /// цепочку. Едущий сдвигает стоящего своего целиком, в остальных случаях — по массе; стоящего чужого не сдвинуть.
    /// </summary>
    private void Separate(int routes)
    {
        _pushed.Clear();
        _inPushed.Clear();
        for (var m = 0; m < routes; m++)
            if (_inPushed.Add(_routes[m].Index))
                _pushed.Add(_routes[m].Index);
        var moved = true;
        for (var pass = 0; pass < Separations && moved; pass++)
        {
            moved = false;
            var count = _pushed.Count;
            for (var p = 0; p < count; p++)
            {
                var body = _pushed[p];
                Gather(body, 0);
                foreach (var other in _neighbours)
                {
                    var (normalX, normalY) = (_x[other] - _x[body], _y[other] - _y[body]);
                    var distance = DetMath.Hypot(normalX, normalY);
                    var overlap = _radius[body] + _radius[other] - distance;
                    if (overlap <= 0)
                        continue;
                    if (distance > 1e-6)
                    {
                        normalX /= distance;
                        normalY /= distance;
                    }
                    else
                    {
                        // Стоят точка в точку: расходятся в сторону, заданную номерами, — одинаково при каждом прогоне.
                        var angle = (double)(((ulong)_entity[body] * 7 + (ulong)_entity[other] * 13) % 360);
                        (normalX, normalY) = (DetMath.Cos(angle), DetMath.Sin(angle));
                    }
                    var bodyFree = Yields(body, other);
                    var otherFree = Yields(other, body);
                    var bodyShare = !bodyFree ? 0 : !otherFree ? 1 : _mass[other] / (_mass[body] + _mass[other]);
                    if (bodyFree && otherFree && _moving[body] != _moving[other])
                        bodyShare = _moving[body] ? 0 : 1;
                    var otherShare = otherFree ? 1 - bodyShare : 0;
                    var otherX = _x[other] + normalX * overlap * otherShare;
                    var otherY = _y[other] + normalY * overlap * otherShare;
                    // Не пускает местность — сдвигается другой.
                    if (otherShare != 0 && !Open(other, otherX, otherY, false))
                    {
                        otherShare = 0;
                        bodyShare = bodyFree ? 1 : 0;
                    }
                    var bodyX = _x[body] - normalX * overlap * bodyShare;
                    var bodyY = _y[body] - normalY * overlap * bodyShare;
                    if (bodyShare != 0 && Open(body, bodyX, bodyY, false))
                    {
                        (_x[body], _y[body]) = (bodyX, bodyY);
                        moved = true;
                    }
                    if (otherShare != 0)
                    {
                        moved = true;
                        (_x[other], _y[other]) = (otherX, otherY);
                        if (_inPushed.Add(other))
                            _pushed.Add(other);
                    }
                }
            }
        }
    }

    /// <summary>Раз в тик, до хода (<c>planPaths</c>): пути упёршимся, ехавшим напрямую (Path.direct и stuck), в норме тика.</summary>
    public void PlanPaths()
    {
        var paths = World.Store(Builtin.Path);
        var direct = paths.RawColumn(F.PathDirect.Slot);
        var stuck = paths.RawColumn(F.PathStuck.Slot);
        _waiting.Clear();
        var entities = paths.Entities;
        for (var row = 0; row < entities.Length; row++)
            if (direct[row] != 0 && stuck[row] != 0)
                _waiting.Add(entities[row]);
        if (_waiting.Count == 0)
            return;
        var searchedBefore = Search.Searched;
        var previous = Search.OpenQuota(PlanTiles);
        try
        {
            foreach (var entity in _waiting)
            {
                var row = paths.RowOf(entity);
                if (row < 0 || paths.RawColumn(F.PathStuck.Slot)[row] == 0)
                    continue;
                var (goalX, goalY, near) = (paths.RawColumn(F.PathGoalX.Slot)[row], paths.RawColumn(F.PathGoalY.Slot)[row], paths.RawColumn(F.PathNear.Slot)[row]);
                Order(entity, (int)goalX, (int)goalY, null, 0, near);
                if (Search.Searched - searchedBefore >= PlanTiles)
                    break;
            }
        }
        finally
        {
            Search.CloseQuota(previous);
        }
    }

    private readonly List<uint> _waiting = [];

    private static byte[] PointBytes(double[] arena, int start, int count)
    {
        var bytes = new byte[count * 8];
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), arena[start + i]);
        return bytes;
    }
}
