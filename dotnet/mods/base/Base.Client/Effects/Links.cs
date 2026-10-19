using System.Buffers.Binary;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>
/// Лучи работы и подачи (combatPass.ts): от строителя или ремонтника к тому, что он строит, чинит, разбирает или копает
/// (Repair.target — хозяину), с мерцанием, искрами и светом сварки; транспортный луч Beam.links (9 байт на связь: u32
/// сущность, u8 тянет ли, i32 номер Proto груза) — бирюзовая полоса, по которой бегут комочки цвета груза; купола щитов —
/// тусклые в покое, ярче с запасом, вспыхивают от погашенного попадания.
/// </summary>
internal sealed class Links(Fx fx, Lacks lacks)
{
    /// <summary>Искры сварки (WELD_SPARKS, WELD_SPREAD, WELD_RATE).</summary>
    private const int WeldSparks = 4;
    private const double WeldSpread = 0.35, WeldRate = 12;

    /// <summary>Груз на луче: шаг и скорость (CARGO_STEP, CARGO_SPEED); насколько луч уходит в основание (BEAM_INSET).</summary>
    private const double CargoStep = 0.3, CargoSpeed = 2.5, BeamInset = 0.4;

    /// <summary>Работа — ближняя к ремонтнику точка основания с отступом (WORK_INSET); точность наводки (REPAIR_AIM).</summary>
    private const double WorkInset = 0.4, RepairAim = 0.2;

    /// <summary>Сколько секунд купол вспыхивает после погашенного попадания (DOME_FLASH).</summary>
    private const double DomeFlash = 0.6;

    private static readonly Rgb Mend = new(0.3f, 1, 0.55f), Wreck = new(1, 0.22f, 0.1f), Blast = new(1, 0.12f, 0.08f), Grade = new(0.55f, 0.3f, 0.1f);
    private static readonly Rgb Teal = new(0.25f, 0.95f, 0.85f), Dome = new(0.35f, 0.7f, 1), Core = new(1, 1, 1);

    private readonly record struct Work(uint From, uint To, double FromX, double FromY, double ToX, double ToY, Rgb Color);

    private readonly List<Work> _works = [];

    /// <summary>Работы этого кадра и свет сварки (до рисунков).</summary>
    public void Emit()
    {
        _works.Clear();
        var c = fx.C;
        var credits = double.NaN;
        foreach (var entity in c.Each(fx.Repair, fx.Position))
        {
            var value = c.Get(entity, fx.Repair)?.Number(fx.Repair["target"]) ?? 0;
            if (value <= 0)
                continue;
            var target = (uint)value;
            // На ходу, недостроенным и разворачиваясь не работают.
            if (!c.Alive(target) || c.Has(entity, fx.Path) || c.Has(entity, fx.Site) || c.Has(entity, fx.Converting) || c.Pose(entity) is not { } from)
                continue;
            if (Stalled(target, ref credits) || BoundsOf(target) is not { } work)
                continue;
            var (fromX, fromY) = c.Has(entity, fx.Unit) ? (from.X, from.Y) : fx.CenterOf(entity) ?? (from.X, from.Y);
            var insetX = Math.Min(WorkInset, work.W / 2);
            var insetY = Math.Min(WorkInset, work.H / 2);
            var toX = Math.Min(work.X + work.W - insetX, Math.Max(work.X + insetX, fromX));
            var toY = Math.Min(work.Y + work.H - insetY, Math.Max(work.Y + insetY, fromY));
            // Юнит работает, только когда смотрит на цель (isAimed).
            if (c.Has(entity, fx.Unit) && Math.Abs(Fx.Wrap(DetMath.Atan2(toY - fromY, toX - fromX) - from.Angle)) > RepairAim)
                continue;
            // Движущийся юнит-цель — там, где нарисован; луч начинается у края ремонтника.
            if (c.Has(target, fx.Unit) && c.Pose(target) is { } drawn)
                (toX, toY) = (drawn.X, drawn.Y);
            var length = Fx.Hypot(toX - fromX, toY - fromY);
            if (c.Has(entity, fx.Unit) && length > 0)
            {
                var reach = Math.Min(fx.Radius(entity), length) / length;
                fromX += (toX - fromX) * reach;
                fromY += (toY - fromY) * reach;
            }
            var demolish = c.Get(target, fx.Site) is { } site && site.Flag(fx.Site["demolish"]) || c.Get(target, fx.Pave) is { } pave && pave.Flag(fx.Pave["remove"]);
            var dig = c.Get(target, fx.Dig) is { } digging ? fx.Str(digging.Number(fx.Dig["kind"])) : null;
            var color = demolish ? Wreck : dig == "peak" ? Blast : dig == "cliff" ? Grade : Mend;
            _works.Add(new Work(entity, target, fromX, fromY, toX, toY, color));
            // Свет сварки мерцает.
            c.Draw.Light(toX, toY, 12, 5, (float)(0.5 + 0.5 * Fx.Noise(entity + target, Math.Floor(fx.Time * WeldRate * 2))));
        }
        foreach (var entity in c.Each(fx.Beam))
            foreach (var (target, pulling, _) in LinksOf(entity))
                if (Tractor(entity, target, pulling) is { } ends)
                    c.Draw.Light((ends.FromX + ends.ToX) / 2, (ends.FromY + ends.ToY) / 2, 10, 4, 0.5f);
    }

    /// <summary>
    /// Стоит ли работа (isStalled): стройка без материалов, починка без кредитов (если она платная); покрытие, рытьё и
    /// разбор не стоят.
    /// </summary>
    private bool Stalled(uint target, ref double credits)
    {
        var c = fx.C;
        if (c.Has(target, fx.Pave) || c.Has(target, fx.Dig))
            return false;
        if (c.Get(target, fx.Site) is { } site)
            return !site.Flag(fx.Site["demolish"]) && lacks.Of.TryGetValue(target, out var good) && good != Lacks.Credits;
        if (fx.RepairCost <= 0)
            return false;
        if (double.IsNaN(credits))
            credits = fx.Credits();
        return credits <= 0;
    }

    /// <summary>Прямоугольник работы: основание здания, площадки, покрытия; у юнита — описанный квадрат.</summary>
    private (double X, double Y, double W, double H)? BoundsOf(uint entity)
    {
        var c = fx.C;
        if (c.Pose(entity) is not { } at)
            return null;
        if (c.Has(entity, fx.Unit))
        {
            var radius = fx.Radius(entity);
            return (at.X - radius, at.Y - radius, radius * 2, radius * 2);
        }
        if (c.Has(entity, fx.Pave) || c.Has(entity, fx.Dig))
            return (at.X, at.Y, 1, 1);
        return fx.SizeOf(entity) is { } size ? (at.X, at.Y, size.W, size.H) : (at.X, at.Y, 1, 1);
    }

    /// <summary>Связи луча: цель, тянет ли сюда, номер груза.</summary>
    private IEnumerable<(uint Target, bool Pulling, int Good)> LinksOf(uint entity)
    {
        if (fx.C.Get(entity, fx.Beam) is not { } beam)
            yield break;
        var bytes = beam.Bytes(fx.Beam["links"]);
        for (var at = 0; at + 9 <= bytes.Length; at += 9)
            yield return (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), bytes[at + 4] != 0,
                fx.GoodOf(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 5))));
    }

    /// <summary>Конец транспортного луча на сущности, протянутого к (x, y): юнит — где нарисован, здание — ближняя часть основания.</summary>
    private (double X, double Y)? EndOf(uint entity, double x, double y)
    {
        var c = fx.C;
        if (c.Pose(entity) is not { } at)
            return null;
        if (c.Has(entity, fx.Unit) || fx.SizeOf(entity) is not { } size)
            return (at.X, at.Y);
        var insetX = Math.Min(BeamInset, size.W / 2);
        var insetY = Math.Min(BeamInset, size.H / 2);
        return (Math.Min(at.X + size.W - insetX, Math.Max(at.X + insetX, x)), Math.Min(at.Y + size.H - insetY, Math.Max(at.Y + insetY, y)));
    }

    /// <summary>Концы транспортного луча: откуда груз уходит и куда приходит (tractorOf).</summary>
    private (double FromX, double FromY, double ToX, double ToY)? Tractor(uint entity, uint target, bool pulling)
    {
        if (fx.CenterOf(entity) is not { } own || fx.CenterOf(target) is not { } other)
            return null;
        if (EndOf(entity, other.X, other.Y) is not { } near || EndOf(target, own.X, own.Y) is not { } far)
            return null;
        var (from, to) = pulling ? (far, near) : (near, far);
        return (from.X, from.Y, to.X, to.Y);
    }

    public void Draw()
    {
        var time = fx.Time;
        // Работа: мерцающий луч, точки на концах и искры, которые разлетаются от места работы и гаснут.
        foreach (var work in _works)
        {
            var (fromX, fromY, toX, toY) = (work.FromX, work.FromY, work.ToX, work.ToY);
            if (!fx.Visible(fromX, fromY) && !fx.Visible(toX, toY))
                continue;
            var seed = work.From * 31.0 + work.To;
            var flicker = 0.55 + 0.45 * Fx.Noise(seed, Math.Floor(time * WeldRate * 2));
            fx.Line(fromX, fromY, toX, toY, 4, work.Color, 0.35 * flicker);
            fx.Line(fromX, fromY, toX, toY, 1.5, work.Color, flicker);
            fx.Dot(fromX, fromY, 4, work.Color, 0.8);
            fx.Dot(toX, toY, 7, work.Color, flicker);
            fx.Dot(toX, toY, 3, Core, flicker);
            var beat = time * WeldRate;
            var phase = beat - Math.Floor(beat);
            for (var i = 0; i < WeldSparks; i++)
            {
                var angle = Fx.Noise(seed + i * 7, Math.Floor(beat)) * Math.PI * 2;
                var reach = WeldSpread * (0.4 + 0.6 * Fx.Noise(seed + i * 13, Math.Floor(beat))) * phase;
                // Искра падает: чем дальше улетела, тем ниже.
                fx.Dot(toX + DetMath.Cos(angle) * reach, toY + DetMath.Sin(angle) * reach + reach * phase * 0.6, 2, i % 2 == 1 ? Core : work.Color, 1 - phase);
            }
        }
        Domes(time);
        Tractors(time);
    }

    /// <summary>Купола щитов: кромка и два кольца ближе к макушке — сверху купол читается как полусфера.</summary>
    private void Domes(double time)
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Shield, fx.Building, fx.Position))
        {
            if (c.Has(entity, fx.Site) || fx.BuildingOf(entity)?.Spec is not { Shield: { } spec } building || c.Get(entity, fx.Shield) is not { } shield || c.Pose(entity) is not { } at)
                continue;
            var charge = shield.Number(fx.Shield["charge"]);
            if (charge <= 0 || spec.Capacity <= 0)
                continue;
            var (cx, cy) = (at.X + building.Width / 2, at.Y + building.Height / 2);
            var radius = spec.Radius;
            if (!fx.Visible(cx - radius, cy - radius, radius * 2, radius * 2, 3))
                continue;
            var hit = shield.Number(fx.Shield["hit"]);
            var since = hit < 0 ? double.PositiveInfinity : (c.Tick - hit + fx.Alpha) * fx.Step;
            var flash = since < DomeFlash ? 1 - since / DomeFlash : 0;
            var level = 0.12 + 0.18 * (charge / spec.Capacity) + 0.6 * flash;
            var shimmer = 0.8 + 0.2 * DetMath.Sin(time * 2 + entity);
            foreach (var (share, lift, thickness, part) in new[] { (1.0, 0.0, 3.0, 1.0), (0.75, 0.18, 2.0, 0.5), (0.42, 0.32, 2.0, 0.35) })
            {
                var alpha = level * part * shimmer;
                if (alpha > 0.004)
                    c.Draw.Circle(cx, cy - radius * lift, radius * share, Dome.Hex, (float)(fx.Sized(thickness) / fx.Pixel), (float)Math.Min(1, alpha));
            }
        }
    }

    /// <summary>Транспортный луч: полоса, концы и груз, который бежит от отдающего к забирающему, проявляясь и тая у концов.</summary>
    private void Tractors(double time)
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Beam))
            foreach (var (target, pulling, good) in LinksOf(entity))
            {
                if (Tractor(entity, target, pulling) is not var (fromX, fromY, toX, toY))
                    continue;
                if (!fx.Visible(fromX, fromY) && !fx.Visible(toX, toY))
                    continue;
                var shimmer = 0.75 + 0.25 * DetMath.Sin(time * 9 + entity + target);
                fx.Line(fromX, fromY, toX, toY, 7, Teal, 0.18 * shimmer);
                fx.Line(fromX, fromY, toX, toY, 2, Teal, 0.55 * shimmer);
                fx.Dot(fromX, fromY, 6, Teal, 0.7);
                fx.Dot(toX, toY, 6, Teal, 0.7);
                var length = Fx.Hypot(toX - fromX, toY - fromY);
                if (length <= 0)
                    continue;
                var cargo = Rgb.Of(Icons.ColorOf(good >= 0 ? Icons.Goods[good] : ""));
                var count = (int)Math.Floor(length / CargoStep);
                var shift = time * CargoSpeed / CargoStep % 1;
                for (var i = 0; i < count; i++)
                {
                    var share = (i + shift) * CargoStep / length;
                    var level = Math.Min(1, Math.Min(share * 4, (1 - share) * 4));
                    var (x, y) = (fromX + (toX - fromX) * share, fromY + (toY - fromY) * share);
                    fx.Dot(x, y, 3, cargo, level);
                    fx.Dot(x, y, 2, cargo, level * 0.6);
                }
            }
    }
}
