using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Base.Tests.Kernel;

/// <summary>
/// Помощники сцен ядра (то, что тесты TS берут прямо из sim-ts: spawnUnit, placeBuilding, creditsOf, canDeploy…). Размер
/// карты — полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class Scene
{
    /// <summary>Местность TS по умолчанию (DEFAULT_SETTINGS.generator) — запись lands.json default.</summary>
    public const string Land = "default";

    public static void SkipUnlessAvailable() => Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is not null, Sim.Unavailable(Sim.DefaultMode) ?? "");

    /// <summary>Партия test с местностью TS: size — полусторона карты.</summary>
    public static Sim Create(int size = 512, object? rules = null, bool? fog = null)
    {
        SkipUnlessAvailable();
        return Sim.Create(new SimOptions { Size = size, Land = Land, Rules = rules, Fog = fog });
    }

    /// <summary>Номер вида «раздел/имя».</summary>
    public static int Proto(Sim sim, string path) => sim.Host.Content.FindProto(path)?.Id ?? -1;

    /// <summary>Юниты вида type по возрастанию номеров (unitsOf).</summary>
    public static List<uint> UnitsOf(Sim sim, string type)
    {
        var id = Proto(sim, "units/" + type);
        return [.. sim.With(Names.Unit.Name, Names.Proto.Name).Where(entity => sim.Num(entity, Names.Proto.Name, Names.Proto.Id) == id)];
    }

    public static int Units(Sim sim, string type) => UnitsOf(sim, type).Count;

    /// <summary>Вид юнита сущности.</summary>
    public static string? TypeOf(Sim sim, uint entity) =>
        sim.Has(entity, Names.Proto.Name) ? sim.Host.Content.Protos[(int)sim.Num(entity, Names.Proto.Name, Names.Proto.Id)].Name : null;

    /// <summary>Главное здание игрока 1 (core); нет — 0.</summary>
    public static uint Core(Sim sim, uint player = 1)
    {
        var id = Proto(sim, "buildings/command");
        return sim.Of(player, Names.Building.Name, Names.Proto.Name).FirstOrDefault(entity => sim.Num(entity, Names.Proto.Name, Names.Proto.Id) == id);
    }

    /// <summary>Здание на тайле (occupancy.at); 0 — свободно.</summary>
    public static uint Occupant(Sim sim, int x, int y) => sim.Host.Tools.Navigation!.Occupancy.At(x, y);

    /// <summary>Юнит в центре тайла (x, y) без проверок места (spawnUnit).</summary>
    public static uint Spawn(Sim sim, string type, uint player, int x, int y) => sim.PutUnit(type, player, x + 0.5, y + 0.5);

    /// <summary>Готовое здание (placeBuilding): правка edit.building.</summary>
    public static uint Place(Sim sim, string type, int x, int y, uint player) => sim.PutBuilding(type, x, y, player);

    // ===== Запросы ядра =====

    public static bool CanPlace(Sim sim, string type, int x, int y) => sim.Query<bool>(KernelQueries.CanPlace, new PlaceArgs { Type = type, X = x, Y = y });

    public static bool CanDeploy(Sim sim, uint player, uint unit) => sim.Query<bool>(KernelQueries.CanDeploy, new DeployArgs { Player = player, Unit = unit });

    public static bool CanDeployAt(Sim sim, uint player, uint unit, int x, int y) =>
        sim.Query<bool>(KernelQueries.CanDeploy, new DeployArgs { Player = player, Unit = unit, At = true, X = x, Y = y });

    public static string[] Queue(Sim sim, uint producer) => sim.Query<NameList>(KernelQueries.Queue, new EntityArgs { Entity = producer }).Items;

    public static string[] Producible(Sim sim, uint producer) => sim.Query<NameList>(KernelQueries.Producible, new EntityArgs { Entity = producer }).Items;

    public static string[] Rewards(Sim sim, uint player) => sim.Query<NameList>(KernelQueries.Rewards, new PlayerArgs { Player = player }).Items;

    public static double Army(Sim sim, uint player) => sim.Query<double>(KernelQueries.Army, new PlayerArgs { Player = player });

    public static bool OverLimit(Sim sim, uint player, string unit) => sim.Query<bool>(KernelQueries.OverLimit, new UnitArgs { Player = player, Unit = unit });

    /// <summary>Тема для юнита; нечего — null.</summary>
    public static string? MissingResearch(Sim sim, uint player, string unit) =>
        sim.Query<string>(KernelQueries.MissingResearch, new UnitArgs { Player = player, Unit = unit }) is { Length: > 0 } topic ? topic : null;

    public static int BuildTicks(Sim sim, string unit) => sim.Query<int>(KernelQueries.BuildTicks, new UnitArgs { Unit = unit });

    // ===== Место =====

    /// <summary>Скала без подножия обрыва целиком под прямоугольником (быстрая проверка до запроса canPlace).</summary>
    public static bool Rock(Sim sim, int x, int y, int w, int h)
    {
        var land = sim.Land!;
        for (var tileY = y; tileY < y + h; tileY++)
            for (var tileX = x; tileX < x + w; tileX++)
                if (land.TerrainAt(tileX, tileY) != TerrainType.Rock || land.IsCliffFoot(tileX + 0.5, tileY + 0.5))
                    return false;
        return true;
    }

    /// <summary>Нет подножий обрыва в поле вокруг площадки главного здания (clearOfCliffs).</summary>
    public static bool ClearOfCliffs(Sim sim, int x, int y)
    {
        for (var tileY = y - 6; tileY < y + 9; tileY++)
            for (var tileX = x - 6; tileX < x + 9; tileX++)
                if (sim.Land!.IsCliffFoot(tileX + 0.5, tileY + 0.5))
                    return false;
        return true;
    }

    /// <summary>Здание вида type встанет левым верхним тайлом в (x, y): сначала скала, потом запрос мода.</summary>
    public static bool Fits(Sim sim, string type, int x, int y, int w, int h) => Rock(sim, x, y, w, h) && CanPlace(sim, type, x, y);

    /// <summary>MCV игрока 1 на скале, где он может развернуться, и стартовый набор вокруг (start в core.test.ts).</summary>
    public static (Sim Sim, uint Mcv, (int X, int Y) Site) Start()
    {
        var sim = Create();
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Fits(sim, "command", x, y, 3, 3) || !ClearOfCliffs(sim, x, y))
                    continue;
                sim.Edit(new EditStartOrder { Player = 1, X = x + 1, Y = y + 1 });
                return (sim, UnitsOf(sim, "mcv")[0], (x, y));
            }
        throw new InvalidOperationException("В мире не нашлось скалы");
    }

    /// <summary>Стартовый набор с развёрнутой базой (deployed в core.test.ts).</summary>
    public static (Sim Sim, uint Base, (int X, int Y) Site) Deployed()
    {
        var (sim, mcv, site) = Start();
        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Seconds(3.1);
        return (sim, Core(sim), site);
    }

    /// <summary>Сущность счёта игрока.</summary>
    public static uint Account(Sim sim, uint player) => sim.Wallet(player);

    /// <summary>Номера i32 подряд.</summary>
    public static byte[] Ids(params int[] ids)
    {
        var bytes = new byte[ids.Length * 4];
        for (var i = 0; i < ids.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), ids[i]);
        return bytes;
    }
}

/// <summary>Таблицы base из mods/base/data (то же, что читает мод): цены, время, лимиты.</summary>
internal static class Data
{
    private static readonly Lazy<global::Base.Shared.Data.BaseTables> Tables = new(() => global::Base.Shared.Data.BaseTables.Load(name =>
    {
        var path = Path.Combine(Sim.ModDir, "data", name + ".json");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }));

    public static global::Base.Shared.Data.BaseTables All => Tables.Value;

    public static Dictionary<string, global::Base.Shared.Data.UnitSpec> Units => Tables.Value.Units;

    public static Dictionary<string, global::Base.Shared.Data.BuildingSpec> Buildings => Tables.Value.Buildings;
}
