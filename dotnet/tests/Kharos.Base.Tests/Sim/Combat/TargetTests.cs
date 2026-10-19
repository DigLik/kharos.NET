using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using MemoryPack;

namespace Kharos.Base.Tests.Combat;

/// <summary>Приказ точкой (packages/tests/target.test.ts): цель выбирает симуляция с учётом тумана.</summary>
public sealed class TargetTests
{
    /// <summary>Партия и металлическое месторождение, справа от которого ровная скала (start).</summary>
    private static (Sim Sim, DepositSpot Spot) Start()
    {
        var sim = Scene.Create(rules: new { techTree = false });
        for (var cellY = -5; cellY < 5; cellY++)
            for (var cellX = -5; cellX < 5; cellX++)
                if (sim.DepositIn(cellX, cellY) is { Kind: "metal" } spot && Scene.Rock(sim, spot.X + 3, spot.Y, 3, 3) && Scene.CanPlace(sim, "metalYard", spot.X + 3, spot.Y))
                    return (sim, spot);
        sim.Dispose();
        throw new InvalidOperationException("В мире не нашлось месторождения");
    }

    /// <summary>Приказ точкой и тик, чтобы он выполнился.</summary>
    private static void Order(Sim sim, uint player, uint[] units, double x, double y, bool queue = false)
    {
        sim.Send(player, new PointOrder { Units = units, X = x, Y = y, Queue = queue });
        sim.Advance();
    }

    [Fact(DisplayName = "приказ точкой грузовику: дроп на виду — вывозить, дроп в тумане — просто ехать")]
    public void TruckPointOrder()
    {
        var (sim, spot) = Start();
        using var _sim = sim;
        var truck = Scene.Spawn(sim, "truck", 1, spot.X + 3, spot.Y);
        var near = sim.DropItems(spot.X + 5, spot.Y, ("metal", 10));
        var far = sim.DropItems(spot.X + 80, spot.Y, ("metal", 10));
        sim.Advance();
        Assert.False(sim.Host.Vision!.Sees(1, spot.X + 80, spot.Y));
        Order(sim, 1, [truck], spot.X + 5.5, spot.Y + 0.5);
        Assert.Equal(near, sim.Hauler(truck).Pickup);
        Order(sim, 1, [truck], spot.X + 80.5, spot.Y + 0.5);
        Assert.NotEqual(far, sim.Hauler(truck).Pickup);
        Assert.Equal(0u, sim.Hauler(truck).Pickup);
        // Сама симуляция без тумана (редактор) цель там находит.
        var resolved = Assert.Single(sim.Resolve(1, [truck], spot.X + 80.5, spot.Y + 0.5, fog: false));
        Assert.Equal((PickupOrder.Name, far), (resolved.Name, resolved.Target));
        Assert.Equal([truck], resolved.Units);
    }

    [Fact(DisplayName = "приказ точкой бойцу: с Shift встаёт в очередь атакой, чужими юнитами не командует, мусор не роняет тик")]
    public void GunnerPointOrder()
    {
        var (sim, spot) = Start();
        using var _sim = sim;
        var gunner = Scene.Spawn(sim, "infantry", 1, spot.X + 3, spot.Y);
        var foe = Scene.Spawn(sim, "truck", 2, spot.X + 6, spot.Y);
        var at = sim.At(foe);
        sim.Advance();
        // Чужой игрок приказывает моим юнитом: ничего не происходит.
        Order(sim, 2, [gunner], at.X, at.Y);
        Assert.Equal(0, sim.Num(gunner, "Armed.ordered"));
        Assert.False(sim.Has(gunner, Names.Path.Name));
        // С Shift приказ встаёт в очередь уже атакой.
        Order(sim, 1, [gunner], spot.X + 3.5, spot.Y + 2.5);
        Order(sim, 1, [gunner], at.X, at.Y, true);
        var queue = MemoryPackSerializer.Deserialize<OrderQueue>(sim.Bytes(gunner, "Orders.list"))!.Items;
        var queued = Assert.Single(queue);
        Assert.Equal(AttackOrder.Name, queued.Name);
        Assert.Equal(foe, MemoryPackSerializer.Deserialize<AttackOrder>(queued.Payload)!.Target);
        // Попал рядом с юнитом, в пределах допуска, — всё равно атака.
        Order(sim, 1, [gunner], at.X + 0.2, at.Y);
        Assert.Equal(foe, sim.TargetOf(gunner));
        Assert.Equal(1, sim.Num(gunner, "Armed.ordered"));
        foreach (var junk in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            sim.Send(1, new PointOrder { Units = [gunner], X = junk, Y = junk });
        foreach (var junk in new byte[][] { [], [1, 2, 3], [255, 255, 255, 255, 1], [.. "{\"toString\":0}"u8] })
            sim.Send(1, PointOrder.Name, junk);
        sim.Advance();
        Assert.Equal(foe, sim.TargetOf(gunner));
        Assert.Equal(1, sim.Num(gunner, "Armed.ordered"));
    }
}
