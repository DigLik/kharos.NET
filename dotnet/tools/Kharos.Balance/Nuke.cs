using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Orders;
using Kharos.Base.Scenario;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел «nuke» (tools/harness/nuke.ts): ядерный удар и защита от него. Цена по описаниям и что сносит взрыв — из данных.
/// Удар по базе песочницы и залп против перехватчика — живые сцены: настоящий пуск ракет по базе партии «sandbox».
/// </summary>
public static class Nuke
{
    private static BaseTables Data => Lib.Data;

    /// <summary>Прочность здания без своего числа (BUILDING_HP): цена на два.</summary>
    private const double BuildingHp = 2;

    private static double HpOf(BuildingSpec spec) => spec.Hp ?? spec.Cost * BuildingHp;

    /// <summary>Во что обходится груз сырьём по цене продажи: изделие — его рецепт, пересчитанный на штуку (worth).</summary>
    private static double Worth(string good)
    {
        if (Data.Resources.TryGetValue(good, out var resource))
            return resource.Price;
        if (!Data.Products.TryGetValue(good, out var spec))
            return 0;
        return spec.Recipe.Sum(pair => Worth(pair.Key) * pair.Value) / spec.Yield;
    }

    private static double WorthOf(Dictionary<string, double> amounts) => amounts.Sum(pair => Worth(pair.Key) * pair.Value);

    private static string ListOf(Dictionary<string, double> amounts) =>
        amounts.Count == 0 ? "—" : string.Join(", ", amounts.Select(pair => $"{Num(pair.Value)} {pair.Key}"));

    /// <summary>1. Цена удара и защиты.</summary>
    private static void PrintPrices()
    {
        var silo = Data.Buildings["silo"];
        var warhead = silo.Warhead!;
        var interceptor = Data.Buildings["interceptor"];
        var intercept = interceptor.Intercept!;
        Log("\nЯдерный удар и защита: цена по описаниям (сырьё — по цене продажи)");
        object[][] rows =
        [
            ["Шахта", silo.Cost, ListOf(silo.Materials), Round(silo.Cost + WorthOf(silo.Materials), 0)],
            ["Боеголовка", warhead.Cost, ListOf(warhead.Materials), Round(warhead.Cost + WorthOf(warhead.Materials), 0)],
            ["Перехватчик", interceptor.Cost, ListOf(interceptor.Materials), Round(interceptor.Cost + WorthOf(interceptor.Materials), 0)],
            ["Его ракета", intercept.Cost, ListOf(intercept.Materials), Round(intercept.Cost + WorthOf(intercept.Materials), 0)],
        ];
        Table(["", "кредиты", "материалы", "всего"], rows, 14, 26);
        var strike = warhead.Cost + WorthOf(warhead.Materials);
        var shot = intercept.Cost + WorthOf(intercept.Materials);
        var dome = interceptor.Cost + WorthOf(interceptor.Materials) + shot * intercept.Capacity;
        Log($"  удар без шахты {Num(Round(strike, 0))} против {Num(Round(shot, 0))} за сбившую его ракету: защита дешевле в {Num(Round(strike / shot, 1))} раза");
        Log($"  перехватчик с полным запасом ({Num(intercept.Capacity)}) — {Num(Round(dome, 0))}: как {Num(Round(dome / strike, 1))} удара; круг {Num(intercept.Radius)} тайлов");
        Log($"  сроки: боеголовка {Minutes(warhead.Seconds)} при полной энергии (шахта просит {Num(-silo.Power!.Value)}), пуск {Num(warhead.Launch)} с, полёт {Num(warhead.Speed)} тайлов/с — {Minutes(1024 / warhead.Speed)} через карту 1024; ракета перехватчика {Minutes(intercept.Seconds)}");
    }

    /// <summary>2. Что сносит взрыв.</summary>
    private static void PrintBlast()
    {
        var blast = Data.Buildings["silo"].Warhead!.Blast;
        var (radius, core, damage) = (blast.Radius, blast.Core, blast.Damage);
        Log($"\nВзрыв: радиус {Num(radius)}, ядро {Num(core)} (полный урон {Num(damage)}), к краю урон падает до нуля; расстояние — до края здания");
        // До какого расстояния гибнет здание этой прочности.
        double Lethal(double hp) => hp > damage ? -1 : core + (radius - core) * (1 - hp / damage);
        var groups = new List<(double Hp, List<string> Types)>();
        foreach (var (type, spec) in Data.Buildings)
        {
            var hp = HpOf(spec);
            var group = groups.FirstOrDefault(item => item.Hp == hp);
            if (group.Types is null)
                groups.Add((hp, [type]));
            else
                group.Types.Add(type);
        }
        var rows = groups.OrderBy(group => group.Hp).Select(group =>
        {
            var reach = Lethal(group.Hp);
            string Lost(double distance) => $"{Num(Report.Round(Math.Min(1, damage * (distance <= core ? 1 : (radius - distance) / (radius - core)) / group.Hp) * 100, 0))}%";
            return (object[])[Num(group.Hp), reach < 0 ? "не гибнет" : Round(reach, 1), Lost(8), Lost(10), Lost(11.5),
                string.Join(", ", group.Types.Take(4)) + (group.Types.Count > 4 ? $" и ещё {group.Types.Count - 4}" : "")];
        });
        Table(["прочность", "гибнет до", "на 8", "на 10", "на 11.5", "здания"], rows, 10, 10);
        var tough = Data.Units.Values.Max(unit => unit.Hp);
        Log($"  юниты: самый прочный — {Num(tough)}, гибнет до {Num(Round(Lethal(tough), 1))} тайлов; площадь гибели здания на 1000 — {Num(Round(Math.PI * Math.Pow(Lethal(1000), 2), 0))} тайлов²");
    }

    /// <summary>Мир без защиты новичка: под ней удар урона не наносит.</summary>
    private static readonly object Rules = new { protection = 0, techTree = false };

    /// <summary>База нападающего в ряд под общей трубой: главное здание, электростанции и шахты с готовыми боеголовками.</summary>
    private static List<uint> AttackerBase(Sim sim, uint player, int nearX, int nearY, int silos)
    {
        var layout = new List<(string Type, int Shift)> { ("command", 0), ("generator", 4), ("generator", 7) };
        var at = 10;
        for (var i = 0; i < silos; i++)
        {
            layout.Add(("silo", at));
            at += (int)Data.Buildings["silo"].Width + 1;
        }
        for (var ring = 0; ring < 300; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        continue;
                    var (x, y) = (nearX + dx, nearY + dy);
                    if (!layout.All(item => Lib.CanPlace(sim, item.Type, x + item.Shift, y)))
                        continue;
                    if (!Enumerable.Range(0, at).All(shift => Lib.CanPlace(sim, "pipe", x + shift, y - 1)))
                        continue;
                    var buildings = layout.Select(item => sim.PutBuilding(item.Type, x + item.Shift, y, player)).ToList();
                    for (var shift = 0; shift < at; shift++)
                        sim.PutBuilding("pipe", x + shift, y - 1, player);
                    sim.SetCredits(player, sim.Credits(player) + 10000);
                    var found = buildings.Skip(3).ToList();
                    foreach (var silo in found)
                        sim.Set(silo, "Silo.ready", 1);
                    return found;
                }
        throw new InvalidOperationException("Не нашлось места под базу нападающего");
    }

    /// <summary>Сколько у игрока зданий и юнитов и сколько они стоили.</summary>
    private static (int Buildings, int Units, double Cost) Census(Sim sim, uint player)
    {
        var (buildings, units, cost) = (0, 0, 0.0);
        foreach (var entity in sim.Of(player, Names.Building.Name))
        {
            buildings++;
            cost += Data.Buildings[Lib.TypeOf(sim, entity)].Cost;
        }
        foreach (var entity in sim.Of(player, Names.Unit.Name))
        {
            units++;
            cost += Data.Units[Lib.TypeOf(sim, entity)].Cost;
        }
        return (buildings, units, cost);
    }

    private static int Flying(Sim sim) => sim.Missiles().Count;

    /// <summary>
    /// Партия «sandbox» с базой игрока 1 (spawnSandbox) и базой нападающего (игрок 2) в стороне; shield — у защищающегося
    /// перехватчик с полным запасом. Цель — середина базы, как её возвращает spawnSandbox: за главным зданием от шахты.
    /// </summary>
    private static (Sim Sim, (double X, double Y) Target, List<uint> Silos) Standoff(int silos, bool shield)
    {
        var sim = Sim.Create(new SimOptions { Game = "sandbox", Size = Lib.HalfSize, Land = "default", Players = [1], Rules = Rules });
        var mine = sim.Of(1, Names.Building.Name).FirstOrDefault(entity => Lib.TypeOf(sim, entity) == "mine");
        if (mine == 0)
            throw new InvalidOperationException("База песочницы не встала");
        var (mineX, mineY) = sim.At(mine);
        var (centerX, centerY) = (mineX + Data.Buildings["mine"].Width / 2, mineY + Data.Buildings["mine"].Height / 2);
        var core = sim.Of(1, Names.Building.Name).Any(entity => Lib.TypeOf(sim, entity) == "command");
        (double X, double Y) target = core ? (centerX + 6, centerY) : (centerX, centerY);
        if (shield)
        {
            var dome = Lib.PlaceNear(sim, "interceptor", target.X, target.Y + 6, 1);
            if (dome == 0)
                throw new InvalidOperationException("Перехватчику не нашлось места");
            sim.ConnectAll(1);
            sim.Set(dome, "Interceptor.missiles", Data.Buildings["interceptor"].Intercept!.Capacity);
        }
        var found = AttackerBase(sim, 2, (int)Economy.JsRound(target.X) + 200, (int)Economy.JsRound(target.Y), silos);
        sim.Seconds(1);
        return (sim, target, found);
    }

    /// <summary>Пускает все ракеты разом в точку и ждёт, пока долетят. Возвращает, сколько взорвалось у цели.</summary>
    private static int Volley(Sim sim, List<uint> silos, double x, double y)
    {
        var hits = 0;
        foreach (var silo in silos)
            sim.Send(2, new LaunchOrder { Silo = silo, X = (int)Economy.JsRound(x), Y = (int)Economy.JsRound(y) });
        sim.Seconds(Data.Buildings["silo"].Warhead!.Launch + 0.5);
        var before = Census(sim, 1);
        var last = before.Buildings + before.Units;
        sim.Until(() =>
        {
            var now = Census(sim, 1);
            if (now.Buildings + now.Units < last)
                hits++;
            last = now.Buildings + now.Units;
            return Flying(sim) == 0;
        }, 600);
        sim.Seconds(2);
        return hits;
    }

    /// <summary>3. Удар по базе песочницы без защиты.</summary>
    private static void PrintStrike()
    {
        var warhead = Data.Buildings["silo"].Warhead!;
        var (sim, target, silos) = Standoff(1, false);
        using var _ = sim;
        var before = Census(sim, 1);
        var start = sim.Ticks;
        foreach (var silo in silos)
            sim.Send(2, new LaunchOrder { Silo = silo, X = (int)Economy.JsRound(target.X), Y = (int)Economy.JsRound(target.Y) });
        sim.Seconds(warhead.Launch + 0.5);
        sim.Until(() => Flying(sim) == 0, 600);
        var flight = (sim.Ticks - start) * Lib.Tick;
        sim.Seconds(2);
        var after = Census(sim, 1);
        var hurt = sim.Of(1, Names.Building.Name).Count(entity => sim.HealthOf(entity) < 1);
        var strike = warhead.Cost + WorthOf(warhead.Materials);
        Log("\nУдар по базе песочницы без защиты: настоящий пуск за 200 тайлов, в середину базы");
        Log($"  от приказа до взрыва   {Minutes(flight)}");
        Log($"  здания                 {before.Buildings} → {after.Buildings}: погибло {before.Buildings - after.Buildings}, ещё {hurt} повреждено");
        Log($"  юниты                  {before.Units} → {after.Units}");
        Log($"  потеряно на            {Num(before.Cost - after.Cost)} кр (без материалов и запасов) — удар стоит {Num(Round(strike, 0))}: окупился в {Num(Round((before.Cost - after.Cost) / strike, 1))} раза");
    }

    /// <summary>4. Залп против одного перехватчика с полным запасом.</summary>
    private static void PrintVolleys()
    {
        var intercept = Data.Buildings["interceptor"].Intercept!;
        var capacity = (int)intercept.Capacity;
        Log($"\nЗалп против одного перехватчика с полным запасом ({capacity} ракеты): сколько боеголовок дошло");
        var rows = new List<object[]>();
        foreach (var count in (int[])[1, capacity, capacity + 1, capacity + 2])
        {
            var (sim, target, silos) = Standoff(count, true);
            using var _ = sim;
            var before = Census(sim, 1);
            Volley(sim, silos, target.X, target.Y);
            var after = Census(sim, 1);
            var left = sim.With(Names.Interceptor.Name).Sum(entity => sim.Num(entity, "Interceptor.missiles"));
            var lost = before.Buildings - after.Buildings;
            rows.Add([count, lost != 0 ? "да" : "нет", lost, before.Cost - after.Cost, left]);
        }
        Table(["ракет", "пробили", "зданий", "потеряно кр", "осталось у перехв."], rows, 6, 14);
        Log($"  новую ракету перехватчик собирает {Minutes(intercept.Seconds)}: залпы чаще этого его истощают");
    }

    public static void Run()
    {
        PrintPrices();
        PrintBlast();
        PrintStrike();
        PrintVolleys();
    }
}
