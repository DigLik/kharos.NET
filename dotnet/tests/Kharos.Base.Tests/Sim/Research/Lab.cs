using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Kharos.Plugins.Game;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Researching;

/// <summary>
/// Сцены исследований и уровней (row, base, plan, learn из research.test.ts и upgrades.test.ts). Размер карты —
/// полусторона: TS size 1024 — здесь 512.
/// </summary>
internal static class Lab
{
    /// <summary>Длина тика (TICK).</summary>
    public const double Tick = Sim.Dt;

    /// <summary>Хранилища по порядку buildings.json (STORES).</summary>
    public static readonly string[] Stores = [.. Tables.Buildings.Where(pair => pair.Value.Stores).Select(pair => pair.Key)];

    /// <summary>Хранилище, которое принимает этот груз (storeFor).</summary>
    public static string StoreFor(string good) => Stores.First(type => Tables.Buildings[type].Accepts.Contains(good));

    public static int Width(string type) => (int)Tables.Buildings[type].Width;

    public static int Height(string type) => (int)Tables.Buildings[type].Height;

    /// <summary>Партия на местности TS (createSim с правилами rules; без них дерево технологий включено).</summary>
    public static Sim Create(object? rules = null) => Scene.Create(512, rules);

    /// <summary>
    /// Ставит здания в ряд с зазором в тайл под одной трубой над рядом (row); null — ряда нет. at — где ряд начинается,
    /// если задано; иначе — первое место у начала мира. Правка здания место проверяет сама, поиск сначала смотрит на скалу.
    /// </summary>
    public static List<uint>? Row(Sim sim, string[] layout, (int X, int Y)? at = null)
    {
        var offsets = new List<int>();
        var width = 0;
        foreach (var type in layout)
        {
            offsets.Add(width);
            width += Width(type) + 1;
        }
        bool Fits(int x, int y)
        {
            for (var i = 0; i < layout.Length; i++)
                if (!Scene.CanPlace(sim, layout[i], x + offsets[i], y))
                    return false;
            for (var dx = 0; dx < width; dx++)
                if (!Scene.CanPlace(sim, "pipe", x + dx, y - 1))
                    return false;
            return true;
        }
        List<uint> Place(int x, int y)
        {
            var buildings = layout.Select((type, i) => sim.PutBuilding(type, x + offsets[i], y, 1)).ToList();
            for (var dx = 0; dx < width; dx++)
                sim.PutBuilding("pipe", x + dx, y - 1, 1);
            return buildings;
        }
        if (at is { } start)
            return Fits(start.X, start.Y) ? Place(start.X, start.Y) : null;
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                // Скала под зданиями и трубой — дёшево, без запросов; остальное решает canPlace.
                if (!Scene.Rock(sim, x, y - 1, width, 1) || !layout.Select((type, i) => Scene.Rock(sim, x + offsets[i], y, Width(type), Height(type))).All(ok => ok))
                    continue;
                if (Fits(x, y))
                    return Place(x, y);
            }
        return null;
    }

    /// <summary>База игрока 1 в одной сети труб: две электростанции, здания из списка и все хранилища (base).</summary>
    public sealed record Base(Sim Sim, List<uint> Plants, List<uint> Buildings, List<uint> Shelves) : IDisposable
    {
        /// <summary>Хранилище груза (storeOf).</summary>
        public uint Store(string good) => Shelves[Array.IndexOf(Stores, StoreFor(good))];

        /// <summary>Кладёт груз в хранилища его вида (stash): ровно столько.</summary>
        public void Stash(params (string Good, double Amount)[] items)
        {
            foreach (var (good, amount) in items)
                Sim.Set(Store(good), "Stock." + good, amount);
        }

        public void Dispose() => Sim.Dispose();
    }

    /// <summary>
    /// База игрока 1 (base в research.test.ts): электростанции, здания из списка и все хранилища под одной трубой.
    /// Главного здания нет: его доход сбивал бы счёт кредитов.
    /// </summary>
    public static Base Make(string[] extra, object? rules = null, ModMode? mode = null)
    {
        var sim = mode is { } exact ? Sim.Create(new SimOptions { Size = 512, Land = Scene.Land, Rules = rules, Mode = exact }) : Create(rules);
        sim.SetCredits(1, 10000);
        var buildings = Row(sim, ["generator", "generator", .. extra, .. Stores]) ?? throw new InvalidOperationException("Не нашлось места под базу");
        return new Base(sim, buildings.GetRange(0, 2), buildings.GetRange(2, extra.Length), buildings.GetRange(2 + extra.Length, Stores.Length));
    }

    /// <summary>Тема в план (front — в начало) и тик (plan).</summary>
    public static void Plan(Sim sim, string topic, bool front = false)
    {
        sim.Send(1, new ResearchOrder { Topic = topic, Front = front });
        sim.Advance();
    }

    /// <summary>Кредиты и доля дохода игрока ровно такие (world.set(entity, Player, { credits, earned })).</summary>
    public static void SetPurse(Sim sim, uint player, double credits, double earned)
    {
        var wallet = sim.Wallet(player);
        sim.Set(wallet, "Player.credits", credits);
        sim.Set(wallet, "Player.earned", earned);
    }

    /// <summary>Склад сущности — только этот груз, остальное — ноль (inventory.items = { good: amount }).</summary>
    public static void Only(Sim sim, uint entity, string good, double amount)
    {
        foreach (var name in Tables.All.Goods)
            sim.Set(entity, "Stock." + name, name == good ? amount : 0);
    }

    /// <summary>Запас игрока в хранилищах его зон (stockOf(...).items[good]).</summary>
    public static double Stocked(Sim sim, string good) => sim.StockOf(1).GetValueOrDefault(good);

    /// <summary>toBeCloseTo(expected, digits): разница меньше половины 10^-digits.</summary>
    public static void Close(double expected, double actual, int digits = 2) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"ждали {expected} с точностью {digits} знаков, а не {actual}");

    /// <summary>Место под здание где угодно у начала мира (для зданий, которые нужны только как «есть у игрока»).</summary>
    public static uint PlaceAnywhere(Sim sim, string type)
    {
        for (var y = 30; y < 200; y++)
            for (var x = 30; x < 200; x++)
                if (Scene.Rock(sim, x, y, Width(type), Height(type)) && Scene.CanPlace(sim, type, x, y))
                    return sim.PutBuilding(type, x, y, 1);
        throw new InvalidOperationException($"Не нашлось места под {type}");
    }

    /// <summary>Есть ли у сущности компонент Upgrade.</summary>
    public static bool Upgrading(Sim sim, uint building) => sim.Has(building, Names.Upgrade.Name);
}
