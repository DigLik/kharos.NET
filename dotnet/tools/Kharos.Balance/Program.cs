using System.Text;

namespace Kharos.Balance;

/// <summary>Разделы харнесса по имени (modes в tools/balance.ts).</summary>
public static class Modes
{
    /// <summary>Разделы, которые идут в «all»: каждый пишет эталон <c>&lt;раздел&gt;.json</c>.</summary>
    public static readonly string[] All = ["combat", "economy", "siege", "raid", "research", "nuke", "movement", "crossing"];

    private static readonly Dictionary<string, Action> Table = new()
    {
        ["table"] = Combat.PrintStats,
        ["duel"] = Combat.PrintDuels,
        ["combat"] = Combat.Run,
        ["economy"] = Economy.Run,
        ["siege"] = Siege.Run,
        ["raid"] = Raid.Run,
        ["research"] = ResearchSection.Run,
        ["nuke"] = Nuke.Run,
        ["movement"] = Movement.Run,
        ["formation"] = Movement.PrintFormation,
        ["surround"] = Movement.PrintSurround,
        ["builders"] = Movement.PrintBuilders,
        ["load"] = Movement.PrintLoad,
        ["crossing"] = Movement.PrintCrossing,
    };

    public static IEnumerable<string> Names => Table.Keys.Append("all");

    /// <summary>Печатает раздел в список элементов (для сверки в BalanceTests): без вывода в консоль, прогресс — в stderr.</summary>
    public static IReadOnlyList<Item> Capture(string mode) =>
        Table.TryGetValue(mode, out var run) ? Report.Capture(run) : throw new ArgumentException($"Нет раздела «{mode}»", nameof(mode));

    /// <summary>Запускает раздел (или «all») под его именем; false — такого нет.</summary>
    public static bool Run(string mode)
    {
        if (mode == "all")
        {
            foreach (var name in All)
                Report.Section(name, Table[name]);
            return true;
        }
        if (!Table.TryGetValue(mode, out var run))
            return false;
        Report.Section(mode, run);
        return true;
    }
}

/// <summary>
/// Балансный харнесс (аналог <c>bun run balance [раздел]</c>): <c>dotnet run --project tools/Kharos.Balance -c Release -- all --out папка</c>.
/// Без <c>--json</c> печатает таблицы текстом; с <c>--json</c> (или KHAROS_HARNESS_JSON=1) — JSON той же формы, что у TS, а
/// <c>--out папка</c> (KHAROS_HARNESS_OUT) — по файлу на раздел, как эталоны tests/Kharos.Base.Tests/Balance/golden.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var json = Environment.GetEnvironmentVariable("KHAROS_HARNESS_JSON") == "1";
        var outDir = Environment.GetEnvironmentVariable("KHAROS_HARNESS_OUT") is { Length: > 0 } env ? env : null;
        string? mode = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    json = true;
                    break;
                default:
                    mode ??= args[i];
                    break;
            }
        }
        Report.Json = json;
        mode ??= "combat";
        if (!Modes.Run(mode))
        {
            Console.Error.WriteLine($"Неизвестный раздел «{mode}». Есть: {string.Join(", ", Modes.Names)}");
            return 1;
        }
        Report.Flush(outDir);
        return 0;
    }
}
