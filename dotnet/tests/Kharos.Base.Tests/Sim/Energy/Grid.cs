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
/// TS size 1024 — здесь 512. Трубы без стройки — перенос connectBuilding и connectAll (piping.ts) на стороне теста.
/// </summary>
internal static class Grid
{
    /// <summary>Скала под базу: высота (PLATEAU) и ширина (WIDE) — тянется вправо дальше зоны главного здания.</summary>
    public const int Plateau = 14, Wide = 26;

    /// <summary>Дальше скольких тайлов трассу трубы не ищут (ROUTE_REACH).</summary>
    private const int RouteReach = 16;

    private static readonly (int Dx, int Dy)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

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

    /// <summary>Подключает готовое здание к ближайшей другой своей сети готовой трубой (connectBuilding).</summary>
    public static List<uint> Connect(Sim sim, uint building)
    {
        var player = sim.OwnerOf(building);
        var type = TypeOf(sim, building);
        if (player == 0 || type is null || Tables.Buildings[type].Pipe is not null || Tables.Buildings[type].Isolated)
            return [];
        var zones = sim.Zones(player);
        var own = zones.FirstOrDefault(zone => zone.Buildings.Contains(building));
        var (x, y) = sim.At(building);
        var spec = Tables.Buildings[type];
        var tiles = Route(sim, player, zones, (int)x, (int)y, (int)spec.Width, (int)spec.Height, zone => zone != own) ?? [];
        return [.. tiles.Select(tile => Scene.Place(sim, "pipe", tile.X, tile.Y, player))];
    }

    /// <summary>Соединяет трубами все сети игрока с первой (connectAll).</summary>
    public static void ConnectAll(Sim sim, uint player)
    {
        for (var joined = true; joined;)
        {
            joined = false;
            var zones = sim.Zones(player);
            if (zones.Length < 2)
                return;
            var main = zones[0];
            foreach (var zone in zones.Skip(1))
            {
                var entity = zone.Buildings.FirstOrDefault(item => Tables.Buildings[TypeOf(sim, item)!].Pipe is null, zone.Buildings[0]);
                var spec = Tables.Buildings[TypeOf(sim, entity)!];
                var (x, y) = sim.At(entity);
                var tiles = Route(sim, player, zones, (int)x, (int)y, (int)spec.Width, (int)spec.Height, other => other == main);
                if (tiles is not { Count: > 0 })
                    continue;
                foreach (var tile in tiles)
                    Scene.Place(sim, "pipe", tile.X, tile.Y, player);
                joined = true;
                break;
            }
        }
    }

    /// <summary>
    /// Трасса наземной трубы от основания до узла сети игрока (pipeRoute): тайлы от основания к сети; пусто — труба уже
    /// подходит; null — сети ближе ROUTE_REACH нет.
    /// </summary>
    private static List<(int X, int Y)>? Route(Sim sim, uint player, ZoneInfo[] zones, int x, int y, int width, int height, Func<ZoneInfo, bool> joins)
    {
        bool Inside(int tx, int ty) => tx >= x && tx < x + width && ty >= y && ty < y + height;
        // Заложенные свои трубы: к ним подключаются, как к готовым.
        var sites = new HashSet<(int, int)>();
        foreach (var site in sim.Of(player, Names.Site.Name, Names.Position.Name))
            if (sim.Num(site, "Site.demolish") == 0 && TypeOf(sim, site) is { } kind && Tables.Buildings[kind].Pipe is not null)
                sites.Add(((int)sim.At(site).X, (int)sim.At(site).Y));
        bool Node(int tx, int ty, bool pipeOnly)
        {
            if (Inside(tx, ty))
                return false;
            if (sites.Contains((tx, ty)))
                return false;
            var other = sim.OccupantAt(tx, ty);
            if (other == 0 || sim.OwnerOf(other) != player || sim.Has(other, Names.Site.Name))
                return false;
            if (pipeOnly && Tables.Buildings[TypeOf(sim, other)!].Pipe is null)
                return false;
            var zone = zones.FirstOrDefault(item => item.Buildings.Contains(other));
            return zone is not null && joins(zone);
        }
        for (var tx = x; tx < x + width; tx++)
            if (Node(tx, y - 1, true) || Node(tx, y + height, true))
                return [];
        for (var ty = y; ty < y + height; ty++)
            if (Node(x - 1, ty, true) || Node(x + width, ty, true))
                return [];

        bool Free(int tx, int ty) => !Inside(tx, ty) && !sites.Contains((tx, ty)) && Scene.CanPlace(sim, "pipe", tx, ty);
        var previous = new Dictionary<(int, int), int>();
        var queue = new List<(int X, int Y, int Length)>();
        void Begin(int tx, int ty)
        {
            if (previous.ContainsKey((tx, ty)) || !Free(tx, ty))
                return;
            previous[(tx, ty)] = -1;
            queue.Add((tx, ty, 1));
        }
        for (var tx = x; tx < x + width; tx++)
        {
            Begin(tx, y - 1);
            Begin(tx, y + height);
        }
        for (var ty = y; ty < y + height; ty++)
        {
            Begin(x - 1, ty);
            Begin(x + width, ty);
        }
        for (var at = 0; at < queue.Count; at++)
        {
            var (tx, ty, length) = queue[at];
            if (Sides.Any(side => Node(tx + side.Dx, ty + side.Dy, false)))
            {
                // Назад по цепочке — от сети к основанию; трасса нужна в обратном порядке.
                var back = new List<(int X, int Y)>();
                for (var i = at; i >= 0; i = previous[(queue[i].X, queue[i].Y)])
                    back.Add((queue[i].X, queue[i].Y));
                back.Reverse();
                return back;
            }
            if (length >= RouteReach)
                continue;
            foreach (var (dx, dy) in Sides)
            {
                if (previous.ContainsKey((tx + dx, ty + dy)) || !Free(tx + dx, ty + dy))
                    continue;
                previous[(tx + dx, ty + dy)] = at;
                queue.Add((tx + dx, ty + dy, length + 1));
            }
        }
        return null;
    }

    /// <summary>Цена здания.</summary>
    public static double Cost(string type) => Tables.Buildings[type].Cost;

    /// <summary>Скала без подножий обрыва в прямоугольнике.</summary>
    public static bool Rock(Sim sim, int x, int y, int w, int h) => Scene.Rock(sim, x, y, w, h);
}
