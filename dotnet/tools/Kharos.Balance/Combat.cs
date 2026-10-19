using Base.Shared;
using Base.Shared.Data;
using Kharos.Base.Scenario;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел боя (tools/harness/combat.ts): боевые числа юнитов и парные бои. Таблица — из данных; бои — два отряда равной
/// цены дерутся на ровном поле, пока одна сторона не погибнет, по нескольку боёв.
/// </summary>
public static class Combat
{
    /// <summary>Сколько реального времени отводится одному бою, секунды.</summary>
    private const double BattleLimit = 240;

    /// <summary>Как часто бойцам раздаются цели, секунды: как в показательном бою.</summary>
    private const double OrdersEvery = 0.25;

    /// <summary>Сколько раз повторяется каждый парный бой.</summary>
    private const int Rounds = 7;

    /// <summary>Цена отряда в парном бою.</summary>
    private const double DuelBudget = 2400;

    private static readonly string[] Armors = ["infantry", "vehicle", "heavy", "air", "building"];

    private static BaseTables Data => Lib.Data;

    /// <summary>Сколько боёв считается разом: KHAROS_BALANCE_JOBS, по умолчанию один. Бои независимы, результат от числа потоков не зависит.</summary>
    public static int Jobs { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("KHAROS_BALANCE_JOBS"), out var parsed) && parsed > 0 ? parsed : 1;

    /// <summary>Всё оружие юнита: своё и на его турелях.</summary>
    private static List<string> WeaponsOf(string type)
    {
        var spec = Data.Units[type];
        var list = new List<string>();
        if (spec.Weapon is not null)
            list.Add(spec.Weapon);
        foreach (var mount in spec.Mounts)
            if (Data.Turrets[mount.Turret].Weapon is { } weapon)
                list.Add(weapon);
        return list;
    }

    private static double DpsAgainst(List<string> weapons, string armor) =>
        weapons.Sum(weapon => Data.Weapons[weapon].Damage / Data.Weapons[weapon].Reload * (Data.Weapons[weapon].Vs.TryGetValue(armor, out var factor) ? factor : 1));

    private static double RangeOf(List<string> weapons) => weapons.Count > 0 ? weapons.Max(weapon => Data.Weapons[weapon].Range) : 0;

    private sealed record Row(string Type, double Cost, double Hp, double Speed, double Range, Dictionary<string, double> Dps, double Raw, double DpsPerCredit, double HpPerCredit);

    private static List<Row> Rows() => [.. Data.Units.Keys.Where(Sim.IsFighter).Select(type =>
    {
        var spec = Data.Units[type];
        var weapons = WeaponsOf(type);
        var raw = weapons.Sum(weapon => Data.Weapons[weapon].Damage / Data.Weapons[weapon].Reload);
        return new Row(type, spec.Cost, spec.Hp, spec.Speed, RangeOf(weapons), Armors.ToDictionary(armor => armor, armor => DpsAgainst(weapons, armor)), raw, raw / spec.Cost, spec.Hp / spec.Cost);
    })];

    /// <summary>Печатает таблицу боевых чисел.</summary>
    public static void PrintStats()
    {
        Log("\nБоевые числа (урон в секунду по классам брони; без поправок — сырой dps)");
        Table(
            ["юнит", "цена", "hp", "скор", "дальн", "пех", "маш", "тяж", "возд", "здан", "сырой", "dps/кр", "hp/кр"],
            Rows().Select(row => (object[])[
                row.Type, row.Cost, row.Hp, Round(row.Speed, 1), Round(row.Range, 1),
                Round(row.Dps["infantry"], 1), Round(row.Dps["vehicle"], 1), Round(row.Dps["heavy"], 1),
                Round(row.Dps["air"], 1), Round(row.Dps["building"], 1), Round(row.Raw, 1),
                Round(row.DpsPerCredit, 3), Round(row.HpPerCredit, 2)]),
            10, 7);
    }

    /// <summary>Один бой двух заданных отрядов на ровном поле. gap — дистанция между передними рядами. Возвращает уцелевших (свои, враги).</summary>
    private static (int Own, int Foe) Fight(IReadOnlyList<string> own, IReadOnlyList<string> foe, int gap)
    {
        using var sim = Lib.NewSim();
        var width = 20 + (int)Math.Ceiling(Math.Max(own.Count, foe.Count) / 3.0) * 2;
        var spot = Lib.Arena(sim, width, 16);
        sim.SpawnBattle(1, spot.X + width / 2, spot.Y + 8, own, foe, gap);
        double spent = 0;
        while (spent < BattleLimit)
        {
            if (!sim.DriveBattle(1))
                break;
            sim.Seconds(OrdersEvery);
            spent += OrdersEvery;
        }
        var left = (Own: 0, Foe: 0);
        foreach (var unit in sim.With(Names.Unit.Name, Names.Owner.Name))
        {
            var player = sim.OwnerOf(unit);
            if (player == 1)
                left.Own++;
            else if (player != 0)
                left.Foe++;
        }
        return left;
    }

    /// <summary>Парный бой: у каждой стороны столько юнитов, сколько влезает в DuelBudget кредитов. Возвращает победы стороны a.</summary>
    private static int Duel(string a, string b)
    {
        var countA = Math.Max(1, (int)Math.Floor(DuelBudget / Data.Units[a].Cost));
        var countB = Math.Max(1, (int)Math.Floor(DuelBudget / Data.Units[b].Cost));
        var own = Enumerable.Repeat(a, countA).ToList();
        var foe = Enumerable.Repeat(b, countB).ToList();
        var winsA = 0;
        for (var round = 0; round < Rounds; round++)
        {
            // Дистанция боя меняется от раунда к раунду: так видно, решает ли бой дальнобойность.
            var result = Fight(own, foe, 4 + round);
            if (result.Own > 0 && result.Foe == 0)
                winsA++;
        }
        return winsA;
    }

    /// <summary>Печатает таблицу парных боёв: строка побеждает столбец.</summary>
    public static void PrintDuels()
    {
        var types = Rows().Select(row => row.Type).ToList();
        Log($"\nПарные бои: цена отряда ~{Num(DuelBudget)}; по {Rounds} боёв; в клетке — доля побед строки");
        var print = Grid(["", .. types]);
        // KHAROS_BALANCE_ONLY=infantry,flamer — только эти строки таблицы (для быстрой проверки); KHAROS_BALANCE_JOBS=N — N боёв разом.
        var only = Environment.GetEnvironmentVariable("KHAROS_BALANCE_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var jobs = Jobs;
        foreach (var a in types)
        {
            if (only is not null && !only.Contains(a))
                continue;
            var cells = new string[types.Count];
            Parallel.For(0, types.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, column =>
            {
                var b = types[column];
                if (a == b)
                {
                    cells[column] = "—";
                    return;
                }
                Progress($"  бой {a} против {b}");
                cells[column] = $"{Duel(a, b)}/{Rounds}";
            });
            print([a, .. cells]);
        }
    }

    /// <summary>Таблица боевых чисел и парные бои разом.</summary>
    public static void Run()
    {
        PrintStats();
        PrintDuels();
    }
}
