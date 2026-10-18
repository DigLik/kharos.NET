using Kharos.Engine.Movement;
using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Movement;

/// <summary>approach.test.ts и unreachable.test.ts: подъезд на длину луча (Navigation.Approach, инструмент Stand).</summary>
public sealed class ApproachTests
{
    private static readonly GeneratorConfig Blank = GeneratorConfig.Default with { Blank = true };

    [Fact(DisplayName = "грузовик едет к зданию дальше окна поиска пути: путь до края окна, остальное — на ходу")]
    public void BeyondWindow()
    {
        var world = new MoveWorld(1024, Blank);
        world.Building(200, 0, 3, 2);
        var truck = world.Spawn("truck", 1, -150, 0);
        Assert.Equal(Navigation.StandResult.Found, world.Nav.Approach(truck, 200, 0, 3, 2, 0, 3, out var plan));
        world.Nav.Apply(truck, plan);
        Assert.True(world.PathNumber(truck, "goalX") > 190);
    }

    [Fact(DisplayName = "места в замкнутом кармане между зданиями грузовик не пробует: подъезжает с открытой стороны")]
    public void Pocket()
    {
        var world = new MoveWorld(256, Blank);
        world.Building(0, 0, 2, 2);
        world.Building(0, 2, 2, 2);
        // Карман x = −1, y = 0…3: слева, сверху и снизу — склады, справа — заводы.
        for (var x = -2; x <= 3; x++)
            world.Building(x, -1, 1, 1);
        for (var y = 0; y <= 4; y++)
            world.Building(-2, y, 1, 1);
        world.Building(-1, 4, 1, 1);
        var truck = world.Spawn("truck", 1, -10, 1);
        Assert.Equal(Navigation.StandResult.Found, world.Nav.Approach(truck, 0, 0, 2, 2, 0, 2, out var plan));
        Assert.False(plan.GoalX == -1 && plan.GoalY is >= 0 and <= 3);
    }

    [Fact(DisplayName = "подъезд к зданию за кольцом труб не съедает тик: за тик грузовики осматривают не больше нормы", Skip = "ждёт С2b: seekBeam и промахи грузовиков — в логистике мода; норма — в тесте инструмента Stand")]
    public void SeekQuota()
    {
    }

    [Fact(DisplayName = "к обнесённой стеной переработке не подъехать: грузовик бросает рейс, руда ложится на землю, завод отмечен", Skip = "ждёт С2b")]
    public void WalledSmelter()
    {
    }

    [Fact(DisplayName = "руда с недоступного завода едет на другой, куда подъехать можно", Skip = "ждёт С2b")]
    public void OtherSmelter()
    {
    }

    [Fact(DisplayName = "запертый грузовик отмечается сам, а здание, к которому он ехал, — нет; груз остаётся в кузове", Skip = "ждёт С2b")]
    public void TrappedTruck()
    {
    }

    [Fact(DisplayName = "подъезд к обнесённому стеной зданию не находится, а запертый юнит не находит его ни к чему")]
    public void WalledApproach()
    {
        var world = new MoveWorld(256, Blank);
        world.Building(0, 0, 3, 3);
        // Кольцо стен на расстоянии 2 от здания.
        for (var i = -3; i <= 5; i++)
        {
            world.Building(i, -3, 1, 1);
            world.Building(i, 5, 1, 1);
            world.Building(-3, i, 1, 1);
            world.Building(5, i, 1, 1);
        }
        var truck = world.Spawn("truck", 1, -20, 1);
        Assert.Equal(Navigation.StandResult.None, world.Nav.Approach(truck, 0, 0, 3, 3, 0, 2, out _));
        // Рядом с самим юнитом — на месте: путь не нужен.
        Assert.Equal(Navigation.StandResult.Here, world.Nav.Approach(truck, -20, 1, 1, 1, 0, 2, out _));
    }
}
