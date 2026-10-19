using Base.Shared.Data;
using Kharos.Base.Tests.Kernel;
using Kharos.Base.Tests.Researching;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Missiles;

/// <summary>База игрока в ряд под одной трубой: главное здание, две электростанции, здание kind и хранилища (base в silo.test.ts и interceptor.test.ts).</summary>
internal sealed record Camp(Sim Sim, int X, int Y, uint Core, uint Building, string[] Stores, List<uint> Shelves)
{
    /// <summary>Хранилище груза (shelf).</summary>
    public uint Shelf(string good) => Shelves[Array.IndexOf(Stores, Lab.StoreFor(good))];

    /// <summary>Кладёт готовое в хранилища сети: ровно столько (stash).</summary>
    public void Stash(params (string Good, double Amount)[] items)
    {
        foreach (var (good, amount) in items)
            Sim.Set(Shelf(good), "Stock." + good, amount);
    }

    /// <summary>Сколько груза лежит в хранилище его вида (stock).</summary>
    public double Stock(string good) => Sim.Stock(Shelf(good), good);
}

/// <summary>Сцены ракет. Размер карты — полусторона: TS size 1024 — здесь 512.</summary>
internal static class Pit
{
    /// <summary>Длина тика (TICK).</summary>
    public const double Tick = Sim.Dt;

    public static readonly object Rules = new { protection = 0, techTree = false };

    /// <summary>Боеголовка шахты (BUILDINGS.silo.warhead) и перехватчик (BUILDINGS.interceptor.intercept).</summary>
    public static WarheadSpec Warhead => Tables.Buildings["silo"].Warhead!;

    public static InterceptSpec Intercept => Tables.Buildings["interceptor"].Intercept!;

    /// <summary>Партия на местности TS с игроками 1…3 (createSim с правилами без защиты новичка и дерева технологий).</summary>
    public static Sim Create(object? rules = null, int size = 512)
    {
        Scene.SkipUnlessAvailable();
        return Sim.Create(new SimOptions { Size = size, Land = Scene.Land, Rules = rules ?? Rules, Players = [1, 2, 3] });
    }

    /// <summary>Левый верхний тайл ближайшего к (fromX, fromY) места под здание вида type (spot).</summary>
    public static (int X, int Y) Spot(Sim sim, int fromX, int fromY, string type = "generator")
    {
        var spec = Tables.Buildings[type];
        var (w, h) = ((int)spec.Width, (int)spec.Height);
        for (var distance = 0; distance < 200; distance++)
            for (var dy = -distance; dy <= distance; dy++)
                for (var dx = -distance; dx <= distance; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == distance && Scene.Fits(sim, type, fromX + dx, fromY + dy, w, h))
                        return (fromX + dx, fromY + dy);
        throw new InvalidOperationException("Не нашлось места");
    }

    /// <summary>
    /// База игрока player как можно ближе к (nearX, nearY): главное здание, две электростанции, kind и хранилища stores в
    /// ряд с зазором в тайл, над рядом труба. Игроку даётся 10000 кредитов.
    /// </summary>
    public static Camp Base(Sim sim, uint player, string kind, int nearX, int nearY, string[]? stores = null)
    {
        stores ??= Lab.Stores;
        sim.SetCredits(player, 10000);
        string[] layout = ["command", "generator", "generator", kind, .. stores];
        var offsets = new List<int>();
        var width = 0;
        foreach (var type in layout)
        {
            offsets.Add(width);
            width += Lab.Width(type) + 1;
        }
        for (var distance = 0; distance < 300; distance++)
            for (var dy = -distance; dy <= distance; dy++)
                for (var dx = -distance; dx <= distance; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != distance)
                        continue;
                    int x = nearX + dx, y = nearY + dy;
                    // Скала под зданиями и трубой — дёшево, без запросов; остальное решает canPlace.
                    if (!Scene.Rock(sim, x, y - 1, width, 1) || !layout.Select((type, i) => Scene.Rock(sim, x + offsets[i], y, Lab.Width(type), Lab.Height(type))).All(ok => ok))
                        continue;
                    if (!layout.Select((type, i) => Scene.CanPlace(sim, type, x + offsets[i], y)).All(ok => ok) || !Enumerable.Range(0, width).All(shift => Scene.CanPlace(sim, "pipe", x + shift, y - 1)))
                        continue;
                    var buildings = layout.Select((type, i) => sim.PutBuilding(type, x + offsets[i], y, player)).ToList();
                    for (var shift = 0; shift < width; shift++)
                        sim.PutBuilding("pipe", x + shift, y - 1, player);
                    return new Camp(sim, x, y, buildings[0], buildings[3], stores, buildings.GetRange(4, stores.Length));
                }
        throw new InvalidOperationException("Не нашлось места под базу");
    }

    /// <summary>Здание или юнит погибли: прочности больше нет (world.has(entity, Health) === false).</summary>
    public static bool Gone(Sim sim, uint entity) => !sim.Has(entity, "Health");

    /// <summary>Прочность долей от полной; погибшего — ноль (world.get(entity, Health)?.value ?? 0).</summary>
    public static double Health(Sim sim, uint entity) => sim.Has(entity, "Health") ? sim.HealthOf(entity) : 0;
}
