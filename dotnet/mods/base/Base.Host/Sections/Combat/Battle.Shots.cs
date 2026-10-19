using Base.Host.Kernel;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Combat.Refs;

namespace Base.Host.Sections.Combat;

// Выстрелы и взрывы: снаряды летят и бьют, долетев; взрывы и следы лучей доживают свой срок.
internal sealed partial class Battle
{
    /// <summary>Выстрел в полёте — строка пишущей таблицы.</summary>
    private struct ShotRow
    {
        public uint Entity;
        public Weapon? Weapon;
        public uint Player, Source, Target;
        public double FromX, FromY, ToX, ToY, Age, Life, X, Y;
        public bool Blocked, Air;
    }

    /// <summary>Взрыв тика: ground — пришёлся в землю, а не в летающего, и оставляет гарь.</summary>
    private readonly record struct Boom(double X, double Y, double Size, bool Ground);

    private ShotRow[] _shotRows = [];
    private int _shotCount;
    private int[] _shotOrder = [];
    private double[] _blastAgeOf = [];
    private int _blastCount;
    private readonly List<Boom> _blasts = [];

    /// <summary>Что исчезает в этом тике: долетевшие выстрелы и догоревшие взрывы.</summary>
    private readonly List<uint> _gone = [];

    private IShields? _shields;

    private void ReadShots(SystemContext ctx)
    {
        var table = ctx.Table(Fight.ShotsT);
        _shotCount = table.Count;
        Grow(ref _shotRows, _shotCount);
        Grow(ref _shotOrder, _shotCount);
        var player = table.Column(_shotPlayer);
        var source = table.Column(_shotSource);
        var target = table.Column(_shotTarget);
        var fromX = table.Column(_shotFromX);
        var fromY = table.Column(_shotFromY);
        var toX = table.Column(_shotToX);
        var toY = table.Column(_shotToY);
        var age = table.Column(_shotAge);
        var life = table.Column(_shotLife);
        var blocked = table.Column(_shotBlocked);
        var air = table.Column(_shotAir);
        var x = table.Column(_shotX);
        var y = table.Column(_shotY);
        var proto = table.Column(_shotProto);
        for (var row = 0; row < _shotCount; row++)
        {
            _shotRows[row] = new ShotRow
            {
                Entity = table.Entities[row], Weapon = Arms.WeaponByProto((int)proto[row]), Player = (uint)player[row], Source = (uint)source[row],
                Target = (uint)target[row], FromX = fromX[row], FromY = fromY[row], ToX = toX[row], ToY = toY[row], Age = age[row], Life = life[row],
                X = x[row], Y = y[row], Blocked = blocked[row] != 0, Air = air[row] != 0,
            };
            _shotOrder[row] = row;
        }
        Array.Sort(_shotOrder, 0, _shotCount, Comparer<int>.Create((a, b) => _shotRows[a].Entity.CompareTo(_shotRows[b].Entity)));
    }

    private void WriteShots(SystemContext ctx)
    {
        var table = ctx.Table(Fight.ShotsT);
        var toX = table.Column(_shotToX);
        var toY = table.Column(_shotToY);
        var age = table.Column(_shotAge);
        var x = table.Column(_shotX);
        var y = table.Column(_shotY);
        for (var row = 0; row < _shotCount; row++)
        {
            ref var shot = ref _shotRows[row];
            toX[row] = shot.ToX;
            toY[row] = shot.ToY;
            age[row] = shot.Age;
            x[row] = shot.X;
            y[row] = shot.Y;
        }
    }

    private void ReadBlasts(SystemContext ctx)
    {
        var table = ctx.Table(Fight.BlastsT);
        _blastCount = table.Count;
        Grow(ref _blastAgeOf, _blastCount);
        var age = table.Column(_blastAge);
        for (var row = 0; row < _blastCount; row++)
            _blastAgeOf[row] = age[row];
    }

    private void WriteBlasts(SystemContext ctx)
    {
        var age = ctx.Table(Fight.BlastsT).Column(_blastAge);
        for (var row = 0; row < _blastCount; row++)
            age[row] = _blastAgeOf[row];
    }

    /// <summary>Взрывы и следы лучей доживают свой срок.</summary>
    private void Age(SystemContext ctx)
    {
        var table = ctx.Table(Fight.BlastsT);
        var life = table.Column(_blastLife);
        var gone = new List<uint>();
        for (var row = 0; row < _blastCount; row++)
            if (++_blastAgeOf[row] >= life[row])
                gone.Add(table.Entities[row]);
        gone.Sort();
        _gone.AddRange(gone);
    }

    /// <summary>Снаряды летят; попадания — после обхода. Долетевшие и догоревшие исчезают.</summary>
    private void Fly(SystemContext ctx)
    {
        _ = ctx;
        var landed = new List<int>();
        var step = _step;
        for (var i = 0; i < _shotCount; i++)
        {
            var row = _shotOrder[i];
            ref var shot = ref _shotRows[row];
            shot.Age++;
            if (shot.Weapon is not { } weapon)
            {
                _gone.Add(shot.Entity);
                continue;
            }
            if (weapon.Speed == 0)
            {
                if (shot.Age >= shot.Life)
                    _gone.Add(shot.Entity);
                continue;
            }
            // Пуля и ракета следят за целью, пока она жива; ядро летит, куда выпущено.
            if (weapon.Shot != Shots.Shell && MarkOf(shot.Target) is { } mark)
            {
                shot.ToX = mark.X;
                shot.ToY = mark.Y;
            }
            var dx = shot.ToX - shot.X;
            var dy = shot.ToY - shot.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var move = weapon.Speed * step;
            // Купол чужого щита на пути: снаряд рвётся на его краю, урон берёт щит.
            if (_shields is { } shields)
            {
                var share = distance > 0 ? Math.Min(1, move / distance) : 1;
                var fromAir = MarkOf(shot.Source)?.Unit?.Air ?? false;
                if (shields.Stopper(shot.Player, shot.X, shot.Y, shot.X + dx * share, shot.Y + dy * share, fromAir) is { } stop)
                {
                    shields.Stop(stop.Dome, weapon.Damage);
                    shot.X = stop.X;
                    shot.Y = stop.Y;
                    _blasts.Add(new Boom(stop.X, stop.Y, weapon.HasSplash ? weapon.Splash : 0.2, false));
                    _gone.Add(shot.Entity);
                    continue;
                }
            }
            if (distance <= move || shot.Age >= shot.Life)
            {
                // Не догнавший цель снаряд сгорает там, где оказался.
                var reached = distance <= move;
                if (reached)
                {
                    shot.X = shot.ToX;
                    shot.Y = shot.ToY;
                }
                if (reached || weapon.HasSplash)
                    landed.Add(row);
                else
                    _gone.Add(shot.Entity);
                continue;
            }
            shot.X += dx / distance * move;
            shot.Y += dy / distance * move;
        }
        foreach (var row in landed)
        {
            ref var shot = ref _shotRows[row];
            var weapon = shot.Weapon!;
            Impact(weapon, shot.Player, shot.Source, shot.Target, shot.X, shot.Y, shot.FromX, shot.FromY, shot.Blocked);
            // Попадание в летающего земли не касается, даже если его сбили, пока снаряд летел.
            _blasts.Add(new Boom(shot.X, shot.Y, weapon.HasSplash ? weapon.Splash : 0.2, !shot.Air));
            _gone.Add(shot.Entity);
        }
        foreach (var entity in _gone)
            _call.Destroy(entity);
    }

    /// <summary>Выстрел: сущность с Position, Shot и Proto оружия.</summary>
    private void SpawnShot(Weapon weapon, uint player, uint source, uint target, double fromX, double fromY, double toX, double toY, int life,
        bool blocked = false, bool air = false)
    {
        var shot = _call.Spawn();
        _call.Add(shot, S.Position).Number(S.PositionX, fromX).Number(S.PositionY, fromY);
        _call.Add(shot, S.Proto).Number(S.ProtoId, weapon.Proto);
        _call.Add(shot, R.Shot).Number(R.ShotPlayer, player).Number(R.ShotSource, source).Number(R.ShotTarget, target)
            .Number(R.ShotFromX, fromX).Number(R.ShotFromY, fromY).Number(R.ShotToX, toX).Number(R.ShotToY, toY)
            .Number(R.ShotLife, life).Flag(R.ShotBlocked, blocked).Flag(R.ShotAir, air);
    }

    /// <summary>След на земле (traces.add): вид по порядку TRACE_LIFE, место, сущность, вид юнита, поворот, игрок, размер.</summary>
    private void Trace(int kind, double x, double y, double entity = 0, double type = 0, double facing = 0, double player = 0, double size = 0)
    {
        if (Traces.Layer is { } layer)
            _call.Commands.PushItem(layer, 0, x, y, [0, 0, kind, entity, type, facing, player, size]);
    }
}
