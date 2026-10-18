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

    /// <summary>Где сущность: середина и размер в тайлах; у юнита — описанный квадрат кольца.</summary>
    private (double X, double Y, double W, double H, bool Round) Bounds(ClientContext c, uint entity)
    {
        var at = c.Get(entity, P.Position)!;
        double x = at.Number(P.Position["x"]), y = at.Number(P.Position["y"]);
        if (c.Get(entity, P.Unit) is { } unit)
        {
            var size = (unit.Number(P.Unit["radius"]) + RingMargin) * 2;
            return (x, y, size, size, true);
        }
        if (c.Get(entity, P.Building) is { } building)
            return (x, y, building.Number(P.Building["width"]), building.Number(P.Building["height"]), false);
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

    /// <summary>Пути, флажки сбора и полоски выпуска у выбранного.</summary>
    private void Marks(ClientContext c, uint[] selection)
    {
        var pixel = 1 / c.Camera.Zoom;
        foreach (var entity in selection)
        {
            var drive = c.Get(entity, P.Drive);
            var at = c.Get(entity, P.Position);
            if (drive is not null && drive.Flag(P.Drive["moving"]) && at is not null)
            {
                double x = at.Number(P.Position["x"]), y = at.Number(P.Position["y"]);
                double tx = drive.Number(P.Drive["tx"]), ty = drive.Number(P.Drive["ty"]);
                Dashes(c, x, y, tx, ty, PathAlpha);
                var half = PathGoal / 2;
                c.Draw.Rect(tx - half, ty - half, tx + half, ty + half, Selected, 2);
            }
            var producer = c.Get(entity, P.Producer);
            if (producer is not null && producer.Flag(P.Producer["rally"]) && at is not null)
            {
                double rx = producer.Number(P.Producer["rallyX"]), ry = producer.Number(P.Producer["rallyY"]);
                Dashes(c, at.Number(P.Position["x"]), at.Number(P.Position["y"]), rx, ry, PathAlpha);
                c.Draw.Rect(rx - pixel, ry - 0.9, rx + pixel, ry, Selected);
                c.Draw.Rect(rx + pixel, ry - 0.9, rx + pixel + 0.45, ry - 0.6, Selected);
            }
        }
        // Полоски выпуска над своими заводами.
        foreach (var entity in c.Each(P.Producer, P.Owner))
        {
            if (c.Get(entity, P.Producer) is not { } producer || !c.Mine(entity) || producer.Number(P.Producer["unit"]) == 0)
                continue;
            var unit = state.Data.Find("units/" + Tools.Text((uint)producer.Number(P.Producer["unit"])));
            var (x, y, w, h, _) = Bounds(c, entity);
            var height = BarHeight * pixel;
            var top = y - h / 2 - BarGap - height;
            var left = x - w / 2;
            var share = Math.Clamp(producer.Number(P.Producer["progress"]) / (unit is not null && unit.TryNumber("time", out var time) ? time : 1), 0, 1);
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
