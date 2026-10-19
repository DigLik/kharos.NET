using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Mining;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Combat.Refs;

namespace Base.Host.Sections.Combat;

// Урон и гибель: попадания, взрывы, обречённые, аварии, едкое болото, гибель с остовами и гарью, трещины фундамента.
internal sealed partial class Battle
{
    /// <summary>Отмечает гибель: в конце тика метка уходит из мира.</summary>
    private void Kill(Mark mark)
    {
        if (mark.Dead)
            return;
        mark.Dead = true;
        _dead.Add(mark);
    }

    /// <summary>
    /// Наносит урон (hit). source — кто стрелял: уцелевший отвечает ему огнём; (fromX, fromY) — откуда: щит гасит только
    /// пришедшее из-за купола.
    /// </summary>
    private void HitMark(Mark mark, double amount, Weapon weapon, uint source, double fromX, double fromY)
    {
        if (mark.Dead)
            return;
        // Тик попадания помнит ремонт: под огнём не чинят.
        mark.Hit = true;
        var damage = amount * weapon.Vs[mark.Armor];
        if (_shields is { } shields)
            damage = shields.Absorb(mark.Player, mark.X, mark.Y, damage, fromX, fromY, MarkOf(source)?.Air ?? false);
        mark.Value -= damage / mark.Hp;
        if (mark.Value <= 0)
        {
            Kill(mark);
            return;
        }
        var from = MarkOf(source);
        // Идущий по приказу не отвлекается; патрульный — отвечает.
        if (from is null || (HasPath(mark.Entity) && PatrolOf(mark.Entity) == 0))
            return;
        var unit = mark.Unit is not null;
        var stance = unit ? StanceOf(mark.Entity) : Stance.Defensive;
        // Кому велено не стрелять, тот не отвечает.
        if (stance == Stance.Passive)
            return;
        // Отвечают и сам юнит, и его турели. Занятый целью, которую выбрал сам и которая ему не опасна, бросает её.
        Answer(mark, mark.Entity, from, source, unit, stance);
        foreach (var turret in TurretsOf(mark.Entity))
            Answer(mark, turret, from, source, unit, stance);
    }

    private void Answer(Mark mark, uint gunner, Mark from, uint source, bool unit, Stance stance)
    {
        var row = _armRow.Get(gunner);
        if (row < 0 || GunOf(gunner) is not { } own || !CanHit(own, mark.Player, from))
            return;
        var target = _armTarget[row];
        if (target != 0 && (_armOrdered[row] || MarkOf(target) is not { } busy || Threatens(busy, mark.Air)))
            return;
        _armTarget[row] = source;
        // Здание отвечает, только если само достаёт до стрелка; держащий позицию тоже не гонится.
        _armChase[row] = unit && stance != Stance.Hold;
        _armOrdered[row] = false;
        _armStuck[row] = 0;
        if (_armChase[row])
            LeaveHome(mark.Entity);
    }

    /// <summary>
    /// Попадание в точку (impact): урон цели или всем врагам в радиусе взрыва. blocked — выстрел остановила стена: взрыв
    /// достаётся только стенам.
    /// </summary>
    private void Impact(Weapon weapon, uint player, uint source, uint target, double x, double y, double fromX, double fromY, bool blocked)
    {
        if (!weapon.HasSplash)
        {
            if (MarkOf(target) is { } mark && CanHit(weapon, player, mark))
                HitMark(mark, weapon.Damage, weapon, source, fromX, fromY);
            return;
        }
        // Список поиска общий на все поиски: попадания его не трогают, но копия надёжнее.
        _impact.Clear();
        _impact.AddRange(_grid.Near(x, y, weapon.Splash));
        foreach (var mark in _impact)
        {
            if (blocked && !mark.Wall)
                continue;
            if (!CanHit(weapon, player, mark))
                continue;
            var distance = mark.DistanceTo(x, y);
            if (distance > weapon.Splash)
                continue;
            HitMark(mark, weapon.Damage * (1 - (1 - SplashEdge) * (distance / weapon.Splash)), weapon, source, fromX, fromY);
        }
    }

    private readonly List<Mark> _impact = [];

    /// <summary>Сдавшийся игрок взрывает своё: пришёл черёд — гибель, как от выстрела.</summary>
    private void Doom(SystemContext ctx)
    {
        var table = ctx.Table(Fight.DoomedT);
        var left = table.Column(_doomedLeft);
        var order = new int[table.Count];
        for (var row = 0; row < order.Length; row++)
            order[row] = row;
        var entities = table.Entities.ToArray();
        Array.Sort(order, (a, b) => entities[a].CompareTo(entities[b]));
        foreach (var row in order)
        {
            if (--left[row] > 0)
                continue;
            if (MarkOf(entities[row]) is { } mark)
                Kill(mark);
            else
                _call.Destroy(entities[row]);
        }
    }

    /// <summary>Аварии и ядерные удары бьют всех вокруг без разбора — своих тоже; щит их не держит, защита новичка — да.</summary>
    private void Bursts(SystemContext ctx)
    {
        var table = ctx.Table(Fight.BurstsT);
        var radii = table.Column(_burstRadius);
        var damages = table.Column(_burstDamage);
        var cores = table.Column(_burstCore);
        var xs = table.Column(_burstX);
        var ys = table.Column(_burstY);
        var order = new int[table.Count];
        for (var row = 0; row < order.Length; row++)
            order[row] = row;
        var entities = table.Entities.ToArray();
        Array.Sort(order, (a, b) => entities[a].CompareTo(entities[b]));
        foreach (var row in order)
        {
            var (x, y, radius, damage, core) = (xs[row], ys[row], radii[row], damages[row], cores[row]);
            foreach (var mark in _grid.Near(x, y, radius))
            {
                if (mark.Dead || _safe.Contains(mark.Player))
                    continue;
                var distance = mark.DistanceTo(x, y);
                if (distance > radius)
                    continue;
                mark.Hit = true;
                var share = distance <= core ? 1 : (radius - distance) / (radius - core);
                mark.Value -= damage * share / mark.Hp;
                if (mark.Value <= 0)
                    Kill(mark);
            }
            _blasts.Add(new Boom(x, y, radius, true));
            _call.Destroy(entities[row]);
        }
    }

    /// <summary>Едкое болото жжёт стоящих в нём наземных (урон местности ходовой, moves.json harm); мост не жжёт.</summary>
    private void Swamp()
    {
        List<Mark>? ground = null;
        foreach (var mark in _marks)
            if (mark.Unit is { MaxHarm: > 0 } && !mark.Air && mark.Radius != 0 && !mark.Dead)
                (ground ??= []).Add(mark);
        if (ground is null)
            return;
        var xs = new double[ground.Count];
        var ys = new double[ground.Count];
        for (var i = 0; i < ground.Count; i++)
        {
            xs[i] = Math.Floor(ground[i].X) + 0.5;
            ys[i] = Math.Floor(ground[i].Y) + 0.5;
        }
        var terrain = new TerrainInfo[ground.Count];
        if (!Tools.Terrain(xs, ys, terrain))
            return;
        for (var i = 0; i < ground.Count; i++)
        {
            // Болото: на солончаках — рассол, в красных пустошах — едкое.
            if (terrain[i].Terrain != 0)
                continue;
            var key = terrain[i].Biome switch
            {
                2 => "toxic",
                1 => "brine",
                _ => "swamp",
            };
            var mark = ground[i];
            if (!mark.Unit!.Harm.TryGetValue(key, out var harm) || harm <= 0)
                continue;
            var (x, y) = ((int)Math.Floor(mark.X), (int)Math.Floor(mark.Y));
            if (Paving.IsPaved(PaveRules.Road, x, y))
                continue;
            // Отметка попадания: пока стоит в болоте, не чинится и сам не залечивается.
            mark.Hit = true;
            mark.Value -= harm * _step;
            if (mark.Value <= 0)
                Kill(mark);
        }
    }

    /// <summary>Погибшие уходят: взрыв, остов техники, авария реактора, груз на землю.</summary>
    private void Die()
    {
        foreach (var mark in _dead)
        {
            _blasts.Add(new Boom(mark.X, mark.Y, mark.Radius != 0 ? mark.Radius * 2 : Math.Max(mark.Width, mark.Height) * 0.7, !mark.Air));
            // От техники остаётся остов, от пехоты — только гарь.
            if (mark.Unit is { } unit && !mark.Air && unit.Spec.Kind != "infantry")
                Trace(Traces.Wreck, mark.X, mark.Y, mark.Entity, mark.Proto, mark.Facing, mark.Player);
            Hooks.ReleaseHauler?.Invoke(_call, mark.Entity);
            // Реактор с топливом взрывается в следующем бою; топливо сгорает в аварии и на землю не падает.
            Reactors.Meltdown(_call, mark.Entity);
            // Груз со склада здания и из кузова остаётся на земле.
            Drops.DropCargo(_call, mark.Entity);
            _call.Destroy(mark.Entity);
        }
        foreach (var (x, y, size, ground) in _blasts)
        {
            var blast = _call.Spawn();
            _call.Add(blast, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
            _call.Add(blast, R.Blast).Number(R.BlastSize, size).Number(R.BlastLife, Math.Floor((0.25 + size * 0.2) / _step + 0.5));
            if (!ground)
                continue;
            Trace(Traces.Scar, x, y, size: size);
            Slabs.Break(_call, x, y, size);
        }
    }
}
