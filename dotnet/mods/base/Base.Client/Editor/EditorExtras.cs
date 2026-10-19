using System.Text.Json;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Кисть мода: номер, ключ названия, приказ мода, варианты (ключи словаря) и нужен ли размер кисти.</summary>
internal sealed record EditorBrush(string Id, string Title, string Order, string[] Options, bool Size);

/// <summary>
/// Точка расширения редактора (Н6-3: мод может всё, что может base) — файл данных <c>data/editor.json</c>. Данные модов
/// накладываются друг на друга (JsonPatch), так что каждый мод дописывает своё:
/// <code>
/// {
///   "brushes": {
///     "lava": { "title": "lava.editor.brush", "order": "lava.paint", "options": ["lava.editor.hot", "lava.editor.cold"], "size": true }
///   },
///   "kinds": { "hide": ["mcv"], "units": ["walker"], "buildings": ["tower"] }
/// }
/// </code>
/// Кисть — ещё один инструмент панели: щелчок и протяжка по карте шлют приказ <c>order</c> с payload
/// <see cref="Base.Shared.Orders.EditBrushCall"/> (MemoryPack): номер кисти, вариант, тайл, размер, игрок. Приказ объявляет
/// и разбирает мод, право редактора проверяет он же. Виды: списки «Здания» и «Юниты» — все записи data/buildings.json и
/// data/units.json (вместе с правками модов), <c>kinds.units</c> и <c>kinds.buildings</c> дописывают виды, которых там нет,
/// <c>kinds.hide</c> прячет. Ставят их правки edit.building и edit.unit по имени вида.
/// </summary>
internal sealed class EditorExtras
{
    public List<EditorBrush> Brushes { get; } = [];
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    public List<string> Units { get; } = [];
    public List<string> Buildings { get; } = [];

    /// <summary>data/editor.json партии; нет файла или он испорчен — пусто (сбой — в журнал мода).</summary>
    public static EditorExtras Load(ClientContext c)
    {
        var extras = new EditorExtras();
        if (!c.TryData("editor", out var bytes))
            return extras;
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return extras;
            if (root.TryGetProperty("brushes", out var brushes) && brushes.ValueKind == JsonValueKind.Object)
                foreach (var brush in brushes.EnumerateObject())
                {
                    var record = brush.Value;
                    if (record.ValueKind != JsonValueKind.Object || Text(record, "order") is not { Length: > 0 } order)
                        continue;
                    var options = record.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                        : [];
                    var size = record.TryGetProperty("size", out var sized) && sized.ValueKind == JsonValueKind.True;
                    extras.Brushes.Add(new EditorBrush(brush.Name, Text(record, "title") ?? brush.Name, order, options, size));
                }
            if (root.TryGetProperty("kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Object)
            {
                extras.Hidden.UnionWith(Strings(kinds, "hide"));
                extras.Units.AddRange(Strings(kinds, "units"));
                extras.Buildings.AddRange(Strings(kinds, "buildings"));
            }
        }
        catch (JsonException error)
        {
            Kharos.Mod.Sdk.Host.Log($"data/editor.json: {error.Message}", Kharos.Mod.Sdk.LogLevel.Warning);
        }
        return extras;
    }

    private static string? Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement record, string name) =>
        record.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
            : [];
}
