using Base.Shared.Orders;
using Kharos.Base.Tests.Combat;
using Kharos.Base.Tests.Construction;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Tactics;

/// <summary>
/// Сцены тактики (field и помощники tactics.test.ts; скала без подножий обрыва — на подножии техника не встаёт, а в TS юнит ставился без проверок). Размер карты — полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class Strip
{
    public const double Tick = Sim.Dt;

    /// <summary>Поводок обороны (LEASH в tactics.ts), тайлов.</summary>
    public const double Leash = 8;

    /// <summary>Партия и тайл полосы скалы 40×9, на которой ставятся бойцы: y — её середина по высоте (field).</summary>
    public static (Sim Sim, int X, int Y) Field()
    {
        var sim = Scene.Create();
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
                if (Yard.Rock(sim, x, y, 40, 9))
                    return (sim, x, y + 4);
        sim.Dispose();
        throw new InvalidOperationException("Нет места");
    }

    /// <summary>Погиб или ранен (hurt).</summary>
    public static bool Hurt(Sim sim, uint entity) => !sim.Alive(entity) || sim.HealthOf(entity) < 1;

    /// <summary>Юнит в центре тайла (spawnUnit).</summary>
    public static uint Put(Sim sim, string type, uint player, int x, int y) => Arena.Spawn(sim, type, player, x, y);

    /// <summary>Юнит в точке (x + 0.5, y + 0.5) при дробных x, y (spawnUnit сдвигает на полтайла и дробное).</summary>
    public static uint Put(Sim sim, string type, uint player, double x, double y)
        => sim.PutUnit(type, player, x + 0.5, y + 0.5);

    /// <summary>Стойка своих юнитов приказом stance.</summary>
    public static void Stance(Sim sim, uint player, string stance, params uint[] units) => sim.Send(player, new StanceOrder { Units = units, Stance = stance });

    public static void Move(Sim sim, uint player, uint[] units, double x, double y, bool queue = false) =>
        sim.Send(player, new MoveOrder { Units = units, X = (int)x, Y = (int)y, Queue = queue });

    public static void Patrol(Sim sim, uint player, uint[] units, double[] points, bool append = false) =>
        sim.Send(player, new PatrolOrder { Units = units, Points = points, Append = append });

    /// <summary>Тактика юнита заводится прямо в мир с местом, откуда он ушёл в погоню (Tactics({ away, homeX, homeY })).</summary>
    public static void Away(Sim sim, uint unit, double homeX, double homeY)
    {
        sim.Add(unit, "Tactics");
        sim.Set(unit, "Tactics.away", 1);
        sim.Set(unit, "Tactics.homeX", homeX);
        sim.Set(unit, "Tactics.homeY", homeY);
    }

    public static bool IsAway(Sim sim, uint unit) => sim.TacticsOf(unit).Away;

    public static double Distance(Sim sim, uint unit, double x, double y) => Sim.Distance(sim.At(unit), (x, y));
}
