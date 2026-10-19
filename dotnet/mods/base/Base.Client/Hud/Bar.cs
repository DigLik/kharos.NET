using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk.Client;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Нижняя панель — перенос gui/bar.ts: мини-карта | сведения о выбранном | сетка команд 4 × 3. На узком экране (уже
/// 1000) — два ряда: мини-карта и сведения, под ними сетка во всю ширину. Те же размеры, рамки, подсказки и карточки.
/// </summary>
internal sealed class Bar(BaseState state, Minimap minimap)
{
    /// <summary>Внутренняя высота панели; на узком — меньше (INNER, INNER_NARROW).</summary>
    public const float Inner = 140, InnerNarrow = 116;

    /// <summary>Высота сетки на узком экране: три ряда по 40.</summary>
    private const float GridNarrow = 3 * 40 + 2 * 4;
    private const float CellWidth = 132;
    private const float Portrait = 96, Tile = 32, Queued = 24;
    private const float GridWidth = 4 * CellWidth + 3 * 4;

    /// <summary>Меньше этого сведениям о выбранном не дать: портрет, промежуток и полторы сотни на текст.</summary>
    private const float InfoMin = 104 + 10 + 24 + 150;

    /// <summary>Отступы панели и промежутки между мини-картой, сведениями и сеткой.</summary>
    private const float BarSpacing = 16 + 8 + 8;

    /// <summary>Сторона мини-карты из настроек (prefs.minimap).</summary>
    private const float MinimapPref = 200;

    /// <summary>Высота нижней панели (barHeight).</summary>
    public static float HeightOf(float width) => width < TopBar.Narrow ? InnerNarrow + GridNarrow + 16 + 4 : Inner + 16;

    /// <summary>Сторона мини-карты (minimapSide).</summary>
    public static float MinimapSide(float width) => width < TopBar.Narrow ? InnerNarrow : MathF.Max(Inner, MathF.Min(MinimapPref, width - BarSpacing - GridWidth - InfoMin));

    private static string Percent(double value) => $"{Math.Round(value * 100, MidpointRounding.AwayFromZero)}%";

    /// <summary>Всё, что панели нужно на кадр.</summary>
    public sealed record Frame(ClientContext C, HudState State, Slot?[] Slots, BaseTables Tables, Tr T, HudParts Parts, Dictionary<uint, long> Heads);

    public void Draw(Frame f)
    {
        var (c, hud) = (f.C, f.State);
        var ui = c.Ui;
        var narrow = ui.Width < TopBar.Narrow;
        var inner = narrow ? InnerNarrow : Inner;
        var side = MinimapSide(ui.Width);
        var slots = hud.Selected ? f.Slots : new Slot?[Commands.Size];
        var top = TopBar.HeightOf(ui.Width);
        var bottom = HeightOf(ui.Width);
        if (hud.Editor)
        {
            ui.Box(new Style { Id = "bar", Anchor = new Anchor(Left: 0, Bottom: 0), Skin = Skin.Panel, Padding = (4, 8), Direction = Direction.Row, Align = Align.End, Height = inner + 16 },
                () => minimap.View(c, f.Parts, side, top, bottom, f.Heads));
            return;
        }
        if (narrow)
        {
            var full = ui.Width - 16;
            ui.Box(new Style { Id = "bar", Anchor = new Anchor(Left: 0, Right: 0, Bottom: 0), Height = bottom, Skin = Skin.Panel, Padding = (8, 8), Direction = Direction.Column, Gap = 4 }, () =>
            {
                ui.Box(new Style { Direction = Direction.Row, Gap = 4, Height = inner }, () =>
                {
                    minimap.View(c, f.Parts, side, top, bottom, f.Heads);
                    if (hud.Selected)
                        Info(f, full - side - 4, inner);
                    else
                        Nothing(ui, f.T, full - side - 4, inner);
                });
                Grid(f, slots, full, GridNarrow);
            });
            return;
        }
        // Мини-карта занимает в ряду всю свою сторону, даже когда выступает над панелью; сведениям — остаток.
        var infoWidth = ui.Width - BarSpacing - side - GridWidth;
        ui.Box(new Style { Id = "bar", Anchor = new Anchor(Left: 0, Right: 0, Bottom: 0), Height = Inner + 16, Skin = Skin.Panel, Padding = (4, 8), Direction = Direction.Row, Align = Align.End, Gap = 8 }, () =>
        {
            ui.Box(new Style { Height = Inner, Direction = Direction.Column, Justify = Align.End }, () => minimap.View(c, f.Parts, side, top, bottom, f.Heads));
            ui.Box(new Style { Direction = Direction.Row, Gap = 8, Height = Inner, Align = Align.Center }, () =>
            {
                if (hud.Selected)
                    Info(f, infoWidth, Inner);
                else
                    Nothing(ui, f.T, infoWidth, Inner);
                Grid(f, slots, GridWidth, Inner);
            });
        });
    }

    // ------------------------------------------------------------------ сетка команд

    /// <summary>Картинка ячейки: номер облика (портрет здания или юнита, значок груза или покрытия); нет — −1.</summary>
    private static int Picture(ClientContext c, Slot slot) =>
        slot.Building is { } building ? c.ProtoId("buildings/" + building)
        : slot.Unit is { } unit ? c.ProtoId("units/" + unit)
        : slot.Good is { } good ? c.LookId("goods/" + good)
        : slot.Pave is { } pave ? c.LookId("portraits/pave/" + pave)
        : -1;

    /// <summary>
    /// Сетка команд 4 × 3: ячейка — кнопка со своей клавишей, картинкой, названием и ценой; полная цена с материалами и
    /// энергией — в карточке при наведении, над сеткой у правого края. Клавиши жмёт <see cref="Hud"/>.
    /// </summary>
    private static void Grid(Frame f, Slot?[] slots, float width, float height)
    {
        var (c, t) = (f.C, f.T);
        var ui = c.Ui;
        var cellWidth = (width - 3 * 4) / 4;
        var cellHeight = (height - 2 * 4) / 3;
        var stock = f.State.Stock?.Items ?? [];
        var zones = false;
        ui.Box(new Style { Id = "commands", Direction = Direction.Column, Gap = 4, Width = width, Height = height }, () =>
        {
            for (var row = 0; row < 3; row++)
                ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                {
                    for (var column = 0; column < 4; column++)
                    {
                        var i = row * 4 + column;
                        var hotkey = Commands.KeyLabel(i);
                        if (slots[i] is not { } slot)
                        {
                            ui.Box(new Style { Width = cellWidth, Height = cellHeight, Skin = Skin.Well, Alpha = 0.35f });
                            continue;
                        }
                        var id = "cell:" + i;
                        var picture = Picture(c, slot);
                        var keyColor = slot.Disabled ? Muted : Gold;
                        // Отступы — как у кнопки страницы: с картинкой она стоит почти у рамки, без неё текст отходит на 8.
                        // Подсказка «Первых шагов» — огнями команды раз в секунду, новое — золотом раз в 1,6 с (pulse в gui/bar.ts).
                        var outline = slot.Hint && !slot.Disabled ? Pulse(ui, 90, 169, 255, 1, 0.3, 1) : slot.Fresh && !slot.Disabled ? Pulse(ui, 240, 201, 90, 1.6, 0.2, 0.95) : (Color?)null;
                        var click = ui.Button(id, Style.Button with { Width = cellWidth, Height = cellHeight, Padding = (0, 6, 0, picture >= 0 ? 4 : 8), Gap = 6, Justify = Align.Start, Outline = outline }, () =>
                        {
                            if (picture >= 0)
                                ui.Box(new Style { Direction = Direction.Stack }, () =>
                                {
                                    // Раздел — стопкой из двух карточек: одна выглядывает из-за другой.
                                    if (slot.Group is not null)
                                        ui.Box(new Style { Self = (Align.End, Align.Start), Width = Tile, Height = Tile, Background = Color.Hex(0x2d4560), Padding = (0, 0, 3, 3) });
                                    var good = slot.Good is not null;
                                    ui.Box(new Style { Width = Tile, Height = Tile, Background = slot.Group is not null ? Color.Hex(0x1c2b3e) : null, Padding = good ? 1 : 0 },
                                        () => ui.Image(picture, good ? 30 : Tile, good ? 30 : Tile));
                                    ui.Box(new Style { Self = (Align.Start, Align.Start), Background = Ink, Padding = (0, 2) }, () => Key(ui, hotkey, keyColor));
                                });
                            ui.Box(new Style { Direction = Direction.Column, Grow = 1, Gap = 0, Clip = true }, () =>
                            {
                                ui.Box(new Style { Direction = Direction.Row, Gap = 6 }, () =>
                                {
                                    ui.Box(new Style { Grow = 1 }, () => ui.Text(slot.Label, Shadowed(Bright, ellipsis: true)));
                                    if (picture < 0)
                                        Key(ui, hotkey, keyColor);
                                });
                                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
                                {
                                    if (slot.Group is { } group)
                                        ui.Text($"{group} ▸", new TextStyle { Color = Dim });
                                    if (slot.Cost is { } cost)
                                    {
                                        Coin(ui);
                                        ui.Text(slot.Gain ? $"+{Commands.N(cost)}" : Commands.N(cost), Shadowed(slot.Gain ? Good : slot.Short ? Warn : Gold));
                                    }
                                    if (slot.Cost is null && slot.Materials is { Count: > 0 })
                                        ui.Text("…", new TextStyle { Color = Dim });
                                });
                            });
                            if (slot.Lamp is { } lamp)
                                ui.Box(new Style { Width = 6, Height = 6, Background = lamp ? Good : Bad });
                        }, active: slot.Active, disabled: slot.Disabled);
                        // Подсказка — карточкой над сеткой, правым краем у указателя.
                        var grid = ui.RectOf("commands");
                        var pointer = c.Input.X;
                        Anchor? anchor = grid is { } rect
                            ? new Anchor(Right: float.IsNaN(pointer) ? 8 : MathF.Max(8, c.ScreenWidth - pointer - 40), Bottom: c.ScreenHeight - rect.Y + 12)
                            : null;
                        ui.Tip(id, () => Card(f, slot, hotkey, stock), anchor);
                        // Курсор на кнопке здания или трубы — на карте видны границы зон строительства.
                        if (slot.Zones && ui.Hovered(id))
                            zones = true;
                        // Новое под указателем становится знакомым: подсветка с него уходит, а с раздела — когда знакомо всё в нём.
                        if (slot.Fresh && slot.Seen is { } seen && ui.Hovered(id))
                            Seen.Mark(c, seen);
                        if (click is { Button: not 2 })
                            slot.Run();
                    }
                });
        });
        if (zones)
            c.Shared[Modes.ZonesHoverKey] = true;
        else
            c.Shared.Remove(Modes.ZonesHoverKey);
    }

    /// <summary>Пульс обводки ячейки: доля от low до high по косинусу с периодом period секунд, восемь ступеней.</summary>
    internal static Color Pulse(Ui ui, byte r, byte g, byte b, double period, double low, double high)
    {
        var phase = ui.Now % period / period;
        var level = Math.Round((low + (high - low) * (0.5 - 0.5 * Math.Cos(phase * Math.PI * 2))) * 8, MidpointRounding.AwayFromZero) / 8;
        return new Color(r, g, b, Ui.Byte((float)(level * 255)));
    }

    /// <summary>
    /// Карточка ячейки (slotCard): название с клавишей, пояснение, параметры юнита, полная цена и энергия, материалы
    /// строками с названием; чего в хранилищах меньше, чем нужно, — «есть/нужно».
    /// </summary>
    private static void Card(Frame f, Slot slot, string hotkey, List<Stack> stock)
    {
        var (c, t) = (f.C, f.T);
        var ui = c.Ui;
        const float width = 264;
        double Stored(string good) => Math.Floor(stock.FirstOrDefault(stack => stack.Resource == good).Amount);
        ui.Box(new Style { Direction = Direction.Column, Gap = 4, Width = width, Align = Align.Stretch }, () =>
        {
            ui.Box(new Style { Direction = Direction.Row, Justify = Align.Between, Align = Align.End, Gap = 12 }, () =>
            {
                ui.Text(slot.Label, new TextStyle { Font = FontName.Title, Color = Bright });
                ui.Text(hotkey, new TextStyle { Font = FontName.Title, Color = Gold });
            });
            if (slot.About is { } about)
                ui.Text(about, new TextStyle { Color = Dim, Wrap = width });
            if (slot.Title is { } title)
                ui.Text(title, new TextStyle { Color = slot.About is not null && slot.Disabled ? Warn : Dim, Wrap = width });
            if (slot.Unit is { } unit)
                foreach (var row in Stats.Unit(f.Tables, unit, t))
                    ui.Box(new Style { Direction = Direction.Row, Gap = 10 }, () =>
                    {
                        ui.Box(new Style { Width = 90 }, () => ui.Text(row.Label, new TextStyle { Color = Dim }));
                        ui.Text(row.Value);
                    });
            if (slot.Cost is not null || slot.Power is not null and not 0)
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 10 }, () =>
                {
                    if (slot.Cost is { } cost)
                        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
                        {
                            Coin(ui);
                            ui.Text(slot.Gain ? $"+{Commands.N(cost)}" : Commands.N(cost), new TextStyle { Color = slot.Gain ? Good : Gold });
                        });
                    if (slot.Power is { } power and not 0)
                    {
                        ui.Text($"⚡{(power > 0 ? "+" : "")}{Commands.N(power)}", new TextStyle { Color = Energy });
                        if (slot.Idle is { } idle)
                            ui.Text(t("grid.idlePower", "idle", Commands.N(idle)), new TextStyle { Color = Dim });
                    }
                });
            foreach (var (resource, amount) in slot.Materials ?? [])
            {
                var have = Stored(resource);
                var lacks = have < amount;
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
                {
                    if (c.LookId("goods/" + resource) is var look and >= 0)
                        ui.Image(look, 16, 16);
                    ui.Box(new Style { Grow = 1 }, () => ui.Text(t("goods." + resource), new TextStyle { Ellipsis = true }));
                    ui.Text(lacks ? $"{Commands.N(have)}/{Commands.N(amount)}" : Commands.N(amount), new TextStyle { Color = lacks ? Warn : Text });
                });
            }
        });
    }

    // ------------------------------------------------------------------ сведения о выбранном

    /// <summary>Кусок строки подсказки: текст или груз со значком и числом.</summary>
    private readonly record struct Run(string? Text, string? Good = null, double? Amount = null, double? Of = null);

    /// <summary>
    /// Строка подсказки панели сведений (hint): текст со значками, перенос по ширине. Тексты подряд склеиваются: движок
    /// собирает в строку со значками только текст рядом со значком.
    /// </summary>
    private static void Hint(ClientContext c, IEnumerable<Run> runs, float width, Color? color = null)
    {
        var style = new TextStyle { Color = color ?? Soft, Wrap = width };
        var list = runs.Where(run => run.Good is not null || !string.IsNullOrEmpty(run.Text)).ToList();
        if (list.All(run => run.Good is null))
        {
            c.Ui.Text(string.Concat(list.Select(run => run.Text)), style);
            return;
        }
        var text = "";
        foreach (var run in list)
        {
            if (run.Good is null)
            {
                text += run.Text;
                continue;
            }
            if (text.Length > 0)
                c.Ui.Text(text, style);
            text = "";
            var look = c.LookId("goods/" + run.Good);
            if (look >= 0)
                c.Ui.Icon(look, 0, GoodSize);
            text = run.Amount is { } amount ? run.Of is { } of ? $"{Commands.N(amount)}/{Commands.N(of)} " : $"{Commands.N(amount)} " : " ";
        }
        if (text.Length > 0)
            c.Ui.Text(text, style);
    }

    private static void Hint(ClientContext c, string text, float width, Color? color = null) => Hint(c, [new Run(text)], width, color);

    private static Run G(string good, double? amount = null, double? of = null) => new(null, good, amount, of);

    /// <summary>Сведения о выбранном (info): портрет (или плитки отряда), название, прочность и всё, что выбранное делает.</summary>
    private void Info(Frame f, float width, float height)
    {
        var (c, hud, t, tables) = (f.C, f.State, f.T, f.Tables);
        var ui = c.Ui;
        var units = hud.Units;
        var building = hud.Building;
        var many = units.Count > 1 || (units.Count > 0 && units[0].Count > 1);
        var health = building is not null ? hud.Health ?? 1 : hud.Army?.Health ?? 1;
        var stats = building is null && units.Count == 1 ? units[0].Type : null;
        var title = building is not null
            ? hud.Buildings > 1
                ? hud.Mixed ? t("hud.buildingsCount", "count", Commands.N(hud.Buildings)) : $"{t("buildings." + building)} ×{hud.Buildings}"
                : hud.Site is { } work ? t(work.Undo ? "hud.undoing" : work.Demolish ? "hud.demolition" : "hud.construction", "name", t("buildings." + building)) : t("buildings." + building)
            : string.Join(", ", units.Select(unit => unit.Count > 1 ? $"{t("units." + unit.Type)} ×{unit.Count}" : t("units." + unit.Type)));
        // Плиток отряда в ряд — сколько влезает рядом с текстом (до четырёх). Ширина подробностей — остаток.
        var columns = Math.Max(1, Math.Min(4, (int)Math.Floor((width - 24 - 10 - 150 + 4) / 44)));
        var left = many ? columns * 40 + (columns - 1) * 4 : 104;
        var text = MathF.Max(40, width - 24 - left - 10);
        var selection = Shared.Selection(c);
        ui.Box(new Style { Width = width, Height = height, Skin = Skin.Well, Padding = (10, 12), Direction = Direction.Row, Gap = 10 }, () =>
        {
            if (many)
            {
                ui.Box(new Style { Direction = Direction.Column, Gap = 4, Width = left, MaxHeight = height - 20, Scroll = true, Id = "info:units" }, () =>
                {
                    for (var start = 0; start < units.Count; start += columns)
                    {
                        var from = start;
                        ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                        {
                            foreach (var (type, count) in units.Skip(from).Take(columns))
                            {
                                var id = "unit:" + type;
                                var click = ui.Button(id, Style.Button with { Width = 40, Height = 40, Padding = 0, Skin = Skin.Well, Direction = Direction.Stack }, () =>
                                {
                                    ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => ui.Image(c.ProtoId("units/" + type), Tile, Tile));
                                    if (count > 1)
                                        ui.Box(new Style { Self = (Align.End, Align.End), Padding = (0, 5, 3, 0) }, () => ui.Text(Commands.N(count), new TextStyle { Color = Bright, Shadow = Ink }));
                                });
                                ui.Tip(id, t("hud.narrowTip", "name", t("units." + type)));
                                if (click is { } press)
                                    Narrow(c, type, press.Shift);
                            }
                        });
                    }
                });
            }
            else
            {
                var proto = building is not null ? c.ProtoId("buildings/" + building) : units.Count > 0 ? c.ProtoId("units/" + units[0].Type) : -1;
                var click = ui.Button("portrait", Style.Button with { Width = 104, Height = 104, Padding = 0, Skin = Skin.Well, Direction = Direction.Stack }, () =>
                {
                    if (proto >= 0)
                        ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => ui.Image(proto, Portrait, Portrait));
                    // Прочность — полоской по низу портрета: красной, когда её мало.
                    ui.Box(new Style { Self = (Align.Start, Align.End), Width = MathF.Max(2, MathF.Round(104 * (float)health, MidpointRounding.AwayFromZero)), Height = 6, Background = health < 0.35 ? Bad : Color.Hex(0x4fd07a) });
                });
                ui.Tip("portrait", t("hud.showOnMap"));
                if (click is not null)
                    LookAt(c, selection);
            }

            ui.Box(new Style { Id = "info:details", Direction = Direction.Column, Gap = 4, Grow = 1, MaxHeight = height - 20, Scroll = true }, () =>
            {
                ui.Box(new Style { Id = "info:title", Width = text }, () =>
                    ui.Text(title, new TextStyle { Font = FontName.Title, Color = Bright, Shadow = Ink, Lift = 2, Ellipsis = true }));
                // Обзор — свой у каждого юнита (Sight.radius): хост считает его по классу и оружию.
                double? seen = stats is not null && selection.Length > 0 && f.Parts.Sight is { } sightRef && c.Get(selection[0], sightRef) is { } sight
                    && HudParts.Num(sight, sightRef, "radius") is var radius and > 0 ? radius : null;
                ui.Tip("info:title", many ? title : stats is not null ? Stats.Text(tables, stats, t, seen) : "");
                if (stats is not null)
                {
                    ui.Box(new Style { Id = "info:stats", Width = text }, () => ui.Text(Stats.Line(tables, stats, t, seen), new TextStyle { Color = Dim, Ellipsis = true }));
                    ui.Tip("info:stats", Stats.Text(tables, stats, t, seen));
                }
                Details(f, text);
            });
        });
    }

    /// <summary>Подробности под названием: энергия, прочность, склад, заказ, работа, стройка, производство.</summary>
    private void Details(Frame f, float text)
    {
        var (c, hud, t) = (f.C, f.State, f.T);
        var ui = c.Ui;
        if (hud.Power is { } power)
            Hint(c, GridText(power, t), text, power.Demand > power.Produced || power.Overload ? Bad : Energy);
        if (hud.Relief)
            Hint(c, t("hud.relief", "below", Commands.N(PowerRules.ReliefBelow), "income", Commands.N(PowerRules.ReliefIncome)), text);
        if (hud.Starved)
            Hint(c, t("hud.starved"), text, Bad);
        if (hud.Switchable is { On: false })
            Hint(c, t("hud.switchedOff"), text, Warn);
        if (hud.Health is { } health)
            Hint(c, t("hud.health", "percent", Percent(health), "repair", Commands.N(hud.Repair)), text);
        if (hud.Shield is { } shield)
            Hint(c, shield.Down > 0 ? t("hud.shieldDown", "seconds", Commands.N(shield.Down)) : t("hud.shield", "charge", Commands.N(shield.Charge), "capacity", Commands.N(shield.Capacity)), text);
        if (hud.Battery is { } battery)
            Hint(c, t(battery.Flow > 0 ? "hud.batteryGiving" : battery.Flow < 0 ? "hud.batteryCharging" : "hud.battery",
                "charge", Commands.N(battery.Charge), "capacity", Commands.N(battery.Capacity), "flow", Commands.N(Math.Abs(battery.Flow))), text);
        if (hud.Army is { } army)
        {
            if (army.Health < 1)
                Hint(c, t("hud.armyHealth", "percent", Percent(army.Health)), text);
            if (army.Ammo is { } ammo && ammo < 1)
                Hint(c, t("hud.armyAmmo", "percent", Percent(ammo)), text);
            if (army.Empty > 0)
                Hint(c, t("hud.armyEmpty", "count", Commands.N(army.Empty)), text);
            if (army.Armed > 0 && hud.Construction is null)
                Hint(c, t("hud.armed"), text);
        }
        if (hud.Stored is { Slots: { } slots })
            Hint(c, [new Run($"{t("hud.stock")}: "), .. slots.Select(slot => G(slot.Resource, slot.Amount, slot.Of))], text);
        if (hud.Stored is { Slots: null } stored)
            Hint(c, [
                new Run($"{(stored.Buffer ? t("hud.orderMaterials") : stored.Store ? t("hud.storage") : t("hud.stock"))}: "),
                .. stored.Items.Select(stack => G(stack.Resource, stack.Amount)),
                new Run(stored.Items.Count == 0 ? t("common.empty") : ""),
                new Run(!stored.Buffer ? t("hud.holds", "capacity", Commands.N(stored.Capacity)) : ""),
                new Run(hud.Reserve is { Amount: > 0 } reserve ? t("hud.reserve", "amount", Commands.N(reserve.Amount)) : ""),
            ], text);
        if (hud.Request is { } request)
            Hint(c, [
                new Run($"{t("hud.request")}: "),
                .. Commands.Wares(f.Tables).Where(ware => request.Wanted.GetValueOrDefault(ware) > 0).Select(ware => G(ware, request.Have.GetValueOrDefault(ware), request.Wanted[ware])),
                new Run(request.Wanted.All(pair => pair.Value <= 0) ? t("hud.requestNone") : ""),
            ], text);
        if (hud.Refinery is { } refinery && f.Tables.Ores.GetValueOrDefault(refinery.Ore)?.Resource is { } product)
            Hint(c, Rich(t("hud.refines", "ore", "\u0001", "oreName", t("goods." + refinery.Ore), "product", "\u0002", "productName", t("goods." + product), "intake", Commands.N(refinery.Intake)),
                [G(refinery.Ore)], [G(product)]), text);
        if (hud.Ammo is { } turretAmmo)
            Hint(c, [new Run($"{t("hud.ammo")}: "), G("ammo", turretAmmo.Have, turretAmmo.Capacity), new Run(turretAmmo.Have <= 0 ? t("hud.ammoEmpty") : t("hud.ammoTip"))], text, turretAmmo.Have <= 0 ? Warn : Soft);
        if (hud.Rods is { } rods)
            Hint(c, [new Run($"{t("hud.rods")}: "), G("rods", rods.Have, rods.Capacity), new Run(rods.Burning ? t("hud.rodsTip") : t("hud.rodsEmpty"))], text, rods.Have <= 0 && !rods.Burning ? Warn : Soft);
        if (hud.Assembly is { } assembly)
        {
            var phase = !assembly.On ? assembly.Progress > 0 ? "offFinishing" : "off" : assembly.Progress > 0 ? "working" : "waiting";
            Hint(c, Rich(t("hud.recipe", "inputs", "\u0001", "output", "\u0002", "seconds", Commands.N(assembly.Seconds)),
                [.. assembly.Inputs.Select(stack => G(stack.Resource, stack.Amount))], [G(assembly.Recipe, assembly.Yield)]), text);
            Widgets.Bar(ui, assembly.Progress, t("hud.assembly", "name", t("goods." + assembly.Recipe), "have", Commands.N(assembly.Have), "state", t("hud.assemblyStates." + phase)), text);
            if (!assembly.On)
                Hint(c, t("hud.plantOff"), text, Warn);
        }
        if (hud.Cargo is { } cargo)
            Hint(c, [
                .. CargoRuns(t, cargo.Items, cargo.Capacity),
                new Run(hud.Haul is { } haul ? " " + JobText(haul.Job, t) : ""),
            ], text);
        if (hud.Deposit is { } deposit)
            Hint(c, Rich(t("hud.depositLeft", "icon", "\u0001", "name", t("goods." + deposit.Kind), "left", deposit.Left > 0 ? t("hud.left", "amount", Commands.N(deposit.Left)) : t("hud.depositDepleted")),
                [G(deposit.Kind)]), text);
        if (hud.Level is { } level)
        {
            Hint(c, level.Level > 1
                ? t("hud.level", "level", Commands.N(level.Level), "max", Commands.N(level.Max), "gain", t("levelGains." + level.Gain), "factor", Commands.N(level.Factor))
                : t("hud.levelBase", "level", Commands.N(level.Level), "max", Commands.N(level.Max)), text);
            if (level.Raising is { } raising)
                Widgets.Bar(ui, raising.Progress, raising.Progress > 0 ? t("hud.upgrading", "percent", Percent(raising.Progress)) : t("hud.waitsToUpgrade"), text);
        }
        if (hud.Silo is { } silo)
        {
            if (silo.Stage == "assembling")
                Widgets.Bar(ui, silo.Progress, t("hud.warheadAssembling", "percent", Percent(silo.Progress)), text);
            else
                Hint(c, silo.Stage == "launching" ? t("hud.launching", "seconds", Commands.N(silo.Launch)) : silo.Stage == "ready" ? t("hud.warheadReady") : t("hud.warheadEmpty"),
                    text, silo.Stage == "launching" ? Warn : Soft);
        }
        if (hud.Interceptor is { } guard)
        {
            if (guard.Assembling)
                Widgets.Bar(ui, guard.Progress, t("hud.interceptorAssembling", "count", Commands.N(guard.Missiles), "capacity", Commands.N(guard.Capacity), "percent", Percent(guard.Progress)), text);
            else
                Hint(c, t("hud.interceptorReady", "count", Commands.N(guard.Missiles), "capacity", Commands.N(guard.Capacity), "radius", Commands.N(guard.Radius)), text);
        }
        if (hud.Research is { Topic: null })
            Hint(c, t("hud.researchIdle"), text);
        if (hud.Research is { Topic: { } topic } research)
        {
            Widgets.Bar(ui, research.Progress, t("hud.researching", "name", t("research." + topic), "percent", Percent(research.Progress)), text);
            if (research.Lack != "off")
                Hint(c, research.Lack == "power" ? t("hud.labNoPower")
                    : research.Lack == "credits" ? t("hud.labNoCredits")
                    : research.Lack is { } lack && lack != "idle" ? t("hud.labLacks", "name", t("goods." + lack))
                    : t("hud.labSpeed", "speed", Commands.N(research.Speed)), text, research.Lack is not null ? Warn : Soft);
        }
        if (hud.Site is { } site)
            Widgets.Bar(ui, site.Progress, site.Demolish
                ? site.Progress < 1 ? t("hud.demolishing", "percent", Percent(site.Progress)) : t("hud.waitsToDemolish")
                : site.Started ? t("hud.building", "percent", Percent(site.Progress)) : t("hud.waitsBuilder"), text);
        if (hud.Conversion is { Progress: { } converting } conversion)
            Widgets.Bar(ui, converting, conversion.Kind == "deploy" ? t("hud.deploying") : t("hud.packing"), text);
        if (hud.Production is { } production)
        {
            if (production.Queue.Count > 0)
            {
                var first = production.Queue[0];
                Widgets.Bar(ui, production.Progress, f.C.ProtoPath(first) is { } path ? t("units." + path[(path.IndexOf('/') + 1)..]) : "?", text);
                ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                {
                    for (var i = 0; i < production.Queue.Count; i++)
                    {
                        var proto = production.Queue[i];
                        var id = "queue:" + i;
                        ui.Box(new Style { Id = id, Width = 32, Height = 32, Skin = Skin.Well, Padding = 4 }, () => ui.Image(proto, Queued, Queued));
                        if (c.ProtoPath(proto) is { } unitPath)
                            ui.Tip(id, t("units." + unitPath[(unitPath.IndexOf('/') + 1)..]));
                    }
                });
            }
            if (!production.IsBuilding && production.Queue.Count > 0)
                Hint(c, t("hud.packedQueue"), text);
            if (production.ArmyLimit is { } limit)
                Hint(c, t("hud.army", "used", Commands.N(limit.Used), "limit", Commands.N(limit.Limit)), text);
            if (production.IsBuilding)
                Hint(c, production.Rally ? t("hud.rallySet") : t("hud.rallyTip"), text);
        }
        if (hud.Construction is { Placing: null, Paving: null })
            Hint(c, t("hud.builderTip"), text);
        if (hud.Conversion is { Kind: "deploy", Progress: null, Choosing: false } mcv)
            Hint(c, mcv.Traveling ? t("hud.deployTravel") : t("hud.mcvTip"), text);
    }

    /// <summary>
    /// Строка словаря со значками (rich): подстановки-кусочки помечены в тексте символами \u0001, \u0002…; на их место
    /// встают куски parts по порядку меток.
    /// </summary>
    private static List<Run> Rich(string template, params List<Run>[] parts)
    {
        var runs = new List<Run>();
        var text = "";
        foreach (var ch in template)
        {
            if (ch is >= '\u0001' and <= '\u0009' && ch - 1 < parts.Length)
            {
                if (text.Length > 0)
                    runs.Add(new Run(text));
                text = "";
                runs.AddRange(parts[ch - 1]);
                continue;
            }
            text += ch;
        }
        if (text.Length > 0)
            runs.Add(new Run(text));
        return runs;
    }

    /// <summary>«Груз: … из N.» со значками (hud.cargo).</summary>
    private static IEnumerable<Run> CargoRuns(Tr t, List<Stack> items, double capacity)
    {
        var template = t("hud.cargo", "items", "\u0001", "capacity", Commands.N(capacity));
        var at = template.IndexOf('\u0001');
        if (at < 0)
            return [new Run(template)];
        List<Run> runs = [new Run(template[..at])];
        if (items.Count == 0)
            runs.Add(new Run(t("common.empty") + " "));
        runs.AddRange(items.Select(stack => G(stack.Resource, stack.Amount)));
        runs.Add(new Run(template[(at + 1)..]));
        return runs;
    }

    /// <summary>Чем заняты выбранные грузовики — словами, с названиями зданий (jobText).</summary>
    private static string JobText(TruckJob job, Tr t)
    {
        var names = job.Buildings.Select(type => t("buildings." + type)).ToList();
        var good = job.Resource is { } resource ? t("goods." + resource) : "";
        return job.Kind switch
        {
            "carry" => names.Count > 1
                ? t("hud.job.carry", "name", good, "from", names[0], "to", names[1], "left", Commands.N(job.Left))
                : t("hud.job.unload", "name", good, "to", names.FirstOrDefault() ?? "", "left", Commands.N(job.Left)),
            "route" => t("hud.job.route", "stops", string.Join(" → ", names)),
            "serve" => t("hud.job.serve", "list", string.Join(", ", names)),
            "supply" => t("hud.job.supply", "name", names.FirstOrDefault() ?? ""),
            _ => t("hud.job." + job.Kind),
        };
    }

    /// <summary>Ничего не выбрано: подсказка по управлению, по предложению в строке.</summary>
    private static void Nothing(Ui ui, Tr t, float width, float height) =>
        ui.Box(new Style { Width = width, Height = height, Skin = Skin.Well, Padding = (10, 12), Direction = Direction.Column, Justify = Align.Center, Gap = 4, Clip = true }, () =>
        {
            foreach (var line in System.Text.RegularExpressions.Regex.Split(t("hud.nothingSelected"), @"(?<=\.)\s+"))
                ui.Text(line, new TextStyle { Color = Muted, Wrap = width - 24 });
        });

    /// <summary>Плитка отряда: щелчок — оставить в выделении только этот вид, Shift — убрать его (narrow).</summary>
    private void Narrow(ClientContext c, string type, bool remove)
    {
        var proto = c.ProtoId("units/" + type);
        var selection = Shared.Selection(c);
        c.Shared[Shared.SelectionKey] = selection.Where(entity => (c.Proto(entity) == proto) != remove).ToArray();
    }

    /// <summary>Камера — к середине выделенного (lookAtSelection).</summary>
    private void LookAt(ClientContext c, uint[] selection)
    {
        var points = selection.Select(entity => state.Center(c, entity)).OfType<(double X, double Y)>().ToList();
        if (points.Count == 0)
            return;
        var (x, y) = (points.Average(p => p.X), points.Average(p => p.Y));
        (c.Camera.X, c.Camera.Y) = (x, y);
        CameraShake.Jump(c, x, y);
    }
}
