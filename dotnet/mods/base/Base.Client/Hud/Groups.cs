using Kharos.Mod.Sdk.Client;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Панель групп над нижней панелью — перенос groups из gui/hud.ts и groupsOf из game/hud.ts: десять ячеек по цифрам 1–9
/// и 0. В занятой — портрет того, кого в группе больше всех, и сколько в ней. Нажатие на занятую выбирает группу (Shift —
/// добавить), на пустую — кладёт туда выделенных; правая кнопка или долгое нажатие кладёт выделенных и в занятую, а по
/// выбранной сейчас группе — очищает ячейку. Сами группы ведёт выбор (Controls/SelectControl.cs): нажатие уходит ему
/// через <see cref="Modes.GroupPressKey"/> в этом же кадре (панели рисуются раньше управления).
/// </summary>
internal static class Groups
{
    /// <summary>Сторона ячейки (GROUP_SLOT) и портрета (TILE).</summary>
    private const float Slot = 44, Tile = 32;

    /// <summary>Группа для панели: сколько живых своих, чей портрет, выбрана ли целиком.</summary>
    private readonly record struct Row(int Count, int Proto, bool Selected);

    public static void Draw(ClientContext c, BaseState state, float bottom, Tr t)
    {
        var ui = c.Ui;
        var selection = Shared.Selection(c);
        var groupable = selection.Any(c.Mine);
        var groups = c.Shared.TryGetValue(Modes.GroupsKey, out var value) && value is Dictionary<char, uint[]> map ? map : null;
        var side = ui.Width <= 520 ? MathF.Floor((ui.Width - 24 - 18) / 10) : Slot;
        var picture = MathF.Min(Tile, side - 8);
        // Низкий экран: ряд, который не влезает в поле между полосой и панелью, не рисуется — он ушёл бы под полосу.
        if (side + 8 > Bar.FieldRoom(ui.Width, ui.Height, TopBar.HeightOf(ui.Width)))
            return;
        // Ячейки — на панели, как у страницы: у неё любая панель HUD была в рамке скина с границей в 4.
        ui.Box(new Style { Id = "groups", Anchor = new Anchor(CenterX: 0, Bottom: bottom + 8), Skin = Skin.Panel, Padding = 4, Direction = Direction.Row, Gap = 2 }, () =>
        {
            foreach (var digit in SelectMarks.GroupOrder)
            {
                var row = groups is not null && groups.TryGetValue(digit, out var members) ? RowOf(c, state, members, selection) : null;
                var open = row is null && groupable;
                var id = $"group:{digit}";
                var style = new Style
                {
                    Width = side, Height = side, Padding = 0, Direction = Direction.Stack,
                    Skin = open ? Skin.ButtonHover : Skin.Button, Alpha = row is not null ? null : open ? 0.85f : 0.5f,
                };
                var click = ui.Button(id, style, () =>
                {
                    if (row is { Proto: >= 0 } shown)
                        ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => ui.Image(shown.Proto, picture, picture));
                    // Цифра и счётчик — внутри рамки в 4 пикселя, как у кнопки страницы.
                    ui.Box(new Style { Self = (Align.Start, Align.Start), Padding = (5, 0, 0, 6) }, () => ui.Text(digit.ToString(), new TextStyle { Color = Gold, Shadow = Ink }));
                    if (row is { } counted)
                        ui.Box(new Style { Self = (Align.End, Align.End), Padding = (0, 6, 5, 0) }, () => ui.Text(counted.Count.ToString(), new TextStyle { Color = Bright, Shadow = Ink }));
                }, active: row is { Selected: true });
                ui.Tip(id, t(row is not null ? "hud.groupTip" : groupable ? "hud.groupPutTip" : "hud.groupEmptyTip", "digit", digit.ToString()));
                if (click is not { } press)
                    continue;
                if (press.Button == 2 || press.Long)
                    c.Shared[Modes.GroupPressKey] = new GroupPress(digit, row is { Selected: true } ? GroupAction.Clear : GroupAction.Assign, false);
                else if (row is not null)
                    c.Shared[Modes.GroupPressKey] = new GroupPress(digit, GroupAction.Select, press.Shift);
                else if (groupable)
                    c.Shared[Modes.GroupPressKey] = new GroupPress(digit, GroupAction.Assign, false);
            }
        });
    }

    /// <summary>Живые свои юниты, здания и площадки группы и кого в ней больше всех (юниты важнее зданий); пусто — null.</summary>
    private static Row? RowOf(ClientContext c, BaseState state, uint[] members, uint[] selection)
    {
        var units = new Dictionary<int, int>();
        var buildings = new Dictionary<int, int>();
        var count = 0;
        foreach (var entity in members)
        {
            if (!c.Alive(entity) || !c.Mine(entity))
                continue;
            var unit = c.Has(entity, state.P.Unit);
            if (!unit && !c.Has(entity, state.P.Footprint))
                continue;
            count++;
            var counts = unit ? units : buildings;
            var proto = c.Proto(entity);
            counts[proto] = counts.GetValueOrDefault(proto) + 1;
        }
        if (count == 0)
            return null;
        static int Most(Dictionary<int, int> counts) => counts.Count == 0 ? -1 : counts.OrderByDescending(pair => pair.Value).First().Key;
        var selected = count == selection.Length && members.Where(entity => c.Alive(entity) && c.Mine(entity)).All(selection.Contains);
        return new Row(count, units.Count > 0 ? Most(units) : Most(buildings), selected);
    }
}

/// <summary>Что сделать с группой по нажатию ячейки панели.</summary>
internal enum GroupAction { Select, Assign, Clear }

/// <summary>Нажатие ячейки группы: цифра, действие, Shift (добавить к выбранным).</summary>
internal readonly record struct GroupPress(char Digit, GroupAction Action, bool Shift);
