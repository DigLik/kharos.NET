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

    /// <summary>Ячейка сетки команд: заказ юнита (Unit — его вид, Cost — цена) или приказ зданию и MCV; Cancel — правый щелчок.</summary>
    private sealed record Slot(string Label, int Picture, double? Cost, bool Short, string? About, Kind? Unit, Action Run, Action? Cancel = null);

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

    /// <summary>
    /// Ячейки сетки команд для выбранного: у своего производящего здания — заказ юнитов (правый щелчок — отмена
    /// последнего), у MCV — развернуть, у готового главного здания — свернуть. Годится ли заказ (требования, тема, лимит),
    /// знает хост: отказ приходит сигналом refused.
    /// </summary>
    private List<Slot> Slots(ClientContext c, uint[] selection)
    {
        var list = new List<Slot>();
        if (selection.Length != 1 || !c.Has(selection[0], S.P.Producer) || !c.Mine(selection[0]))
            return list;
        var entity = selection[0];
        if (c.Has(entity, S.P.Unit))
        {
            // MCV — свёрнутая база: пока едет или разворачивается, можно отменить.
            if (c.Has(entity, S.P.Converting) || c.Has(entity, S.P.Deploys))
                list.Add(new Slot("Отмена", -1, null, false, "Отменить разворот: MCV останется машиной", null,
                    () => c.Send(CancelDeployOrder.Name, MemoryPackSerializer.Serialize(new CancelDeployOrder { Unit = entity }))));
            else
                list.Add(new Slot("Развернуть", c.ProtoId("buildings/" + Command), null, false, "Развернуть базу на месте: MCV становится главным зданием", null,
                    () => c.Send(DeployOrder.Name, MemoryPackSerializer.Serialize(new DeployOrder { Unit = entity }))));
            return list;
        }
        var money = S.Credits(c);
        foreach (var name in S.KindOf(c, entity)?.Strings("produces") ?? [])
        {
            if (S.Data.Find("units/" + name) is not { } unit)
                continue;
            var cost = unit.TryNumber("cost", out var price) ? price : (double?)null;
            list.Add(new Slot(unit.Text("title") ?? name, S.UnitProto(c, name), cost, cost is { } p && money < p, unit.Text("about"), unit,
                () => c.Send(ProduceOrder.Name, MemoryPackSerializer.Serialize(new ProduceOrder { Producer = entity, Unit = name })),
                () => c.Send(CancelProductionOrder.Name, MemoryPackSerializer.Serialize(new CancelProductionOrder { Producer = entity }))));
        }
        if (S.KindOf(c, entity)?.Name == Command && !c.Has(entity, S.P.Converting))
            list.Add(new Slot("Свернуть", c.ProtoId("units/mcv"), null, false, "Свернуть базу обратно в MCV", null,
                () => c.Send(PackOrder.Name, MemoryPackSerializer.Serialize(new PackOrder { Building = entity }))));
        return list;
    }

    /// <summary>Главное здание: в него разворачивается MCV.</summary>
    private const string Command = "command";

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
            foreach (var row in slot.Unit is null ? [] : Stats.Unit(slot.Unit))
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
                        if (click is { Button: 2 })
                            slot.Cancel?.Invoke();
                        else if (click is not null || (c.Input.Hits.Contains(KeyNames[i]) && !c.Input.Ctrl && !c.Input.Keys.Contains("ctrl")))
                            slot.Run();
                    }
                });
        });
    }

    // ------------------------------------------------------------------ сведения о выбранном

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
                // Обзор — свой у каждого юнита (Sight.radius): хост считает его по классу и оружию.
                double? seen = c.Get(first, S.P.Sight) is { } sight && sight.Number(S.P.Sight["radius"]) > 0 ? sight.Number(S.P.Sight["radius"]) : null;
                ui.Tip("info:title", many ? title : unitKind is not null ? Stats.Text(unitKind, seen) : "");
                if (unitKind is not null)
                {
                    ui.Box(new Style { Id = "info:stats", Width = text }, () => ui.Text(Stats.Line(unitKind, seen), new TextStyle { Color = Dim, Ellipsis = true }));
                    ui.Tip("info:stats", Stats.Text(unitKind, seen));
                }
                if (!many && c.Get(first, S.P.Converting) is { } converting && converting.Number(S.P.Converting["total"]) > 0)
                    Bar(ui, 1 - converting.Number(S.P.Converting["left"]) / converting.Number(S.P.Converting["total"]),
                        c.Has(first, S.P.Unit) ? "Разворачивается" : "Сворачивается", text);
                if (!many && c.Mine(first) && c.Get(first, S.P.Producer) is { } producer)
                    ProducerInfo(c, first, producer, text);
            });
        });
    }

    /// <summary>Картинка вида в клетке size × size; у сущности нет вида — пусто.</summary>
    private static void Picture(Ui ui, int proto, float size)
    {
        if (proto >= 0)
            ui.Image(proto, size, size);
    }

    /// <summary>
    /// Что делает производитель: полоса готовности первого заказа (Producer.current — вид юнита или −1; progress — в
    /// тиках), очередь (Producer.queue — виды юнитов, первый строится), у здания — подсказка про точку сбора. Щелчок по
    /// значку очереди снимает последний заказ.
    /// </summary>
    private void ProducerInfo(ClientContext c, uint entity, ComponentValues producer, float text)
    {
        var ui = c.Ui;
        var queue = BaseState.Queue(producer, S.P.Producer);
        if ((int)producer.Number(S.P.Producer["current"]) is var current and >= 0 && queue.Count > 0)
        {
            var unit = S.KindOf(c, current);
            Bar(ui, producer.Number(S.P.Producer["progress"]) / S.BuildTicks(c, unit), unit?.Text("title") ?? unit?.Name ?? "?", text);
            ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
            {
                for (var i = 0; i < queue.Count; i++)
                {
                    var proto = queue[i];
                    var id = "queue:" + (i + 1);
                    ui.Box(new Style { Id = id, Width = 32, Height = 32, Skin = Skin.Well, Padding = 4 }, () => Picture(ui, proto, Queued));
                    if (ui.Clicked(id) is not null)
                        c.Send(CancelProductionOrder.Name, MemoryPackSerializer.Serialize(new CancelProductionOrder { Producer = entity }));
                }
            });
        }
        if (c.Has(entity, S.P.Building))
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
                // Месторождения, что клиент знает (слой deposits), — цветом своего ресурса; середина — центр 2 × 2.
                foreach (var spot in Deposits(c, size))
                {
                    var (x, y) = At(spot.X + 1, spot.Y + 1);
                    Dot(ui, Math.Floor(y - dot / 2), Math.Floor(x - dot / 2), dot, dot, spot.Color);
                }
                var owned = c.Each(S.P.Owner, S.P.Position);
                var owners = new double[owned.Length];
                var places = new (double X, double Y, double Angle)[owned.Length];
                c.Column(owned, S.P.Owner, "player", owners);
                c.Poses(owned, places);
                for (var i = 0; i < owned.Length; i++)
                {
                    var entity = owned[i];
                    var footprint = c.Has(entity, S.P.Building) ? S.Size(c, entity) : null;
                    var (px, py, _) = places[i];
                    if ((footprint is null && !c.Has(entity, S.P.Unit)) || !(c.Mine(entity) || c.Sees(px, py)))
                        continue;
                    var color = owners[i] == c.Player ? Own : owners[i] == 0 ? Neutral : Foe;
                    // Место здания — левый верхний тайл, юнита — середина.
                    var (x, y) = footprint is { } size2 ? At(px + size2.W / 2, py + size2.H / 2) : At(px, py);
                    double w = dot, h = dot;
                    if (footprint is { } area)
                    {
                        w = Math.Max(dot, area.W * scale);
                        h = Math.Max(dot, area.H * scale);
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
            var units = Shared.Selection(c).Where(entity => c.Has(entity, S.P.Mover) && c.Mine(entity)).ToArray();
            if (units.Length > 0)
                c.Send(MoveOrder.Name, MemoryPackSerializer.Serialize(new MoveOrder { Units = units, X = (int)Math.Floor(x), Y = (int)Math.Floor(y), Queue = input.Shift }));
        }
    }

    /// <summary>Известное месторождение для мини-карты: левый верхний тайл и цвет ресурса.</summary>
    private readonly record struct Spot(double X, double Y, Color Color);

    private readonly List<Spot> _spots = [];
    private double _spotsAt = double.NegativeInfinity;

    /// <summary>
    /// Месторождения, что клиент знает (предметы слоя deposits; под туманом — только разведанные), раз в секунду:
    /// слой не меняется чаще, а кадр не должен ходить за ним каждый раз. Цвет — look.color записи вида.
    /// </summary>
    private List<Spot> Deposits(ClientContext c, double size)
    {
        if (S.P.Deposits is not { } layer || c.Time - _spotsAt < 1)
            return _spots;
        _spotsAt = c.Time;
        _spots.Clear();
        var kindField = Math.Max(0, layer.Fields.ToList().IndexOf("kind"));
        foreach (var item in Tools.Items(layer, 0, 0, 0, size * 1.5))
        {
            var kind = (int)item.Fields[kindField];
            if ((uint)kind >= (uint)DepositArt.Types.Length)
                continue;
            var color = S.Data.Find("deposits/" + DepositArt.Types[kind])?.Number("color", 0xc8c8c8) ?? 0xc8c8c8;
            _spots.Add(new Spot(item.X, item.Y, Color.Hex((uint)color)));
        }
        return _spots;
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
