using System.Text;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Панель игрока — перенос ui/hud.luau (hud.ts — верхняя полоса, bar.ts — нижняя: мини-карта, сведения о выбранном,
/// сетка команд 4 × 3). Те же размеры, рамки скина, шрифты и цвета.
/// </summary>
internal sealed class Hud(BaseState state)
{
    // Верхняя полоса и нижняя панель: высоты, ячейки, портреты — как в hud.ts и bar.ts.
    private const float TopHeight = 38;
    private const float Inner = 140;
    private const float CellWidth = 132;
    private const float GridWidth = 4 * CellWidth + 3 * 4;
    private const float BarSpacing = 16 + 8 + 8;
    private const float Portrait = 96;
    private const float Tile = 32;
    private const float Queued = 24;

    // Сторона мини-карты: из настроек 200, но не больше, чем оставляет сведениям ширина экрана (minimapSide).
    private const float MinimapSide = 200;
    private const float InfoMin = 104 + 10 + 24 + 150;
    private static readonly string[] GridKeys = ["T", "Y", "U", "I", "G", "H", "J", "K", "B", "N", "M", ","];
    private static readonly string[] KeyNames = ["t", "y", "u", "i", "g", "h", "j", "k", "b", "n", "m", ","];

    private static readonly Color Own = Color.Hex(0x5aa9ff), Foe = Color.Hex(0xff6b5a), Neutral = Color.Hex(0xc8c8c8);

    /// <summary>Ячейка сетки команд.</summary>
    private sealed record Slot(string Label, int Picture, double? Cost, bool Short, string? About, Kind Unit, Action Run);

    private BaseState S => state;

    public void Draw(ClientContext c)
    {
        TopBar(c);
        BottomBar(c);
    }

    // ------------------------------------------------------------------ верхняя полоса

    private void TopBar(ClientContext c)
    {
        var ui = c.Ui;
        ui.Box(new Style { Id = "top", Anchor = new Anchor(Left: 0, Right: 0, Top: 0), Height = TopHeight, Skin = Skin.Panel, Padding = (4, 12), Direction = Direction.Row, Align = Align.Center, Gap = 12 }, () =>
        {
            ui.Box(new Style { Id = "credits", Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
            {
                Coin(ui);
                ui.Text(Lua.ToString(Math.Floor(S.Credits(c))), new TextStyle { Font = FontName.Title, Color = Gold, Shadow = Ink, Lift = 2 });
                var income = S.Data.Income;
                if (income > 0)
                    ui.Text("+" + Lua.ToString(income) + "/с", new TextStyle { Color = Good });
            });
            ui.Tip("credits", "Кредиты");
            ui.Space(0, 0, 1);
            // Час пишет погода; в самом первом кадре её ещё не было — считаем сами (в пилоте там 00:00).
            var hour = c.Shared.TryGetValue(Shared.HourKey, out var h) && h is double value ? value : Weather.HourAt(c.Clock);
            ui.Box(new Style { Id = "clock", Direction = Direction.Row, Gap = 4 }, () =>
                ui.Text($"{(int)Math.Floor(hour):00}:{(int)(Math.Floor(hour * 60) % 60):00}", new TextStyle { Color = Dim }));
            ui.Tip("clock", "Время суток");
            ui.Button("menu", Style.Button with { Padding = (2, 10), Height = 30 }, () => ui.Text("Меню", Shadowed()));
            ui.Tip("menu", "Меню игры");
        });
    }

    // ------------------------------------------------------------------ сетка команд

    /// <summary>Ячейки сетки команд для выбранного: у своего завода — заказ юнитов.</summary>
    private List<Slot> Slots(ClientContext c, uint[] selection)
    {
        var list = new List<Slot>();
        if (selection.Length != 1 || !c.Has(selection[0], S.P.Producer) || !c.Mine(selection[0]))
            return list;
        var building = selection[0];
        var money = S.Credits(c);
        foreach (var name in S.KindOf(c, building)?.Strings("produces") ?? [])
        {
            if (S.Data.Find("units/" + name) is not { } unit)
                continue;
            var cost = unit.TryNumber("cost", out var price) ? price : (double?)null;
            list.Add(new Slot(unit.Text("title") ?? name, S.UnitProto(c, name), cost, cost is { } p && money < p, unit.Text("about"), unit,
                () => c.Send("produce", MemoryPackSerializer.Serialize(new ProduceOrder { Producer = building, Unit = name }))));
        }
        return list;
    }

    /// <summary>Карточка ячейки (slotCard в bar.ts): название с клавишей, пояснение, параметры юнита, цена.</summary>
    private static void Card(Ui ui, Slot slot, string hotkey)
    {
        const float width = 264;
        ui.Box(new Style { Direction = Direction.Column, Gap = 4, Width = width, Align = Align.Stretch }, () =>
        {
            ui.Box(new Style { Direction = Direction.Row, Justify = Align.Between, Align = Align.End, Gap = 12 }, () =>
            {
                ui.Text(slot.Label, new TextStyle { Font = FontName.Title, Color = Bright });
                ui.Text(hotkey, new TextStyle { Font = FontName.Title, Color = Gold });
            });
            if (slot.About is { } about)
                ui.Text(about, new TextStyle { Color = Dim, Wrap = width });
            foreach (var row in Stats.Unit(slot.Unit))
                ui.Box(new Style { Direction = Direction.Row, Gap = 10 }, () =>
                {
                    ui.Box(new Style { Width = 90 }, () => ui.Text(row.Label, new TextStyle { Color = Dim }));
                    ui.Text(row.Value);
                });
            if (slot.Cost is { } cost)
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
                {
                    Coin(ui);
                    ui.Text(Lua.ToString(cost), new TextStyle { Color = Gold });
                });
        });
    }

    private static void CommandGrid(ClientContext c, List<Slot> list, float width, float height)
    {
        var ui = c.Ui;
        var cellWidth = (width - 3 * 4) / 4;
        var cellHeight = (height - 2 * 4) / 3;
        ui.Box(new Style { Id = "commands", Direction = Direction.Column, Gap = 4, Width = width, Height = height }, () =>
        {
            for (var row = 0; row < 3; row++)
                ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                {
                    for (var column = 0; column < 4; column++)
                    {
                        var i = row * 4 + column;
                        if (i >= list.Count)
                        {
                            ui.Box(new Style { Width = cellWidth, Height = cellHeight, Skin = Skin.Well, Alpha = 0.35f });
                            continue;
                        }
                        var slot = list[i];
                        var hotkey = GridKeys[i];
                        var id = "cell:" + (i + 1);
                        var click = ui.Button(id, Style.Button with { Width = cellWidth, Height = cellHeight, Padding = (0, 6, 0, slot.Picture >= 0 ? 4 : 8), Gap = 6, Justify = Align.Start }, () =>
                        {
                            if (slot.Picture >= 0)
                                ui.Box(new Style { Direction = Direction.Stack }, () =>
                                {
                                    ui.Box(new Style { Width = Tile, Height = Tile }, () => ui.Image(slot.Picture, Tile, Tile));
                                    ui.Box(new Style { Self = (Align.Start, Align.Start), Background = Ink, Padding = (0, 2) }, () => Key(ui, hotkey, Gold));
                                });
                            ui.Box(new Style { Direction = Direction.Column, Grow = 1, Gap = 0, Clip = true }, () =>
                            {
                                ui.Box(new Style { Direction = Direction.Row, Gap = 6 }, () =>
                                    ui.Box(new Style { Grow = 1 }, () => ui.Text(slot.Label, Shadowed(Bright, ellipsis: true))));
                                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
                                {
                                    if (slot.Cost is { } cost)
                                    {
                                        Coin(ui);
                                        ui.Text(Lua.ToString(cost), Shadowed(slot.Short ? Warn : Gold));
                                    }
                                });
                            });
                        });
                        // Подсказка — карточкой над сеткой, правым краем у указателя.
                        var grid = ui.RectOf("commands");
                        var pointer = c.Input.X;
                        Anchor? anchor = grid is { } rect
                            ? new Anchor(Right: float.IsNaN(pointer) ? 8 : MathF.Max(8, c.ScreenWidth - pointer - 40), Bottom: c.ScreenHeight - rect.Y + 12)
                            : null;
                        ui.Tip(id, () => Card(ui, slot, hotkey), anchor);
                        if (click is not null || (c.Input.Hits.Contains(KeyNames[i]) && !c.Input.Ctrl && !c.Input.Keys.Contains("ctrl")))
                            slot.Run();
                    }
                });
        });
    }

    // ------------------------------------------------------------------ сведения о выбранном

    /// <summary>Очередь заказов здания (Producer.queue — байты): имена видов построчно, UTF-8.</summary>
    private static List<string> ReadQueue(byte[] bytes) =>
        bytes.Length == 0 ? [] : [.. Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private void Info(ClientContext c, uint[] selection, float width, float height)
    {
        var ui = c.Ui;
        var many = selection.Length > 1;
        var first = selection[0];
        var kind = S.KindOf(c, first);
        var title = kind is null ? "?" : kind.Text("title") ?? kind.Name;
        if (many)
        {
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (var entity in selection)
            {
                var name = S.KindOf(c, entity)?.Text("title") ?? "?";
                if (!counts.ContainsKey(name))
                {
                    counts[name] = 0;
                    order.Add(name);
                }
                counts[name]++;
            }
            title = string.Join(", ", order.Select(name => counts[name] > 1 ? name + " ×" + counts[name] : name));
        }
        var columns = Math.Max(1, Math.Min(4, (int)Math.Floor((width - 24 - 10 - 150 + 4) / 44)));
        var left = many ? columns * 40 + (columns - 1) * 4 : 104;
        var text = MathF.Max(40, width - 24 - left - 10);
        ui.Box(new Style { Width = width, Height = height, Skin = Skin.Well, Padding = (10, 12), Direction = Direction.Row, Gap = 10 }, () =>
        {
            if (many)
            {
                ui.Box(new Style { Direction = Direction.Column, Gap = 4, Width = left, MaxHeight = height - 20, Scroll = true, Id = "info:units" }, () =>
                {
                    for (var start = 0; start < selection.Length; start += columns)
                    {
                        var from = start;
                        ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                        {
                            for (var index = from; index < Math.Min(selection.Length, from + columns); index++)
                            {
                                var entity = selection[index];
                                ui.Button("unit:" + (index + 1), Style.Button with { Width = 40, Height = 40, Padding = 0, Skin = Skin.Well, Direction = Direction.Stack }, () =>
                                    ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => Picture(ui, c.Proto(entity), Tile)));
                            }
                        });
                    }
                });
            }
            else
            {
                ui.Button("portrait", Style.Button with { Width = 104, Height = 104, Padding = 0, Skin = Skin.Well, Direction = Direction.Stack }, () =>
                {
                    ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => Picture(ui, c.Proto(first), Portrait));
                    ui.Box(new Style { Self = (Align.Start, Align.End), Width = 104, Height = 6, Background = Color.Hex(0x4fd07a) });
                });
                ui.Tip("portrait", "Показать на карте");
            }
            ui.Box(new Style { Id = "info:details", Direction = Direction.Column, Gap = 4, Grow = 1, MaxHeight = height - 20, Scroll = true }, () =>
            {
                ui.Box(new Style { Id = "info:title", Width = text }, () =>
                    ui.Text(title, new TextStyle { Font = FontName.Title, Color = Bright, Shadow = Ink, Lift = 2, Ellipsis = true }));
                // Параметры юнита — строкой, целиком — в подсказке.
                var unitKind = !many && c.Has(first, S.P.Unit) ? kind : null;
                ui.Tip("info:title", many ? title : unitKind is not null ? Stats.Text(unitKind) : "");
                if (unitKind is not null)
                {
                    ui.Box(new Style { Id = "info:stats", Width = text }, () => ui.Text(Stats.Line(unitKind), new TextStyle { Color = Dim, Ellipsis = true }));
                    ui.Tip("info:stats", Stats.Text(unitKind));
                }
                if (!many && c.Get(first, S.P.Producer) is { } producer)
                    ProducerInfo(c, producer, text);
            });
        });
    }

    /// <summary>Картинка вида в клетке size × size; у сущности нет вида — пусто.</summary>
    private static void Picture(Ui ui, int proto, float size)
    {
        if (proto >= 0)
            ui.Image(proto, size, size);
    }

    /// <summary>Что делает здание: полоса готовности, очередь, подсказка про точку сбора.</summary>
    private void ProducerInfo(ClientContext c, ComponentValues producer, float text)
    {
        var ui = c.Ui;
        var current = (uint)producer.Number(S.P.Producer["unit"]);
        if (current != 0)
        {
            var name = Tools.Text(current);
            var unit = S.Data.Find("units/" + name);
            Bar(ui, producer.Number(S.P.Producer["progress"]) / (unit is not null && unit.TryNumber("time", out var time) ? time : 1), unit?.Text("title") ?? name, text);
            var queued = new List<string> { name };
            queued.AddRange(ReadQueue(producer.Bytes(S.P.Producer["queue"])));
            ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
            {
                for (var i = 0; i < queued.Count; i++)
                {
                    var proto = S.UnitProto(c, queued[i]);
                    ui.Box(new Style { Id = "queue:" + (i + 1), Width = 32, Height = 32, Skin = Skin.Well, Padding = 4 }, () => Picture(ui, proto, Queued));
                }
            });
        }
        ui.Text(producer.Flag(S.P.Producer["rally"]) ? "Готовые едут к флажку; правый щелчок по карте — перенести" : "Правый щелчок по карте — точка сбора готовых",
            new TextStyle { Color = Soft, Wrap = text });
    }

    /// <summary>Ничего не выбрано: подсказка по управлению.</summary>
    private static void Nothing(Ui ui, float width, float height) =>
        ui.Box(new Style { Width = width, Height = height, Skin = Skin.Well, Padding = (10, 12), Direction = Direction.Column, Justify = Align.Center, Gap = 4, Clip = true }, () =>
        {
            foreach (var line in (ReadOnlySpan<string>)["Ничего не выбрано.", "Левая кнопка — выбрать, рамка — несколько юнитов, правая — приказ."])
                ui.Text(line, new TextStyle { Color = Muted, Wrap = float.PositiveInfinity });
        });

    // ------------------------------------------------------------------ мини-карта

    /// <summary>
    /// Мини-карта — как minimap.ts и minimapView в bar.ts: местность, здания и юниты цветом стороны, рамка экрана;
    /// левая кнопка (и протяжка) ставит туда камеру, правая шлёт выделенных.
    /// </summary>
    private void MinimapView(ClientContext c, float side)
    {
        var ui = c.Ui;
        var size = S.Data.FieldSize(c.Game);
        var span = size * 2;
        var inner = side - 8;
        var scale = inner / span;
        var k = inner / 132;
        var dot = Math.Max(2, Lua.Round(2 * k));
        (double X, double Y) At(double x, double y) => ((x + size) * scale, (y + size) * scale);
        ui.Box(new Style { Id = "minimap", Width = side, Height = side, Skin = Skin.Well, Padding = 4, Clip = true }, () =>
        {
            ui.Box(new Style { Direction = Direction.Stack, Width = inner, Height = inner, Background = Color.Hex(0x05090f) }, () =>
            {
                ui.Minimap(inner, inner);
                // Разведанные месторождения — цветом своего ресурса.
                var deposits = c.Each(S.P.Deposit, S.P.Position);
                var spots = new (double X, double Y, double Angle)[deposits.Length];
                c.Poses(deposits, spots);
                for (var i = 0; i < deposits.Length; i++)
                {
                    var (px, py, _) = spots[i];
                    if (S.KindOf(c, deposits[i]) is not { } kind || !kind.TryNumber("color", out var color) || !c.Explored(px, py))
                        continue;
                    var (x, y) = At(px, py);
                    Dot(ui, Math.Floor(y - dot / 2), Math.Floor(x - dot / 2), dot, dot, Color.Hex((uint)color));
                }
                var owned = c.Each(S.P.Owner, S.P.Position);
                var owners = new double[owned.Length];
                var places = new (double X, double Y, double Angle)[owned.Length];
                c.Column(owned, S.P.Owner, "player", owners);
                c.Poses(owned, places);
                for (var i = 0; i < owned.Length; i++)
                {
                    var entity = owned[i];
                    var building = c.Get(entity, S.P.Building);
                    var (px, py, _) = places[i];
                    if ((building is null && !c.Has(entity, S.P.Unit)) || !(c.Mine(entity) || c.Sees(px, py)))
                        continue;
                    var color = owners[i] == c.Player ? Own : owners[i] == 0 ? Neutral : Foe;
                    var (x, y) = At(px, py);
                    double w = dot, h = dot;
                    if (building is not null)
                    {
                        w = Math.Max(dot, building.Number(S.P.Building["width"]) * scale);
                        h = Math.Max(dot, building.Number(S.P.Building["height"]) * scale);
                    }
                    Dot(ui, Math.Floor(y - h / 2), Math.Floor(x - w / 2), w, h, color);
                }
                // Рамка экрана.
                var (x1, y1) = c.ToWorld(0, 0);
                var (x2, y2) = c.ToWorld(c.ScreenWidth, c.ScreenHeight);
                var (leftEdge, topEdge) = At(x1, y1);
                var (rightEdge, bottomEdge) = At(x2, y2);
                var line = Math.Max(1, Lua.Round(k));
                double w2 = Lua.Round(rightEdge - leftEdge), h2 = Lua.Round(bottomEdge - topEdge);
                var frame = Color.Hex(0xe4f4ff);
                foreach (var (px, py, pw, ph) in (ReadOnlySpan<(double, double, double, double)>)[(leftEdge, topEdge, w2, line), (leftEdge, topEdge + h2 - line, w2, line), (leftEdge, topEdge, line, h2), (leftEdge + w2 - line, topEdge, line, h2)])
                    Dot(ui, Lua.Round(py), Lua.Round(px), pw, ph, frame);
            });
        });
        var held = ui.Held("minimap");
        var click = ui.Clicked("minimap");
        (double X, double Y) TileAt(double u, double v) => (-size + u * span, -size + v * span);
        if (held is { } at)
            (c.Camera.X, c.Camera.Y) = TileAt(at.U, at.V);
        if (click is { Button: 2 } && ui.RectOf("minimap") is { } rect)
        {
            var input = c.Input;
            var u = Math.Clamp((input.X - rect.X - 4) / (rect.W - 8), 0, 1);
            var v = Math.Clamp((input.Y - rect.Y - 4) / (rect.H - 8), 0, 1);
            var (x, y) = TileAt(u, v);
            var units = Shared.Selection(c).Where(entity => c.Has(entity, S.P.Drive)).ToArray();
            if (units.Length > 0)
                c.Send("move", MemoryPackSerializer.Serialize(new MoveOrder { Units = units, X = (int)Math.Floor(x), Y = (int)Math.Floor(y) }));
        }
    }

    /// <summary>Точка или рамка мини-карты: прямоугольник w × h, левый верхний угол сдвинут на (left, top) от края.</summary>
    private static void Dot(Ui ui, double top, double left, double w, double h, Color color) =>
        ui.Box(new Style { Self = (Align.Start, Align.Start), Padding = ((float)top, 0, 0, (float)left) }, () =>
            ui.Box(new Style { Width = (float)w, Height = (float)h, Background = color }));

    // ------------------------------------------------------------------ нижняя панель

    private void BottomBar(ClientContext c)
    {
        var ui = c.Ui;
        var selection = Shared.Selection(c);
        var selected = selection.Length > 0;
        var side = MathF.Max(Inner, MathF.Min(MinimapSide, ui.Width - BarSpacing - GridWidth - InfoMin));
        var infoWidth = ui.Width - BarSpacing - side - GridWidth;
        ui.Box(new Style { Id = "bar", Anchor = new Anchor(Left: 0, Right: 0, Bottom: 0), Height = Inner + 16, Skin = Skin.Panel, Padding = (4, 8), Direction = Direction.Row, Align = Align.End, Gap = 8 }, () =>
        {
            ui.Box(new Style { Height = Inner, Direction = Direction.Column, Justify = Align.End }, () => MinimapView(c, side));
            ui.Box(new Style { Direction = Direction.Row, Gap = 8, Height = Inner, Align = Align.Center }, () =>
            {
                if (selected)
                    Info(c, selection, infoWidth, Inner);
                else
                    Nothing(ui, infoWidth, Inner);
                CommandGrid(c, selected ? Slots(c, selection) : [], GridWidth, Inner);
            });
        });
    }
}
