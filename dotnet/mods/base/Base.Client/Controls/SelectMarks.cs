using System.Buffers.Binary;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Отметки выделения — перенос selectionPass.ts: кольца вокруг выбранных юнитов и рамки вокруг зданий, пути (пунктир по
/// точкам), цели стрелков, очередь приказов Shift, точка сбора, круг раздачи машины снабжения, цифры групп над юнитами,
/// полоски производства, превращения и стройки, рамки заложенных площадок, рамка, которую тянут мышью. Рисуется поверх
/// мира в тайлах (слой Draw выше освещения — ночью не темнеет).
/// </summary>
internal sealed class SelectMarks(BaseState state, PlaceWorld world)
{
    // Зелёный выделения приглушённый: чистый резал глаз на тёмной земле. Яркость колец и рамок — 0,7 (настройка prefs().selection).
    public const uint Selected = 0x73eb8c;
    private const float Brightness = 0.7f;
    private const uint Target = 0xff594d, BarBack = 0x080d14, Converting = 0xffcc4d, Producing = 0x59a6ff, GroupText = 0xf2d973;
    private const double RingMargin = 0.2;
    private const float BoxFillAlpha = 0.12f, HitAlpha = 0.5f, NetworkAlpha = 0.35f;
    private const float PathDot = 3, PathGap = 14, PathAlpha = 0.55f, QueueAlpha = 0.35f, TargetAlpha = 0.7f, GroupAlpha = 0.75f;
    private const double PathGoal = 0.4;
    private const float BarHeight = 4, BarGap = 0.3f;

    /// <summary>Цифры групп пиксельным шрифтом 3×5: строка на ряд, # — закрашенная клетка.</summary>
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['1'] = [".#.", "##.", ".#.", ".#.", "###"],
        ['2'] = ["##.", "..#", ".#.", "#..", "###"],
        ['3'] = ["##.", "..#", ".#.", "..#", "##."],
        ['4'] = ["#.#", "#.#", "###", "..#", "..#"],
        ['5'] = ["###", "#..", "##.", "..#", "##."],
        ['6'] = [".##", "#..", "###", "#.#", "###"],
        ['7'] = ["###", "..#", ".#.", ".#.", ".#."],
        ['8'] = ["###", "#.#", "###", "#.#", "###"],
        ['9'] = ["###", "#.#", "###", "..#", "##."],
    };

    /// <summary>Клетка шрифта цифр группы в пикселях экрана.</summary>
    private const float DigitCell = 2;

    /// <summary>Порядок цифр групп: у юнита из нескольких групп видна первая по нему.</summary>
    public const string GroupOrder = "1234567890";

    private ComponentRef? _armed, _orders;

    private Parts P => state.P;

    // ------------------------------------------------------------------ где сущность

    /// <summary>
    /// Где сущность: середина и размер в тайлах; у юнита — описанный квадрат кольца (Body.radius + запас), у здания и
    /// площадки — основание (Footprint от левого верхнего тайла, место здания — он).
    /// </summary>
    public (double X, double Y, double W, double H, bool Round)? Bounds(ClientContext c, uint entity)
    {
        if (c.Pose(entity) is not { } at)
            return null;
        if (c.Has(entity, P.Unit))
        {
            var size = (state.Radius(c, entity) + RingMargin) * 2;
            return (at.X, at.Y, size, size, true);
        }
        if (state.Size(c, entity) is { } area)
            return (at.X + area.W / 2, at.Y + area.H / 2, area.W, area.H, false);
        return null;
    }

    /// <summary>Середина сущности в тайлах: юнита — где он нарисован, здания или площадки — середина основания.</summary>
    private (double X, double Y)? CenterOf(ClientContext c, uint entity) => state.Center(c, entity);

    // ------------------------------------------------------------------ рисунок

    private static float Pixel(ClientContext c) => (float)(1 / c.Camera.Zoom);

    /// <summary>Кольцо вокруг юнита или рамка в два пикселя вокруг здания; share — насколько ярко, до настройки игрока.</summary>
    public void Mark(ClientContext c, uint entity, float share)
    {
        if (!c.Alive(entity) || Bounds(c, entity) is not { } b)
            return;
        var alpha = share * Brightness;
        if (b.Round)
            // Кольцо — как спрайт 64 пикселя с ободом в 4: толщина растёт с размером.
            c.Draw.Circle(b.X, b.Y, b.W / 2 - b.W * 3 / 64, Selected, (float)(b.W * c.Camera.Zoom * 4 / 64), alpha);
        else
            c.Draw.Rect(b.X - b.W / 2, b.Y - b.H / 2, b.X + b.W / 2, b.Y + b.H / 2, Selected, 2, alpha);
    }

    /// <summary>Пунктир от (x1, y1) к (x2, y2): точки PathDot пикселей через PathGap пикселей экрана, от конца.</summary>
    private static void Dashes(ClientContext c, double x1, double y1, double x2, double y2, float alpha, uint color = Selected)
    {
        if (x1 == x2 && y1 == y2)
            return;
        c.Draw.Dashes(x1, y1, x2, y2, color, PathGap, PathDot, alpha);
    }

    private static void GoalFrame(ClientContext c, double x, double y, double size, uint color)
    {
        var half = size / 2;
        c.Draw.Rect(x - half, y - half, x + half, y + half, color, 2);
    }

    /// <summary>Точки пути юнита (Path.points — f64 x, y подряд, центры тайлов).</summary>
    private static List<(double X, double Y)> Waypoints(ComponentValues path, FieldRef field)
    {
        var bytes = path.Bytes(field);
        var list = new List<(double, double)>(bytes.Length / 16);
        for (var at = 0; at + 16 <= bytes.Length; at += 16)
            list.Add((BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at)), BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 8))));
        return list;
    }

    /// <summary>Куда ведёт приказ из очереди; null — приказ без места.</summary>
    private (double X, double Y)? QueueTarget(ClientContext c, QueuedOrder item)
    {
        try
        {
            switch (item.Name)
            {
                case MoveOrder.Name when MemoryPackSerializer.Deserialize<MoveOrder>(item.Payload) is { } move:
                    return (move.X + 0.5, move.Y + 0.5);
                case PointOrder.Name when MemoryPackSerializer.Deserialize<PointOrder>(item.Payload) is { } point:
                    return (point.X, point.Y);
                case AttackOrder.Name when MemoryPackSerializer.Deserialize<AttackOrder>(item.Payload) is { } attack:
                    return CenterOf(c, attack.Target);
                case AssistOrder.Name when MemoryPackSerializer.Deserialize<AssistOrder>(item.Payload) is { } assist:
                    return CenterOf(c, assist.Site);
                case HaulOrder.Name when MemoryPackSerializer.Deserialize<HaulOrder>(item.Payload) is { } haul:
                    return CenterOf(c, haul.Mine);
                case PickupOrder.Name when MemoryPackSerializer.Deserialize<PickupOrder>(item.Payload) is { } pickup:
                    return CenterOf(c, pickup.Drop);
                case SupplyOrder.Name when MemoryPackSerializer.Deserialize<SupplyOrder>(item.Payload) is { } supply:
                    return CenterOf(c, supply.Target);
            }
        }
        catch (MemoryPackSerializationException)
        {
            // Битый приказ в очереди — просто без пунктира.
        }
        return null;
    }

    /// <summary>
    /// Пути выбранных юнитов и их очереди приказов, цели стрелков, флажки сбора, круги раздачи — под кольцами. Точки
    /// отсчитываются от конца отрезка, поэтому стоят на земле неподвижно, а не ползут вместе с юнитом.
    /// </summary>
    public void Routes(ClientContext c, IReadOnlyList<uint> selection)
    {
        var pixel = Pixel(c);
        var selected = new HashSet<uint>(selection);
        _armed ??= c.Component("Armed");
        _orders ??= c.Component("Orders");
        foreach (var entity in selection)
        {
            if (!c.Has(entity, P.Unit) || c.Pose(entity) is not { } pose)
                continue;
            var path = c.Get(entity, P.Path);
            var (px, py) = (pose.X, pose.Y);
            (double X, double Y)? end = null;
            if (path is not null)
            {
                var points = Waypoints(path, P.Path["points"]);
                if (points.Count > 0)
                {
                    foreach (var (wx, wy) in points)
                    {
                        Dashes(c, px, py, wx, wy, PathAlpha);
                        (px, py) = (wx, wy);
                    }
                    end = (px, py);
                    GoalFrame(c, px, py, PathGoal, Selected);
                }
            }
            // Очередь приказов: от конца нынешнего пути через места следующих приказов.
            if (c.Get(entity, _orders)?.Bytes(_orders["list"]) is { Length: > 0 } list)
            {
                var (fx, fy) = end ?? (pose.X, pose.Y);
                foreach (var item in MemoryPackSerializer.Deserialize<OrderQueue>(list)?.Items ?? [])
                {
                    if (QueueTarget(c, item) is not { } to)
                        continue;
                    Dashes(c, fx, fy, to.X, to.Y, QueueAlpha);
                    GoalFrame(c, to.X, to.Y, PathGoal, Selected);
                    (fx, fy) = to;
                }
            }
        }

        // Цели выбранных стрелков: пунктир от стрелка к тому, в кого он целится, — его или турелей на нём. Цель хост шлёт
        // только хозяину, поэтому у чужих выбранных её нет. Одна цель — одна рамка, сколько бы ни целилось.
        var marked = new HashSet<uint>();
        void Aim(uint shooter, uint target)
        {
            if (target == 0 || !selected.Contains(shooter) || !c.Alive(target) || CenterOf(c, shooter) is not { } from || CenterOf(c, target) is not { } to)
                return;
            Dashes(c, from.X, from.Y, to.X, to.Y, TargetAlpha, Target);
            if (marked.Add(target))
                GoalFrame(c, to.X, to.Y, PathGoal * 2, Target);
        }
        foreach (var entity in selection)
            if (c.Get(entity, _armed) is { } armed)
                Aim(entity, (uint)armed.Number(_armed["target"]));
        foreach (var turret in c.Each(_armed, P.Mount))
            if (c.Get(turret, P.Mount) is { } mount && selected.Contains((uint)mount.Number(P.Mount["parent"])) && c.Get(turret, _armed) is { } shooter)
                Aim((uint)mount.Number(P.Mount["parent"]), (uint)shooter.Number(_armed["target"]));

        foreach (var entity in selection)
        {
            // Точка сбора выбранного здания: пунктир от его середины и флажок на месте.
            if (c.Get(entity, P.Producer) is { } producer && producer.Flag(P.Producer["rally"]) && c.Has(entity, P.Building) && CenterOf(c, entity) is { } center)
            {
                double rx = producer.Number(P.Producer["rallyX"]) + 0.5, ry = producer.Number(P.Producer["rallyY"]) + 0.5;
                Dashes(c, center.X, center.Y, rx, ry, PathAlpha);
                // Флажок: древко и полотнище.
                c.Draw.Rect(rx - pixel, ry - 0.9, rx + pixel, ry, Selected);
                c.Draw.Rect(rx + pixel, ry - 0.9, rx + pixel + 0.45, ry - 0.6, Selected);
            }
            // Круг раздачи выбранной машины снабжения: свои внутри него пополняют магазины с её склада.
            if (c.Has(entity, P.Unit) && SuppliesRadius(c, entity) is { } radius && c.Pose(entity) is { } at)
            {
                var step = PathGap * pixel;
                c.Draw.DashedCircle(at.X, at.Y, radius, Selected, (int)Math.Ceiling(Math.PI * 2 * radius / step), PathDot, PathAlpha);
            }
        }
    }

    private double? SuppliesRadius(ClientContext c, uint entity)
    {
        var path = c.ProtoPath(c.Proto(entity));
        return path is not null && path.StartsWith("units/", StringComparison.Ordinal) && world.Tables(c).Units.GetValueOrDefault(path["units/".Length..])?.Supplies is { } supplies ? supplies.Radius : null;
    }

    /// <summary>Цифры групп над своими юнитами, что на экране: в тёмной плашке у правого нижнего края кольца.</summary>
    public void Groups(ClientContext c, IReadOnlyDictionary<char, uint[]> groups, ICollection<uint> selection)
    {
        if (groups.Count == 0)
            return;
        var digitOf = new Dictionary<uint, char>();
        foreach (var digit in GroupOrder)
            if (groups.TryGetValue(digit, out var members))
                foreach (var entity in members)
                    digitOf.TryAdd(entity, digit);
        var cell = DigitCell / c.Camera.Zoom;
        var (x1, y1) = c.ToWorld(0, 0);
        var (x2, y2) = c.ToWorld(c.ScreenWidth, c.ScreenHeight);
        foreach (var (entity, digit) in digitOf)
        {
            if (!c.Alive(entity) || !c.Mine(entity) || !c.Has(entity, P.Unit) || c.Pose(entity) is not { } at)
                continue;
            if (at.X < x1 - 1 || at.X > x2 + 1 || at.Y < y1 - 1 || at.Y > y2 + 1 || Bounds(c, entity) is not { } b)
                continue;
            double left = b.X + b.W / 2 - cell * 3, top = b.Y + b.H / 2 - cell * 5;
            var alpha = selection.Contains(entity) ? 1 : GroupAlpha;
            c.Draw.Rect(left - cell, top - cell, left - cell + cell * 5, top - cell + cell * 7, BarBack, null, 0.85f * alpha);
            var rows = Glyphs[digit];
            for (var y = 0; y < rows.Length; y++)
                for (var x = 0; x < rows[y].Length; x++)
                    if (rows[y][x] == '#')
                        c.Draw.Rect(left + x * cell, top + y * cell, left + (x + 1) * cell, top + (y + 1) * cell, GroupText, null, alpha);
        }
    }

    /// <summary>Полоска прогресса над сущностью: value от 0 до 1.</summary>
    private void Bar(ClientContext c, uint entity, double value, uint color)
    {
        if (Bounds(c, entity) is not { } b)
            return;
        var pixel = Pixel(c);
        var height = BarHeight * pixel;
        var top = b.Y - b.H / 2 - BarGap - height;
        double left = b.X - b.W / 2;
        c.Draw.Rect(left - pixel, top - pixel, left + b.W + pixel, top + height + pixel, BarBack);
        c.Draw.Rect(left, top, left + b.W * Math.Clamp(value, 0, 1), top + height, color);
    }

    /// <summary>Полоски превращения и производства над своими сущностями (полоску стройки и рамку площадки рисует Effects/Power.cs).</summary>
    public void Bars(ClientContext c)
    {
        // Превращение MCV в здание и обратно.
        foreach (var entity in c.Each(P.Converting, P.Owner))
            if (c.Mine(entity) && c.Get(entity, P.Converting) is { } conv && conv.Number(P.Converting["total"]) > 0)
                Bar(c, entity, 1 - conv.Number(P.Converting["left"]) / conv.Number(P.Converting["total"]), Converting);
        // Выпуск: current ≥ 0 — идёт заказ; progress — в тиках.
        foreach (var entity in c.Each(P.Producer, P.Owner))
        {
            if (c.Get(entity, P.Producer) is not { } producer || !c.Mine(entity) || producer.Number(P.Producer["current"]) < 0 || c.Has(entity, P.Converting))
                continue;
            var unit = state.KindOf(c, (int)producer.Number(P.Producer["current"]));
            Bar(c, entity, producer.Number(P.Producer["progress"]) / state.BuildTicks(c, unit), Producing);
        }
    }

    /// <summary>Рамка выделения, которую тянут мышью (углы в тайлах).</summary>
    public static void Box(ClientContext c, double x1, double y1, double x2, double y2)
    {
        c.Draw.Rect(x1, y1, x2, y2, Selected, null, BoxFillAlpha);
        c.Draw.Rect(x1, y1, x2, y2, Selected, 1);
    }

    public const float BoxHitAlpha = HitAlpha;
    public const float NetworkShare = NetworkAlpha;
}
