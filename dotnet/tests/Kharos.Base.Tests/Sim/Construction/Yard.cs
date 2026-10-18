using Base.Shared;
using Base.Shared.Orders;
using Kharos.Engine.Terrain;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Construction;

/// <summary>
/// Сцены стройки (start в construction.test.ts и paving.test.ts): база игрока 1 на просторной скале, строители рядом,
/// место под здание. Размер карты — полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class Yard
{
    /// <summary>Стороны скалы без обрывов под тестовую базу (PLATEAU в TS — 12; шире — с запасом под соседние площадки).</summary>
    public const int Width = 14, Height = 12;

    /// <summary>База: партия, главное здание, строители, место под генератор (x + 7, y + 4), левый верхний угол скалы.</summary>
    public sealed record Base(Sim Sim, uint Core, List<uint> Builders, (int X, int Y) Site, (int X, int Y) At) : IDisposable
    {
        public void Dispose() => Sim.Dispose();
    }

    /// <summary>Партия test с местностью TS, без дерева технологий (если rules не заданы).</summary>
    public static Sim Create(object? rules = null, int size = 512) => Scene.Create(size, rules ?? new { techTree = false });

    /// <summary>Скала без подножий обрыва в прямоугольнике.</summary>
    public static bool Rock(Sim sim, int x, int y, int w, int h)
    {
        var land = sim.Land!;
        for (var ty = y; ty < y + h; ty++)
            for (var tx = x; tx < x + w; tx++)
                if (land.TerrainAt(tx, ty) != TerrainType.Rock || land.IsCliffFoot(tx + 0.5, ty + 0.5))
                    return false;
        return true;
    }

    /// <summary>Скала (без проверки обрывов) в прямоугольнике — rock() в zones.test.ts.</summary>
    public static bool Stone(Sim sim, int x, int y, int w, int h)
    {
        var land = sim.Land!;
        for (var ty = y; ty < y + h; ty++)
            for (var tx = x; tx < x + w; tx++)
                if (land.TerrainAt(tx, ty) != TerrainType.Rock)
                    return false;
        return true;
    }

    /// <summary>
    /// Игрок 1 развернул главное здание на просторной скале; рядом стартовый набор и два строителя. fits — что ещё
    /// нужно тесту от места вокруг левого верхнего угла скалы.
    /// </summary>
    public static Base Start(Func<Sim, int, int, bool>? fits = null, object? rules = null)
    {
        var sim = Create(rules);
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Rock(sim, x, y, Width, Height) || !(fits?.Invoke(sim, x, y) ?? true))
                    continue;
                sim.Edit(new EditStartOrder { Player = 1, X = x + 2, Y = y + 2 });
                Builders.AddBuilders(sim, 1, x + 2, y + 2);
                sim.Send(1, new DeployOrder { Unit = Scene.UnitsOf(sim, "mcv")[0] });
                sim.Seconds(6);
                return new Base(sim, Scene.Core(sim), Scene.UnitsOf(sim, "builder"), (x + 7, y + 4), (x, y));
            }
        sim.Dispose();
        throw new InvalidOperationException("В мире не нашлось места под базу");
    }

    /// <summary>
    /// Ближайший к (x, y) квадрат side×side сплошной местности terrain без подножий обрыва (find в paving.test.ts; у
    /// подножия не строят и не кладут покрытие, а местность .NET другая — исключаем явно).
    /// </summary>
    public static (int X, int Y) Find(Sim sim, TerrainType terrain, int x, int y, int side)
    {
        var land = sim.Land!;
        for (var radius = 0; radius < 200; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                        continue;
                    var all = true;
                    for (var ty = 0; ty < side && all; ty++)
                        for (var tx = 0; tx < side && all; tx++)
                            all = land.TerrainAt(x + dx + tx, y + dy + ty) == terrain && !land.IsCliffFoot(x + dx + tx + 0.5, y + dy + ty + 0.5);
                    if (all)
                        return (x + dx, y + dy);
                }
        throw new InvalidOperationException("Не нашлось местности");
    }

    /// <summary>Первый тайл от середины карты по спирали, где выполняется fits (near в earthworks.test.ts).</summary>
    public static (int X, int Y) Near(Func<int, int, bool> fits)
    {
        for (var radius = 0; radius < 400; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && fits(dx, dy))
                        return (dx, dy);
        throw new InvalidOperationException("Не нашлось места");
    }

    /// <summary>Вид здания или стройки сущности (Proto «buildings/вид»).</summary>
    public static string? TypeOf(Sim sim, uint entity) =>
        sim.Has(entity, Names.Proto.Name) && sim.Host.Content.Protos[(int)sim.Num(entity, "Proto.id")] is { Section: "buildings" } proto ? proto.Name : null;

    /// <summary>Стройки вида type.</summary>
    public static List<uint> SitesOf(Sim sim, string type) => [.. sim.With(Names.Site.Name).Where(entity => TypeOf(sim, entity) == type)];

    /// <summary>Стройка (Site) жива и есть у сущности.</summary>
    public static double Progress(Sim sim, uint site) => sim.Num(site, "Site.progress");

    /// <summary>Тиков работы одного строителя на здание (siteTicks).</summary>
    public static int SiteTicks(string type) => (int)Math.Floor(Tables.Buildings[type].Cost / 20 / Sim.Dt + 0.5);

    /// <summary>Кредиты и доли игрока ровно столько (запись в счёт напрямую).</summary>
    public static void Wallet(Sim sim, uint player, double credits, double earned = 0)
    {
        var account = sim.Wallet(player);
        sim.Set(account, "Player.credits", credits);
        sim.Set(account, "Player.earned", earned);
    }

    /// <summary>Темы игрока (Science.done) — номера Proto «research/тема».</summary>
    public static void Research(Sim sim, uint player, params string[] topics) =>
        sim.Set(sim.Wallet(player), "Science.done", Scene.Ids([.. topics.Select(topic => Scene.Proto(sim, "research/" + topic))]));
}
