using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Выделение — перенос левой кнопки и цифр из controls.ts: щелчок — свой юнит или своё здание под курсором, рамка — свои
/// юниты в ней (нет юнитов — здания), Shift добавляет (по уже выбранному — снимает), двойной щелчок или Ctrl+щелчок — все
/// свои того же вида на экране, Ctrl+цифра запоминает группу, цифра выбирает её (Shift добавляет, повторно за
/// <see cref="DoubleTap"/> — камера летит к группе), Esc бросает начатую рамку, потом снимает выделение. Здания с юнитами
/// вместе не выбираются. Выделенное — <see cref="Shared.SelectionKey"/> (его видят приказы и панели), отметки — SelectMarks.
/// </summary>
internal sealed class SelectControl(BaseState state, PlaceWorld world)
{
    /// <summary>Пока тянут рамку, в общей памяти лежит этот ключ: край экрана тогда двигает камеру.</summary>
    public const string BoxKey = "selection.box";

    /// <summary>Двойной щелчок и повторное нажатие цифры — за столько секунд.</summary>
    private const double DoubleTap = 0.4;

    /// <summary>Если мышь с нажатой кнопкой сдвинулась меньше, это щелчок, а не перетаскивание, в пикселях экрана.</summary>
    private const float ClickSlop = 4;

    /// <summary>Насколько мимо юнита можно щёлкнуть, чтобы всё равно выбрать его, в пикселях экрана.</summary>
    private const float PickMargin = 6;

    private readonly SelectMarks _marks = new(state, world);
    private readonly Dictionary<char, uint[]> _groups = [];
    private (float X, float Y, double Wx, double Wy)? _start;
    private bool _dragged;
    private (uint Entity, double Time)? _lastUnit, _lastBuilding;
    private (char Digit, double Time)? _lastGroup;

    private Parts P => state.P;

    // ------------------------------------------------------------------ кого выбрать

    private bool IsUnit(ClientContext c, uint entity) => c.Has(entity, P.Unit);

    /// <summary>Свои юниты, центр которых попал в прямоугольник в тайлах.</summary>
    private List<uint> UnitsInBox(ClientContext c, double x1, double y1, double x2, double y2)
    {
        double left = Math.Min(x1, x2), top = Math.Min(y1, y2), right = Math.Max(x1, x2), bottom = Math.Max(y1, y2);
        var found = new List<uint>();
        foreach (var entity in c.Within(left, top, right, bottom, P.Unit))
            if (c.Mine(entity))
                found.Add(entity);
        return found;
    }

    /// <summary>Свои здания и площадки, середина которых попала в прямоугольник в тайлах; kind — только этого вида.</summary>
    private List<uint> BuildingsInBox(ClientContext c, double x1, double y1, double x2, double y2, int? proto = null)
    {
        double left = Math.Min(x1, x2), top = Math.Min(y1, y2), right = Math.Max(x1, x2), bottom = Math.Max(y1, y2);
        var found = new List<uint>();
        // Место здания — левый верхний тайл, самое широкое основание — 4 тайла: искать надо с запасом.
        foreach (var entity in c.Within(left - 4, top - 4, right, bottom, P.Footprint))
        {
            if (!c.Mine(entity) || (proto is { } wanted && c.Proto(entity) != wanted) || state.Center(c, entity) is not { } middle)
                continue;
            if (middle.X >= left && middle.X <= right && middle.Y >= top && middle.Y <= bottom)
                found.Add(entity);
        }
        return found;
    }

    /// <summary>Кого выбирает рамка: юниты в ней главнее, здания — только если юнитов нет.</summary>
    private List<uint> BoxHits(ClientContext c, double x1, double y1, double x2, double y2)
    {
        var units = UnitsInBox(c, x1, y1, x2, y2);
        return units.Count > 0 ? units : BuildingsInBox(c, x1, y1, x2, y2);
    }

    /// <summary>Свой юнит под точкой в тайлах: ближайший из тех, в чей круг (радиус и запас в пикселях) она попала.</summary>
    private uint? UnitAt(ClientContext c, double x, double y)
    {
        var margin = PickMargin / c.Camera.Zoom;
        uint? best = null;
        var bestDistance = double.MaxValue;
        foreach (var entity in c.Within(x - 3, y - 3, x + 3, y + 3, P.Unit))
        {
            if (!c.Mine(entity) || c.Pose(entity) is not { } at)
                continue;
            var distance = Math.Sqrt((at.X - x) * (at.X - x) + (at.Y - y) * (at.Y - y));
            if (distance > state.Radius(c, entity) + margin || distance >= bestDistance)
                continue;
            best = entity;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>Своё здание или площадка на тайле под точкой; нет — null.</summary>
    private uint? OwnBuildingAt(ClientContext c, double x, double y)
    {
        int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y);
        var entity = Tools.Occupant(tx, ty);
        if (entity == 0)
            entity = world.SiteAt(c, tx, ty, 1, 1);
        return entity != 0 && c.Mine(entity) ? entity : null;
    }

    /// <summary>Выбирает list; add — добавить к выбранным. Здания с юнитами вместе не выбираются.</summary>
    private void Select(ClientContext c, List<uint> selection, IReadOnlyList<uint> list, bool add)
    {
        if (!add)
            selection.Clear();
        var buildings = list.Count > 0 && !IsUnit(c, list[0]);
        selection.RemoveAll(entity => IsUnit(c, entity) == buildings);
        foreach (var entity in list)
            if (!selection.Contains(entity))
                selection.Add(entity);
    }

    // ------------------------------------------------------------------ группы

    /// <summary>Живые свои юниты и здания группы; погибших и чужих (их номер мог достаться новой сущности) группа забывает.</summary>
    private uint[] GroupOf(ClientContext c, char digit)
    {
        if (!_groups.TryGetValue(digit, out var members))
            return [];
        var alive = members.Where(entity => c.Alive(entity) && c.Mine(entity) && (IsUnit(c, entity) || c.Has(entity, P.Footprint))).ToArray();
        if (alive.Length > 0)
            _groups[digit] = alive;
        else
            _groups.Remove(digit);
        return alive;
    }

    private void SelectGroup(ClientContext c, List<uint> selection, char digit, bool add)
    {
        var group = GroupOf(c, digit);
        if (group.Length == 0)
            return;
        Select(c, selection, group, add);
        // Повторный выбор той же группы подряд наводит на неё камеру.
        if (_lastGroup is { } last && last.Digit == digit && c.Time - last.Time < DoubleTap)
            LookAtSelection(c, selection);
        _lastGroup = (digit, c.Time);
    }

    private void AssignGroup(ClientContext c, char digit, IEnumerable<uint> selection)
    {
        var own = selection.Where(c.Mine).ToArray();
        if (own.Length > 0)
            _groups[digit] = own;
        else
            _groups.Remove(digit);
    }

    /// <summary>Камера летит к середине выделенного.</summary>
    private static void LookAtSelectionOf(ClientContext c, IEnumerable<uint> selection)
    {
        double x = 0, y = 0;
        var count = 0;
        foreach (var entity in selection)
            if (c.Pose(entity) is { } at)
            {
                x += at.X;
                y += at.Y;
                count++;
            }
        if (count > 0)
            CameraShake.FlyTo(c, x / count, y / count);
    }

    private static void LookAtSelection(ClientContext c, List<uint> selection) => LookAtSelectionOf(c, selection);

    // ------------------------------------------------------------------ кадр

    /// <summary>Бросает начатое мышью: рамку выделения.</summary>
    private bool CancelDrag(ClientContext c)
    {
        var had = _start is not null;
        _start = null;
        _dragged = false;
        c.Shared.Remove(BoxKey);
        return had;
    }

    public void Update(ClientContext c, bool listen = true)
    {
        var input = c.Input;
        // Погибшие и исчезнувшие выпадают из выделения.
        var selection = Shared.Selection(c).Where(c.Alive).ToList();
        List<uint> hits = [];
        if (!listen)
            CancelDrag(c);

        if (listen && input.Pressed(PointerButton.Left) && input.Inside && !input.OverUi && !input.Alt)
        {
            _start = (input.X, input.Y, input.WorldX, input.WorldY);
            _dragged = false;
        }
        if (_start is { } start)
        {
            if (Math.Sqrt((input.X - start.X) * (input.X - start.X) + (input.Y - start.Y) * (input.Y - start.Y)) > ClickSlop)
                _dragged = true;
            if (input.Down(PointerButton.Left) && _dragged)
            {
                c.Shared[BoxKey] = true;
                SelectMarks.Box(c, start.Wx, start.Wy, input.WorldX, input.WorldY);
                // Кого выберет рамка, если отпустить её сейчас, — бледнее выбранных.
                hits = BoxHits(c, start.Wx, start.Wy, input.WorldX, input.WorldY);
            }
            if (input.Released(PointerButton.Left))
            {
                if (c.Shared.Remove(Shared.SwallowKey))
                {
                    // Долгое касание стало приказом (OrderControl): отпускание не выделяет.
                }
                else if (_dragged)
                    Select(c, selection, BoxHits(c, start.Wx, start.Wy, input.WorldX, input.WorldY), input.Shift);
                else
                    Click(c, selection, input.WorldX, input.WorldY, input.Shift, input.Ctrl);
                CancelDrag(c);
            }
            else if (!input.Down(PointerButton.Left))
                CancelDrag(c);
        }

        // Цифры: Ctrl запоминает группу, без Ctrl — выбирает; Shift добавляет к выбранным.
        foreach (var key in listen ? (IEnumerable<string>)input.Hits : [])
            if (key.Length == 1 && char.IsAsciiDigit(key[0]))
            {
                if (input.Ctrl)
                    AssignGroup(c, key[0], selection);
                else
                    SelectGroup(c, selection, key[0], input.Shift);
            }

        // Ячейка панели групп (Hud/Groups.cs).
        if (c.Shared.Remove(Modes.GroupPressKey, out var pressed) && pressed is GroupPress press)
            switch (press.Action)
            {
                case GroupAction.Select:
                    SelectGroup(c, selection, press.Digit, press.Shift);
                    break;
                case GroupAction.Assign:
                    AssignGroup(c, press.Digit, selection);
                    break;
                case GroupAction.Clear:
                    _groups.Remove(press.Digit);
                    break;
            }

        // Esc: сначала брошенная протяжка, потом выделение.
        if (listen && input.Hits.Contains("escape") && !CancelDrag(c))
            selection.Clear();

        var result = selection.ToArray();
        c.Shared[Shared.SelectionKey] = result;
        c.Shared[Modes.GroupsKey] = new Dictionary<char, uint[]>(_groups);

        // Отметки: пути и цели — под кольцами, кольца — поверх.
        _marks.Bars(c);
        _marks.Routes(c, result);
        _marks.Groups(c, _groups, result);
        foreach (var entity in hits)
            if (!selection.Contains(entity))
                _marks.Mark(c, entity, SelectMarks.BoxHitAlpha);
        foreach (var entity in result)
            _marks.Mark(c, entity, 1);
    }

    /// <summary>Щелчок без протяжки: свой юнит, а если его нет — своё здание; пустое место снимает выделение.</summary>
    private void Click(ClientContext c, List<uint> selection, double x, double y, bool shift, bool ctrl)
    {
        var now = c.Time;
        var unit = UnitAt(c, x, y);
        var twice = unit is { } u && _lastUnit is { } last && last.Entity == u && now - last.Time < DoubleTap;
        _lastUnit = unit is { } clicked ? (clicked, now) : null;
        if (unit is { } one)
        {
            if (twice || ctrl)
            {
                // Двойной щелчок или Ctrl — все свои юниты этого вида на экране.
                var (x1, y1) = c.ToWorld(0, 0);
                var (x2, y2) = c.ToWorld(c.ScreenWidth, c.ScreenHeight);
                var kind = c.Proto(one);
                Select(c, selection, UnitsInBox(c, x1, y1, x2, y2).Where(other => c.Proto(other) == kind).ToList(), shift);
            }
            else if (shift && selection.Contains(one))
                selection.Remove(one);
            else
                Select(c, selection, [one], shift);
            return;
        }
        // Не юнит — тогда, может быть, своё здание или площадка: с Shift — добавить или снять, двойной щелчок или Ctrl —
        // все свои здания этого вида на экране.
        var building = OwnBuildingAt(c, x, y);
        var builtTwice = building is { } b && _lastBuilding is { } before && before.Entity == b && now - before.Time < DoubleTap;
        _lastBuilding = building is { } own ? (own, now) : null;
        if (building is not { } found)
        {
            if (!shift)
                selection.Clear();
            return;
        }
        if (builtTwice || ctrl)
        {
            var (x1, y1) = c.ToWorld(0, 0);
            var (x2, y2) = c.ToWorld(c.ScreenWidth, c.ScreenHeight);
            Select(c, selection, BuildingsInBox(c, x1, y1, x2, y2, c.Proto(found)), shift);
        }
        else if (shift && selection.Contains(found))
            selection.Remove(found);
        else
            Select(c, selection, [found], shift);
    }
}
