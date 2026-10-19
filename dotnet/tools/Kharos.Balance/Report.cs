using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kharos.Balance;

/// <summary>Что напечатал раздел: строка текста, таблица или пропущенный кусок (нет в .NET).</summary>
public abstract record Item;

/// <summary>Строка текста как есть.</summary>
public sealed record TextItem(string Text) : Item;

/// <summary>Таблица: ячейки — строки или числа. First и Rest — ширина столбцов в тексте; Grid — без разделителей (startGrid).</summary>
public sealed record TableItem(object[] Head, List<object[]> Rows) : Item
{
    public int First { get; init; } = 14;
    public int Rest { get; init; } = 9;
    public bool Grid { get; init; }
}

/// <summary>
/// Кусок раздела, который .NET пока не считает: <see cref="Covers"/> элементов эталона TS он заменяет, причина — в
/// <see cref="Reason"/>. Сверка такие куски пропускает и показывает в отчёте.
/// </summary>
public sealed record SkipItem(string Reason, int Covers) : Item;

/// <summary>
/// Машинный вывод, как у lib.ts в TS (KHAROS_HARNESS_JSON=1): раздел — список элементов; текст — строки Console.log как
/// есть, таблица — ячейки как были (числа остаются числами). Раздел (и каждый кусок <see cref="Parallel"/>) печатает в свой
/// буфер; без JSON буфер раздела выводится текстом, когда раздел готов.
/// </summary>
public static class Report
{
    /// <summary>Печатать JSON (раздел за разделом в конце), а не текст.</summary>
    public static bool Json { get; set; }

    private static readonly List<(string Name, List<Item> Items)> Collected = [];
    private static readonly AsyncLocal<List<Item>?> Current = new();
    private static readonly List<Item> Main = [];

    /// <summary>Что напечатал <paramref name="run"/>: свой буфер на время прогона.</summary>
    public static List<Item> Collect(Action run)
    {
        var was = Current.Value;
        var items = new List<Item>();
        Current.Value = items;
        try
        {
            run();
        }
        finally
        {
            Current.Value = was;
        }
        return items;
    }

    /// <summary>Готовые элементы раздела (для сверки в тестах).</summary>
    public static IReadOnlyList<Item> Capture(Action run) => Collect(run);

    /// <summary>Куски, каждый в свой буфер, разом (при <see cref="Work.Jobs"/> = 1 — по очереди); буферы — по порядку.</summary>
    public static List<Item>[] Gather(IReadOnlyList<Action> parts)
    {
        if (Work.Jobs == 1 || parts.Count <= 1)
            return [.. parts.Select(Collect)];
        var tasks = parts.Select(part => Task.Run(() => Collect(part))).ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException error) when (error.InnerExceptions.Count >= 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
        return [.. tasks.Select(task => task.Result)];
    }

    /// <summary>Куски раздела разом; напечатанное ими идёт в вывод раздела в порядке кусков.</summary>
    public static void Parallel(params Action[] parts)
    {
        foreach (var items in Gather(parts))
            Out.AddRange(items);
    }

    /// <summary>Готовый раздел: с JSON — в вывод в конце, без него — сразу текстом.</summary>
    public static void Section(string name, List<Item> items)
    {
        if (Json)
            Collected.Add((name, items));
        else
            Print(items);
    }

    private static List<Item> Out
    {
        get
        {
            if (Work.InScene)
                throw new InvalidOperationException("Сцена в Work.Map не печатает: печатает кусок по её результату");
            return Current.Value ?? Main;
        }
    }

    /// <summary>console.log: строка (или несколько) текста.</summary>
    public static void Log(string text = "")
    {
        var output = Out;
        foreach (var line in text.Split('\n'))
            output.Add(new TextItem(line));
    }

    /// <summary>Ход длинного расчёта — в stderr, чтобы stdout остался чистым для JSON.</summary>
    public static void Progress(string text) => Console.Error.WriteLine(text);

    /// <summary>Кусок, который .NET не считает; covers — сколько элементов эталона TS он заменяет.</summary>
    public static void Skip(string reason, int covers)
    {
        Progress($"  пропущено: {reason}");
        Out.Add(new SkipItem(reason, covers));
    }

    /// <summary>Текстом: строки, таблицы по ширине столбцов, пропуски.</summary>
    private static void Print(IEnumerable<Item> items)
    {
        foreach (var item in items)
            switch (item)
            {
                case TextItem text:
                    Console.WriteLine(text.Text);
                    break;
                case TableItem table:
                    string Line(object[] cells) => table.Grid
                        ? string.Concat(cells.Select(cell => Pad(cell, table.First)))
                        : string.Join(' ', cells.Select((cell, i) => Pad(cell, i == 0 ? table.First : table.Rest)));
                    Console.WriteLine(Line(table.Head));
                    foreach (var row in table.Rows)
                        Console.WriteLine(Line(row));
                    break;
                case SkipItem skip:
                    Console.WriteLine($"  [пропущено: {skip.Reason}]");
                    break;
            }
    }

    // ===== Формат =====

    /// <summary>Число как в TS: <c>Number(value.toFixed(digits))</c>.</summary>
    public static double Round(double value, int digits = 2) =>
        double.IsFinite(value) ? Math.Round(value, digits, MidpointRounding.AwayFromZero) + 0.0 : value;

    /// <summary>Число как текст в шаблонной строке TS: целое без точки, дробное кратчайшим представлением.</summary>
    public static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Pad(object? value, int width) => (value is double number ? Num(number) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").PadLeft(width);

    /// <summary>Время как «м:сс» с выравниванием минут (clock в lib.ts).</summary>
    public static string Clock(double time) => $"{Math.Floor(time / 60)}:{Math.Floor(time % 60).ToString(CultureInfo.InvariantCulture).PadLeft(2)}";

    /// <summary>Время как «м:сс» без выравнивания минут (minutes в research.ts, clock в nuke.ts).</summary>
    public static string Minutes(double time) => $"{Math.Floor(time / 60)}:{Math.Floor(time % 60):00}";

    /// <summary>Время как «ч:мм» (hours в research.ts).</summary>
    public static string Hours(double time) => $"{Math.Floor(time / 3600)}:{Math.Floor(time % 3600 / 60):00}";

    // ===== Таблицы =====

    /// <summary>Печатает таблицу: первый столбец шире остальных (printTable).</summary>
    public static void Table(object[] head, IEnumerable<object[]> rows, int first = 14, int rest = 9) =>
        Out.Add(new TableItem(head, rows.ToList()) { First = first, Rest = rest });

    /// <summary>Таблица без разделителей, строки добавляются по одной (startGrid).</summary>
    public static Action<object[]> Grid(object[] head, int width = 10)
    {
        var rows = new List<object[]>();
        Out.Add(new TableItem(head, rows) { First = width, Grid = true });
        return rows.Add;
    }

    // ===== Вывод JSON =====

    /// <summary>Элемент как узел JSON, форма TS: <c>{ text }</c>, <c>{ table: { head, rows } }</c>, <c>{ skip, covers }</c>.</summary>
    public static JsonObject ToNode(Item item) => item switch
    {
        TextItem text => new JsonObject { ["text"] = text.Text },
        TableItem table => new JsonObject
        {
            ["table"] = new JsonObject
            {
                ["head"] = new JsonArray([.. table.Head.Select(Cell)]),
                ["rows"] = new JsonArray([.. table.Rows.Select(row => (JsonNode?)new JsonArray([.. row.Select(Cell)]))]),
            },
        },
        SkipItem skip => new JsonObject { ["skip"] = skip.Reason, ["covers"] = skip.Covers },
        _ => throw new InvalidOperationException(),
    };

    private static JsonNode? Cell(object cell) => cell switch
    {
        string text => JsonValue.Create(text),
        double number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        _ => JsonValue.Create(Convert.ToString(cell, CultureInfo.InvariantCulture)),
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 1, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Раздел как <c>{ "items": [...] }</c> (файл эталона TS).</summary>
    public static string SectionJson(IEnumerable<Item> items) =>
        new JsonObject { ["items"] = new JsonArray([.. items.Select(item => (JsonNode?)ToNode(item))]) }.ToJsonString(Indented) + "\n";

    /// <summary>Выводит накопленное: <paramref name="outDir"/> — по файлу на раздел, иначе одним объектом в stdout.</summary>
    public static void Flush(string? outDir)
    {
        if (!Json)
        {
            Print(Main);
            return;
        }
        var sections = Collected.ToList();
        if (Main.Count > 0)
            sections.Insert(0, ("main", Main));
        sections.RemoveAll(section => section.Items.Count == 0);
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            foreach (var (name, items) in sections)
                File.WriteAllText(Path.Combine(outDir, name + ".json"), SectionJson(items), new UTF8Encoding(false));
            return;
        }
        var all = new JsonObject();
        foreach (var (name, items) in sections)
            all[name] = new JsonObject { ["items"] = new JsonArray([.. items.Select(item => (JsonNode?)ToNode(item))]) };
        Console.Out.Write(all.ToJsonString() + "\n");
    }
}
