using System.Text.Json;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Мини-карта — перенос game/minimap.ts и minimapView из gui/bar.ts. Местность и туман рисует движок (ui.Minimap, Д1);
/// поверх — месторождения, здания и юниты цветом стороны (свои, союзники, чужие, ничьи), метки пуска и удара, ракеты,
/// сигналы союзников и рамка видимой части экрана. Левая кнопка (и протяжка) ставит туда камеру, правая шлёт
/// выделенных, Alt — сигнал союзникам.
/// </summary>
internal sealed class Minimap(BaseState state)
{
    /// <summary>Сторона холста, под которую подобраны размеры значков (BASE_SIZE).</summary>
    private const float BaseSize = 132;

    public static readonly Color Own = Color.Hex(0x5aa9ff), Foe = Color.Hex(0xff6b5a), Ally = Color.Hex(0x4ec973), Neutral = Color.Hex(0xc8c8c8);
    private static readonly Color Launch = Color.Hex(0xffb347), Frame = Color.Hex(0xe4f4ff), Missile = Color.Hex(0xffffff), Back = Color.Hex(0x05090f);

    /// <summary>Радиус взрыва боеголовки, тайлы (BUILDINGS.silo.warhead.blast.radius): круг удара на мини-карте.</summary>
    private double _blast = -1;

    /// <summary>Границы карты (left, top, right, bottom); null — ещё не знаем.</summary>
    private (double Left, double Top, double Right, double Bottom)? _bounds;

    private Parts P => state.P;

    /// <summary>Квадрат мини-карты: тайл в левом верхнем углу и сколько тайлов в стороне (fit).</summary>
    public (double Left, double Top, double Span) Fit(ClientContext c)
    {
        _bounds ??= Bounds(c);
        var (left, top, right, bottom) = _bounds.Value;
        var span = Math.Max(right - left, bottom - top);
        return (left - (span - (right - left)) / 2, top - (span - (bottom - top)) / 2, span);
    }

    /// <summary>Границы карты из настроек партии (Settings, map); нет — по стороне поля партии.</summary>
    private (double, double, double, double) Bounds(ClientContext c)
    {
        try
        {
            using var document = JsonDocument.Parse(c.Settings());
            if (document.RootElement.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Object)
                return (map.GetProperty("left").GetDouble(), map.GetProperty("top").GetDouble(), map.GetProperty("right").GetDouble(), map.GetProperty("bottom").GetDouble());
        }
        catch (JsonException)
        {
        }
        var size = state.Data.FieldSize(c.Game);
        return (-size, -size, size, size);
    }

    /// <summary>Тайл под точкой мини-карты; координаты — доли стороны от 0 до 1 (tileAt).</summary>
    public (double X, double Y) TileAt(ClientContext c, double u, double v)
    {
        var (left, top, span) = Fit(c);
        return (left + u * span, top + v * span);
    }

    /// <summary>Мини-карта стороной side в рамке колодца; top и bottom — сколько экрана закрыто панелями сверху и снизу.</summary>
    public void View(ClientContext c, HudParts parts, float side, float top, float bottom, Dictionary<uint, long> heads)
    {
        var ui = c.Ui;
        var inner = side - 8;
        var (left, mapTop, span) = Fit(c);
        var scale = inner / span;
        var k = inner / BaseSize;
        var dot = Math.Max(2, Math.Round(2 * k, MidpointRounding.AwayFromZero));
        double X(double x) => (x - left) * scale;
        double Y(double y) => (y - mapTop) * scale;
        Color ColorOf(uint player) => player == c.Player ? Own : player == 0 ? Neutral : HudReader.Allied(heads, c.Player, player) ? Ally : Foe;
        ui.Box(new Style { Id = "minimap", Width = side, Height = side, Skin = Skin.Well, Padding = 4, Clip = true }, () =>
        {
            ui.Box(new Style { Direction = Direction.Stack, Width = inner, Height = inner, Background = Back }, () =>
            {
                ui.Minimap(inner, inner);
                // Месторождения, что клиент знает (слой deposits), — цветом своего ресурса; середина — центр 2 × 2.
                foreach (var spot in Deposits(c))
                    Dot(ui, Y(spot.Y + 1) - dot / 2, X(spot.X + 1) - dot / 2, dot, dot, spot.Color);
                var owned = c.Each(P.Owner, P.Position);
                var owners = new double[owned.Length];
                var places = new (double X, double Y, double Angle)[owned.Length];
                c.Column(owned, P.Owner, "player", owners);
                c.Poses(owned, places);
                for (var i = 0; i < owned.Length; i++)
                {
                    var entity = owned[i];
                    var color = ColorOf((uint)owners[i]);
                    var (px, py, _) = places[i];
                    if ((c.Has(entity, P.Building) || (parts.Site is { } site && c.Has(entity, site))) && state.Size(c, entity) is { } size)
                        // Место здания — левый верхний тайл.
                        Dot(ui, Y(py), X(px), Math.Max(dot, size.W * scale), Math.Max(dot, size.H * scale), color);
                    else if (c.Has(entity, P.Unit))
                        Dot(ui, Y(py) - dot / 2, X(px) - dot / 2, dot, dot, color);
                }
                Marks(c, parts, X, Y, k, scale);
                // Рамка экрана: только видимая часть — то, что закрыто панелями, на экране не видно.
                var (x1, y1) = c.ToWorld(0, top);
                var (x2, y2) = c.ToWorld(c.ScreenWidth, c.ScreenHeight - bottom);
                var line = Math.Max(1, Math.Round(k, MidpointRounding.AwayFromZero));
                var (fx, fy) = (Math.Round(X(x1), MidpointRounding.AwayFromZero), Math.Round(Y(y1), MidpointRounding.AwayFromZero));
                var (fw, fh) = (Math.Round((x2 - x1) * scale, MidpointRounding.AwayFromZero), Math.Round((y2 - y1) * scale, MidpointRounding.AwayFromZero));
                Dot(ui, fy, fx, fw, line, Frame);
                Dot(ui, fy + fh - line, fx, fw, line, Frame);
                Dot(ui, fy, fx, line, fh, Frame);
                Dot(ui, fy, fx + fw - line, line, fh, Frame);
            });
        });
        Input(c);
    }

    /// <summary>
    /// Ядерный удар: место пуска — янтарный крест, точка удара — красный круг взрыва, ракета — белая точка; сигнал
    /// союзника — зелёное кольцо, расходится раз в полсекунды.
    /// </summary>
    private void Marks(ClientContext c, HudParts parts, Func<double, double> x, Func<double, double> y, double k, double scale)
    {
        var ui = c.Ui;
        var line = Math.Max(1, Math.Round(2 * k, MidpointRounding.AwayFromZero));
        if (parts.Mark is { } mark)
            foreach (var entity in c.Each(mark, P.Position))
            {
                if (c.Pose(entity) is not { } at || c.Get(entity, mark) is not { } values)
                    continue;
                var kind = HudParts.Str(values, mark, "kind");
                var (mx, my) = (x(at.X), y(at.Y));
                if (kind == "launch")
                {
                    var arm = Math.Max(2, Math.Round(4 * k));
                    for (var d = -arm; d <= arm; d += line)
                    {
                        Dot(ui, my + d - line / 2, mx + d - line / 2, line, line, Launch);
                        Dot(ui, my - d - line / 2, mx + d - line / 2, line, line, Launch);
                    }
                }
                else if (kind == "ping")
                    Ring(ui, mx, my, (3 + c.Time * 2 % 1 * 12) * k, line, Ally);
                else
                {
                    if (_blast < 0)
                        _blast = Tables?.Buildings.GetValueOrDefault("silo")?.Warhead?.Blast.Radius ?? 0;
                    Ring(ui, mx, my, Math.Max(4 * k, _blast * scale), line, Foe);
                }
            }
        if (parts.Missile is { } missile)
            foreach (var entity in c.Each(missile))
            {
                if (c.Get(entity, missile) is not { } values)
                    continue;
                var (mx, my) = (HudParts.Num(values, missile, "x", double.NaN), HudParts.Num(values, missile, "y", double.NaN));
                if (double.IsNaN(mx) || double.IsNaN(my))
                    continue;
                Dot(ui, y(my) - 1.5 * k, x(mx) - 1.5 * k, 3 * k, 3 * k, Missile);
            }
        // Уведомления (раздел Alerts, c.Shared): мигающее кольцо там, где напали, заметили врага или нашли месторождение.
        if (c.Shared.TryGetValue(AlertsSection.ListKey, out var listed) && listed is Alert[] alerts
            && c.Shared.TryGetValue(AlertsSection.ClockKey, out var clock) && clock is double now)
            foreach (var alert in alerts)
            {
                var age = now - alert.At;
                if (age > AlertWatch.Seconds)
                    continue;
                var pulse = age * 2 % 1;
                var tint = alert.Kind is "attacked" or "nuke" ? Foe : alert.Kind == "ping" ? Ally : alert.Kind is "found" or "intercepted" ? Color.Hex(0x8fe0a8) : Color.Hex(0xffd27a);
                Ring(ui, x(alert.X), y(alert.Y), (3 + pulse * 12) * k, line, tint.Faded((float)(1 - pulse)));
            }
    }

    /// <summary>Таблицы base (ставит HUD): радиус взрыва боеголовки.</summary>
    public Base.Shared.Data.BaseTables? Tables { get; set; }

    /// <summary>Кольцо из точек: круглого на мини-карте нет, только прямоугольники.</summary>
    private static void Ring(Ui ui, double x, double y, double radius, double line, Color color)
    {
        var count = Math.Clamp((int)Math.Ceiling(radius * 2), 8, 48);
        for (var i = 0; i < count; i++)
        {
            var angle = i * Math.Tau / count;
            Dot(ui, y + Math.Sin(angle) * radius - line / 2, x + Math.Cos(angle) * radius - line / 2, line, line, color);
        }
    }

    /// <summary>Щелчки: левая кнопка и протяжка — камера туда; правая — выделенные едут; Alt — сигнал союзникам.</summary>
    private void Input(ClientContext c)
    {
        var ui = c.Ui;
        var held = ui.Held("minimap");
        var click = ui.Clicked("minimap");
        if (held is { } at && !c.Input.Alt)
        {
            // Прыжок, а не полёт: замер скорости камеры (К2) его не считает.
            var (x, y) = TileAt(c, at.U, at.V);
            (c.Camera.X, c.Camera.Y) = (x, y);
            CameraShake.Jump(c, x, y);
        }
        if (click is { } press && (press.Button == 2 || press.Alt) && ui.RectOf("minimap") is { } rect)
        {
            var u = Math.Clamp((c.Input.X - rect.X - 4) / (rect.W - 8), 0, 1);
            var v = Math.Clamp((c.Input.Y - rect.Y - 4) / (rect.H - 8), 0, 1);
            var (x, y) = TileAt(c, u, v);
            if (press.Button == 2)
            {
                var units = Shared.Selection(c).Where(entity => c.Has(entity, P.Mover) && c.Mine(entity)).ToArray();
                if (units.Length > 0)
                    c.Send(MoveOrder.Name, MemoryPackSerializer.Serialize(new MoveOrder { Units = units, X = (int)Math.Floor(x), Y = (int)Math.Floor(y), Queue = press.Shift }));
            }
            else
                c.Send(PingOrder.Name, MemoryPackSerializer.Serialize(new PingOrder { X = x, Y = y }));
        }
    }

    /// <summary>Известное месторождение для мини-карты: левый верхний тайл и цвет ресурса.</summary>
    private readonly record struct Spot(double X, double Y, Color Color);

    private readonly List<Spot> _spots = [];
    private double _spotsAt = double.NegativeInfinity;

    /// <summary>
    /// Месторождения, что клиент знает (предметы слоя deposits; под туманом — только разведанные), раз в секунду: слой не
    /// меняется чаще, а кадр не должен ходить за ним каждый раз. Цвет — look.color записи вида.
    /// </summary>
    private List<Spot> Deposits(ClientContext c)
    {
        if (P.Deposits is not { } layer || c.Time - _spotsAt < 1)
            return _spots;
        _spotsAt = c.Time;
        _spots.Clear();
        var kindField = Math.Max(0, layer.Fields.ToList().IndexOf("kind"));
        var (left, top, span) = Fit(c);
        foreach (var item in Tools.Items(layer, 0, left + span / 2, top + span / 2, span))
        {
            var kind = (int)item.Fields[kindField];
            if ((uint)kind >= (uint)DepositArt.Types.Length)
                continue;
            var color = state.Data.Find("deposits/" + DepositArt.Types[kind])?.Number("color", 0xc8c8c8) ?? 0xc8c8c8;
            _spots.Add(new Spot(item.X, item.Y, Color.Hex((uint)color)));
        }
        return _spots;
    }

    /// <summary>Точка или рамка мини-карты: прямоугольник w × h, левый верхний угол сдвинут на (left, top) от края.</summary>
    private static void Dot(Ui ui, double top, double left, double w, double h, Color color)
    {
        (top, left) = (Math.Floor(top), Math.Floor(left));
        // За левым и верхним краем — обрезать: отступ не бывает меньше нуля.
        if (top < 0)
            (h, top) = (h + top, 0);
        if (left < 0)
            (w, left) = (w + left, 0);
        if (w <= 0 || h <= 0)
            return;
        ui.Box(new Style { Self = (Align.Start, Align.Start), Padding = ((float)top, 0, 0, (float)left) }, () =>
            ui.Box(new Style { Width = (float)w, Height = (float)h, Background = color }));
    }
}
