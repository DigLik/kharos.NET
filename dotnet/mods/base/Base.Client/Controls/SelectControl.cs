using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Выделение — перенос controls/select.luau: щелчок — своя сущность под курсором, рамка — свои юниты в ней, Shift —
/// добавить, Esc — снять, двойной щелчок — все свои того же вида на экране. Выделенное — <see cref="Shared.SelectionKey"/>
/// (его видят приказы и панели).
/// </summary>
internal sealed class SelectControl(BaseState state)
{
    /// <summary>Сдвиг указателя, после которого щелчок становится рамкой, в пикселях.</summary>
    private const float Threshold = 6;

    /// <summary>Двойной щелчок — за столько секунд.</summary>
    private const double Double = 0.35;

    // Вид отметок — как selectionPass.ts: приглушённый зелёный, яркость из настроек (0,7 по умолчанию).
    private const uint Selected = 0x73eb8c;
    private const float Brightness = 0.7f;
    private const double RingMargin = 0.2;
    private const float BoxFillAlpha = 0.12f;
    private const float HitAlpha = 0.5f;
    private const float PathDot = 3, PathGap = 14, PathAlpha = 0.55f, PathGoal = 0.4f;
    private const float BarHeight = 4, BarGap = 0.3f;
    private const uint BarBack = 0x080d14, BarProducing = 0x59a6ff;

    private (float X, float Y, double Wx, double Wy)? _start;
    private (double Time, uint Entity) _last = (-1, 0);

    private Parts P => state.P;

    /// <summary>
    /// Где сущность: середина и размер в тайлах; у юнита — описанный квадрат кольца (радиус — Body.radius), у здания —
    /// основание (Footprint от левого верхнего тайла, место здания — он).
    /// </summary>
    private (double X, double Y, double W, double H, bool Round) Bounds(ClientContext c, uint entity)
    {
        var at = c.Get(entity, P.Position)!;
        double x = at.Number(P.Position["x"]), y = at.Number(P.Position["y"]);
        if (c.Has(entity, P.Unit))
        {
            var size = (state.Radius(c, entity) + RingMargin) * 2;
            return (x, y, size, size, true);
        }
        if (state.Size(c, entity) is { } area)
            return (x + area.W / 2, y + area.H / 2, area.W, area.H, false);
        return (x, y, 1, 1, true);
    }

    /// <summary>Кольцо вокруг юнита или рамка в два пикселя вокруг здания.</summary>
    private void Mark(ClientContext c, uint entity, float share)
    {
        if (!c.Alive(entity) || !c.Has(entity, P.Position))
            return;
        var (x, y, w, h, round) = Bounds(c, entity);
        var alpha = share * Brightness;
        if (round)
            // Кольцо — как спрайт 64 пикселя с ободом в 4: толщина растёт с размером.
            c.Draw.Circle(x, y, w / 2 - w * 3 / 64, Selected, (float)(w * c.Camera.Zoom * 4 / 64), alpha);
        else
            c.Draw.Rect(x - w / 2, y - h / 2, x + w / 2, y + h / 2, Selected, 2, alpha);
    }

    /// <summary>Пунктир от (x1, y1) к (x2, y2): точки PathDot пикселей через PathGap пикселей экрана, от конца.</summary>
    private static void Dashes(ClientContext c, double x1, double y1, double x2, double y2, float alpha)
    {
        var pixel = 1 / c.Camera.Zoom;
        double dot = PathDot * pixel, step = PathGap * pixel;
        var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        double along = 0;
        while (along < length)
        {
            var x = x2 + (x1 - x2) / length * along;
            var y = y2 + (y1 - y2) / length * along;
            c.Draw.Rect(x - dot / 2, y - dot / 2, x + dot / 2, y + dot / 2, Selected, null, alpha);
            along += step;
        }
    }

    /// <summary>Точки пути юнита (Path.points — f64 x, y подряд, центры тайлов); пусто — пути нет или не посчитан.</summary>
    private static List<(double X, double Y)> Waypoints(ComponentValues path, FieldRef field)
    {
        var bytes = path.Bytes(field);
        var list = new List<(double, double)>(bytes.Length / 16);
        for (var at = 0; at + 16 <= bytes.Length; at += 16)
            list.Add((System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at)), System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 8))));
        return list;
    }

    /// <summary>Пути, флажки сбора и полоски выпуска у выбранного.</summary>
    private void Marks(ClientContext c, uint[] selection)
    {
        var pixel = 1 / c.Camera.Zoom;
        foreach (var entity in selection)
        {
            // Едет ли юнит — есть ли у него Path; путь приходит только хозяину.
            var path = c.Get(entity, P.Path);
            var pose = c.Pose(entity);
            if (path is not null && pose is { } at)
            {
                var (px, py) = (at.X, at.Y);
                var (tx, ty) = (path.Number(P.Path["goalX"]) + 0.5, path.Number(P.Path["goalY"]) + 0.5);
                foreach (var (wx, wy) in Waypoints(path, P.Path["points"]))
                {
                    Dashes(c, px, py, wx, wy, PathAlpha);
                    (px, py) = (wx, wy);
                    (tx, ty) = (wx, wy);
                }
                Dashes(c, px, py, tx, ty, PathAlpha);
                var half = PathGoal / 2;
                c.Draw.Rect(tx - half, ty - half, tx + half, ty + half, Selected, 2);
            }
            var producer = c.Get(entity, P.Producer);
            if (producer is not null && producer.Flag(P.Producer["rally"]) && state.Center(c, entity) is { } center)
            {
                // Точка сбора — тайл: флажок на его середине.
                double rx = producer.Number(P.Producer["rallyX"]) + 0.5, ry = producer.Number(P.Producer["rallyY"]) + 0.5;
                Dashes(c, center.X, center.Y, rx, ry, PathAlpha);
                c.Draw.Rect(rx - pixel, ry - 0.9, rx + pixel, ry, Selected);
                c.Draw.Rect(rx + pixel, ry - 0.9, rx + pixel + 0.45, ry - 0.6, Selected);
            }
        }
        // Полоски выпуска над своими производителями (current ≥ 0 — идёт заказ; progress — в тиках).
        foreach (var entity in c.Each(P.Producer, P.Owner))
        {
            if (c.Get(entity, P.Producer) is not { } producer || !c.Mine(entity) || producer.Number(P.Producer["current"]) < 0 || !c.Has(entity, P.Building))
                continue;
            var unit = state.KindOf(c, (int)producer.Number(P.Producer["current"]));
            var (x, y, w, h, _) = Bounds(c, entity);
            var height = BarHeight * pixel;
            var top = y - h / 2 - BarGap - height;
            var left = x - w / 2;
            var share = Math.Clamp(producer.Number(P.Producer["progress"]) / state.BuildTicks(c, unit), 0, 1);
            c.Draw.Rect(left - pixel, top - pixel, left + w + pixel, top + height + pixel, BarBack);
            c.Draw.Rect(left, top, left + w * share, top + height, BarProducing);
        }
    }

    public void Update(ClientContext c)
    {
        var input = c.Input;
        // Погибшие выпадают сами.
        var selection = Shared.Selection(c).Where(c.Alive).ToList();

        if (input.Pressed(PointerButton.Left) && input.Inside && !input.OverUi)
            _start = (input.X, input.Y, input.WorldX, input.WorldY);
        if (_start is { } start)
        {
            var moved = Math.Abs(input.X - start.X) + Math.Abs(input.Y - start.Y) > Threshold;
            if (input.Down(PointerButton.Left) && moved)
            {
                c.Draw.Rect(start.Wx, start.Wy, input.WorldX, input.WorldY, Selected, null, BoxFillAlpha);
                c.Draw.Rect(start.Wx, start.Wy, input.WorldX, input.WorldY, Selected, 1);
                // Кого выберет рамка, если отпустить её сейчас, — бледнее выбранных.
                foreach (var entity in c.Within(start.Wx, start.Wy, input.WorldX, input.WorldY, P.Unit))
                    if (c.Mine(entity) && !selection.Contains(entity))
                        Mark(c, entity, HitAlpha);
            }
            // Долгое касание стало приказом (OrderControl): отпускание не выделяет.
            if (input.Released(PointerButton.Left) && c.Shared.ContainsKey(Shared.SwallowKey))
            {
                c.Shared.Remove(Shared.SwallowKey);
                _start = null;
                return;
            }
            if (input.Released(PointerButton.Left))
            {
                var picked = new List<uint>();
                if (moved)
                {
                    foreach (var entity in c.Within(start.Wx, start.Wy, input.WorldX, input.WorldY, P.Unit))
                        if (c.Mine(entity))
                            picked.Add(entity);
                }
                else if (c.Pick(input.WorldX, input.WorldY) is { } entity && c.Mine(entity))
                {
                    picked = [entity];
                    // Двойной щелчок: все свои того же вида на экране.
                    if (_last.Entity == entity && c.Time - _last.Time < Double)
                    {
                        var kind = c.Proto(entity);
                        var (x1, y1) = c.ToWorld(0, 0);
                        var (x2, y2) = c.ToWorld(c.ScreenWidth, c.ScreenHeight);
                        picked = [.. c.Within(x1, y1, x2, y2).Where(other => c.Mine(other) && c.Proto(other) == kind)];
                    }
                    _last = (c.Time, entity);
                }
                if (input.Shift)
                {
                    foreach (var entity in picked)
                        if (!selection.Contains(entity))
                            selection.Add(entity);
                }
                else
                    selection = picked;
                _start = null;
            }
        }
        if (!input.Down(PointerButton.Left))
            _start = null;
        if (input.Hits.Contains("escape"))
            selection = [];
        var result = selection.ToArray();
        c.Shared[Shared.SelectionKey] = result;

        Marks(c, result);
        foreach (var entity in result)
            Mark(c, entity, 1);
    }
}
