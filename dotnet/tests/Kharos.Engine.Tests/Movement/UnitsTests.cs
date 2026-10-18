using Kharos.Engine.Movement;
using Kharos.Engine.Numerics;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Movement;

/// <summary>units.test.ts: ход юнитов на компонентах движка (createSim → <see cref="MoveWorld"/>, move → orderGroupMove).</summary>
public sealed class UnitsTests
{
    private static readonly string[] StartingUnits = ["mcv", "buggy", "buggy", "infantry", "infantry", "infantry"];

    /// <summary>Стартовый набор игрока 1 у начала мира (spawnStartingUnits: места — freeTilesNear).</summary>
    private static (MoveWorld World, uint[] Units) Start()
    {
        var world = new MoveWorld();
        var tiles = new List<int>();
        world.Nav.FreeTilesNear(0, 0, StartingUnits.Length, 0, null, false, tiles);
        var units = new uint[Math.Min(StartingUnits.Length, tiles.Count / 2)];
        for (var i = 0; i < units.Length; i++)
            units[i] = world.Spawn(StartingUnits[i], 1, tiles[i * 2], tiles[i * 2 + 1]);
        return (world, units);
    }

    /// <summary>Крутит мир, пока кто-то идёт, и проверяет, что юниты не входят друг в друга. Сколько тиков прошло.</summary>
    private static int RunApart(MoveWorld world, params uint[] units)
    {
        var ticks = 0;
        for (; ticks < 20 * 60 && world.PathCount > 0; ticks++)
        {
            world.Step();
            foreach (var a in units)
                foreach (var b in units)
                {
                    if (a >= b)
                        continue;
                    var (ax, ay) = world.At(a);
                    var (bx, by) = world.At(b);
                    var reach = world.World.Number(a, Builtin.Body, 0) + world.World.Number(b, Builtin.Body, 0);
                    Assert.True(DetMath.Hypot(ax - bx, ay - by) > reach - 0.05, $"тик {ticks}: {a} и {b} вошли друг в друга");
                }
        }
        return ticks;
    }

    private static void Move(MoveWorld world, uint[] units, int x, int y) => world.Nav.OrderGroup(units, x, y);

    [Fact(DisplayName = "стартовый набор появляется на проходимых тайлах, каждый на своём")]
    public void StartingSet()
    {
        var (world, units) = Start();
        Assert.Equal(StartingUnits.Length, units.Length);
        var tiles = units.Select(world.TileOf).ToList();
        foreach (var (x, y) in tiles)
            Assert.True(world.Nav.IsWalkable(x, y));
        Assert.Equal(units.Length, tiles.Distinct().Count());
    }

    [Fact(DisplayName = "юниты доходят до цели и встают на разные тайлы")]
    public void ArriveApart()
    {
        var (world, units) = Start();
        var (x, y) = world.TileOf(units[0]);
        Move(world, units, x + 20, y + 6);
        Assert.Equal(units.Length, world.PathCount);
        for (var i = 0; i < 20 * 60 && world.PathCount > 0; i++)
            world.Step();
        Assert.Equal(0, world.PathCount);
        var tiles = units.Select(world.TileOf).ToList();
        Assert.Equal(units.Length, tiles.Distinct().Count());
        foreach (var (tileX, tileY) in tiles)
        {
            Assert.True(world.Nav.IsWalkable(tileX, tileY));
            Assert.True(Math.Sqrt(Math.Pow(tileX - x - 20, 2) + Math.Pow(tileY - y - 6, 2)) < 30);
        }
    }

    [Fact(DisplayName = "за тик юнит проходит не больше своей скорости и запоминает прошлое место")]
    public void SpeedLimit()
    {
        // Прошлого места в компонентах больше нет (сглаживает клиент): проверяется только скорость.
        var (world, units) = Start();
        var mcv = units[0];
        var (x, y) = world.TileOf(mcv);
        Move(world, [mcv], x + 20, y);
        double moved = 0;
        for (var i = 0; i < 40; i++)
        {
            var before = world.At(mcv);
            world.Step();
            var after = world.At(mcv);
            moved = DetMath.Hypot(after.X - before.X, after.Y - before.Y);
            Assert.True(moved <= 2.5 * MoveWorld.Tick + 1e-9);
        }
        // Развернувшись к цели, машина идёт полным ходом.
        Assert.Equal(2.5 * MoveWorld.Tick, moved, 9);
    }

    [Fact(DisplayName = "юнит поворачивает плавно: за тик не больше своей скорости поворота")]
    public void SmoothTurn()
    {
        var (world, units) = Start();
        var mcv = units[0];
        var (x, y) = world.TileOf(mcv);
        // MCV появляется, глядя вниз; цель — сзади и сбоку.
        Assert.Equal(Moved.Set, world.Nav.Order(mcv, x + 6, y - 10));
        var turn = MoveWorld.Spec("mcv").Turn;
        var angles = new HashSet<double>();
        for (var i = 0; i < 20 * 60 && world.HasPath(mcv); i++)
        {
            var before = world.Facing(mcv);
            world.Step();
            var facing = world.Facing(mcv);
            var turned = Math.Abs(Math.Atan2(Math.Sin(facing - before), Math.Cos(facing - before)));
            Assert.True(turned <= turn * MoveWorld.Tick + 1e-9);
            angles.Add(facing);
        }
        // Угол не привязан к восьми или шестнадцати направлениям.
        Assert.True(angles.Count > 20);
        Assert.Equal((x + 6, y - 10), world.TileOf(mcv));
    }

    [Fact(DisplayName = "толпа, посланная в одну точку, доезжает: никто не стоит с приказом вечно, зажатый соседями")]
    public void CrowdArrives()
    {
        var world = new MoveWorld(config: GeneratorConfig.Default with { Seed = 2 });
        (int X, int Y)? at = null;
        for (var y = -200; y < 200 && at is null; y += 3)
            for (var x = -200; x < 200 && at is null; x += 3)
            {
                var open = true;
                for (var dy = 0; dy < 40 && open; dy++)
                    for (var dx = 0; dx < 60 && open; dx++)
                        open = world.Nav.IsWalkable(x + dx, y + dy);
                if (open)
                    at = (x, y);
            }
        Assert.NotNull(at);
        var (left, top) = at.Value;
        string[] types = ["infantry", "rocketeer", "buggy", "lancer", "tank", "tesla", "carrier", "flak"];
        var units = Enumerable.Range(0, 160).Select(i => world.Spawn(types[i % types.Length], 1, left + 2 + i % 13 * 2, top + 2 + i / 13 * 2)).ToArray();
        Move(world, units, left + 50, top + 20);
        Assert.All(units, entity => Assert.True(world.HasPath(entity)));
        for (var tick = 0; tick < 4 * 60 / MoveWorld.Tick && units.Any(world.HasPath); tick++)
            world.Step();
        Assert.DoesNotContain(units, world.HasPath);
    }

    [Fact(DisplayName = "встречные юниты расходятся, не проходя друг сквозь друга")]
    public void Oncoming()
    {
        foreach (var type in (string[])["infantry", "builder", "mcv"])
        {
            var world = new MoveWorld();
            var (x, y) = world.Plateau(30, 9, 3);
            var left = world.Spawn(type, 1, x + 2, y + 4);
            var right = world.Spawn(type, 1, x + 26, y + 4);
            Move(world, [left], x + 23, y + 4);
            Move(world, [right], x + 5, y + 4);
            RunApart(world, left, right);
            Assert.Equal((x + 23, y + 4), world.TileOf(left));
            Assert.Equal((x + 5, y + 4), world.TileOf(right));
        }
    }

    [Fact(DisplayName = "стоящего юнита объезжают, а занятая им цель заменяется соседней")]
    public void AroundStanding()
    {
        var world = new MoveWorld();
        var (x, y) = world.Plateau(30, 9, 3);
        var guard = world.Spawn("mcv", 1, x + 14, y + 4);
        var runner = world.Spawn("builder", 1, x + 2, y + 4);
        Move(world, [runner], x + 26, y + 4);
        RunApart(world, guard, runner);
        Assert.Equal((x + 26, y + 4), world.TileOf(runner));
        Assert.Equal((x + 14, y + 4), world.TileOf(guard));

        Move(world, [runner], x + 14, y + 4);
        RunApart(world, guard, runner);
        Assert.False(world.HasPath(runner));
        var (tileX, tileY) = world.TileOf(runner);
        Assert.True(Math.Sqrt(Math.Pow(tileX - x - 14, 2) + Math.Pow(tileY - y - 4, 2)) < 4);
    }

    [Fact(DisplayName = "двое, посланные в одну или соседние точки, не кружат друг вокруг друга, а встают рядом")]
    public void TwoMeet()
    {
        foreach (var (a, b, offset) in ((string, string, int)[])[("infantry", "infantry", 0), ("tank", "tank", 0), ("carrier", "carrier", 1), ("buggy", "carrier", 1)])
        {
            var world = new MoveWorld();
            var (x, y) = world.Plateau(30, 9, 3);
            var first = world.Spawn(a, 1, x + 2, y + 4);
            var second = world.Spawn(b, 1, x + 26, y + 4);
            Move(world, [first], x + 14, y + 4);
            Move(world, [second], x + 14 + offset, y + 4);
            // Ехать каждому — секунд пять; кружащие не встали бы и за двадцать.
            world.Seconds(8);
            foreach (var entity in (uint[])[first, second])
            {
                Assert.False(world.HasPath(entity), $"{a}/{b}: {entity} кружит");
                var (tileX, tileY) = world.TileOf(entity);
                Assert.True(Math.Sqrt(Math.Pow(tileX - x - 14, 2) + Math.Pow(tileY - y - 4, 2)) < 4);
            }
        }
    }

    [Fact(DisplayName = "быстрый юнит обгоняет медленного, а медленный потом объезжает вставшего у него на пути")]
    public void Overtake()
    {
        var world = new MoveWorld();
        var (x, y) = world.Plateau(30, 9, 3);
        var slow = world.Spawn("mcv", 1, x + 5, y + 4);
        var fast = world.Spawn("builder", 1, x + 2, y + 4);
        Move(world, [slow], x + 27, y + 4);
        Move(world, [fast], x + 23, y + 4);
        RunApart(world, slow, fast);
        Assert.Equal((x + 27, y + 4), world.TileOf(slow));
        // Строитель пришёл первым и встал на пути MCV; тот объехал его или сдвинул плечом — недалеко от своего места.
        var (tileX, tileY) = world.TileOf(fast);
        Assert.True(Math.Sqrt(Math.Pow(tileX - x - 23, 2) + Math.Pow(tileY - y - 4, 2)) < 4);
    }

    [Fact(DisplayName = "чужими юнитами командовать нельзя", Skip = "ждёт Б1: проверка приказа move — в Kernel/Commands")]
    public void ForeignUnits()
    {
    }

    [Fact(DisplayName = "здание на пути заставляет проложить путь заново")]
    public void BuildingInTheWay()
    {
        var (world, units) = Start();
        var scout = units[3];
        // Скала 3×3 подальше от юнитов: на ней — здание, когда юнит уже идёт.
        (int X, int Y)? site = null;
        for (var radius = 6; radius < 200 && site is null; radius++)
            for (var dy = -radius; dy <= radius && site is null; dy++)
                for (var dx = -radius; dx <= radius && site is null; dx++)
                {
                    var ok = true;
                    for (var ty = dy; ty < dy + 3 && ok; ty++)
                        for (var tx = dx; tx < dx + 3 && ok; tx++)
                            ok = world.Flat(tx, ty);
                    if (ok)
                        site = (dx, dy);
                }
        Assert.NotNull(site);
        var (x, y) = site.Value;
        // Цель — за будущим зданием, если смотреть от юнита.
        var (fromX, fromY) = world.TileOf(scout);
        var goalX = x + 1 + Math.Sign(x + 1 - fromX) * 4;
        var goalY = y + 1 + Math.Sign(y + 1 - fromY) * 4;
        Move(world, [scout], goalX, goalY);
        world.Step();
        var building = world.Building(x, y, 3, 3);
        for (var i = 0; i < 20 * 120 && world.HasPath(scout); i++)
        {
            world.Step();
            var (tileX, tileY) = world.TileOf(scout);
            Assert.Equal(0u, world.Nav.Occupancy.At(tileX, tileY));
        }
        Assert.False(world.HasPath(scout));
        Assert.Equal(building, world.Nav.Occupancy.At(x, y));
    }

    [Fact(DisplayName = "сохранение посреди пути продолжается так же, как оригинал")]
    public void ThroughSave()
    {
        var (world, units) = Start();
        var (x, y) = world.TileOf(units[0]);
        Move(world, units, x + 15, y - 4);
        for (var i = 0; i < 20; i++)
            world.Step();
        var copy = world.Copy();
        for (var i = 0; i < 200; i++)
        {
            world.Step();
            copy.Step();
        }
        Assert.Equal(WorldSave.Save(world.World), WorldSave.Save(copy.World));
    }

    [Fact(DisplayName = "юнит попадает в точку сразу за крутым поворотом, а не кружит вокруг неё")]
    public void SharpTurn()
    {
        var world = new MoveWorld();
        var (x, y) = world.Plateau(30, 9, 3);
        foreach (var type in (string[])["mcv", "builder", "infantry"])
        {
            var unit = world.Spawn(type, 1, x + 1, y + 4);
            // Последняя точка — в тайле вбок от предпоследней: ближе, чем радиус разворота машины на ходу.
            world.GivePath(unit, [x + 8.5, y + 4.5, x + 8.5, y + 5.5], x + 8, y + 5);
            for (var i = 0; i < 20 * 10; i++)
                world.Step();
            Assert.False(world.HasPath(unit));
            Assert.Equal((x + 8.5, y + 5.5), world.At(unit));
            world.World.Destroy(unit);
        }
    }

    [Fact(DisplayName = "путь, оборванный лимитом поиска, юнит продолжает с конца и доходит до цели")]
    public void LimitedPathContinues()
    {
        var world = new MoveWorld();
        var x = 0;
        while (!world.Nav.IsWalkable(x, 0))
            x++;
        const int y = 0;
        var buggy = world.Spawn("buggy", 1, x, y);
        // Дальняя достижимая цель: её находит поиск без лимита.
        (int X, int Y)? goal = null;
        var tiles = new List<int>();
        var grid = new Walk(world.Nav);
        for (var r = 60; goal is null && r < 120; r += 4)
            foreach (var (dx, dy) in ((int, int)[])[(r, 0), (-r, 0), (0, r), (0, -r)])
            {
                if (!world.Nav.IsWalkable(x + dx, y + dy))
                    continue;
                new PathSearch().FindPath(grid, x, y, x + dx, y + dy, 0, 1e6, 1, tiles);
                if (tiles.Count > 0 && tiles[^2] == x + dx && tiles[^1] == y + dy)
                {
                    goal = (x + dx, y + dy);
                    break;
                }
            }
        Assert.NotNull(goal);
        // Первый поиск обрывается на 200 тайлах.
        world.Nav.Order(buggy, goal.Value.X, goal.Value.Y, limit: 200);
        for (var i = 0; i < 20 * 120 && world.HasPath(buggy); i++)
            world.Step();
        var (atX, atY) = world.TileOf(buggy);
        Assert.True(Math.Sqrt(Math.Pow(atX - goal.Value.X, 2) + Math.Pow(atY - goal.Value.Y, 2)) < 2);
    }

    private sealed class Walk(Navigation nav) : IPathGrid
    {
        public bool Walkable(int x, int y) => nav.IsWalkable(x, y);
    }

    [Fact(DisplayName = "юниты, едущие через одну точку навстречу друг другу, доезжают, а не кружат хороводом")]
    public void RoundDance()
    {
        foreach (var type in (string[])["buggy", "infantry", "tank"])
        {
            var world = new MoveWorld(256, GeneratorConfig.Default with { Seed = 7 });
            var units = new List<uint>();
            // По кругу, каждый — к противоположной точке: все пересекаются в центре.
            for (var i = 0; i < 24; i++)
            {
                var angle = i / 24.0 * Math.PI * 2;
                var unit = world.Spawn(type, 1, DetMath.Cos(angle) * 8, DetMath.Sin(angle) * 8);
                world.Nav.Order(unit, (int)Math.Floor(-DetMath.Cos(angle) * 8), (int)Math.Floor(-DetMath.Sin(angle) * 8));
                units.Add(unit);
            }
            for (var tick = 0; tick < 20 * 100 && units.Any(world.HasPath); tick++)
                world.Step();
            Assert.DoesNotContain(units, world.HasPath);
        }
    }

    [Fact(DisplayName = "к цели на здании юнит идёт на ближайший к ней тайл, а не ищет путь в неё")]
    public void TargetOnBuilding()
    {
        var world = new MoveWorld();
        var (x, y) = world.Plateau(20, 20);
        world.Building(x + 12, y + 8, 2, 2);
        var unit = world.Spawn("infantry", 1, x + 1, y + 9);
        var before = world.Nav.Search.Searched;
        world.Nav.Order(unit, x + 13, y + 9);
        Assert.True(world.Nav.Search.Searched - before < 500);
        var (goalX, goalY) = ((int)world.PathNumber(unit, "goalX"), (int)world.PathNumber(unit, "goalY"));
        Assert.True(world.Nav.IsWalkable(goalX, goalY, true));
        Assert.Equal(1, Math.Max(Math.Abs(goalX - x - 13), Math.Abs(goalY - y - 9)));
    }

    [Fact(DisplayName = "цель в замкнутом кармане не ищется долгим поиском")]
    public void ClosedPocket()
    {
        var world = new MoveWorld();
        var (x, y) = world.Plateau(20, 20);
        // Кольцо стен вокруг тайла (x + 15, y + 10).
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0)
                    world.Building(x + 15 + dx, y + 10 + dy, 1, 1, 2);
        var unit = world.Spawn("infantry", 1, x + 1, y + 10);
        var before = world.Nav.Search.Searched;
        world.Nav.Order(unit, x + 15, y + 10);
        Assert.True(world.Nav.Search.Searched - before < PathSearch.SearchLimit * 2);
    }
}
