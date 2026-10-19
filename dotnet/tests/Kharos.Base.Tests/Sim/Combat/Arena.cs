using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Combat;

/// <summary>
/// Сцены боя (field, untilShot и помощники combat.test.ts, towers.test.ts, repairStation.test.ts). Размер карты —
/// полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class Arena
{
    public const double Tick = 0.05;

    /// <summary>Партия test с местностью TS (createSim(options)).</summary>
    public static Sim Create(object? rules = null, int size = 512) => Scene.Create(size, rules);

    /// <summary>Партия и левый верхний тайл ровного проходимого поля width×height (field): y — его середина по высоте.</summary>
    public static (Sim Sim, int X, int Y) Field(int width = 24, int height = 7, object? rules = null, bool middle = true)
    {
        var sim = Create(rules);
        for (var y = -40; y < 40; y++)
            for (var x = -40; x < 40; x++)
            {
                if (!Walkable(sim, x, y, width, height))
                    continue;
                return (sim, x, middle ? y + height / 2 : y);
            }
        sim.Dispose();
        throw new InvalidOperationException("В мире не нашлось ровного поля");
    }

    private static bool Walkable(Sim sim, int x, int y, int width, int height)
    {
        for (var dy = 0; dy < height; dy++)
            for (var dx = 0; dx < width; dx++)
                if (!sim.Walkable(x + dx, y + dy))
                    return false;
        return true;
    }

    /// <summary>Юнит в центре тайла (spawnUnit).</summary>
    public static uint Spawn(Sim sim, string type, uint player, int x, int y)
    {
        sim.EnsurePlayer(player);
        return Scene.Spawn(sim, type, player, x, y);
    }

    /// <summary>
    /// Готовое здание (placeBuilding): в TS места не проверяются, правка же проверяет — под основные здания на песке
    /// сначала кладётся фундамент.
    /// </summary>
    public static uint Place(Sim sim, string type, int x, int y, uint player)
    {
        var spec = Tables.Buildings[type];
        if (spec.Pipe is null && !spec.Defense)
            for (var ty = y; ty < y + (int)spec.Height; ty++)
                for (var tx = x; tx < x + (int)spec.Width; tx++)
                    if (sim.Land!.TerrainAt(tx, ty) == TerrainType.Sand)
                        sim.Lay(PaveRules.Foundation, tx, ty, player);
        return Scene.Place(sim, type, x, y, player);
    }

    /// <summary>Крутит партию, пока не появится выстрел (untilShot).</summary>
    public static void UntilShot(Sim sim)
    {
        for (var i = 0; i < 100 && sim.Count(Names.Shot.Name) == 0; i++)
            sim.Advance();
        Assert.True(sim.Count(Names.Shot.Name) > 0, "выстрела нет");
    }

    public static double Health(Sim sim, uint entity) => sim.HealthOf(entity);

    /// <summary>Цель первой турели юнита (gunTarget).</summary>
    public static uint GunTarget(Sim sim, uint entity) => sim.TargetOf(sim.TurretsOf(entity)[0]);

    public static UnitSpec Unit(string type) => Tables.Units[type];

    public static WeaponSpec Weapon(string type) => Tables.All.Weapons[type];

    /// <summary>Прочность здания вида (buildingHp).</summary>
    public static double BuildingHp(string type) => Tables.Buildings[type].Hp ?? Tables.Buildings[type].Cost * 2;

    /// <summary>Поворот юнита (Unit.facing).</summary>
    public static double Facing(Sim sim, uint entity) => sim.Num(entity, "Facing.angle");

    /// <summary>Угол в −π..π (wrap).</summary>
    public static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor(angle / (Math.PI * 2) + 0.5);

    /// <summary>Вид оружия выстрела.</summary>
    public static string? WeaponOf(Sim sim, uint shot) =>
        sim.Host.Content.Protos[(int)sim.Num(shot, "Proto.id")] is { Section: "weapons" } proto ? proto.Name : null;
}
