using Base.Shared.Orders;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Окно исследований поверх игры (gui/tech.ts): сверху очередь, ниже все темы плитками — сначала то, что можно вести
/// сейчас (зелёные), потом то, что станет можно (жёлтые), затем закрытые (красные) и исследованные. Щелчок ставит тему
/// в конец очереди вместе с недостающими предшественниками (хост достраивает путь по pathTo), Shift — в начало; щелчок
/// по теме в очереди и правый щелчок по плитке убирают её. Наведение показывает карточку: что тема даёт и что от неё зависит.
/// </summary>
internal sealed class TechWindow
{
    /// <summary>Стороны значков: на плитке и в строке.</summary>
    private const float Icon = 48, Small = 24;

    /// <summary>Плитка не уже этого; промежуток; ширина карточки.</summary>
    private const float TileMin = 120, Gap = 6, CardWidth = 330;

    private static readonly Color Bright = Color.Hex(0xeef6ff), Dim = Color.Hex(0x8fa9c2), Soft = Color.Hex(0xb4c9dd), Muted = Color.Hex(0x6184a3),
        Gold = Color.Hex(0xf0c95a), Good = Color.Hex(0x8fe0a8), Warn = Color.Hex(0xffb070), Bad = Color.Hex(0xff7a66), Power = Color.Hex(0x8fd0ff),
        Steel = Color.Hex(0x41617f), Team = Color.Hex(0x5aa9ff), TeamDark = Color.Hex(0x10358f), Well = Color.Hex(0x070d15), Ink = Color.Hex(0x0b111b);

    private static readonly Dictionary<TopicStatus, (Color Line, Color Back, Color Text)> Colors = new()
    {
        [TopicStatus.Available] = (Good, new Color(143, 224, 168, 51), Good),
        [TopicStatus.Soon] = (Gold, new Color(240, 201, 90, 51), Gold),
        [TopicStatus.Locked] = (Bad, new Color(255, 122, 102, 56), Bad),
        [TopicStatus.Done] = (Steel, new Color(97, 132, 163, 46), Dim),
    };

    /// <summary>Руда, которую находит открытие месторождения: её значок и рисуется на теме.</summary>
    private static readonly Dictionary<string, string> Found = new()
    {
        ["metal"] = "metalOre", ["silicon"] = "siliconOre", ["fuel"] = "fuelOre", ["kharite"] = "khariteOre", ["lithium"] = "lithiumOre", ["uranium"] = "uraniumOre",
    };

    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    private static TextStyle Shadowed(Color? color = null) => new() { Color = color ?? TextStyle.DefaultColor, Shadow = Ink, Lift = 1 };

    /// <summary>Строка словаря; запросы к движку — раз на ключ и язык.</summary>
    private string T(ClientContext c, string key, params string[] args)
    {
        var id = c.Lang + "|" + key + "|" + string.Join('|', args);
        if (!_texts.TryGetValue(id, out var text))
            _texts[id] = text = c.Text(key, args);
        return text;
    }

    /// <summary>Значок темы: модернизация — здание со стрелками уровня, находка — руда, научный метод — лаборатория, иначе — первое, что тема открывает.</summary>
    private static int IconOf(ClientContext c, TopicView view)
    {
        if (view.Topic is "levels2" or "levels3")
            return c.LookId("portraits/level/" + (view.Topic == "levels2" ? 2 : 3));
        if (view.Trigger is not null && Found.TryGetValue(view.Trigger, out var ore))
            return c.LookId("goods/" + ore);
        if (view.Trigger == "lab")
            return c.ProtoId("buildings/lab");
        if (view.Paves.Length > 0)
            return c.LookId("portraits/pave/" + view.Paves[0]);
        if (view.Buildings.Length > 0)
            return c.ProtoId("buildings/" + view.Buildings[0]);
        if (view.Units.Length > 0)
            return c.ProtoId("units/" + view.Units[0]);
        return c.LookId("portraits/level/1");
    }

    private static void Picture(Ui ui, int look, float size)
    {
        if (look >= 0)
            ui.Image(look, size, size);
        else
            ui.Space(size, size);
    }

    private string LackName(ClientContext c, string lack) => lack switch
    {
        "credits" => T(c, "tech.lackCredits"),
        "off" => T(c, "tech.lackOff"),
        "power" or "idle" => T(c, "tech.lackPower"),
        _ => T(c, "goods." + lack),
    };

    /// <summary>Груз и его количество в строке (res).</summary>
    private static void Resource(ClientContext c, Ui ui, string good, double? amount = null, Color? color = null)
    {
        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 3 }, () =>
        {
            Picture(ui, c.LookId("goods/" + good), 15);
            if (amount is { } count)
                ui.Text(Lua.ToString(count), new TextStyle { Color = color ?? TextStyle.DefaultColor });
        });
    }

    private static void Coin(Ui ui, double cost, Color color) =>
        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 3 }, () =>
        {
            ui.Coin(16);
            ui.Text(Lua.ToString(cost), new TextStyle { Color = color });
        });

    /// <summary>Строка темы: значок и название цвета её доступности.</summary>
    private void Line(ClientContext c, TopicView view)
    {
        var ui = c.Ui;
        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
        {
            Picture(ui, IconOf(c, view), Small);
            ui.Text(T(c, "research." + view.Topic), new TextStyle { Color = Colors[view.Status].Text });
        });
    }

    /// <summary>Подсказка темы: что она даёт и открывает, цена или условие, что нужно раньше и что откроется дальше.</summary>
    private void Card(ClientContext c, TopicView view, Dictionary<string, TopicView> byTopic, Dictionary<string, List<string>> children)
    {
        var ui = c.Ui;
        var inner = CardWidth - 20;
        ui.Box(new Style { Direction = Direction.Column, Gap = 6, Width = inner }, () =>
        {
            ui.Text(T(c, "research." + view.Topic), new TextStyle { Font = FontName.Title, Color = Bright, Wrap = inner });
            ui.Text(view.Trigger is not null && view.Status != TopicStatus.Done ? T(c, "tech.triggerTopic") : T(c, "tech.status." + StatusKey(view.Status)),
                new TextStyle { Color = Colors[view.Status].Text });
            ui.Text(T(c, "researchInfo." + view.Topic), new TextStyle { Wrap = inner });
            foreach (var type in view.Buildings)
                Opens(ui, c.ProtoId("buildings/" + type), T(c, "buildings." + type));
            foreach (var type in view.Units)
                Opens(ui, c.ProtoId("units/" + type), T(c, "units." + type));
            foreach (var kind in view.Paves)
                Opens(ui, c.LookId("portraits/pave/" + kind), T(c, "grid.tools." + kind));
            if (view.Trigger is not null)
            {
                if (view.Status != TopicStatus.Done)
                    ui.Text(T(c, "tech.triggers." + view.Trigger), new TextStyle { Color = Power, Wrap = inner });
            }
            else if (view.Status != TopicStatus.Done)
            {
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
                {
                    Coin(ui, view.Cost, Gold);
                    foreach (var (good, amount) in view.Materials)
                        Resource(c, ui, good, amount, Soft);
                    ui.Space(0, 0, 1);
                    ui.Text(T(c, "tech.time", "time", view.Time), new TextStyle { Color = Dim });
                });
            }
            if (view.After.Length > 0)
                ui.Box(new Style { Direction = Direction.Column, Gap = 2 }, () =>
                {
                    ui.Text(T(c, "tech.before"), new TextStyle { Color = Dim });
                    foreach (var before in view.After)
                        if (byTopic.TryGetValue(before, out var parent))
                            Line(c, parent);
                });
            if (children.TryGetValue(view.Topic, out var next) && next.Count > 0)
                ui.Box(new Style { Direction = Direction.Column, Gap = 2 }, () =>
                {
                    ui.Text(T(c, "tech.leadsTo"), new TextStyle { Color = Dim });
                    // Каждая тема — один раз, там, где встретилась впервые; вложенность — отступом.
                    var seen = new HashSet<string> { view.Topic };
                    void Walk(string topic, int depth)
                    {
                        var layer = children.GetValueOrDefault(topic, []).Where(child => !seen.Contains(child)).ToList();
                        seen.UnionWith(layer);
                        foreach (var child in layer)
                        {
                            ui.Box(new Style { Padding = (0, 0, 0, 6 + depth * 14) }, () => Line(c, byTopic[child]));
                            Walk(child, depth + 1);
                        }
                    }
                    Walk(view.Topic, 0);
                });
        });
    }

    private static void Opens(Ui ui, int look, string name) =>
        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
        {
            Picture(ui, look, Small);
            ui.Text(name);
        });

    private static string StatusKey(TopicStatus status) => status switch
    {
        TopicStatus.Done => "done",
        TopicStatus.Available => "available",
        TopicStatus.Soon => "soon",
        _ => "locked",
    };

    /// <summary>Плитка темы: значок на цвете доступности, место в очереди, готовность, название, цена и срок.</summary>
    private void Tile(ClientContext c, TopicView view, float width, Dictionary<string, TopicView> byTopic, Dictionary<string, List<string>> children)
    {
        var ui = c.Ui;
        var id = "tech:" + view.Topic;
        // Тему-открытие и открытую в очередь не ставят.
        var idle = view.Status == TopicStatus.Done || view.Trigger is not null;
        var colors = Colors[view.Status];
        var click = ui.Button(id, new Style
        {
            Width = width, Direction = Direction.Column, Align = Align.Stretch, Justify = Align.Start, Gap = 2, Padding = 6,
            Alpha = view.Status == TopicStatus.Done ? 0.7f : null,
        }, () =>
        {
            ui.Box(new Style { Height = 56, Direction = Direction.Stack, Background = colors.Back }, () =>
            {
                ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => Picture(ui, IconOf(c, view), Icon));
                ui.Box(new Style { Self = (Align.Start, Align.End), Width = width - 12, Height = 3, Background = colors.Line });
                if (view.Order > 0)
                    ui.Box(new Style { Self = (Align.End, Align.Start), Background = TeamDark, Padding = (0, 4) }, () => ui.Text(view.Order.ToString(), new TextStyle { Color = Bright }));
                if (view.Status != TopicStatus.Done && view.Progress > 0)
                    ui.Box(new Style { Self = (Align.Center, Align.End), Padding = (0, 0, 6, 0) }, () =>
                        ui.Box(new Style { Width = width - 20, Height = 4, Background = Well, Direction = Direction.Row }, () =>
                            ui.Box(new Style { Width = (float)Math.Round((width - 20) * view.Progress), Height = 4, Background = Team })));
            });
            ui.Box(new Style { MinHeight = 28, Padding = (0, 2) }, () => ui.Text(T(c, "research." + view.Topic), Shadowed(Bright) with { Wrap = width - 16 }));
            if (view.Status != TopicStatus.Done && view.Trigger is null)
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6, Padding = (0, 2) }, () =>
                {
                    Coin(ui, view.Cost, Gold);
                    ui.Space(0, 0, 1);
                    ui.Text(view.Time, new TextStyle { Color = Dim });
                });
            if (view.Trigger is not null && view.Status != TopicStatus.Done)
                ui.Box(new Style { Padding = (0, 2) }, () => ui.Text(T(c, "tech.triggers." + view.Trigger), new TextStyle { Color = Power, Wrap = width - 16 }));
        }, active: view.Order > 0);
        // Карточка — справа от плитки, а у правого края экрана — слева; в нижней половине — вровень с низом плитки.
        if (ui.RectOf(id) is { } at)
        {
            var toLeft = at.X + at.W + 8 + CardWidth > ui.Width;
            var low = at.Y > ui.Height / 2;
            ui.Tip(id, () => Card(c, view, byTopic, children), new Anchor(
                Left: toLeft ? null : at.X + at.W + 8, Right: toLeft ? ui.Width - at.X + 8 : null,
                Top: low ? null : at.Y, Bottom: low ? MathF.Max(8, ui.Height - at.Y - at.H) : null));
        }
        if (click is not { } clicked)
            return;
        if (clicked.Button == 2)
        {
            if (view.Order > 0)
                c.Send(DequeueOrder.Name, MemoryPackSerializer.Serialize(new DequeueOrder { Topic = view.Topic }));
        }
        else if (!idle)
            c.Send(ResearchOrder.Name, MemoryPackSerializer.Serialize(new ResearchOrder { Topic = view.Topic, Front = clicked.Shift }));
    }

    /// <summary>Окно под верхней полосой, во всю высоту экрана. Возвращает true, если его закрыли.</summary>
    public bool Draw(ClientContext c, TechState tech, float top)
    {
        var ui = c.Ui;
        var byTopic = tech.Topics.ToDictionary(view => view.Topic);
        var children = new Dictionary<string, List<string>>();
        foreach (var view in tech.Topics)
            foreach (var before in view.After)
            {
                if (!children.TryGetValue(before, out var list))
                    children[before] = list = [];
                list.Add(view.Topic);
            }
        // Сначала зелёные, потом жёлтые; внутри цвета — по ярусам. Красные — под своим заголовком, открытое — в конце.
        static List<TopicView> Order(IEnumerable<TopicView> list) => [.. list.OrderBy(view => (int)view.Status).ThenBy(view => view.Tier)];
        var open = Order(tech.Topics.Where(view => view.Status is TopicStatus.Available or TopicStatus.Soon));
        var later = Order(tech.Topics.Where(view => view.Status == TopicStatus.Locked));
        var done = Order(tech.Topics.Where(view => view.Status == TopicStatus.Done));
        var labs = tech.Labs;
        var width = MathF.Min(1180, ui.Width - 16);
        var height = ui.Height - top - 16;
        var inner = width - 40;
        var columns = Math.Max(1, (int)Math.Floor((inner - 4 + Gap) / (TileMin + Gap)));
        var tileWidth = (float)Math.Floor((inner - 4 - Gap * (columns - 1)) / columns);
        var closed = false;

        void Grid(List<TopicView> list) =>
            ui.Box(new Style { Direction = Direction.Column, Gap = Gap }, () =>
            {
                for (var start = 0; start < list.Count; start += columns)
                {
                    var from = start;
                    ui.Box(new Style { Direction = Direction.Row, Gap = Gap, Align = Align.Stretch }, () =>
                    {
                        foreach (var view in list.Skip(from).Take(columns))
                            Tile(c, view, tileWidth, byTopic, children);
                    });
                }
            });

        ui.Box(new Style { Id = "tech", Anchor = new Anchor(CenterX: 0, Top: top + 8), Width = width, Height = height, Skin = Skin.Panel, Padding = (16, 20), Direction = Direction.Column, Align = Align.Stretch, Gap = 8 }, () =>
        {
            ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 16 }, () =>
            {
                ui.Text(T(c, "tech.title"), new TextStyle { Font = FontName.Title, Color = Bright, Shadow = Ink, Lift = 2 });
                ui.Text(labs.Count == 0 ? T(c, "tech.noLabs") : T(c, "tech.labs", "count", labs.Count.ToString(), "working", labs.Working.ToString(), "speed", Lua.ToString(labs.Speed)), new TextStyle { Color = Dim });
                ui.Space(0, 0, 1);
                if (ui.Button("tech:close", Style.Button with { Padding = (2, 8) }, () => ui.Text("✕", Shadowed())) is not null)
                    closed = true;
                ui.Tip("tech:close", T(c, "tech.closeTip"));
            });

            ui.Box(new Style { Id = "tech:queue", Direction = Direction.Row, Align = Align.Center, Gap = 6, MinHeight = 36, Clip = true }, () =>
            {
                ui.Text(T(c, "tech.queue"), new TextStyle { Color = Dim });
                if (tech.Plan.Count == 0)
                    ui.Text(T(c, "tech.queueEmpty"), new TextStyle { Color = Dim });
                for (var i = 0; i < tech.Plan.Count; i++)
                {
                    var topic = tech.Plan[i];
                    if (!byTopic.TryGetValue(topic, out var view))
                        continue;
                    var id = "tech:queued:" + topic;
                    var first = i == 0;
                    var click = ui.Button(id, Style.Button with { Padding = (2, 6, 2, 2), Gap = 4 }, () =>
                    {
                        Picture(ui, IconOf(c, view), Small);
                        ui.Text(T(c, "research." + topic), Shadowed());
                        if (first)
                            ui.Text($"{Math.Round(view.Progress * 100)}%{(tech.Left is { } left ? " · " + left : "")}", new TextStyle { Color = Team, Shadow = Ink, Lift = 1 });
                    }, active: topic == tech.Current);
                    ui.Tip(id, T(c, "tech.dequeueTip"));
                    if (click is not null)
                        c.Send(DequeueOrder.Name, MemoryPackSerializer.Serialize(new DequeueOrder { Topic = topic }));
                }
                if (tech.Plan.Count > 0)
                {
                    ui.Space(0, 0, 1);
                    if (ui.Button("tech:clear", Style.Button with { Padding = (2, 10) }, () => ui.Text(T(c, "tech.cancel"), Shadowed())) is not null)
                        c.Send(CancelResearchOrder.Name, MemoryPackSerializer.Serialize(new CancelResearchOrder()));
                    ui.Tip("tech:clear", T(c, "tech.cancelTip"));
                }
            });
            if (tech.Current is not null && labs.Lacks.Count > 0)
                ui.Box(new Style { Id = "tech:lacks", Direction = Direction.Row, Align = Align.Center, Gap = 12 }, () =>
                {
                    ui.Text(T(c, "tech.lacks"), new TextStyle { Color = Warn });
                    foreach (var (lack, count) in labs.Lacks)
                        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 2 }, () =>
                        {
                            if (lack is not ("credits" or "off" or "power" or "idle"))
                                Resource(c, ui, lack);
                            ui.Text(LackName(c, lack), new TextStyle { Color = Warn });
                            ui.Text(" " + T(c, "tech.labCount", "count", count.ToString()), new TextStyle { Color = Dim });
                        });
                });

            ui.Box(new Style { Id = "tech:list", Grow = 1, Direction = Direction.Column, Gap = 6, Scroll = true, Padding = 2 }, () =>
            {
                Grid(open);
                if (later.Count > 0)
                {
                    ui.Box(new Style { Padding = (6, 0, 0, 0) }, () => ui.Text(T(c, "tech.later"), new TextStyle { Color = Muted }));
                    Grid(later);
                }
                if (done.Count > 0)
                {
                    ui.Box(new Style { Padding = (6, 0, 0, 0) }, () => ui.Text(T(c, "tech.researched"), new TextStyle { Color = Muted }));
                    Grid(done);
                }
            });
            ui.Text(T(c, "tech.hint"), new TextStyle { Color = Muted, Wrap = inner });
        });
        return closed;
    }
}
