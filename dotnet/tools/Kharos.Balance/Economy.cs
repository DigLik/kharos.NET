using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Base.Scenario;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел «economy» (tools/harness/economy.ts): рудник, производство и разгон. Потолок по описаниям, замер живой линии
/// добычи в симуляции, производство, заводы изделий, боеприпасы турелей и разгон настоящими командами от стартового набора.
/// </summary>
public static class Economy
{
    /// <summary>Переработка принимает руды в секунду (REFINE_RATE; Refining.Rate в моде).</summary>
    public const double RefineRate = 1.5;

    /// <summary>Сколько секунд идёт сборка изделия на единицу рецепта (ASSEMBLE_RATE).</summary>
    public const double AssembleRate = 1;

    private static BaseTables Data => Lib.Data;

    /// <summary>JS Math.round: половина — вверх.</summary>
    public static double JsRound(double value) => Math.Floor(value + 0.5);

    private static double LineCost => Data.Buildings["mine"].Cost + Data.Buildings["smelter"].Cost;

    private static double TruckCapacity => Data.Units["truck"].Inventory ?? 0;

    private static readonly string[] DepositTypes = [.. Data.Deposits.Keys];

    /// <summary>Сколько всего в рецепте (totalOf).</summary>
    public static double TotalOf(Dictionary<string, double> amounts) => amounts.Values.Sum();

    /// <summary>Сколько секунд идёт одна сборка изделия (cycleSeconds).</summary>
    public static double CycleSeconds(string product) => TotalOf(Data.Products[product].Recipe) / AssembleRate;

    /// <summary>1. Потолок по описаниям: добыча месторождения, цена продажи, окупаемость линии и сколько шахт кормит завод.</summary>
    private static void PrintLineMath()
    {
        Log("\nДобыча и продажа: потолок по описаниям, без дороги и полёта корабля");
        Table(
            ["ресурс", "добыча/с", "цена", "выручка/с", "шахт на завод", "окупаемость", "запас"],
            DepositTypes.Select(kind =>
            {
                var deposit = Data.Deposits[kind];
                var price = Data.Resources[kind].Price;
                var revenue = deposit.Rate * price;
                return (object[])[kind, Round(deposit.Rate, 1), price, Round(revenue, 1), Round(RefineRate / deposit.Rate, 1),
                    $"{Num(JsRound(LineCost / revenue))} с", $"{Num(deposit.Min)}–{Num(deposit.Max)}"];
            }));
        Log($"  линия — шахта ({Num(Data.Buildings["mine"].Cost)}) и переработка ({Num(Data.Buildings["smelter"].Cost)}) = {Num(LineCost)} кр; трубы — {Num(Data.Buildings["pipe"].Cost)} кр за тайл сверх того");
        Log($"  переработка принимает {Num(RefineRate)} руды/с: столько шахт она держит; вторая шахта на том же заводе — {Num(Data.Buildings["mine"].Cost)} кр");
        Log("  запас — сколько даёт одно месторождение");
    }

    // ===== Рудник =====

    /// <summary>Рудник в симуляции: шахта, переработка, база рядом и космопорт — всё на одной сети труб.</summary>
    private sealed record Mine(Sim Sim, uint Port, int Pipes);

    /// <summary>Ставит готовое здание у месторождения, оставляя вокруг шахты место под трубы (placeNearMine).</summary>
    public static uint PlaceNearMine(Sim sim, DepositSpot spot, string type, int x, int y, uint player = 1)
    {
        bool Ok(int tx, int ty) => Lib.PlaceableNear(sim, type, tx, ty, 1, 0) is not null && Lib.RectsApart(Lib.RectOf(type, tx, ty), Lib.RectOf("mine", spot.X, spot.Y), 2);
        return Lib.SpotNear(x, y, Ok, 14) is { } tile ? sim.PutBuilding(type, tile.X, tile.Y, player) : 0;
    }

    /// <summary>База у ближайшего к началу мира месторождения металла: всё готовое — меряется поток, а не стройка.</summary>
    private static Mine MineScene()
    {
        var sim = Lib.NewSim();
        foreach (var cell in Lib.Rings(0, 0, 6))
        {
            if (sim.DepositIn(cell.X, cell.Y) is not { Kind: "metal" } spot)
                continue;
            sim.PutBuilding("mine", spot.X, spot.Y, 1);
            var (cx, cy) = (spot.X + 1, spot.Y + 1);
            var core = PlaceNearMine(sim, spot, "command", cx + 7, cy);
            var silo = PlaceNearMine(sim, spot, "metalYard", cx + 4, cy + 6);
            var refinery = PlaceNearMine(sim, spot, "smelter", cx + 4, cy - 4);
            var port = PlaceNearMine(sim, spot, "spaceport", cx + 8, cy + 5);
            var generator = PlaceNearMine(sim, spot, "generator", cx + 6, cy - 5);
            var generator2 = PlaceNearMine(sim, spot, "generator", cx + 8, cy - 5);
            var matter = PlaceNearMine(sim, spot, "matter", cx + 3, cy - 5);
            if (core == 0 || silo == 0 || refinery == 0 || port == 0 || generator == 0 || generator2 == 0 || matter == 0)
            {
                sim.Dispose();
                continue;
            }
            sim.ConnectAll(1);
            return new Mine(sim, port, Lib.CountBuildings(sim, 1, "pipe"));
        }
        throw new InvalidOperationException("Не нашлось места под рудник у месторождения металла");
    }

    private static double MetalOf(Sim sim, uint player) => sim.StockOf(player).GetValueOrDefault("metal");

    /// <summary>
    /// Гоняет рудник time секунд, держа заявку на продажу металла открытой, как это делает игрок. Возвращает, на какой
    /// секунде пришла первая продажа: 0 — не дождались. Продажа — это когда у космопорта пропадает заявка.
    /// </summary>
    private static int RunMine(Mine scene, int time, int firstSale = 0)
    {
        var (sim, port, _) = scene;
        var sale = firstSale;
        for (var second = 0; second < time; second++)
            for (var tick = 0; tick < Lib.Ticks; tick++)
            {
                if (sim.CanSell(1, port))
                {
                    var stock = Math.Floor(MetalOf(sim, 1));
                    if (stock >= TruckCapacity)
                        sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = stock });
                }
                var ordered = sim.Has(port, Names.Trade.Name);
                sim.Advance();
                if (sale == 0 && ordered && !sim.Has(port, Names.Trade.Name))
                    sale = second;
            }
        return sale;
    }

    /// <summary>Сколько металла в мире: в хранилищах, на заводе и в трюме космопорта.</summary>
    private static double MetalInWorld(Sim sim) => sim.With(Names.Stock.Name).Sum(entity => sim.Num(entity, Names.Stock.Name, "metal"));

    /// <summary>2. Замер живой линии: пять минут рудника с продажей. Возвращает выручку линии в кредитах в секунду.</summary>
    private static double MeasureMine()
    {
        var scene = MineScene();
        using var sim = scene.Sim;
        var pipes = scene.Pipes;
        const int warmup = 120;
        var firstSale = RunMine(scene, warmup);
        var before = sim.Credits(1);
        var held = MetalInWorld(sim);
        var passive = sim.Economy(1).Income;
        RunMine(scene, 180, firstSale);
        var measured = (sim.Credits(1) - before) / 180;
        // Продажа идёт партиями: что из окна замера осталось на складах или пришло в него с прогрева — тоже выручка линии.
        var price = Data.Resources["metal"].Price;
        var line = Math.Max(0, measured - passive + (MetalInWorld(sim) - held) * price / 180);
        var piping = pipes * Data.Buildings["pipe"].Cost;
        var invested = Data.Buildings["mine"].Cost + piping + Data.Buildings["metalYard"].Cost + Data.Buildings["smelter"].Cost + Data.Buildings["spaceport"].Cost + 2 * Data.Buildings["generator"].Cost;

        Log("\nРудник в симуляции: шахта, переработка, хранилище и космопорт на одной сети труб; продажа металла, 5 минут");
        Log($"  труб                   {pipes} тайлов, {Num(piping)} кр");
        Log($"  первая продажа         {(firstSale != 0 ? Clock(firstSale) : "не дождались")}");
        Log($"  доход всего            {Num(Round(measured, 1))} кр/с");
        Log($"  пассивка               {Num(Round(passive, 1))} кр/с (главное здание и генератор материи)");
        Log($"  выручка линии          {Num(Round(line, 1))} кр/с (с металлом, который за замер лёг на склады)");
        Log($"  через переработку      {Num(Round(line / price, 1))} металла/с (шахта даёт 1 руды/с, завод принимает {Num(RefineRate)}/с)");
        if (line > 0)
        {
            Log($"  окупаемость линии      {Num(JsRound(LineCost / line))} с (шахта и переработка, {Num(LineCost)} кр)");
            Log($"  вложено в рудник       {Num(invested)} кр, окупается за {Num(JsRound(invested / line))} с");
        }
        return line;
    }

    // ===== Производство =====

    /// <summary>Материалы в сырых ресурсах: изделия раскладываются по рецепту цеха на то, из чего их собирают (rawOf).</summary>
    private static Dictionary<string, double> RawOf(Dictionary<string, double> materials)
    {
        var raw = new Dictionary<string, double>();
        foreach (var (good, amount) in materials)
        {
            if (Data.Products.TryGetValue(good, out var product))
            {
                foreach (var (resource, need) in product.Recipe)
                    raw[resource] = raw.GetValueOrDefault(resource) + need * amount / product.Yield;
            }
            else
                raw[good] = raw.GetValueOrDefault(good) + amount;
        }
        return raw;
    }

    /// <summary>3. Что тратит производство и сколько линий его кормят.</summary>
    private static void PrintProduction(double lineRate)
    {
        var feed = Math.Max(lineRate, 0.1);
        Log("\nПроизводство: что здание тратит в секунду, если работает без остановки");
        var rows = new List<object[]>();
        foreach (var (type, building) in Data.Buildings)
            foreach (var unit in building.Produces)
            {
                var info = Data.Units[unit];
                var burn = info.Cost / info.BuildTime;
                var raw = RawOf(info.Materials);
                double Share(string resource) => raw.GetValueOrDefault(resource) / info.BuildTime;
                var lines = new[] { "metal", "silicon", "fuel", "kharite" }.Sum(resource => Share(resource) / Data.Deposits[resource].Rate);
                object Show(double value) => value > 0 ? Round(value, 2) : "—";
                // Материалы по цене продажи: столько кредитов игрок не выручил, пустив сырьё на юнит.
                var value = raw.Sum(pair => pair.Value * Data.Resources[pair.Key].Price);
                rows.Add([
                    type, unit, info.Cost, Round(info.BuildTime, 0), Round(burn, 1),
                    Show(Share("metal")), Show(Share("silicon")), Show(Share("fuel")), Show(Share("kharite")),
                    Round(lines, 1), Round(burn / feed, 1), value != 0 ? $"{Num(JsRound(value / (value + info.Cost) * 100))}%" : "—"]);
            }
        Table(["здание", "юнит", "цена", "время", "кр/с", "м/с", "к/с", "т/с", "х/с", "линий", "по деньгам", "материалы"], rows, 12, 8);
        Log("  кр/с — кредитов в секунду; м/к/т/х — материалов в секунду, изделия разложены по рецепту цеха;");
        Log("  материалы — доля сырья по цене продажи в полной цене юнита (кредиты плюс сырьё), цель 25–40%;");
        Log($"  линий — столько шахт нужно на материалы, и каждая кормится переработкой (завод держит {Num(RefineRate)} руды/с);");
        Log($"  по деньгам — на кредиты при выручке {Num(Round(feed, 1))} кр/с");
    }

    /// <summary>
    /// 4. Производные: что ест цех на каждом рецепте, сколько линий его кормят и сколько он даёт; сколько турелей держит
    /// одна линия металла через цех на патронах (§3.7 design.md: цель — 2–4 турели под огнём).
    /// </summary>
    private static void PrintProducts()
    {
        Log("\nЗаводы изделий: рецепт, что завод ест и что даёт, работая без остановки");
        var rows = new List<object[]>();
        foreach (var (product, spec) in Data.Products)
        {
            var seconds = CycleSeconds(product);
            var plant = Data.Buildings.FirstOrDefault(pair => pair.Value.Assembles == product).Key;
            object Show(string resource) => spec.Recipe.TryGetValue(resource, out var need) && need != 0 ? Round(need / seconds, 2) : "—";
            var lines = spec.Recipe.Sum(pair => pair.Value / seconds / Data.Deposits[pair.Key].Rate);
            object Stored = plant is not null && Data.Buildings[plant].Limits.TryGetValue(product, out var limit) ? limit : "—";
            rows.Add([product, Round(spec.Yield / seconds, 2), Show("metal"), Show("silicon"), Show("kharite"), Round(lines, 2), Stored]);
        }
        Table(["изделие", "штук/с", "м/с", "к/с", "х/с", "линий", "склад"], rows);
        Log("  линий — сколько линий добычи завод съедает, работая без остановки; склад — сколько готового он держит у себя:");
        Log("  набрал и сеть не забирает — встаёт");

        var ammo = Data.Products["ammo"];
        var ammoPerLine = ammo.Yield / TotalOf(ammo.Recipe) * Data.Deposits["metal"].Rate;
        Log("\nБоеприпасы: что тратит турель под непрерывным огнём и сколько их держит одна линия металла");
        Table(
            ["турель", "патр/с", "запас", "хватает", "на линию"],
            Data.Buildings.Where(pair => pair.Value.Ammo).Select(pair =>
            {
                var weapon = Data.Weapons[Data.Turrets[pair.Value.Mounts[0].Turret].Weapon!];
                var burn = (weapon.Ammo ?? 0) / weapon.Reload;
                var capacity = pair.Value.Inventory ?? 0;
                return (object[])[pair.Key, Round(burn, 2), capacity, $"{Num(JsRound(capacity / burn))} с", Round(ammoPerLine / burn, 1)];
            }),
            14, 10);
        Log($"  линия металла через цех даёт {Num(Round(ammoPerLine, 1))} патронов/с; хватает — сколько турель стреляет на своём запасе");
    }

    // ===== Разгон =====

    private sealed class Opening
    {
        public uint Mcv;
        public uint Core;
        public uint Port;
        public List<uint> Builders = [];
    }

    private sealed class Step
    {
        public string Title = "";
        public double Cost;
        public bool Started;
        public Func<Sim, Opening, bool> Run = (_, _) => true;
        public Func<Sim, Opening, bool> Done = (_, _) => true;
    }

    /// <summary>Свободный строитель: не занят стройкой.</summary>
    private static uint FreeBuilder(Sim sim, Opening opening) => opening.Builders.FirstOrDefault(builder => !sim.Has(builder, Names.Builds.Name));

    /// <summary>Ключ сети здания: первое здание сети (networkOf); 0 — не в сети.</summary>
    private static uint NetworkKey(Sim sim, uint entity) => sim.NetworkOf(entity);

    /// <summary>
    /// Трасса наземной трубы от основания до узла сети игрока: тайлы x, y подряд, от основания к сети (pipeRoute). Пусто —
    /// к основанию уже подходит своя труба. null — сети ближе 16 тайлов нет. joins — какую сеть годится брать.
    /// </summary>
    private static int[]? PipeRoute(Sim sim, uint player, int x, int y, int width, int height, Func<uint, bool> joins)
    {
        const int reach = 16;
        (int, int)[] sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        bool Inside(int tx, int ty) => tx >= x && tx < x + width && ty >= y && ty < y + height;
        // Заложенные свои трубы: к ним подключаются, как к готовым — но здесь, как в TS, при условии joins их не берём.
        var sites = new HashSet<(int, int)>();
        foreach (var site in sim.Of(player, Names.Site.Name, Names.Position.Name))
            if (sim.Num(site, Names.Site.Name, Names.Site.Demolish) == 0 && Lib.Data.Buildings[Lib.TypeOf(sim, site)].Pipe is not null)
            {
                var (sx, sy) = sim.At(site);
                sites.Add(((int)sx, (int)sy));
            }
        bool Node(int tx, int ty, bool pipeOnly)
        {
            if (Inside(tx, ty))
                return false;
            if (sites.Contains((tx, ty)))
                return false;
            var other = sim.OccupantAt(tx, ty);
            if (other == 0 || sim.OwnerOf(other) != player || sim.Has(other, Names.Site.Name))
                return false;
            if (pipeOnly && Lib.Data.Buildings[Lib.TypeOf(sim, other)].Pipe is null)
                return false;
            var zone = NetworkKey(sim, other);
            return zone != 0 && joins(zone);
        }
        for (var tx = x; tx < x + width; tx++)
            if (Node(tx, y - 1, true) || Node(tx, y + height, true))
                return [];
        for (var ty = y; ty < y + height; ty++)
            if (Node(x - 1, ty, true) || Node(x + width, ty, true))
                return [];

        bool Free(int tx, int ty) => !Inside(tx, ty) && !sites.Contains((tx, ty)) && Lib.CanPlace(sim, "pipe", tx, ty);
        var previous = new Dictionary<(int, int), int>();
        var queue = new List<(int X, int Y, int Length)>();
        void Start(int tx, int ty)
        {
            if (previous.ContainsKey((tx, ty)) || !Free(tx, ty))
                return;
            previous[(tx, ty)] = -1;
            queue.Add((tx, ty, 1));
        }
        for (var tx = x; tx < x + width; tx++)
        {
            Start(tx, y - 1);
            Start(tx, y + height);
        }
        for (var ty = y; ty < y + height; ty++)
        {
            Start(x - 1, ty);
            Start(x + width, ty);
        }
        for (var at = 0; at < queue.Count; at++)
        {
            var (tx, ty, length) = queue[at];
            if (sides.Any(side => Node(tx + side.Item1, ty + side.Item2, false)))
            {
                var back = new List<int>();
                for (var i = at; i >= 0; i = previous[(queue[i].X, queue[i].Y)])
                {
                    back.Add(queue[i].X);
                    back.Add(queue[i].Y);
                }
                var tiles = new List<int>();
                for (var i = back.Count - 2; i >= 0; i -= 2)
                {
                    tiles.Add(back[i]);
                    tiles.Add(back[i + 1]);
                }
                return [.. tiles];
            }
            if (length >= reach)
                continue;
            foreach (var (dx, dy) in sides)
            {
                if (previous.ContainsKey((tx + dx, ty + dy)) || !Free(tx + dx, ty + dy))
                    continue;
                previous[(tx + dx, ty + dy)] = at;
                queue.Add((tx + dx, ty + dy, length + 1));
            }
        }
        return null;
    }

    /// <summary>Порядок разгона: типовой старт игрока — от разворота MCV до второго генератора материи (makeSteps).</summary>
    private static List<Step> MakeSteps(Opening opening, (int X, int Y) core, DepositSpot deposit)
    {
        var mine = Lib.RectOf("mine", deposit.X, deposit.Y);
        Step Build(string title, string type, int count, int x, int y) => new()
        {
            Title = title,
            Cost = Data.Buildings[type].Cost,
            Run = (sim, _) =>
            {
                var builder = FreeBuilder(sim, opening);
                if (builder == 0)
                    return false;
                bool Suit(int tx, int ty) => sim.CanBuild(1, type, tx, ty) && (type == "mine" || Lib.RectsApart(Lib.RectOf(type, tx, ty), mine, 1));
                if (Lib.SpotNear(x, y, Suit, 20) is not { } tile)
                    return false;
                sim.Send(1, new BuildOrder { Building = type, X = tile.X, Y = tile.Y, Builders = [builder] });
                return true;
            },
            // Заложено: дальше к площадке тянут трубу, по ней придут материалы стройки.
            Done = (sim, _) => Lib.CountBuildings(sim, 1, type, true) >= count,
        };
        Step Ready(string title, string type, int count) => new() { Title = title, Done = (sim, _) => Lib.CountBuildings(sim, 1, type) >= count };
        Step Connect(string type)
        {
            uint Loose(Sim sim, Opening current)
            {
                var home = current.Core == 0 ? 0 : NetworkKey(sim, current.Core);
                foreach (var entity in sim.Of(1, Names.Building.Name, Names.Proto.Name))
                    if (Lib.TypeOf(sim, entity) == type && NetworkKey(sim, entity) != home)
                        return entity;
                return 0;
            }
            return new Step
            {
                Title = $"труба к зданию «{type}»",
                Run = (sim, current) =>
                {
                    var entity = Loose(sim, current);
                    var builder = FreeBuilder(sim, current);
                    if (entity == 0 || builder == 0 || current.Core == 0)
                        return entity == 0;
                    var home = NetworkKey(sim, current.Core);
                    var (px, py) = sim.At(entity);
                    var (width, height) = Lib.SizeOf(type);
                    var tiles = PipeRoute(sim, 1, (int)px, (int)py, width, height, zone => zone == home);
                    if (tiles is null || tiles.Length == 0)
                        return false;
                    sim.Send(1, new PipesOrder { Tiles = tiles, Builders = [builder] });
                    return true;
                },
                Done = (sim, current) => Loose(sim, current) == 0,
            };
        }
        Step Produce(string title, string unit, int count) => new()
        {
            Title = title,
            Cost = Data.Units[unit].Cost * count,
            Run = (sim, current) =>
            {
                if (current.Core == 0)
                    return false;
                for (var i = Lib.CountUnits(sim, 1, unit); i < count; i++)
                    sim.Send(1, new ProduceOrder { Producer = current.Core, Unit = unit });
                return true;
            },
            Done = (sim, _) => Lib.CountUnits(sim, 1, unit) >= count,
        };
        // Тема исследования: игрок ставит её в очередь, лаборатории ведут её сами; готово — когда тема изучена.
        Step Research(string topic) => new()
        {
            Title = $"исследовано: {topic}",
            Run = (sim, _) =>
            {
                // Открытия (наука, месторождение в обзоре) засчитываются сами и на тик позже события: до них тему не поставить.
                if (!ResearchRules.Before(Data.Research, topic).Where(before => ResearchRules.IsTrigger(Data.Research, before)).All(before => sim.HasResearch(1, before)))
                    return false;
                sim.Send(1, new ResearchOrder { Topic = topic });
                return true;
            },
            Done = (sim, _) => sim.HasResearch(1, topic),
        };
        return
        [
            new Step
            {
                Title = "разворот MCV",
                Run = (sim, _) =>
                {
                    sim.Send(1, new DeployOrder { Unit = opening.Mcv });
                    return true;
                },
                Done = (_, current) => current.Core != 0,
            },
            // Строителей в стартовом наборе нет: их заказывают в развёрнутом главном здании.
            Produce("строители", "builder", 2),
            // Электростанция у главного здания: энергия общая на зону, трубы ей не нужны — да их ещё и не исследовали.
            Build("заложено: электростанция", "generator", 1, core.X + 5, core.Y - 4),
            Ready("электростанция", "generator", 1),
            // Лаборатория открывает «Научный метод», с ним — трубы; руду металла она изучает из своей шахты по трубам.
            Build("заложено: лаборатория", "lab", 1, core.X - 4, core.Y - 4),
            Ready("лаборатория", "lab", 1),
            Research("pipes"),
            Connect("lab"),
            // Генератор материи — первый доход, пока нет продажи: ему нужна сеть главного здания, то есть трубы.
            Build("заложено: генератор материи", "matter", 1, core.X + 2, core.Y - 4),
            Connect("matter"),
            Ready("генератор материи", "matter", 1),
            Build("заложено: шахта", "mine", 1, deposit.X, deposit.Y),
            Connect("mine"),
            Ready("шахта", "mine", 1),
            Research("metalOre"),
            // Переработка до хранилища: хранилище металла открывается ею, и без неё продавать нечего.
            Build("заложено: переработка", "smelter", 1, core.X + 5, core.Y + 4),
            Connect("smelter"),
            Ready("переработка", "smelter", 1),
            Build("заложено: хранилище", "metalYard", 1, core.X + 4, core.Y + 7),
            Connect("metalYard"),
            Ready("хранилище", "metalYard", 1),
            Build("заложено: космопорт", "spaceport", 1, core.X + 8, core.Y + 4),
            Connect("spaceport"),
            Ready("космопорт", "spaceport", 1),
            Build("заложено: вторая электростанция", "generator", 2, core.X - 5, core.Y - 4),
            Ready("вторая электростанция", "generator", 2),
            Build("заложено: второй генератор материи", "matter", 2, core.X - 2, core.Y - 4),
            Connect("matter"),
            Ready("второй генератор материи", "matter", 2),
        ];
    }

    /// <summary>Обновляет, что уже построено и кто свободен (refresh).</summary>
    private static void Refresh(Sim sim, Opening opening)
    {
        foreach (var entity in sim.Of(1, Names.Building.Name, Names.Proto.Name))
        {
            if (sim.Has(entity, Names.Site.Name))
                continue;
            switch (Lib.TypeOf(sim, entity))
            {
                case "command":
                    opening.Core = entity;
                    break;
                case "spaceport":
                    opening.Port = entity;
                    break;
            }
        }
        opening.Builders = [.. sim.Of(1, Names.Unit.Name, Names.Proto.Name).Where(entity => Lib.TypeOf(sim, entity) == "builder")];
    }

    /// <summary>Стартовая точка разгона: месторождение металла и место под главное здание рядом.</summary>
    private static (DepositSpot Spot, (int X, int Y) Core) StartSpot(Sim sim)
    {
        foreach (var cell in Lib.Rings(0, 0, 6))
        {
            if (sim.DepositIn(cell.X, cell.Y) is not { Kind: "metal" } spot)
                continue;
            // Главное здание не строят: его разворачивает MCV на своё место, поэтому проверяется только место. От
            // месторождения оно стоит в стороне: займи оно его, шахту будет не поставить.
            bool Suit(int x, int y) => Lib.CanPlace(sim, "command", x, y, 2) && Lib.RectsApart(Lib.RectOf("command", x, y), Lib.RectOf("mine", spot.X, spot.Y), 1);
            if (Lib.SpotNear(spot.X + 1, spot.Y + 1, Suit, 12) is { } core)
                return (spot, core);
        }
        throw new InvalidOperationException("Не нашлось места под старт у месторождения металла");
    }

    /// <summary>Русские имена наград: в таблице наград они названы по-английски, игроку они знакомы по-русски.</summary>
    private static readonly Dictionary<string, string> RewardNames = new()
    {
        ["deploy"] = "разворот MCV", ["unit"] = "первый юнит", ["generator"] = "электростанция",
        ["matter"] = "генератор материи", ["mine"] = "шахта", ["metalYard"] = "хранилище",
    };

    /// <summary>4. Разгон настоящими командами: от разворота MCV до второго генератора материи.</summary>
    private static void PrintOpening()
    {
        using var sim = Lib.NewSim();
        var (spot, core) = StartSpot(sim);
        var center = (X: core.X + 1, Y: core.Y + 1);
        // Стартовый набор настоящего игрока: MCV, бойцы и кредиты — MCV встаёт на место главного здания.
        sim.Edit(new EditStartOrder { Player = 1, X = center.X, Y = center.Y });
        var mcv = sim.Of(1, Names.Unit.Name).First(entity => Lib.TypeOf(sim, entity) == "mcv");
        sim.Set(mcv, "Position.x", center.X);
        sim.Set(mcv, "Position.y", center.Y);
        var opening = new Opening { Mcv = mcv };
        var steps = MakeSteps(opening, center, spot);

        const int limit = 600;
        var events = new List<(double Time, string Text)>();
        var curve = new List<object[]>();
        var known = new HashSet<string>(sim.Query<NameList>(KernelQueries.Rewards, new PlayerArgs { Player = 1 }).Items);
        var index = 0;
        var sales = 0;
        var minute = sim.Credits(1);
        double minuteRewards = 0, minuteSpend = 0;

        for (var tick = 0; tick < (int)Math.Round(limit / Lib.Tick); tick++)
        {
            var time = tick * Lib.Tick;
            // Продажа: игрок держит заявку открытой, как только есть что везти.
            var port = opening.Port;
            if (port != 0 && sim.CanSell(1, port))
            {
                var stock = Math.Floor(MetalOf(sim, 1));
                if (stock >= TruckCapacity)
                    sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = stock });
            }
            var step = index < steps.Count ? steps[index] : null;
            // Стройку закладывают на свободные кредиты: за вычетом обещанного начатым, см. freeCredits.
            if (step is { Started: false } && sim.FreeCredits(1) >= step.Cost && step.Run(sim, opening))
            {
                step.Started = true;
                minuteSpend += step.Cost;
            }
            var ordered = port != 0 && sim.Has(port, Names.Trade.Name);
            sim.Advance();
            Refresh(sim, opening);
            // Корабль улетел: заявка пропала, кредиты начислены.
            if (ordered && !sim.Has(port, Names.Trade.Name))
            {
                sales++;
                if (sales == 1)
                    events.Add((time, "первая продажа металла"));
            }
            // Награды за вехи — это тоже доход, но разовый: в доходе на графике их не считаем.
            foreach (var key in sim.Query<NameList>(KernelQueries.Rewards, new PlayerArgs { Player = 1 }).Items)
            {
                if (!known.Add(key))
                    continue;
                var amount = Data.Rewards.TryGetValue(key, out var reward) ? reward.Credits : 0;
                minuteRewards += amount;
                events.Add((time, $"награда: {(RewardNames.TryGetValue(key, out var name) ? name : key)} +{Num(amount)}"));
            }
            if (step is { Started: true } && step.Done(sim, opening))
            {
                events.Add((time, step.Title));
                index++;
            }
            if ((tick + 1) % Lib.Ticks == 0)
            {
                var second = (tick + 1) / Lib.Ticks;
                var credits = sim.Credits(1);
                if (second % 60 == 0)
                {
                    // Доход — это всё, что пришло не от разовых наград: продажи и пассивка, за вычетом потраченного.
                    var income = (credits - minute - minuteRewards + minuteSpend) / 60;
                    curve.Add([Clock(second), credits, Round(income, 1), Math.Floor(MetalOf(sim, 1))]);
                    minute = credits;
                    minuteRewards = 0;
                    minuteSpend = 0;
                }
            }
        }

        Log("\nРазгон настоящими командами: от стартового набора до второго генератора материи");
        foreach (var (time, text) in events.OrderBy(item => item.Time))
            Log($"  {Clock(time).PadLeft(5)}  {text}");
        if (index < steps.Count)
            Log($"  застряли на шаге «{steps[index].Title}»: не хватило места, денег или строителя");
        Table(["минута", "кредиты", "доход/с", "металл"], curve);
        Log($"  продаж за прогон: {sales}; доход/с — продажи и пассивка, без разовых наград за вехи");
    }

    /// <summary>Раздел целиком: сначала рудник, потом производство по его выручке, потом разгон.</summary>
    public static void Run()
    {
        // Рудник с тем, что считается по его выручке, и разгон — две партии разом.
        Report.Parallel(
            () =>
            {
                PrintLineMath();
                var lineRate = MeasureMine();
                PrintProduction(lineRate);
                PrintProducts();
            },
            PrintOpening);
    }
}
