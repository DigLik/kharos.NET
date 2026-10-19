using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Base.Scenario;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел «research» (tools/harness/research.ts): компоненты и лестница исследований. Потолок по описаниям, харит на карте,
/// живая линия компонентов в симуляции, цены тем по правилам темпа и когда они открываются, уровни зданий.
/// </summary>
public static class ResearchSection
{
    private const double RefineRatio = 1;

    private static BaseTables Data => Lib.Data;

    /// <summary>Харита на один компонент и кремния на единицу харита — по рецепту цеха.</summary>
    private static double KhariteForPart => Data.Products["parts"].Recipe["kharite"] / Data.Products["parts"].Yield;

    private static double SiliconForPart => Data.Products["parts"].Recipe["silicon"] / Data.Products["parts"].Yield;

    /// <summary>Среднее месторождение харита в компонентах.</summary>
    private static double AverageDeposit => (Data.Deposits["kharite"].Min + Data.Deposits["kharite"].Max) / 2 / RefineRatio / KhariteForPart;

    /// <summary>Правила серверной сессии: DEFAULT_RULES с поправками SESSION_RULES.</summary>
    private static RulesSpec Session => new() { ResearchScale = 1, ResearchGrowth = 3, ResearchSlow = 10, UpgradeScale = 5 };

    /// <summary>1. Потолок по описаниям: месторождение харита в компонентах.</summary>
    private static void PrintSupply()
    {
        var kharite = Data.Deposits["kharite"];
        var (min, max, rate) = (kharite.Min, kharite.Max, kharite.Rate);
        var parts = Data.Products["parts"];
        double PartsOf(double reserve) => Math.Floor(reserve / RefineRatio / KhariteForPart);
        var perHour = rate / RefineRatio / KhariteForPart * 3600;
        var siliconRate = rate * SiliconForPart / KhariteForPart;
        Log("\nКомпоненты: потолок по описаниям");
        Log($"  рецепт                 {Num(parts.Recipe["silicon"])} кремния + {Num(parts.Recipe["kharite"])} харита → {Num(parts.Yield)}; цех собирает {Num(Round(parts.Yield / Economy.CycleSeconds("parts") * 3600, 0))} в час (ASSEMBLE_RATE {Num(Economy.AssembleRate)})");
        Log($"  шахта харита           {Num(rate)} руды/с — {Num(Round(perHour, 0))} компонентов в час, пока месторождение живо: узкое место — шахта, не цех");
        Log($"  месторождение          {Num(min)}–{Num(max)} харита = {Num(PartsOf(min))}–{Num(PartsOf(max))} компонентов; шахта выбирает его за {Minutes(min / rate)}–{Minutes(max / rate)}");
        Log($"  кремний                {Num(Round(siliconRate, 2))}/с на шахту харита: шахта кремния ({Num(Data.Deposits["silicon"].Rate)}/с) кормит {Num(Round(Data.Deposits["silicon"].Rate / siliconRate, 1))} шахты харита");
        Log($"  продать вместо этого   месторождение харита — {Num(min * Data.Resources["kharite"].Price)}–{Num(max * Data.Resources["kharite"].Price)} кр, по {Num(Data.Resources["kharite"].Price)} за штуку");
    }

    /// <summary>Все месторождения карты: генератор считает ячейку при первом обращении.</summary>
    private static List<DepositSpot> DepositsOf(Sim sim, int half)
    {
        var spots = new List<DepositSpot>();
        int Cell(int tile) => (int)Math.Floor(tile / (double)Sim.DepositCell);
        for (var cy = Cell(-half); cy <= Cell(half - 1); cy++)
            for (var cx = Cell(-half); cx <= Cell(half - 1); cx++)
                if (sim.DepositIn(cx, cy) is { } spot)
                    spots.Add(spot);
        return spots;
    }

    /// <summary>2. Карта: сколько харита на карте каждого размера и сколько — вокруг одной базы.</summary>
    private static void PrintMap()
    {
        Log("\nХарит на карте: генератор по умолчанию, по три зерна на размер");
        var rows = new List<object[]>();
        double[] radii = [100, 200, 300];
        var reach = new List<double>[] { [], [], [] };
        var whole = Ladder(Session).Parts;
        int[] sizes = [256, 1024, 2048];
        // Все карты (размер и зерно) — разом.
        var maps = Work.Map([.. sizes.SelectMany(size => Enumerable.Range(0, 3).Select(n => (Size: size, N: n)))], map =>
        {
            var (size, n) = map;
            Progress($"  карта {size}, зерно {n}");
            using var sim = Sim.Create(new SimOptions { Size = size / 2, Land = Lib.LandWithSeed(1337 + n * 7919), Players = [], Rules = new { protection = 0 } });
            var spots = DepositsOf(sim, size / 2);
            var kharite = spots.Where(spot => spot.Kind == "kharite").ToList();
            var near = new List<double>[] { [], [], [] };
            // Досягаемость меряем на карте, где есть куда расти: от каждого двадцатого месторождения как от базы.
            if (size == 2048)
            {
                var half = size / 2.0 - radii[2];
                for (var i = 0; i < spots.Count; i++)
                {
                    var baseSpot = spots[i];
                    if (i % 20 != 0 || Math.Abs(baseSpot.X) > half || Math.Abs(baseSpot.Y) > half)
                        continue;
                    for (var r = 0; r < radii.Length; r++)
                        near[r].Add(kharite.Where(spot => Math.Sqrt(Math.Pow(spot.X - baseSpot.X, 2) + Math.Pow(spot.Y - baseSpot.Y, 2)) <= radii[r]).Sum(spot => spot.Reserve));
                }
            }
            return (All: spots.Count, Count: kharite.Count, Total: kharite.Sum(spot => spot.Reserve), Near: near);
        });
        for (var s = 0; s < sizes.Length; s++)
        {
            var size = sizes[s];
            var counts = new List<int>();
            var totals = new List<double>();
            var all = 0;
            for (var n = 0; n < 3; n++)
            {
                var map = maps[s * 3 + n];
                all += map.All;
                counts.Add(map.Count);
                totals.Add(map.Total);
                for (var r = 0; r < radii.Length; r++)
                    reach[r].AddRange(map.Near[r]);
            }
            var parts = totals.Average() / RefineRatio / KhariteForPart;
            rows.Add([$"{size}×{size}", Economy.JsRound(all / 3.0), $"{counts.Min()}–{counts.Max()}", Economy.JsRound(parts), Round(parts / whole, 1)]);
        }
        Table(["карта", "месторожд.", "из них харит", "компонентов", "деревьев"], rows, 12, 13);
        Log("  компонентов — если весь харит карты пустить в цех; деревьев — сколько полных деревьев исследований");
        Log($"  по правилам сервера (×{Num(Session.ResearchScale)}, ярус ×{Num(Session.ResearchGrowth)}) это кормит, если харит не продавать и не тратить на технику");
        Log("\nХарит вокруг базы (карта 2048, базы — вдали от края)");
        Table(
            ["радиус", "компонентов", "от–до", "месторожд."],
            radii.Select((radius, r) =>
            {
                var list = reach[r].Select(kharite => kharite / RefineRatio / KhariteForPart).ToList();
                var mean = list.Average();
                return (object[])[$"{Num(radius)} тайлов", Economy.JsRound(mean), $"{Num(Economy.JsRound(list.Min()))}–{Num(Economy.JsRound(list.Max()))}", Round(mean / AverageDeposit, 1)];
            }),
            12, 13);
    }

    /// <summary>Сколько компонентов в мире: в цехе и в шкафах.</summary>
    private static double PartsIn(Sim sim) => sim.With(Names.Stock.Name).Sum(entity => sim.Num(entity, Names.Stock.Name, "parts"));

    /// <summary>Ближайшее к началу мира месторождение харита.</summary>
    private static DepositSpot KhariteSpot(Sim sim)
    {
        foreach (var cell in Lib.Rings(0, 0, 8))
            if (sim.DepositIn(cell.X, cell.Y) is { Kind: "kharite" } spot)
                return spot;
        throw new InvalidOperationException("Не нашлось харита рядом с началом мира");
    }

    /// <summary>
    /// 3. Живая линия: готовая база у месторождения харита. Кремний лежит в хранилищах с запасом на всё месторождение;
    /// шкафов столько, чтобы все компоненты легли на склад. Возвращает компонентов в секунду, пока шахта копает, и секунды до первого.
    /// </summary>
    private static (double Rate, int First) MeasureLine()
    {
        using var sim = Lib.NewSim();
        var kharite = KhariteSpot(sim);
        sim.PutBuilding("mine", kharite.X, kharite.Y, 1);
        var parts = kharite.Reserve / RefineRatio / KhariteForPart;
        var lockers = (int)Math.Ceiling(parts / Data.Buildings["partsLocker"].Inventory!.Value) + 1;
        var stores = (int)Math.Ceiling(parts * SiliconForPart / Data.Buildings["siliconStore"].Inventory!.Value);
        IEnumerable<(string, int, int)> Grid(string type, int count, int dx, int dy, int step) =>
            Enumerable.Range(0, count).Select(i => (type, dx + i % 4 * step, dy + i / 4 * step));
        var layout = new List<(string Type, int Dx, int Dy)>
        {
            ("command", 7, 0), ("enricher", 4, -4), ("partsPlant", 4, 5), ("generator", 6, -7), ("generator", 9, -7),
        };
        layout.AddRange(Grid("partsLocker", lockers, 8, 5, 2));
        layout.AddRange(Grid("siliconStore", stores, -10, -4, 3));
        foreach (var (type, dx, dy) in layout)
        {
            var entity = Economy.PlaceNearMine(sim, kharite, type, kharite.X + 1 + dx, kharite.Y + 1 + dy);
            if (entity == 0)
                throw new InvalidOperationException($"Нет места под «{type}» у харита");
            if (type == "siliconStore")
                sim.SetStock(entity, "silicon", Data.Buildings["siliconStore"].Inventory!.Value);
            // Завод изделий ставится выключенным.
            if (type == "partsPlant")
                sim.SetWorking(1, entity, true);
        }
        sim.ConnectAll(1);

        const int limit = 60 * 60;
        var first = 0;
        var dry = 0;
        var marks = new List<double>();
        for (var second = 1; second <= limit; second++)
        {
            sim.Advance(Lib.Ticks);
            var held = PartsIn(sim);
            if (first == 0 && held >= 1)
                first = second;
            if (dry == 0 && sim.ReserveLeft(kharite.X, kharite.Y) <= 0)
                dry = second;
            if (second % 60 == 0)
                marks.Add(held);
            // Месторождение выбрано и цех доел остатки.
            if (dry != 0 && second - dry > 120 && marks.Count > 2 && marks[^1] == marks[^2])
                break;
        }
        var total = PartsIn(sim);
        // Поток — между первым компонентом и концом месторождения: разгон и доедание остатков в него не входят.
        var steady = dry > first
            ? (marks[dry / 60 - 1] - marks[(int)Math.Ceiling(first / 60.0)]) / (((dry / 60 - 1) - (int)Math.Ceiling(first / 60.0)) * 60)
            : 0;
        Log("\nЛиния компонентов в симуляции: шахта харита, обогащение, цех и шкафы на одной сети труб; кремний — со склада");
        Log($"  месторождение          {Num(kharite.Reserve)} харита");
        Log($"  первый компонент       {(first != 0 ? Minutes(first) : "не дождались")}");
        Log($"  поток                  {Num(Round(steady * 3600, 0))} компонентов в час (по описаниям — {Num(Round(Data.Deposits["kharite"].Rate / RefineRatio / KhariteForPart * 3600, 0))})");
        Log($"  месторождение кончилось {(dry != 0 ? Minutes(dry) : "не кончилось за час")}");
        Log($"  всего компонентов      {Num(Round(total, 0))} из {Num(Math.Floor(kharite.Reserve / RefineRatio / KhariteForPart))} возможных");
        return (steady, first);
    }

    private sealed record Step(string Topic, double Cost, double Parts, double Seconds);

    /// <summary>Типовой порядок тем: по ярусам сложности, в ярусе — как в описании. Цена от порядка не зависит.</summary>
    private static List<string> TypicalOrder() => [.. Data.Research.Keys.OrderBy(topic => ResearchRules.Tier(Data.Research, topic))];

    private static (List<Step> Steps, double Cost, double Parts, double Seconds) Ladder(RulesSpec pace)
    {
        var steps = TypicalOrder().Select(topic =>
        {
            var price = ResearchRules.Price(Data.Research, topic, pace);
            return new Step(topic, price.Cost, price.Materials.Where(item => item.Good == "parts").Select(item => item.Amount).FirstOrDefault(), price.Seconds);
        }).ToList();
        return (steps, steps.Sum(step => step.Cost), steps.Sum(step => step.Parts), steps.Sum(step => step.Seconds));
    }

    private sealed record Scale(string Name, double Mines, double Income);

    private sealed record Timed(Step Step, double Start, double Done, string Waits);

    /// <summary>
    /// Когда открывается каждая ступень: labs лабораторий ведут темы подряд вместе; тема кончается не раньше, чем
    /// кончилась прошлая и накоплены компоненты и кредиты на неё целиком (timeline).
    /// </summary>
    private static List<Timed> Timeline(RulesSpec pace, Scale scale, double rate, double move, int labs = 1)
    {
        // Доля времени, когда шахта копает: месторождение выбирается за life, потом переезд.
        var life = AverageDeposit / rate;
        var parts = scale.Mines * rate * (life / (life + move));
        double free = 0, spentParts = 0, spentCredits = 0;
        return [.. Ladder(pace).Steps.Select(step =>
        {
            spentParts += step.Parts;
            spentCredits += step.Cost;
            var byParts = spentParts / parts;
            var byCredits = spentCredits / scale.Income;
            var start = Math.Max(free + step.Seconds / labs, Math.Max(byParts, byCredits));
            free = start;
            return new Timed(step, start, free, start == byParts ? "харит" : start == byCredits ? "кредиты" : "лаборатории");
        })];
    }

    private static string PaceTitle(RulesSpec pace) => $"×{Num(pace.ResearchScale)}, ×{Num(pace.ResearchGrowth)}, ×{Num(pace.ResearchSlow)}";

    private static RulesSpec Pace(double scale, double growth, double slow = 1) => new() { ResearchScale = scale, ResearchGrowth = growth, ResearchSlow = slow };

    /// <summary>4. Лестница: цены ступеней, когда они открываются, и что будет при других правилах.</summary>
    private static void PrintLadder(double rate, int first)
    {
        var session = Session;
        var whole = Ladder(session);
        Log($"\nЛестница исследований по правилам сервера: цена ×{Num(session.ResearchScale)}, каждый ярус ×{Num(session.ResearchGrowth)}, срок ещё ×{Num(session.ResearchSlow)}; порядок — по ярусам");
        Table(
            ["тема", "кредиты", "компон.", "срок", "месторожд."],
            [
                .. whole.Steps.Select(step => (object[])[step.Topic, step.Cost, step.Parts, Hours(step.Seconds), Round(step.Parts / AverageDeposit, 1)]),
                ["всё дерево", whole.Cost, whole.Parts, Hours(whole.Seconds), Round(whole.Parts / AverageDeposit, 1)],
            ],
            12, 11);
        Log($"  месторожд. — сколько средних месторождений харита ({Num(AverageDeposit)} компонентов) уходит на тему");

        // Переезд на новое месторождение: шахта, обогащение и труба — как первый компонент на готовой базе, плюс дорога.
        double move = first + 240;
        Scale[] scales = [new("малая", 1, 12), new("средняя", 2, 25), new("большая", 4, 50)];
        Log("\nКогда открывается тема: часы работы базы от готовой лаборатории (ч:мм), одна лаборатория");
        var lines = scales.Select(scale => Timeline(session, scale, rate, move)).ToList();
        Table(
            ["тема", .. scales.SelectMany(scale => (object[])[scale.Name, "ждёт"])],
            whole.Steps.Select((step, i) => (object[])[step.Topic, .. lines.SelectMany(line => (object[])[Hours(line[i].Done), line[i].Waits])]),
            12, 10);
        foreach (var scale in scales)
            Log($"  {scale.Name.PadRight(8)} — шахт харита без простоя: {Num(scale.Mines)}, доход {Num(scale.Income)} кр/с");
        Log($"  переезд на новое месторождение — {Minutes(move)}; ждёт — чего тема дожидалась последним");

        Log("\nТо же при других правилах: всё дерево");
        RulesSpec[] paces = [Data.Rules, Pace(1, 3, 6), session, Pace(1, 3, 20), Pace(2, 3, 10), Pace(1, 3.5, 10), Pace(10, 2, 12)];
        Table(
            ["правила", "кредиты", "компон.", "месторожд.", "лаборат.", .. scales.Select(scale => (object)scale.Name), "4 лаб.", "первая"],
            paces.Select(pace =>
            {
                var tree = Ladder(pace);
                return (object[])[
                    PaceTitle(pace), tree.Cost, tree.Parts, Round(tree.Parts / AverageDeposit, 1), Hours(tree.Seconds),
                    .. scales.Select(scale => (object)Hours(Timeline(pace, scale, rate, move)[^1].Done)),
                    Hours(Timeline(pace, scales[1], rate, move, 4)[^1].Done),
                    Hours(Timeline(pace, scales[0], rate, move)[0].Done)];
            }),
            12, 11);
        Log("  лаборат. — сколько идут сами исследования одной лаборатории; малая, средняя, большая — когда открыто всё дерево");
        Log("  с одной лабораторией; 4 лаб. — у средней базы с четырьмя; первая — первая тема у малой базы");
    }

    /// <summary>Типовая база серверной сессии: что из неё поднимается уровнями и в скольких штуках.</summary>
    private static readonly (string Type, int Count)[] TypicalBase =
    [
        ("generator", 4), ("mine", 4), ("smelter", 1), ("siliconWorks", 1), ("enricher", 1), ("metalYard", 2), ("siliconStore", 1), ("khariteVault", 1),
        ("blockYard", 1), ("ammoBunker", 1), ("partsLocker", 2), ("spaceport", 1),
    ];

    /// <summary>5. Уровни зданий: во что обходится поднять типовую базу и как это соотносится с деревом исследований.</summary>
    private static void PrintLevels()
    {
        var session = Session;
        var upgradable = TypicalBase.Where(item => BuildRules.LevelGain(Data.Buildings[item.Type]) is not null).ToList();
        var rows = new List<object[]>();
        var total = new double[6];
        foreach (var (type, count) in upgradable)
        {
            var cells = new List<double>();
            for (var level = 1; level < BuildRules.MaxLevel; level++)
            {
                var (credits, materials) = BuildRules.UpgradeCost(Data.Buildings[type], level, session.UpgradeScale);
                cells.Add(credits * count);
                cells.Add(materials.GetValueOrDefault("blocks") * count);
                cells.Add(materials.GetValueOrDefault("parts") * count);
            }
            for (var i = 0; i < cells.Count; i++)
                total[i] += cells[i];
            rows.Add([$"{type} ×{count}", .. cells.Select(cell => (object)cell)]);
        }
        rows.Add(["вся база", .. total.Select(cell => (object)cell)]);
        var tree = Ladder(session);
        Log($"\nУровни зданий по правилам сервера (×{Num(session.UpgradeScale)}): поднять типовую базу ({upgradable.Sum(item => item.Count)} зданий) до второго и до третьего уровня");
        Table(["здание", "кр → 2", "блоков", "компон.", "кр → 3", "блоков", "компон."], rows, 16, 9);
        Log($"  обе ступени — {Num(total[0] + total[3])} кр, {Num(total[1] + total[4])} стройблоков и {Num(total[2] + total[5])} компонентов: {Num(Round((total[0] + total[3]) / tree.Cost * 100, 0))}% кредитов и {Num(Round((total[2] + total[5]) / tree.Parts * 100, 0))}% компонентов дерева исследований");
        Log($"  сами уровни открывают темы levels2 ({Num(tree.Steps.First(step => step.Topic == "levels2").Parts)} компонентов) и levels3 ({Num(tree.Steps.First(step => step.Topic == "levels3").Parts)})");
    }

    public static void Run()
    {
        // Карты и живая линия — разом.
        Report.Parallel(
            () =>
            {
                PrintSupply();
                PrintMap();
            },
            () =>
            {
                var (rate, first) = MeasureLine();
                PrintLadder(rate, first);
                PrintLevels();
            });
    }
}
