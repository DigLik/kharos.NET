using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Logistics;

/// <summary>
/// Помощники сцен логистики (то, что тесты TS берут из sim-ts: placeBuilding, spawnUnit, addCredits). Размер карты —
/// полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class LogisticsScene
{
    /// <summary>Длина тика.</summary>
    public const double Tick = Sim.Dt;

    /// <summary>Партия на местности TS по умолчанию.</summary>
    public static Sim Create(int size = 512, object? rules = null) => Scene.Create(size, rules);

    /// <summary>Пустая ровная карта (generator blank).</summary>
    public static Sim Blank(int size = 128)
    {
        Scene.SkipUnlessAvailable();
        return Sim.Create(new SimOptions { Size = size, Land = new { blank = true } });
    }

    /// <summary>
    /// Готовое здание без стройки (placeBuilding). Правка здания место проверяет, а в TS — нет: на песке под основанием
    /// сначала кладётся фундамент (трубам и обороне он не нужен).
    /// </summary>
    public static uint Place(Sim sim, string type, int x, int y, uint player = 1)
    {
        var spec = Tables.Buildings[type];
        if (spec.Pipe is null && !spec.Defense)
            for (var ty = y; ty < y + (int)spec.Height; ty++)
                for (var tx = x; tx < x + (int)spec.Width; tx++)
                    if (sim.Land!.TerrainAt(tx, ty) == TerrainType.Sand)
                        sim.Lay(PaveRules.Foundation, tx, ty, player);
        return sim.PutBuilding(type, x, y, player);
    }

    /// <summary>Юнит в центре тайла (spawnUnit).</summary>
    public static uint Spawn(Sim sim, string type, uint player, int x, int y) => Scene.Spawn(sim, type, player, x, y);

    /// <summary>Скала без подножий обрыва под прямоугольником.</summary>
    public static bool Rock(Sim sim, int x, int y, int w, int h) => Scene.Rock(sim, x, y, w, h);

    /// <summary>Ширина вида здания.</summary>
    public static int Width(string type) => (int)Tables.Buildings[type].Width;

    /// <summary>Сколько груза на складе.</summary>
    public static double Amount(Sim sim, uint entity, string good) => sim.Stock(entity, good);

    /// <summary>
    /// Главное здание, две электростанции и здания из списка в ряд на скале, с зазором gap (base в request/routes/drops):
    /// каждое — своя сеть; под ними — место для грузовика (truck — поставить его в (x + 11, y + 4)).
    /// </summary>
    public static (Sim Sim, List<uint> Buildings, uint Truck, int X, int Y) Row(string[] types, bool truck = true, int gap = 4, object? rules = null)
    {
        var sim = Create(512, rules);
        sim.SetCredits(1, 10000);
        var layout = new List<(string Type, int Dx)> { ("command", 0), ("generator", 4), ("generator", 7) };
        var at = 10;
        foreach (var type in types)
        {
            layout.Add((type, at));
            at += Width(type) + gap;
        }
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                if (!layout.All(item => Rock(sim, x + item.Dx, y, Width(item.Type), (int)Tables.Buildings[item.Type].Height)) || !Rock(sim, x + 10, y + 4, 3, 3))
                    continue;
                if (!layout.All(item => Scene.CanPlace(sim, item.Type, x + item.Dx, y)) || !Scene.CanPlace(sim, "command", x + 10, y + 4))
                    continue;
                var buildings = layout.Select(item => sim.PutBuilding(item.Type, x + item.Dx, y, 1)).ToList();
                var unit = truck ? Spawn(sim, "truck", 1, x + 11, y + 4) : 0;
                return (sim, buildings.Skip(3).ToList(), unit, x, y);
            }
        throw new InvalidOperationException("Не нашлось места");
    }

    /// <summary>Крутит, пока условие не выполнится; не дождались — провал (until в тестах TS).</summary>
    public static void Until(Sim sim, Func<bool> done, double limit = 120) => Assert.True(sim.Until(done, limit), "Не дождались");
}
