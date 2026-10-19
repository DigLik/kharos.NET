using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Energy;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Купола щитов (shields.ts). Генератор щита (BuildingSpec.shield) держит купол: урон по своим и союзным зданиям и юнитам
/// под ним от стрелков снаружи купола и от летающих гасит запас щита, а не прочность. Кто зашёл под купол, бьёт мимо щита.
/// Снаряды — пулю, ракету, ядро, бомбу — купол останавливает на себе: чужой снаряд, прилетевший из-за купола или сброшенный
/// летающим, рвётся на его краю, и его урон берёт запас щита. Луч и разряд — не снаряды: их урон купол гасит у цели.
/// Пробитый до нуля купол гаснет на <see cref="Down"/> секунд и потом растёт с нуля (<see cref="Charging"/>).
/// <para>
/// Купола тика (<see cref="Of"/>) читаются один раз на бой и живут в памяти вызова; урон купола пишется в Shield сразу же,
/// командой вызова (запрос только читает — там запись пропускается).
/// </para>
/// </summary>
internal sealed class Domes : IShields
{
    /// <summary>На сколько секунд гаснет пробитый купол (SHIELD_DOWN).</summary>
    public const double Down = 10;

    /// <summary>Купол, который сейчас гасит урон.</summary>
    public sealed class Dome
    {
        public uint Entity, Player;
        public double X, Y, Radius, Charge, Hit, Down;
    }

    private readonly Call _call;
    private readonly List<Dome> _list;
    private readonly Dictionary<uint, Dome> _byEntity = [];

    private Domes(Call call, List<Dome> list)
    {
        _call = call;
        _list = list;
        foreach (var dome in list)
            _byEntity[dome.Entity] = dome;
    }

    /// <summary>Купола, которые держатся сейчас: готовый генератор, с энергией и с запасом (domes). Нет ни одного щита в мире — null.</summary>
    public static Domes? Of(Call call)
    {
        if (Tools.Count(S.Shield) == 0)
            return null;
        var list = new List<Dome>();
        var tick = (double)call.Tick;
        foreach (var entity in Core.All(S.Shield, S.Building, S.Owner))
        {
            var charge = call.Num(entity, S.Shield, Refs.R.ShieldCharge);
            if (charge <= 0 || tick < call.Num(entity, S.Shield, Refs.R.ShieldDown) || call.Has(entity, S.Site))
                continue;
            if (Books.ShareOf(call, entity) == 0 || Shield(call, entity) is not { } spec)
                continue;
            var (x, y) = call.At(entity);
            var building = Factory.Building(call.BuildingType(entity))!;
            list.Add(new Dome
            {
                Entity = entity, Player = call.OwnerOf(entity), X = x + building.Width / 2, Y = y + building.Height / 2, Radius = spec.Radius,
                Charge = charge, Hit = call.Num(entity, S.Shield, Refs.R.ShieldHit, -1), Down = call.Num(entity, S.Shield, Refs.R.ShieldDown),
            });
        }
        return new Domes(call, list);
    }

    /// <summary>Щит вида здания; не генератор — null.</summary>
    public static Base.Shared.Data.ShieldSpec? Shield(Call call, uint entity) => Factory.Building(call.BuildingType(entity))?.Shield;

    /// <summary>Купола, как есть сейчас (для запроса и отладки).</summary>
    public IReadOnlyList<Dome> Items => _list;

    public bool Any => _list.Count > 0;

    /// <summary>Держит ли купол удар сейчас: с запасом и не погашен (holds).</summary>
    private bool Holds(Dome dome) => dome.Charge > 0 && _call.Tick >= dome.Down;

    public double Absorb(uint player, double x, double y, double damage, double fromX, double fromY, bool fromAir)
    {
        Dome? best = null;
        foreach (var dome in _list)
        {
            if (!Holds(dome) || DetMath.Hypot(x - dome.X, y - dome.Y) > dome.Radius)
                continue;
            if (!fromAir && DetMath.Hypot(fromX - dome.X, fromY - dome.Y) <= dome.Radius)
                continue;
            if (dome.Player != player && !Attack.Allied(_call, dome.Player, player))
                continue;
            if (best is null || dome.Charge > best.Charge)
                best = dome;
        }
        return best is null ? damage : damage - Drain(best, damage);
    }

    /// <summary>Списывает урон с запаса купола; запас кончился — купол гаснет на <see cref="Down"/>. Возвращает, сколько списано (drain).</summary>
    private double Drain(Dome dome, double damage)
    {
        var taken = Math.Min(dome.Charge, damage);
        dome.Charge -= taken;
        dome.Hit = _call.Tick;
        if (dome.Charge <= 0)
        {
            dome.Charge = 0;
            dome.Down = _call.Tick + Math.Floor(Down / Core.Step + 0.5);
        }
        if (_call.Writes)
            _call.Set(dome.Entity, S.Shield).Number(Refs.R.ShieldCharge, dome.Charge).Number(Refs.R.ShieldHit, dome.Hit).Number(Refs.R.ShieldDown, dome.Down);
        return taken;
    }

    public (double X, double Y, uint Dome)? Stopper(uint player, double x, double y, double x1, double y1, bool fromAir)
    {
        (Dome Dome, double X, double Y, double At)? best = null;
        var dx = x1 - x;
        var dy = y1 - y;
        foreach (var dome in _list)
        {
            if (!Holds(dome) || dome.Player == player || Attack.Allied(_call, dome.Player, player))
                continue;
            var inside = DetMath.Hypot(x - dome.X, y - dome.Y) <= dome.Radius;
            if (inside)
            {
                if (fromAir && (best is null || best.Value.At > 0))
                    best = (dome, x, y, 0);
                continue;
            }
            // Где отрезок пути входит в круг: наименьший корень |p + t·d − c| = r на [0, 1].
            var fx = x - dome.X;
            var fy = y - dome.Y;
            var a = dx * dx + dy * dy;
            if (a == 0)
                continue;
            var b = 2 * (fx * dx + fy * dy);
            var c = fx * fx + fy * fy - dome.Radius * dome.Radius;
            var disc = b * b - 4 * a * c;
            if (disc < 0)
                continue;
            var at = (-b - Math.Sqrt(disc)) / (2 * a);
            if (at < 0 || at > 1 || (best is not null && best.Value.At <= at))
                continue;
            best = (dome, x + dx * at, y + dy * at, at);
        }
        return best is { } found ? (found.X, found.Y, found.Dome.Entity) : null;
    }

    public void Stop(uint dome, double damage)
    {
        if (_byEntity.TryGetValue(dome, out var found))
            Drain(found, damage);
    }
}
