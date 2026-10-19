using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Combat;

/// <summary>
/// Погоня к месту у цели (не из TS: найдено балансом, осада «3 артиллерии»). Место гонящегося — точка на выстреле от края
/// цели, а путь прокладывается в тайл этой точки. Остановленный на дальнем краю этого тайла (новым приказом атаки,
/// соседом) стоит в тайле своего места, но не на выстреле, и поиск пути в свой же тайл пуст: юнит не сдвинулся бы.
/// </summary>
public sealed class ChaseTests
{
    [Fact(DisplayName = "артиллерия в тайле своего места, но чуть дальше выстрела, доезжает до места и бьёт стену")]
    public void ArtilleryInSlotTileClosesTheLastStep()
    {
        Scene.SkipUnlessAvailable();
        using var sim = Sim.Create(new SimOptions { Size = 128, Land = new { blank = true }, Rules = new { protection = 0 } });
        var wall = sim.PutBuilding("wall", 40, 40, 2);
        // До края стены 11,05 при дальности 11; место — в тайле (29, 38), где юнит уже стоит.
        var gun = sim.PutUnit("artillery", 1, 29.05, 38.53);
        sim.SetFacing(0, gun);
        sim.Send(1, new AttackOrder { Units = [gun], Target = wall });
        Assert.True(sim.Until(() => sim.HealthOf(wall) < 1, 4), "артиллерия так и не выстрелила");
        var (x, _) = sim.At(gun);
        Assert.True(x > 29.05, $"артиллерия не сдвинулась: x = {x}");
    }
}
