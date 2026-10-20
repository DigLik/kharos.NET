using System.Text;
using System.Text.RegularExpressions;
using Kharos.Balance;

namespace Kharos.Base.Tests.Balance;

/// <summary>
/// Баланс .NET против эталонов TS (Н7-1 б, <c>Balance/golden/ts/*.json</c>). Разделы без правила погони — economy, research,
/// movement, nuke и <c>combat/table</c> — в допуске регрессии (<see cref="Tolerance.Default"/>: доли ±5 %, время ±2 с, счёт ±1).
/// Разделы с атакой — <c>combat/duel</c>, siege, raid — тот же победитель, время и остаток ±10 % (<see cref="Attack"/>).
/// Расхождения сверх допуска, причина которых найдена и принята, перечислены в <see cref="Accepted"/> и в
/// <c>Balance/golden/ts/DIFF.md</c>; тест падает только на непринятых. Как и <see cref="BalanceTests"/>, — <c>Explicit</c>:
/// <c>dotnet run -c Release --project tests/Kharos.Base.Tests -- --explicit only --filter-class "*BalanceVsTsTests"</c>.
/// </summary>
public sealed class BalanceVsTsTests
{
    private static readonly Regex Fraction = new(@"^(\d+)/(\d+)$", RegexOptions.Compiled);

    /// <summary>Разделы с атакой: время и остаток ±10 %, счёт ±1.</summary>
    public static readonly Tolerance Attack = Tolerance.Default with { Share = 0.10 };

    /// <summary>Клетка «k/n» парного боя — доля побед строки: сверяется победитель (k &gt; n/2), а не число побед.</summary>
    private static string? DuelWinner(string golden, string actual)
    {
        if (golden == actual)
            return null;
        if (Fraction.Match(golden) is not { Success: true } g || Fraction.Match(actual) is not { Success: true } a)
            return "текст отличается";
        static bool Wins(Match m) => 2 * int.Parse(m.Groups[1].Value) > int.Parse(m.Groups[2].Value);
        return Wins(g) == Wins(a) ? null : $"другой победитель: {golden} против {actual}";
    }

    /// <summary>«Турелей» штурма: победил атакующий, если снёс все три; число снесённых при поражении — остаток в счёт ±1.</summary>
    private static string? SiegeWinner(string golden, string actual)
    {
        if (golden == actual)
            return null;
        if (Fraction.Match(golden) is not { Success: true } g || Fraction.Match(actual) is not { Success: true } a)
            return "текст отличается";
        static bool Won(Match m) => m.Groups[1].Value == m.Groups[2].Value;
        if (Won(g) != Won(a))
            return $"другой победитель: {golden} против {actual}";
        return Math.Abs(int.Parse(g.Groups[1].Value) - int.Parse(a.Groups[1].Value)) <= Attack.Count ? null : $"снесено {golden} против {actual}";
    }

    /// <summary>
    /// Принятые расхождения (<c>Balance/golden/ts/DIFF.md</c>): (раздел, номер элемента в файле эталона TS, строка таблицы по
    /// первой ячейке или null — весь элемент) → причина. «Б2: …» — ошибка или подозрение для пакета Б2: исправив, он
    /// убирает строку отсюда и из DIFF.md, и сверка снова требует допуска.
    /// </summary>
    private static readonly (string Section, int Item, string? Row, string Why)[] Accepted =
    [
        ("combat", 5, "lancer", "ход боя: lancer против drone 3/7 → 4/7, один бой из семи у ничьей"),
        ("combat", 5, "carrier", "Б2: carrier против lancer 5/7 → 2/7, носитель турелей"),
        ("combat", 5, "drone", "Б2: drone против tesla 4/7 → 0/7, носитель турелей"),
        ("siege", 3, null, "правило погони (повтор приказа раз в 0,25 с) и ход боя; остаток — Б2"),
        ("raid", 3, null, "правило погони (повтор приказа раз в 0,25 с) и ход боя; остаток — Б2"),
        ("movement", 2, "встречные 12+12", "движок: рулёжка на встречных, .NET вихляет меньше"),
        ("movement", 2, "атака 12", "Б2: ход боя, места у цели (один приказ, без погони)"),
        ("movement", 5, null, "ход боя: места у цели, у .NET стреляют все (один приказ, без погони)"),
    ];

    /// <summary>Прогоняет раздел харнесса и сверяет с эталоном TS; падает на непринятых расхождениях.</summary>
    private static void Check(string mode)
    {
        var (section, _, tolerance, offset, take) = Sections.Single(entry => entry.Mode == mode);
        Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is not null, Sim.Unavailable(Sim.DefaultMode) ?? "");
        var items = Modes.Capture(mode);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Balance", "actual"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "Balance", "actual", mode + ".json"), Report.SectionJson(items), new UTF8Encoding(false));
        var report = Verdict(section, items, tolerance, offset, take, out var failed);
        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(failed == 0, report);
    }

    /// <summary>Сверка раздела с эталоном TS: отчёт и сколько расхождений не принято.</summary>
    public static string Verdict(string section, IReadOnlyList<Item> items, Tolerance tolerance, int offset, int? take, out int failed)
    {
        var all = GoldenCompare.Ts(section);
        var expected = take is { } count ? new System.Text.Json.Nodes.JsonArray([.. all.Skip(offset).Take(count).Select(node => node?.DeepClone())]) : all;
        var result = GoldenCompare.Compare(expected, items, tolerance, take is null ? offset : 0);
        var accepted = new List<(string Diff, string Why)>();
        var used = new HashSet<string>();
        var open = new List<string>();
        foreach (var diff in result.Diffs)
        {
            // Номер элемента в сообщении — от начала куска; в списке принятых — от начала файла эталона.
            var at = Regex.Match(diff, @"^#(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) + (take is null ? 0 : offset) : -1;
            var why = Accepted.FirstOrDefault(entry => entry.Section == section && entry.Item == at
                && (entry.Row is null || Regex.IsMatch(diff, $@"^#\d+ строка \d+ «{Regex.Escape(entry.Row)}»"))).Why;
            if (why is not null)
            {
                accepted.Add((diff, why));
                used.Add(why);
            }
            else
                open.Add(diff);
        }
        var report = new StringBuilder($"{section}/{Part(offset, take)} против TS: сверено ячеек и строк {result.Cells}, вне допуска {result.Diffs.Count}, из них принято {accepted.Count}, не принято {open.Count}\n");
        foreach (var skipped in result.Skipped)
            report.AppendLine("  пропущено: " + skipped);
        foreach (var diff in open)
            report.AppendLine("  НЕ ПРИНЯТО " + diff);
        foreach (var (diff, why) in accepted)
            report.AppendLine($"  принято ({why}) " + diff);
        // Принятое, которого больше нет (например, Б2 исправил): строку пора убрать отсюда и из DIFF.md.
        var first = take is null ? 0 : offset;
        var last = take is { } length ? offset + length - 1 : int.MaxValue;
        foreach (var entry in Accepted.Where(entry => entry.Section == section && entry.Item >= first && entry.Item <= last && !used.Contains(entry.Why)))
            report.AppendLine($"  устарело (расхождения нет): {entry.Why}");
        failed = open.Count;
        return report.ToString();

        static string Part(int offset, int? take) => take is null ? "всё" : $"{offset}..{offset + take - 1}";
    }

    /// <summary>Как сверяется каждый раздел: (раздел, режим харнесса, допуск, с какого элемента эталона, сколько элементов).</summary>
    private static readonly (string Section, string Mode, Tolerance Tolerance, int Offset, int? Take)[] Sections =
    [
        ("combat", "table", Tolerance.Default, 0, 3),
        ("combat", "duel", Attack with { Columns = new Dictionary<string, Func<string, string, string?>> { ["*"] = DuelWinner } }, 3, 3),
        ("economy", "economy", Tolerance.Default, 0, null),
        ("siege", "siege", Attack with { Columns = new Dictionary<string, Func<string, string, string?>> { ["турелей"] = SiegeWinner } }, 0, null),
        ("raid", "raid", Attack, 0, null),
        ("research", "research", Tolerance.Default, 0, null),
        ("nuke", "nuke", Tolerance.Default, 0, null),
        ("movement", "movement", Tolerance.Default, 0, null),
    ];

    /// <summary>
    /// Сверка готового вывода харнесса без прогона: <c>KHAROS_BALANCE_FROM=&lt;папка&gt;</c> — файлы <c>all --out</c>
    /// (по разделу на файл). Для разбора расхождений и правки DIFF.md: секунды вместо минуты.
    /// </summary>
    [Fact(Explicit = true, DisplayName = "баланс против TS: готовый вывод харнесса из KHAROS_BALANCE_FROM")]
    public void FromFiles()
    {
        var from = Environment.GetEnvironmentVariable("KHAROS_BALANCE_FROM");
        Assert.SkipWhen(from is not { Length: > 0 }, "KHAROS_BALANCE_FROM не задан");
        var report = new StringBuilder();
        var failed = 0;
        foreach (var (section, mode, tolerance, offset, take) in Sections)
        {
            var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(from!, section + ".json")))!["items"]!.AsArray();
            var items = GoldenCompare.Items(file);
            var part = take is { } count ? items.Skip(offset).Take(count).ToList() : items;
            report.Append(Verdict(section, part, tolerance, offset, take, out var open));
            failed += open;
        }
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Assert.True(failed == 0, report.ToString());
    }

    [Fact(Explicit = true, DisplayName = "баланс против TS: боевые числа юнитов (таблица из данных) в допуске")]
    public void CombatTable() => Check("table");

    [Fact(Explicit = true, DisplayName = "баланс против TS: в парных боях тот же победитель")]
    public void CombatDuels() => Check("duel");

    [Fact(Explicit = true, DisplayName = "баланс против TS: рудник, производство и разгон в допуске")]
    public void Economy() => Check("economy");

    [Fact(Explicit = true, DisplayName = "баланс против TS: штурм узла обороны — тот же победитель, время и остаток ±10 %")]
    public void Siege() => Check("siege");

    [Fact(Explicit = true, DisplayName = "баланс против TS: набег на экономику — тот же исход, время и остаток ±10 %")]
    public void Raid() => Check("raid");

    [Fact(Explicit = true, DisplayName = "баланс против TS: компоненты и лестница исследований в допуске")]
    public void Research() => Check("research");

    [Fact(Explicit = true, DisplayName = "баланс против TS: ядерный удар и защита в допуске")]
    public void Nuke() => Check("nuke");

    [Fact(Explicit = true, DisplayName = "баланс против TS: движение — строй, окружение, строители, нагрузка в допуске")]
    public void Movement() => Check("movement");
}
