namespace Base.Host.Sections.Combat;

/// <summary>Всё, во что можно попасть (Mark в combat.ts): юнит или здание. Собирается раз в тик; объекты переиспользуются.</summary>
internal sealed class Mark
{
    public uint Entity;
    public uint Player;

    /// <summary>Центр.</summary>
    public double X, Y;

    /// <summary>Прямоугольник основания у здания; у юнита — нулевой, в его центре.</summary>
    public double Left, Top, Width, Height;

    /// <summary>Радиус юнита; у здания ноль.</summary>
    public double Radius;

    public bool Air;
    public int Armor;

    /// <summary>Стена: остановленный ею выстрел достаётся только стенам.</summary>
    public bool Wall;

    public double Hp;

    /// <summary>Прочность (Health.value) — урон пишется сюда, в мир уходит в конце боя.</summary>
    public double Value;

    /// <summary>Попадание в этом тике (Health.hit = тик).</summary>
    public bool Hit;

    /// <summary>Чем вооружена, считая турели: бит 1 — по земле, бит 2 — по воздуху; −1 — ещё не считали.</summary>
    public int Arms;

    /// <summary>Номер последнего поиска по сетке, нашедшего метку.</summary>
    public int Seen;

    public bool Dead;

    public UnitArms? Unit;
    public BuildingArms? Building;

    /// <summary>Куда смотрит юнит (Facing); у здания — 0.</summary>
    public double Facing;
    public bool Turned;

    /// <summary>Proto сущности.</summary>
    public int Proto;

    /// <summary>Магазин юнита (Magazine.ammo) с тратами этого тика; нет компонента — <see cref="Ammo"/> не в счёт.</summary>
    public bool Magazine;
    public double Ammo;

    /// <summary>Расстояние от точки до края цели (distanceTo).</summary>
    public double DistanceTo(double x, double y) => Math.Max(0, RectDistance(Left, Top, Width, Height, x, y) - Radius);

    /// <summary>Расстояние от точки до прямоугольника (rectDistance); внутри — ноль.</summary>
    public static double RectDistance(double left, double top, double width, double height, double x, double y)
    {
        var dx = Math.Max(Math.Max(left - x, 0), x - left - width);
        var dy = Math.Max(Math.Max(top - y, 0), y - top - height);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Метки по ячейкам MARK_CELL (gridOf): стрелку, взрыву и разряду нужны только те, что рядом. Здание лежит во всех ячейках,
/// которые накрывает. Список ответа один на все поиски — обходить его сразу, до следующего поиска.
/// </summary>
internal sealed class MarkGrid
{
    /// <summary>Сторона ячейки, тайлы.</summary>
    public const int Cell = 8;

    /// <summary>Самый большой радиус юнита: на столько метка может выступать из своей ячейки.</summary>
    public const double Pad = 2;

    private readonly Dictionary<long, List<Mark>> _cells = [];
    private readonly Stack<List<Mark>> _free = [];
    private readonly List<Mark> _found = [];
    private int _query;

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    public void Clear()
    {
        foreach (var list in _cells.Values)
        {
            list.Clear();
            _free.Push(list);
        }
        _cells.Clear();
        _query = 0;
    }

    public void Put(Mark mark)
    {
        var left = (int)Math.Floor(mark.Left / Cell);
        var top = (int)Math.Floor(mark.Top / Cell);
        var right = (int)Math.Floor((mark.Left + mark.Width) / Cell);
        var bottom = (int)Math.Floor((mark.Top + mark.Height) / Cell);
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var key = Key(x, y);
                if (!_cells.TryGetValue(key, out var cell))
                    _cells[key] = cell = _free.Count > 0 ? _free.Pop() : [];
                cell.Add(mark);
            }
    }

    /// <summary>Метки, край которых может оказаться не дальше radius от точки: каждая по разу, в порядке ячеек.</summary>
    public List<Mark> Near(double x, double y, double radius)
    {
        var current = ++_query;
        _found.Clear();
        var reach = radius + Pad;
        var bottom = (int)Math.Floor((y + reach) / Cell);
        var right = (int)Math.Floor((x + reach) / Cell);
        for (var cellY = (int)Math.Floor((y - reach) / Cell); cellY <= bottom; cellY++)
            for (var cellX = (int)Math.Floor((x - reach) / Cell); cellX <= right; cellX++)
            {
                if (!_cells.TryGetValue(Key(cellX, cellY), out var cell))
                    continue;
                foreach (var mark in cell)
                {
                    if (mark.Seen == current)
                        continue;
                    mark.Seen = current;
                    _found.Add(mark);
                }
            }
        return _found;
    }
}
