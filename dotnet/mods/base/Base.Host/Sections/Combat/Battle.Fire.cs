using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Mining;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

// Стрелки: высматривают врага, гонятся, наводятся и стреляют (основной обход fight).
internal sealed partial class Battle
{
    /// <summary>Носители, которым в этот тик уже проложен путь к цели.</summary>
    private readonly HashSet<uint> _chasing = [];
    private int _searches;

    private void Shoot()
    {
        CollectPursuers();
        var count = _armCount;
        // Обход каждый тик начинается с нового места: иначе норма поисков пути доставалась бы одним и тем же.
        var first = count > 0 ? (int)(_tick * ShooterStride % (ulong)count) : 0;
        for (var index = 0; index < count; index++)
            Fire(_shooters[(first + index) % count]);
    }

    private void Fire(int row)
    {
        var entity = _armEntity[row];
        if (_armCooldown[row] > 0)
            _armCooldown[row]--;
        // Турель стреляет с носителя: её место — своё, а чей выстрел и кому отвечать огнём — носителя.
        var carrier = CarrierOf(entity);
        var mounted = carrier != entity;
        var body = MarkOf(carrier);
        var mountRow = _mountRow.Get(entity);
        var turret = mountRow >= 0 ? Arms.Turret(_mountProtoOf[mountRow]) : null;
        var unitTurner = !mounted ? body?.Unit : null;
        var weapon = GunOf(entity);
        if (body is null || (unitTurner is null && turret is null) || weapon is null || _converting.Contains(carrier) || _off.Contains(carrier))
            return;
        var (selfX, selfY, player) = (_armX[row], _armY[row], body.Player);
        var range = weapon.Range + (body.Building is not null ? TowerRange : 0);

        var target = MarkOf(_armTarget[row]);
        if (target is not null && !CanHit(weapon, player, target))
            target = null;
        // Гонится за целью тот, кто ездит: юнит сам, турель — на своём носителе. Стреляет турель и на ходу.
        var mover = carrier;
        var moving = HasPath(mover);
        var unitCarrier = body.Unit;
        // Расстрелявший магазин молчит: цель бросает и новой не ищет, пока не пополнится.
        if (unitCarrier is not null && (Hooks.Empty?.Invoke(_call, carrier) ?? false))
        {
            if (_armChase[row])
                RemovePath(mover);
            _armTarget[row] = 0;
            _armChase[row] = false;
            _armOrdered[row] = false;
            return;
        }
        var tactics = TacticsOf(carrier);
        // Идущий за боеприпасами в погоню не уходит: бьёт, до кого достаёт, как держащий позицию.
        var stance = unitCarrier is not null ? (tactics?.Resupply == true ? Stance.Hold : StanceOf(carrier)) : Stance.Defensive;
        // Агрессивный в походе высматривает врага, как патрульный: бьёт всё, что увидел по дороге, и идёт дальше.
        var patrol = tactics is not null ? PatrolOf(carrier) : 0;
        var patrolling = patrol > 0 || (stance == Stance.Aggressive && tactics?.Away == true);
        // Турель носителя, едущего по приказу игрока, — только на выстрел: стреляет на ходу, но носитель за врагом не уводит.
        var led = moving && !patrolling && !InChase(carrier);
        var lookout = stance == Stance.Aggressive && unitCarrier is not null && !led ? Math.Max(range, unitCarrier.Sight) : range;

        // Занятый целью, которую выбрал сам и которая ему не опасна, бросает её ради того, кто опасен.
        if (target is not null && !_armOrdered[row] && stance != Stance.Passive && Core.OnTurn(_tick, entity, ScanTicks) && !Threatens(target, body.Air))
        {
            if (Look(weapon, player, body.Air, selfX, selfY, lookout, range, true) is { } danger)
            {
                target = danger;
                _armTarget[row] = danger.Entity;
                _armStuck[row] = 0;
                if (danger.DistanceTo(selfX, selfY) > range && unitCarrier is not null)
                {
                    _armChase[row] = true;
                    LeaveHome(carrier);
                }
            }
        }
        if (target is null)
        {
            _armTarget[row] = 0;
            _armChase[row] = false;
            _armOrdered[row] = false;
            // Свободный высматривает врага в пределах выстрела. Идущий по приказу не высматривает, патрульный — да.
            if (stance == Stance.Passive || (moving && !mounted && !patrolling) || !Core.OnTurn(_tick, entity, ScanTicks))
                return;
            // В походе агрессивный отвлекается только на тех, кто ему опасен.
            target = Look(weapon, player, body.Air, selfX, selfY, lookout, range, moving && patrol == 0 && tactics?.Away == true);
            if (target is null)
                return;
            _armTarget[row] = target.Entity;
            if (target.DistanceTo(selfX, selfY) > range)
            {
                _armChase[row] = true;
                LeaveHome(carrier);
            }
        }

        // Юнит в обороне, убежавший за врагом дальше поводка, бросает погоню и вернётся.
        if (!_armOrdered[row] && _armChase[row] && stance == Stance.Defensive && tactics is { Away: true }
            && Distance(selfX - tactics.HomeX - 0.5, selfY - tactics.HomeY - 0.5) > Leash)
        {
            _armTarget[row] = 0;
            _armChase[row] = false;
            return;
        }

        var distance = target.DistanceTo(selfX, selfY);
        if (distance > range)
        {
            Chase(row, entity, mover, mounted, moving, target, weapon);
            return;
        }
        // В мёртвой зоне оружие молчит. Сам выбранную цель стрелок бросает.
        if (distance < weapon.MinRange)
        {
            if (!_armChase[row])
                _armTarget[row] = 0;
            return;
        }
        // На выстреле: гнавшийся встаёт; едущий к своему месту у цели едет дальше (турель на нём стреляет на ходу).
        var path = moving ? _paths.GetValueOrDefault(mover) : null;
        var at = MarkOf(mover)!;
        var toSlot = path is { Slot: true } && _armChase[row] && path.Wait < SlotPatience
            && Distance(at.X - path.GoalX - 0.5, at.Y - path.GoalY - 0.5) > SlotReach;
        if (toSlot && !mounted)
            return;
        if (moving && !toSlot && (!mounted || _armChase[row] || patrolling))
            RemovePath(mover);

        var wanted = DetMath.Atan2(target.Y - selfY, target.X - selfX);
        double facing, radius;
        if (unitTurner is not null)
        {
            body.Facing = TurnToward(body.Facing, wanted, unitTurner.Spec.Turn * _step);
            body.Turned = true;
            facing = body.Facing;
            radius = unitTurner.Spec.Radius;
        }
        else
        {
            // Турель хранит поворот относительно носителя, а наводят её в мировых углах.
            var hull = HullFacing(carrier);
            var now = Wrap(hull + _mountAngleOf[mountRow]);
            var next = TurnToward(now, wanted, turret!.Turn * _step);
            _mountAngleOf[mountRow] = Wrap(next - hull);
            _mountTurned[mountRow] = true;
            facing = Wrap(hull + _mountAngleOf[mountRow]);
            radius = turret.Radius;
        }
        if (_armCooldown[row] > 0 || Math.Abs(Wrap(wanted - facing)) > Aim)
            return;
        // Турель здания стреляет боеприпасами с его склада; та, что на энергии, — энергией зоны.
        var power = PowerFor(body);
        if (power <= 0 || !Spend(body, weapon))
            return;
        _armCooldown[row] = Math.Max(1, Math.Floor(weapon.Reload / _step / power + 0.5));

        // Выстрел — из точки перед стрелком.
        var fromX = selfX + DetMath.Cos(facing) * radius;
        var fromY = selfY + DetMath.Sin(facing) * radius;
        // Стена на линии огня принимает выстрел на себя. Турели зданий бьют поверх; летающего стена не укрывает.
        var overWall = body.Building is not null || body.Air || target.Air || !weapon.StopsAtWall || weapon.Lob;
        var wall = overWall ? null : WallOnPath(player, selfX, selfY, target.X, target.Y);
        var hitX = wall?.X ?? target.X;
        var hitY = wall?.Y ?? target.Y;
        if (weapon.Speed > 0)
        {
            // Ядру отпущено время до точки падения, наводящимся — пока не улетят слишком далеко.
            var reach = weapon.Shot == Shots.Shell ? Distance(hitX - fromX, hitY - fromY) : range * Overfly;
            var life = Math.Max(1, (int)Math.Ceiling(reach / weapon.Speed / _step));
            SpawnShot(weapon, player, carrier, wall?.Wall.Entity ?? target.Entity, fromX, fromY, hitX, hitY, life, wall is not null, wall is null && target.Air);
            return;
        }
        // Луч стены не обходит: лазер бьёт в неё, а не в цель за ней.
        if (wall is { } blocked)
        {
            SpawnShot(weapon, player, carrier, blocked.Wall.Entity, fromX, fromY, hitX, hitY, BeamTicks);
            Trace(Traces.Burn, hitX, hitY);
            HitMark(blocked.Wall, weapon.Damage, weapon, carrier, fromX, fromY);
            return;
        }
        // Лазер, разряд и огонь бьют сразу; разряд и огонь перескакивают дальше на ближайших врагов.
        var damage = weapon.Damage;
        var (beamX, beamY) = (fromX, fromY);
        Mark? next2 = target;
        var struck = new List<uint>();
        for (var jump = 0; next2 is not null && jump <= weapon.Chain; jump++)
        {
            var current = next2;
            struck.Add(current.Entity);
            SpawnShot(weapon, player, carrier, current.Entity, beamX, beamY, current.X, current.Y, BeamTicks);
            // Разряд, перескочивший под куполом, всё равно пришёл снаружи: щит судит по стрелку.
            HitMark(current, damage, weapon, carrier, fromX, fromY);
            // Луч прожигает землю там, куда попал.
            if (!current.Air)
                Trace(Traces.Burn, current.X, current.Y);
            damage *= ChainDecay;
            (beamX, beamY) = (current.X, current.Y);
            next2 = null;
            var nearest = ChainRange;
            foreach (var mark in _grid.Near(current.X, current.Y, ChainRange))
            {
                if (struck.Contains(mark.Entity) || !CanHit(weapon, player, mark))
                    continue;
                var d = mark.DistanceTo(current.X, current.Y);
                if (d >= nearest)
                    continue;
                nearest = d;
                next2 = mark;
            }
        }
    }

    /// <summary>Поводок обороны (LEASH в tactics.ts).</summary>
    private const double Leash = 8;

    private static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    /// <summary>
    /// Ближайший враг в пределах обзора (look): сперва опасные стрелку, потом юниты, потом здания; dangerous — только
    /// опасные.
    /// </summary>
    private Mark? Look(Weapon weapon, uint player, bool air, double x, double y, double lookout, double range, bool dangerous)
    {
        Mark? found = null;
        var best = double.PositiveInfinity;
        foreach (var mark in _grid.Near(x, y, lookout))
        {
            if (!CanHit(weapon, player, mark))
                continue;
            var danger = Threatens(mark, air);
            if (dangerous && !danger)
                continue;
            var distance = mark.DistanceTo(x, y);
            if (distance > lookout || distance < weapon.MinRange)
                continue;
            var order = distance + (mark.Armor == Arms.BuildingArmor ? range : 0) + (danger ? 0 : Harmless);
            if (order >= best)
                continue;
            best = order;
            found = mark;
        }
        return found;
    }

    /// <summary>Цель вне выстрела: гонящийся юнит едет на своё место у цели (или на выстрел от неё); турели — с носителем.</summary>
    private void Chase(int row, uint entity, uint mover, bool mounted, bool moving, Mark target, Weapon weapon)
    {
        if (!_armChase[row] || MarkOf(mover)?.Unit is null)
        {
            _armTarget[row] = 0;
            return;
        }
        // Носитель везёт к цели одна его турель за тик.
        if (mounted && _chasing.Contains(mover))
            return;
        // Вставший трогается снова не каждый тик, а не нашедший пути — всё реже.
        var every = moving ? ChaseTicks : ScanTicks << (int)Math.Min(_armStuck[row], StuckLimit);
        if (!Core.OnTurn(_tick, entity, every))
            return;
        // Норма поисков тика вышла — дальше все ждут своей очереди, что бы ни вышло с местом: его и не раздаём.
        if (_searches >= ChaseSearches)
            return;
        var slot = SlotOf(target, mover);
        var goalX = (int)Math.Floor(slot?.X ?? target.X);
        var goalY = (int)Math.Floor(slot?.Y ?? target.Y);
        // Цель с места не сошла — прежний путь годится; но у самого места путь прокладывается заново.
        var path = _paths.GetValueOrDefault(mover);
        var at = MarkOf(mover)!;
        var close = slot is { } s && Distance(at.X - s.X, at.Y - s.Y) < SlotReplan;
        if (path is not null && !close && path.Slot == slot.HasValue && Math.Abs(path.GoalX - goalX) <= 1 && Math.Abs(path.GoalY - goalY) <= 1)
            return;
        // Остальные дождутся своей очереди: сотня поисков пути за тик — заметная запинка.
        if (_searches++ >= ChaseSearches)
            return;
        // Идти надо не в саму цель, а на выстрел от неё.
        var near = slot.HasValue ? 0 : Math.Max(0, weapon.Range - ChaseMargin) + Math.Max(target.Width, target.Height) / 2;
        _chasing.Add(mover);
        var plan = Tools.Plan(mover, goalX, goalY, near, 0, PathFlags.None, ChasePool, ChaseTiles);
        switch (plan.Status)
        {
            case PlanStatus.Found:
                Moves.WritePath(_call, mover, plan.Points, plan.GoalX, plan.GoalY, false, plan.Roads, 0, near);
                if (slot.HasValue)
                    _call.Set(mover, S.Path).Flag(S.PathSlot, true);
                _paths[mover] = new PathState { GoalX = plan.GoalX, GoalY = plan.GoalY, Wait = 0, Slot = slot.HasValue };
                _armStuck[row] = 0;
                break;
            case PlanStatus.None:
                RemovePath(mover);
                _armStuck[row]++;
                break;
            default:
                // Поиск оборвала норма: путь, может, и есть; прежний — как был.
                if (_paths.TryGetValue(mover, out var kept))
                {
                    _armStuck[row] = 0;
                    if (slot.HasValue && !kept.Slot)
                    {
                        kept.Slot = true;
                        _call.Set(mover, S.Path).Flag(S.PathSlot, true);
                    }
                }
                break;
        }
    }

    /// <summary>На какую долю скорострельности хватает энергии носителю: потребитель — доля зоны, остальные — 1.</summary>
    private double PowerFor(Mark body) => body.Building is { Consumer: true } ? Books.ShareOf(_call, body.Entity) : 1;

    /// <summary>Списывает боеприпасы на выстрел (spend): у турели здания — со склада, у юнита — из магазина. false — нечем.</summary>
    private bool Spend(Mark body, Weapon weapon)
    {
        if (body.Unit is { } unit)
        {
            if (!(Hooks.UnitHasAmmo?.Invoke(_call, body.Entity, unit.Type, weapon) ?? true))
                return false;
            Hooks.SpendAmmo?.Invoke(_call, body.Entity, unit.Type, weapon);
            return true;
        }
        if (body.Building is not { Spec.Ammo: true } || weapon.Ammo == 0)
            return true;
        if (!_call.Has(body.Entity, S.Stock) || Stocks.Amount(_call, body.Entity, Ammo) < weapon.Ammo - 1e-9)
            return false;
        Stocks.Take(_call, body.Entity, Ammo, weapon.Ammo);
        return true;
    }

    /// <summary>Груз боеприпасов.</summary>
    private const string Ammo = "ammo";

    /// <summary>
    /// Первая чужая стена на отрезке выстрела и точка, где снаряд в неё входит (wallOnPath); null — путь свободен. Своя и
    /// союзная стена не мешает. Стены — из меток тика по тайлам: инструмент Ray движка не знает, чья стена и где в неё
    /// входит снаряд, а таблица меток и так собрана.
    /// </summary>
    private (Mark Wall, double X, double Y)? WallOnPath(uint player, double x0, double y0, double x1, double y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        if ((dx == 0 && dy == 0) || _walls.Count == 0)
            return null;
        var lastX = (int)Math.Floor(x1);
        var lastY = (int)Math.Floor(y1);
        var stepX = dx > 0 ? 1 : -1;
        var stepY = dy > 0 ? 1 : -1;
        var deltaX = dx != 0 ? Math.Abs(1 / dx) : double.PositiveInfinity;
        var deltaY = dy != 0 ? Math.Abs(1 / dy) : double.PositiveInfinity;
        var tileX = (int)Math.Floor(x0);
        var tileY = (int)Math.Floor(y0);
        var crossX = dx != 0 ? (dx > 0 ? tileX + 1 - x0 : x0 - tileX) / Math.Abs(dx) : double.PositiveInfinity;
        var crossY = dy != 0 ? (dy > 0 ? tileY + 1 - y0 : y0 - tileY) / Math.Abs(dy) : double.PositiveInfinity;
        double t = 0;
        for (var tile = 0; tile < 64; tile++)
        {
            if (_walls.TryGetValue(TileKey(tileX, tileY), out var wall) && wall.Player != player && !Allied(player, wall.Player))
                return (wall, x0 + dx * t, y0 + dy * t);
            if (tileX == lastX && tileY == lastY)
                return null;
            if (crossX < crossY)
            {
                t = crossX;
                tileX += stepX;
                crossX += deltaX;
            }
            else
            {
                t = crossY;
                tileY += stepY;
                crossY += deltaY;
            }
        }
        return null;
    }
}
