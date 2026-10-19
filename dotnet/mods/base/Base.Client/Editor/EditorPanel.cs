using System.Globalization;
using Base.Shared.Orders;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Панель редактора справа (editorPanel.ts): игрок (номер и ник, новый игрок, его камера и кредиты), инструменты и их
/// настройки — кисть земли (вид, биом, ярус, кромка, барханы, форма, размер), покрытие, здания и юниты по видам,
/// месторождения с запасом, кисти модов; у выбранного — месторождения (вид, остаток) и сущности (владелец, прочность,
/// снять задания, снести). Внизу — что под указателем и ответ правки. Числа — полями только из цифр с пределом длины.
/// Кнопки сохранения и выхода — у оболочки (Shell/Editor*).
/// </summary>
internal sealed class EditorPanel(EditorModel model)
{
    /// <summary>Ширина панели и её содержимого.</summary>
    public const float Width = 300;
    private const float Inner = Width - 24;

    private static readonly Color Bright = Color.Hex(0xeef6ff), Dim = Color.Hex(0x8fa9c2), Bad = Color.Hex(0xff7a66);

    // Варианты кисти: значение и ключ названия (null — «как есть»).
    private static readonly (int? Value, string Key)[] Terrains =
        [(null, "common.asIs"), (2, "editor.terrains.rock"), (1, "editor.terrains.sand"), (0, "editor.terrains.swamp"), (3, "editor.terrains.mountain")];
    private static readonly (int? Value, string Key)[] Biomes =
        [(null, "common.asIs"), (0, "editor.biomes.erg"), (1, "editor.biomes.saltFlats"), (2, "editor.biomes.redWastes"), (3, "editor.biomes.marsh")];
    private static readonly (int? Value, string Key)[] Dunes =
        [(null, "common.asIs"), (0, "editor.dunes.natural"), (1, "editor.dunes.none"), (2, "editor.dunes.many")];
    private static readonly (bool? Value, string Key)[] Cliffs = [(null, "common.asIs"), (true, "editor.cliff"), (false, "editor.gentle")];
    private static readonly string[] Shapes = ["square", "circle", "spray"];
    private static readonly (string? Value, string Key)[] Pavings = [("road", "editor.pavings.road"), ("foundation", "editor.pavings.foundation"), (null, "editor.pavings.none")];

    private BaseState State => model.State;

    public void Draw(ClientContext c)
    {
        var ui = c.Ui;
        var tool = model.Tool;
        ui.Box(new Style
        {
            Id = "editor", Anchor = new Anchor(Right: 8, Top: 8, Bottom: 8), Width = MathF.Min(Width, ui.Width - 32), Skin = Skin.Panel,
            Padding = 12, Direction = Direction.Column, Gap = 10, Align = Align.Stretch,
        }, () =>
        {
            ui.Text(c.Text("editor.title"), new TextStyle { Font = FontName.Title, Color = Bright });
            ui.Box(new Style { Id = "editor:body", Grow = 1, Scroll = true, Direction = Direction.Column, Gap = 10, Align = Align.Stretch }, () =>
            {
                PlayerBlock(c);
                ToolBlock(c);
                if (tool == "select" && model.Spots.Count > 0)
                    SpotsBlock(c);
                if (tool == "select" && model.Selection.Count > 0)
                    Inspector(c);
                if (tool == "select" && (model.Selection.Count > 0 || model.Spots.Count > 0) && Button(c, "editor:erase", c.Text("editor.eraseSelected"), danger: true))
                    model.EraseSelection(c);
            });
            ui.Box(new Style { MinHeight = 16 }, () => ui.Text(Footer(c), new TextStyle { Color = model.Status.Length > 0 ? Bad : Dim, Wrap = Inner }));
        });
    }

    // ------------------------------------------------------------------ игрок

    private void PlayerBlock(ClientContext c)
    {
        var ui = c.Ui;
        var players = model.Players(c);
        ui.Box(new Style { Direction = Direction.Column, Gap = 6 }, () =>
        {
            ui.Text(c.Text("editor.player"), new TextStyle { Color = Dim });
            var options = new List<(string Key, string Label)> { ("0", "0") };
            options.AddRange(players.Select(entry => (Num(entry.Player), EditorModel.Label(entry))));
            options.Add(("new", "+"));
            var picked = Chips(c, "editor:player", options, model.Player.ToString(CultureInfo.InvariantCulture));
            if (picked == "new")
                model.Player = model.AddPlayer(c);
            else if (picked is not null)
                model.Player = uint.Parse(picked, CultureInfo.InvariantCulture);
            if (model.Player == 0)
                return;
            ui.Box(new Style { Direction = Direction.Row, Gap = 2, Wrap = true }, () =>
            {
                if (Button(c, "editor:camera", c.Text("editor.cameraHere"), small: true))
                    model.Send(c, EditCameraOrder.Name, new EditCameraOrder { Player = model.Player, X = c.Camera.X, Y = c.Camera.Y, Zoom = c.Camera.Zoom });
                if (Button(c, "editor:dropCamera", c.Text("editor.dropCamera"), small: true))
                    model.Send(c, EditCameraOrder.Name, new EditCameraOrder { Player = model.Player, Clear = true });
            });
            // Кредиты видны только свои (чужой счёт до клиента не доходит): у чужого поле пустое, но правится.
            var credits = model.Player == c.Player ? Num(Math.Floor(State.Credits(c))) : "";
            if (NumberField(c, "editor:credits", c.Text("common.credits"), credits, 9) is { } amount)
                model.Send(c, EditCreditsOrder.Name, new EditCreditsOrder { Player = model.Player, Amount = amount });
        });
    }

    // ------------------------------------------------------------------ инструменты

    private void ToolBlock(ClientContext c)
    {
        var ui = c.Ui;
        ui.Box(new Style { Direction = Direction.Column, Gap = 6 }, () =>
        {
            var tools = EditorModel.Tools.Select(id => (id, c.Text("editor.tools." + id))).ToList();
            tools.AddRange(model.Extras.Brushes.Select(brush => ("brush:" + brush.Id, c.Text(brush.Title))));
            if (Chips(c, "editor:tool", tools, model.Tool) is { } next)
            {
                model.Tool = next;
                model.Typed.Clear();
            }
            switch (model.Tool)
            {
                case "paint":
                    PaintOptions(c);
                    break;
                case "paving":
                    if (Choice(c, "editor:paving", c.Text("editor.tools.paving"), Pavings, model.Pave) is { } pave)
                        model.Pave = pave.Value;
                    SizeRow(c);
                    break;
                case "building" or "unit":
                    KindList(c);
                    break;
                case "deposit":
                    DepositOptions(c);
                    break;
                case "select":
                    ui.Text(c.Text("editor.selectNote"), new TextStyle { Color = Dim, Wrap = Inner });
                    break;
                default:
                    if (model.ModBrush is { } brush)
                    {
                        if (brush.Options.Length > 0)
                        {
                            var current = model.Options.GetValueOrDefault(brush.Id) ?? brush.Options[0];
                            if (Chips(c, "editor:option", [.. brush.Options.Select(option => (option, c.Text(option)))], current) is { } option)
                                model.Options[brush.Id] = option;
                        }
                        if (brush.Size)
                            SizeRow(c);
                    }
                    break;
            }
        });
    }

    private void PaintOptions(ClientContext c)
    {
        var brush = model.Brush;
        if (Choice(c, "editor:terrain", c.Text("editor.ground"), Terrains, brush.Terrain) is { } terrain)
            brush.Terrain = terrain.Value;
        if (Choice(c, "editor:biome", c.Text("editor.biome"), Biomes, brush.Biome) is { } biome)
            brush.Biome = biome.Value;
        (int? Value, string Key)[] tiers = [(null, "common.asIs"), (0, "0"), (1, "1"), (2, "2"), (3, "3")];
        if (Choice(c, "editor:tier", c.Text("editor.tier"), tiers, brush.Tier) is { } tier)
            brush.Tier = tier.Value;
        if (Choice(c, "editor:cliff", c.Text("editor.edge"), Cliffs, brush.Cliff) is { } cliff)
            brush.Cliff = cliff.Value;
        if (Choice(c, "editor:dunes", c.Text("editor.dunesOnSand"), Dunes, brush.Dunes) is { } dunes)
            brush.Dunes = dunes.Value;
        c.Ui.Text(c.Text("editor.shape"), new TextStyle { Color = Dim });
        if (Chips(c, "editor:shape", [.. Shapes.Select(shape => (shape, c.Text("editor.shapes." + shape)))], brush.Shape ?? "square") is { } shape)
            brush.Shape = shape;
        SizeRow(c);
        c.Ui.Text(c.Text("editor.paintNote", "tiles", "6") + c.Text("editor.paintNoteLocal"), new TextStyle { Color = Dim, Wrap = Inner });
    }

    /// <summary>Размер кисти: 1…15 через два, кнопками.</summary>
    private void SizeRow(ClientContext c)
    {
        var ui = c.Ui;
        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
        {
            if (Button(c, "editor:size:less", "−", small: true, disabled: model.Size <= 1))
                model.Size = Math.Max(1, model.Size - 2);
            ui.Text(c.Text("editor.brush", "size", Num(model.Size)), new TextStyle { Color = Dim });
            if (Button(c, "editor:size:more", "+", small: true, disabled: model.Size >= 15))
                model.Size = Math.Min(15, model.Size + 2);
        });
    }

    /// <summary>Виды зданий или юнитов в два столбца, список прокручивается сам.</summary>
    private void KindList(ClientContext c)
    {
        var ui = c.Ui;
        var buildings = model.Tool == "building";
        var kinds = buildings ? model.BuildingKinds(c) : model.UnitKinds(c);
        var current = buildings ? model.Building : model.Unit;
        var half = (Inner - 2) / 2;
        ui.Box(new Style { Id = "editor:list", Direction = Direction.Column, Gap = 2, MaxHeight = MathF.Round(ui.Height * 0.4f), Scroll = true }, () =>
        {
            for (var i = 0; i < kinds.Count; i += 2)
                ui.Box(new Style { Direction = Direction.Row, Gap = 2 }, () =>
                {
                    foreach (var kind in kinds.Skip(i).Take(2))
                    {
                        var name = c.Text((buildings ? "buildings." : "units.") + kind);
                        if (Button(c, "editor:item:" + kind, name, small: true, active: kind == current, width: half))
                        {
                            if (buildings)
                                model.Building = kind;
                            else
                                model.Unit = kind;
                        }
                    }
                });
        });
    }

    private void DepositOptions(ClientContext c)
    {
        var kinds = model.DepositKinds(c);
        c.Ui.Text(c.Text("editor.newDeposit"), new TextStyle { Color = Dim });
        if (Chips(c, "editor:kind", [.. kinds.Select(kind => (kind.Kind, c.Text("goods." + kind.Kind)))], model.Deposit) is { } picked)
        {
            model.Deposit = picked;
            model.Reserve = kinds.Find(kind => kind.Kind == picked).Max;
            model.Typed.Remove("editor:reserve");
        }
        if (NumberField(c, "editor:reserve", c.Text("editor.reserve"), Num(model.Reserve), 6) is { } reserve)
            model.Reserve = reserve;
        c.Ui.Text(c.Text("editor.depositNote"), new TextStyle { Color = Dim, Wrap = Inner });
    }

    // ------------------------------------------------------------------ выбранное

    private void SpotsBlock(ClientContext c)
    {
        var kinds = model.DepositKinds(c);
        var spots = model.Spots.Select(spot => model.DepositUnder(c, spot.X + 0.5, spot.Y + 0.5)).OfType<(int X, int Y, int Kind, double Reserve)>().ToList();
        if (spots.Count == 0)
            return;
        string KindName(int kind) => (uint)kind < (uint)kinds.Count ? kinds[kind].Kind : "metal";
        c.Ui.Box(new Style { Direction = Direction.Column, Gap = 6 }, () =>
        {
            c.Ui.Text(spots.Count == 1 ? $"{c.Text("goods." + KindName(spots[0].Kind))} · {spots[0].X}, {spots[0].Y}" : c.Text("editor.deposits", "count", Num(spots.Count)), new TextStyle { Color = Dim });
            var same = spots.All(spot => spot.Kind == spots[0].Kind) ? KindName(spots[0].Kind) : null;
            c.Ui.Text(c.Text("editor.kind"), new TextStyle { Color = Dim });
            if (Chips(c, "editor:spotKind", [.. kinds.Select(kind => (kind.Kind, c.Text("goods." + kind.Kind)))], same) is { } kind)
                model.Send(c, EditSetDepositOrder.Name, new EditSetDepositOrder { Spots = EditorModel.Flat(model.Spots), Kind = kind });
            if (NumberField(c, "editor:left", c.Text(spots.Count > 1 ? "editor.leftEach" : "editor.left"), Num(Math.Floor(spots[0].Reserve)), 6) is { } left)
                model.Send(c, EditSetDepositOrder.Name, new EditSetDepositOrder { Spots = EditorModel.Flat(model.Spots), Left = left });
        });
    }

    /// <summary>Выбранное: владелец, прочность, задания. Если выбрано несколько — правится каждое.</summary>
    private void Inspector(ClientContext c)
    {
        var ui = c.Ui;
        var entities = model.Selection.ToArray();
        var first = entities[0];
        var kind = State.KindOf(c, first);
        var name = kind is null ? "—" : c.Text(kind.Path.Replace('/', '.'));
        var owner = c.Get(first, State.P.Owner) is { } values ? (uint)values.Number(State.P.Owner["player"]) : 0;
        var health = c.Component("Health");
        var units = entities.Where(entity => c.Has(entity, State.P.Unit)).ToArray();
        ui.Box(new Style { Direction = Direction.Column, Gap = 6 }, () =>
        {
            ui.Text(entities.Length > 1 ? name + c.Text("editor.andMore", "count", Num(entities.Length - 1)) : name, new TextStyle { Color = Bright });
            ui.Text(c.Text("editor.owner"), new TextStyle { Color = Dim });
            var options = new List<(string, string)> { ("0", "0") };
            options.AddRange(model.Players(c).Select(entry => (Num(entry.Player), EditorModel.Label(entry))));
            if (Chips(c, "editor:owner", options, Num(owner)) is { } picked)
                model.Send(c, EditOwnerOrder.Name, new EditOwnerOrder { Entities = entities, Player = uint.Parse(picked, CultureInfo.InvariantCulture) });
            if (c.Get(first, health) is { } life && life.Number(health["max"]) > 0)
            {
                var share = (int)Math.Round(life.Number(health["value"]) / life.Number(health["max"]) * 100);
                if (NumberField(c, "editor:health", c.Text("editor.health", "percent", Num(share)), Num(share), 3) is { } next && next is >= 1 and <= 100)
                    model.Send(c, EditHealthOrder.Name, new EditHealthOrder { Entities = entities, Share = next / 100 });
            }
            if (units.Length > 0 && Button(c, "editor:clear", c.Text("editor.clearTasks"), small: true))
                model.Send(c, EditClearTasksOrder.Name, new EditClearTasksOrder { Entities = entities });
        });
    }

    // ------------------------------------------------------------------ подвал

    /// <summary>Что под указателем: тайл, земля, биом, подножие; и ответ правки.</summary>
    private string Footer(ClientContext c)
    {
        var where = "";
        if (model.Hover is { } hover && c.Terrain(hover.X + 0.5, hover.Y + 0.5, out var tile))
        {
            var terrain = Terrains.FirstOrDefault(option => option.Value == tile.Terrain).Key;
            var biome = Biomes.FirstOrDefault(option => option.Value == tile.Biome).Key;
            where = $"{hover.X}, {hover.Y} · {(terrain is null ? "—" : c.Text(terrain))} · {(biome is null ? "—" : c.Text(biome))}{(tile.Foot ? " · " + c.Text("editor.foot") : "")}";
        }
        return model.Status.Length == 0 ? where : where.Length == 0 ? model.Status : $"{where} · {model.Status}";
    }

    // ------------------------------------------------------------------ виджеты

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Кнопка с подписью; true — нажата в этом кадре.</summary>
    private static bool Button(ClientContext c, string id, string label, bool small = false, bool active = false, bool disabled = false, bool danger = false, float? width = null)
    {
        var style = Style.Button with { Padding = small ? (2, 6) : (4, 10), Height = small ? 24 : null, Width = width };
        if (danger)
            style = style with { Skin = Skin.Danger };
        return c.Ui.Button(id, style, () => c.Ui.Text(label, new TextStyle { Font = small ? FontName.Small : FontName.Body, Ellipsis = width is not null }), active, disabled) is not null;
    }

    /// <summary>Варианты в ряд с переносом (chips): номер узла — «id:ключ»; выбранный — нажатым. Возвращает новый выбор.</summary>
    private static string? Chips(ClientContext c, string id, IReadOnlyList<(string Key, string Label)> options, string? current)
    {
        string? picked = null;
        c.Ui.Box(new Style { Direction = Direction.Row, Gap = 2, Wrap = true, MaxWidth = Inner }, () =>
        {
            foreach (var (key, label) in options)
                if (Button(c, $"{id}:{key}", label, small: true, active: key == current) && key != current)
                    picked = key;
        });
        return picked;
    }

    /// <summary>Строка выбора с подписью сверху; ключ варианта null — «как есть» (узел «id:-»).</summary>
    private static (T Value, bool _)? Choice<T>(ClientContext c, string id, string title, IReadOnlyList<(T Value, string Key)> options, T current)
    {
        c.Ui.Text(title, new TextStyle { Color = Dim });
        var keys = options.Select(option => Key(option.Value)).ToList();
        var picked = Chips(c, id, [.. options.Select((option, i) => (keys[i], c.Text(option.Key)))], Key(current));
        return picked is null ? null : (options[keys.IndexOf(picked)].Value, true);
    }

    private static string Key<T>(T value) => value switch
    {
        null => "-",
        bool flag => flag ? "yes" : "no",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    /// <summary>
    /// Поле числа (FieldWith: только цифры, предел длины): подпись слева, поле справа. Пока его правят, показывается
    /// набранное; набранное число возвращается в кадре правки.
    /// </summary>
    private double? NumberField(ClientContext c, string id, string label, string value, int maxLength)
    {
        var ui = c.Ui;
        double? result = null;
        if (ui.Edited(id) is { } edited)
        {
            model.Typed[id] = edited;
            if (double.TryParse(edited, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                result = number;
        }
        ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Justify = Align.Between, Width = Inner }, () =>
        {
            ui.Text(label);
            ui.Field(id, model.Typed.GetValueOrDefault(id) ?? value, new FieldOptions { Digits = true, MaxLength = maxLength }, new Style { Width = 90 });
        });
        return result;
    }
}
