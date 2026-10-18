using Kharos.Engine.Movement;

namespace Kharos.Engine.Tests.Movement;

/// <summary>Поиск и спрямление пути (path.ts): части units.test.ts и terrainSpeed.test.ts на сетках из строк.</summary>
public sealed class PathTests
{
    /// <summary>Сетка из строк: # — стена, остальное проходимо; за краями — стена.</summary>
    private sealed class Rows(params string[] rows) : IPathGrid
    {
        public bool Walkable(int x, int y) => y >= 0 && y < rows.Length && x >= 0 && x < rows[y].Length && rows[y][x] != '#';
    }

    private sealed class Grid(Func<int, int, bool> walkable, Func<int, int, double>? slowness = null) : IPathGrid
    {
        public bool Walkable(int x, int y) => walkable(x, y);
        public double Slowness(int x, int y) => slowness?.Invoke(x, y) ?? 1;
    }

    private static List<int> Find(IPathGrid grid, int fromX, int fromY, int toX, int toY, double near = 0, double limit = PathSearch.SearchLimit, PathSearch? search = null)
    {
        var path = new List<int>();
        (search ?? new PathSearch()).FindPath(grid, fromX, fromY, toX, toY, near, limit, 1, path);
        return path;
    }

    private static List<double> Smooth(IPathGrid grid, double fromX, double fromY, IEnumerable<int> tiles)
    {
        var into = new List<double>();
        PathSearch.SmoothPath(grid, fromX, fromY, [.. tiles.Select(value => value + 0.5)], into);
        return into;
    }

    [Fact(DisplayName = "путь обходит стену и не режет углы")]
    public void AroundWall()
    {
        var grid = new Rows(".....", ".###.", ".....");
        var path = Find(grid, 0, 2, 4, 0);
        Assert.Equal([4, 0], path[^2..]);
        for (var i = 0; i < path.Count; i += 2)
            Assert.True(grid.Walkable(path[i], path[i + 1]));
        // Мимо угла стены наискось нельзя: из (3, 2) в (4, 1) только через (4, 2).
        Assert.Equal([1, 2, 2, 2, 3, 2, 4, 2, 4, 1, 4, 0], path);
    }

    [Fact(DisplayName = "путь по открытому месту идёт наискось")]
    public void Diagonal()
    {
        var grid = new Rows("....", "....", "....", "....");
        Assert.Equal([1, 1, 2, 2, 3, 3], Find(grid, 0, 0, 3, 3));
        Assert.Empty(Find(grid, 0, 0, 0, 0));
    }

    [Fact(DisplayName = "к недостижимой цели путь ведёт до ближайшего тайла")]
    public void Unreachable()
    {
        var grid = new Rows("..#..", "..#..", "..#..");
        Assert.Equal([1, 1], Find(grid, 0, 1, 4, 1)[^2..]);
        // Цель внутри стены.
        Assert.Equal([1, 1], Find(grid, 0, 1, 2, 1)[^2..]);
    }

    [Fact(DisplayName = "спрямление выбрасывает лишние точки, но не ведёт сквозь стену")]
    public void Smoothing()
    {
        var open = new Rows(".....", ".....", ".....");
        Assert.Equal([4.5, 2.5], Smooth(open, 0.5, 0.5, Find(open, 0, 0, 4, 2)));

        var walled = new Rows(".....", ".###.", ".....");
        var around = Find(walled, 0, 2, 4, 0);
        var smooth = Smooth(walled, 0.5, 2.5, around);
        Assert.Equal([4.5, 0.5], smooth[^2..]);
        Assert.True(smooth.Count < around.Count);
        var (fromX, fromY) = (0.5, 2.5);
        for (var i = 0; i < smooth.Count; i += 2)
        {
            Assert.True(PathSearch.IsClear(walled, fromX, fromY, smooth[i], smooth[i + 1]));
            (fromX, fromY) = (smooth[i], smooth[i + 1]);
        }
    }

    [Fact(DisplayName = "путь объезжает болото, если в объезд быстрее, и спрямление не срезает через него угол")]
    public void AroundSwamp()
    {
        // Болото — полоса x от 5 до 14 при y от −3 до 3; в обход сверху — чуть длиннее, зато по твёрдому.
        static bool Swamp(int x, int y) => x >= 5 && x < 15 && y >= -3 && y <= 3;
        var grid = new Grid((x, y) => Math.Abs(x) < 40 && Math.Abs(y) < 40, (x, y) => Swamp(x, y) ? 5 : 1);
        var tiles = Find(grid, 0, 0, 20, 0);
        for (var i = 0; i < tiles.Count; i += 2)
            Assert.False(Swamp(tiles[i], tiles[i + 1]));
        var points = Smooth(grid, 0.5, 0.5, tiles);
        var (fromX, fromY) = (0.5, 0.5);
        for (var i = 0; i < points.Count; i += 2)
        {
            for (var t = 0.0; t <= 1; t += 0.05)
                Assert.False(Swamp((int)Math.Floor(fromX + (points[i] - fromX) * t), (int)Math.Floor(fromY + (points[i + 1] - fromY) * t)));
            (fromX, fromY) = (points[i], points[i + 1]);
        }
        // Без замедления — напрямик.
        Assert.Equal(20, Find(new Grid((x, y) => Math.Abs(x) < 40 && Math.Abs(y) < 40), 0, 0, 20, 0).Count / 2);
    }

    [Fact(DisplayName = "норма поиска снимается и после исключения, а оборванный ею поиск не отнимает у юнита прежний путь")]
    public void QuotaRestored()
    {
        var search = new PathSearch();
        var open = new Grid((_, _) => true);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            var previous = search.OpenQuota(0);
            try
            {
                Assert.Empty(Find(open, 0, 0, 5, 0, search: search));
                throw new InvalidOperationException("сбой");
            }
            finally
            {
                search.CloseQuota(previous);
            }
        }));
        Assert.NotEmpty(Find(open, 0, 0, 5, 0, search: search));
        Assert.False(search.Cut);
        // Цель за стеной: поиск упирается в собственный лимит. Это не обрыв нормой, даже когда норма ему равна.
        var walled = new Grid((x, y) => Math.Max(Math.Abs(x - 40), Math.Abs(y)) != 3);
        var quota = search.OpenQuota(PathSearch.SearchLimit);
        Assert.NotEmpty(Find(walled, 0, 0, 40, 0, search: search));
        Assert.False(search.Cut);
        // А следующему поиску в том же тике нормы уже не хватает: он оборван.
        Find(walled, 0, 0, 40, 0, search: search);
        Assert.True(search.Cut);
        search.CloseQuota(quota);

        // Нормы не осталось: новый приказ не выполнен, но и прежний путь цел — юнит попробует в следующий тик.
        var world = new MoveWorld();
        var (x, y) = world.Plateau(8, 3);
        var unit = world.Spawn("mcv", 1, x + 1, y + 1);
        Assert.Equal(Moved.Set, world.Nav.Order(unit, x + 4, y + 1));
        var before = world.Points(unit);
        Assert.NotEmpty(before);
        var previous = world.Nav.Search.OpenQuota(0);
        Assert.Equal(Moved.Kept, world.Nav.Order(unit, x + 1, y + 2));
        world.Nav.Search.CloseQuota(previous);
        Assert.Equal(before, world.Points(unit));
    }
}
