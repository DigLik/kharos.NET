using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kharos.Balance;

/// <summary>Что напечатал раздел: строка текста, таблица или пропущенный кусок (нет в .NET).</summary>
public abstract record Item;

/// <summary>Строка текста как есть.</summary>
public sealed record TextItem(string Text) : Item;

/// <summary>Таблица: ячейки — строки или числа.</summary>
public sealed record TableItem(object[] Head, List<object[]> Rows) : Item;

/// <summary>
/// Кусок раздела, который .NET пока не считает: <see cref="Covers"/> элементов эталона TS он заменяет, причина — в
/// <see cref="Reason"/>. Сверка такие куски пропускает и показывает в отчёте.
/// </summary>
public sealed record SkipItem(string Reason, int Covers) : Item;

/// <summary>
/// Машинный вывод, как у lib.ts в TS (KHAROS_HARNESS_JSON=1): раздел — список элементов; текст — строки Console.log как
/// есть, таблица — ячейки как были (числа остаются числами). Без JSON строки и таблицы печатаются сразу.
/// </summary>
public static class Report
{
    /// <summary>Печатать JSON (раздел за разделом в конце), а не текст по ходу.</summary>
    public static bool Json { get; set; }

    private static readonly Dictionary<string, List<Item>> Collected = [];
    private static List<Item>? _current;

    /// <summary>Запускает раздел под своим именем.</summary>
    public static void Section(string name, Action run)
    {
        if (!Json)
        {
            run();
            return;
        }
        _current = Collected[name] = [];
        try
        {
            run();
        }
        finally
        {
            _current = null;
        }
    }

    /// <summary>Готовые элементы раздела (для сверки в тестах).</summary>
    public static IReadOnlyList<Item> Capture(Action run)
    {
        var was = (Json, _current);
        Json = true;
        var items = new List<Item>();
        _current = items;
        try
        {
            run();
        }
        finally
        {
            (Json, _current) = was;
        }
        return items;
    }

    private static List<Item> Out => _current ??= Collected.TryGetValue("main", out var main) ? main : Collected["main"] = [];

    /// <summary>console.log: строка (или несколько) текста.</summary>
    public static void Log(string text = "")
    {
        foreach (var line in text.Split('\n'))
        {
            if (Json)
                Out.Add(new TextItem(line));
            else
                Console.WriteLine(line);
        }
    }

    /// <summary>Ход длинного расчёта — в stderr, чтобы stdout остался чистым для JSON.</summary>
    public static void Progress(string text) => Console.Error.WriteLine(text);

    /// <summary>Кусок, который .NET не считает; covers — сколько элементов эталона TS он заменяет.</summary>
    public static void Skip(string reason, int covers)
    {
        Progress($"  пропущено: {reason}");
        if (Json)
            Out.Add(new SkipItem(reason, covers));
        else
            Console.WriteLine($"  [пропущено: {reason}]");
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
    public static void Table(object[] head, IEnumerable<object[]> rows, int first = 14, int rest = 9)
    {
        var list = rows.ToList();
        if (Json)
        {
            Out.Add(new TableItem(head, list));
            return;
        }
        string Line(object[] cells) => string.Join(' ', cells.Select((cell, i) => Pad(cell, i == 0 ? first : rest)));
        Console.WriteLine(Line(head));
        foreach (var row in list)
            Console.WriteLine(Line(row));
    }

    /// <summary>Таблица без разделителей, строки идут по мере готовности (startGrid).</summary>
    public static Action<object[]> Grid(object[] head, int width = 10)
    {
        var rows = new List<object[]>();
        string Line(object[] cells) => string.Concat(cells.Select(cell => Pad(cell, width)));
        if (Json)
            Out.Add(new TableItem(head, rows));
        else
            Console.WriteLine(Line(head));
        return cells =>
        {
            if (Json)
                rows.Add(cells);
            else
                Console.WriteLine(Line(cells));
        };
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
            return;
        foreach (var name in Collected.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList())
            Collected.Remove(name);
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            foreach (var (name, items) in Collected)
                File.WriteAllText(Path.Combine(outDir, name + ".json"), SectionJson(items), new UTF8Encoding(false));
            return;
        }
        var all = new JsonObject();
        foreach (var (name, items) in Collected)
            all[name] = new JsonObject { ["items"] = new JsonArray([.. items.Select(item => (JsonNode?)ToNode(item))]) };
        Console.Out.Write(all.ToJsonString() + "\n");
    }
}
