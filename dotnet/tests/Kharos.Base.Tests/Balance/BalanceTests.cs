using System.Text;
using Kharos.Balance;

namespace Kharos.Base.Tests.Balance;

/// <summary>
/// Балансный харнесс против эталонов TS (docs/dotnet-n5.md, Н5.6): сцены tools/Kharos.Balance прогоняются на .NET и
/// сверяются с <c>Balance/golden/*.json</c> (<c>KHAROS_HARNESS_JSON=1 bun tools/balance.ts all</c>) с допуском: доли ±5%, время
/// ±2 с, счёт ±1. Долгие (минуты) и нужны не в каждом прогоне, поэтому <c>Explicit</c>: запускаются руками,
/// <c>dotnet run -c Release --project tests/Kharos.Base.Tests -- --explicit only --filter-class "*BalanceTests"</c>
/// (или <c>--filter-method "*Siege"</c> для одного). Режим кода мода — <c>KHAROS_MODS</c> (CLR по умолчанию). Что .NET пока
/// не считает, харнесс пропускает с причиной (<c>SkipItem</c>) — такие куски перечислены в выводе теста, но не валят его.
/// Тест падает, если доля расхождений больше <c>KHAROS_BALANCE_ALLOW</c> (по умолчанию 0); вывод .NET лежит рядом с тестами в
/// <c>Balance/actual/&lt;раздел&gt;.json</c> — тот же вид, что у эталона, для diff.
/// </summary>
public sealed class BalanceTests
{
    private static void Check(string golden, string mode, int offset = 0, int? take = null)
    {
        Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is not null, Sim.Unavailable(Sim.DefaultMode) ?? "");
        var items = Modes.Capture(mode);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Balance", "actual"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "Balance", "actual", mode + ".json"), Report.SectionJson(items), new UTF8Encoding(false));
        var all = GoldenCompare.Golden(golden);
        var expected = take is { } count ? new System.Text.Json.Nodes.JsonArray([.. all.Skip(offset).Take(count).Select(node => node?.DeepClone())]) : all;
        var result = GoldenCompare.Compare(expected, items, take is null ? offset : 0);
        var allowed = double.TryParse(Environment.GetEnvironmentVariable("KHAROS_BALANCE_ALLOW"), System.Globalization.CultureInfo.InvariantCulture, out var share) ? share : 0;
        var report = new StringBuilder($"{mode}: сверено ячеек и строк {result.Cells}, расхождений {result.Diffs.Count} ({result.Share:P1}), допуск {allowed:P1}\n");
        foreach (var skipped in result.Skipped)
            report.AppendLine("  пропущено: " + skipped);
        foreach (var diff in result.Diffs.Take(200))
            report.AppendLine("  " + diff);
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Assert.True(result.Share <= allowed, report.ToString());
    }

    [Fact(Explicit = true, DisplayName = "баланс: боевые числа юнитов (таблица из данных) совпадают с TS")]
    public void CombatTable() => Check("combat", "table", 0, 3);

    [Fact(Explicit = true, DisplayName = "баланс: парные бои отрядов равной цены совпадают с TS (долго: до получаса, KHAROS_BALANCE_JOBS ускоряет)")]
    public void CombatDuels()
    {
        if (Environment.GetEnvironmentVariable("KHAROS_BALANCE_JOBS") is null)
            Kharos.Balance.Combat.Jobs = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        Check("combat", "duel", 3, 3);
    }

    [Fact(Explicit = true, DisplayName = "баланс: рудник, производство и разгон совпадают с TS")]
    public void Economy() => Check("economy", "economy");

    [Fact(Explicit = true, DisplayName = "баланс: штурм узла обороны совпадает с TS")]
    public void Siege() => Check("siege", "siege");

    [Fact(Explicit = true, DisplayName = "баланс: набег на экономику совпадает с TS")]
    public void Raid() => Check("raid", "raid");

    [Fact(Explicit = true, DisplayName = "баланс: компоненты и лестница исследований совпадают с TS")]
    public void Research() => Check("research", "research");

    [Fact(Explicit = true, DisplayName = "баланс: ядерный удар и защита — цены, взрыв, удар по базе песочницы и залп против перехватчика совпадают с TS")]
    public void Nuke() => Check("nuke", "nuke");

    [Fact(Explicit = true, DisplayName = "баланс: движение — строй, окружение, строители, нагрузка совпадают с TS")]
    public void Movement() => Check("movement", "movement");
}
