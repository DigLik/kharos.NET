using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Energy;

/// <summary>
/// Сцены энергии (start, put, field из income.test.ts, reactor.test.ts, lithium.test.ts). Размер карты — полусторона:
/// TS size 1024 — здесь 512. Трубы без стройки — правки edit.connect и edit.connectAll раздела Pipes (connectBuilding и connectAll в piping.ts).
/// </summary>
internal static class Grid
{
    /// <summary>Скала под базу: высота (PLATEAU) и ширина (WIDE) — тянется вправо дальше зоны главного здания.</summary>
    public const int Plateau = 14, Wide = 26;

    /// <summary>Партия test с местностью TS, без дерева технологий (если rules не заданы).</summary>
    public static Sim Create(object? rules = null, int size = 512) => Scene.Create(size, rules ?? new { techTree = false });

    /// <summary>Партия на пустой земле (generator blank).</summary>
    public static Sim Blank(int size = 128, object? rules = null)
    {
        Scene.SkipUnlessAvailable();
        return Sim.Create(new SimOptions { Size = size, Land = new { blank = true }, Rules = rules });
    }

    /// <summary>Игрок 1 развернул главное здание в углу просторной скалы; (X, Y) — левый верхний тайл скалы (start).</summary>
    public static (Sim Sim, int X, int Y) Start(object? rules = null)
    {
        var sim = Create(rules);
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Scene.Rock(sim, x, y, Wide, Plateau))
                    continue;
                sim.Edit(new EditStartOrder { Player = 1, X = x + 2, Y = y + 2 });
                Builders.AddBuilders(sim, 1, x + 2, y + 2);
                sim.Send(1, new DeployOrder { Unit = Scene.UnitsOf(sim, "mcv")[0] });
                sim.Seconds(3.1);
                return (sim, x, y);
            }
        sim.Dispose();
        throw new InvalidOperationException("В мире не нашлось места под базу");
    }

    /// <summary>
    /// Левый верхний тайл ровного поля width×height у начала мира (field). В TS — проходимого; здесь — скалы без подножий
    /// обрыва: правка здания проверяет место, а основные здания встают только на скалу.
    /// </summary>
    public static (int X, int Y) Field(Sim sim, int width, int height)
    {
        for (var y = -60; y < 60; y++)
            for (var x = -60; x < 60; x++)
                if (Scene.Rock(sim, x, y, width, height))
                    return (x, y);
        throw new InvalidOperationException("В мире не нашлось ровного поля");
    }

    /// <summary>Готовое здание игрока 1 в обход стройки, подключённое трубой к ближайшей сети (put).</summary>
    public static uint Put(Sim sim, string type, int x, int y, uint player = 1)
    {
        var building = Place(sim, type, x, y, player);
        Connect(sim, building);
        return building;
    }

    /// <summary>
    /// Готовое здание в обход стройки (placeBuilding). На песке (пустая земля, generator blank) под ним сначала кладётся
    /// фундамент: в TS placeBuilding места не проверяет, а правка здания — проверяет.
    /// </summary>
    public static uint Place(Sim sim, string type, int x, int y, uint player = 1)
    {
        var spec = Tables.Buildings[type];
        if (spec.Pipe is null && !spec.Defense)
            for (var ty = y; ty < y + (int)spec.Height; ty++)
                for (var tx = x; tx < x + (int)spec.Width; tx++)
                    if (sim.Land!.TerrainAt(tx, ty) == TerrainType.Sand)
                        sim.Lay(PaveRules.Foundation, tx, ty, player);
        return Scene.Place(sim, type, x, y, player);
    }

    /// <summary>Главное здание игрока 1.</summary>
    public static uint Core(Sim sim) => Scene.Core(sim);

    /// <summary>Вид здания или стройки сущности.</summary>
    public static string? TypeOf(Sim sim, uint entity) =>
        sim.Has(entity, Names.Proto.Name) && sim.Host.Content.Protos[(int)sim.Num(entity, "Proto.id")] is { Section: "buildings" } proto ? proto.Name : null;

    /// <summary>Подключает готовое здание к ближайшей другой своей сети готовой трубой (connectBuilding, правка edit.connect).</summary>
    public static List<uint> Connect(Sim sim, uint building) => sim.ConnectBuilding(building);

    /// <summary>Соединяет трубами все сети игрока с первой (connectAll, правки edit.connectAll).</summary>
    public static void ConnectAll(Sim sim, uint player) => sim.ConnectAll(player);

    /// <summary>Цена здания.</summary>
    public static double Cost(string type) => Tables.Buildings[type].Cost;

    /// <summary>Скала без подножий обрыва в прямоугольнике.</summary>
    public static bool Rock(Sim sim, int x, int y, int w, int h) => Scene.Rock(sim, x, y, w, h);
}
