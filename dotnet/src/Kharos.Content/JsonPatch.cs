using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kharos.Content;

/// <summary>
/// Патчи данных модов: файл data/x.json позднего мода накладывается на тот же файл ранних. Объект на объект —
/// поле за полем вглубь; null — убрать поле; иначе — заменить. Так мод меняет число у чужого вида, добавляет вид или
/// убирает его (как patch у Luau-пилота, только данными).
/// </summary>
public static class JsonPatch
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Разбор файла данных; не JSON — <see cref="ContentException"/>.</summary>
    public static JsonNode Parse(ReadOnlySpan<byte> bytes, string where)
    {
        try
        {
            return JsonNode.Parse(bytes, documentOptions: Options) ?? throw new ContentException($"{where}: пусто (null)");
        }
        catch (JsonException error)
        {
            throw new ContentException($"{where}: {error.Message}", error);
        }
    }

    /// <summary>Накладывает patch на target; возвращает итог (target, если оба — объекты, иначе копию patch).</summary>
    public static JsonNode? Merge(JsonNode? target, JsonNode? patch)
    {
        if (target is JsonObject into && patch is JsonObject from)
        {
            foreach (var (name, value) in from.ToList())
            {
                if (value is null)
                {
                    into.Remove(name);
                    continue;
                }
                if (into[name] is JsonObject existing && value is JsonObject)
                    Merge(existing, value);
                else
                    into[name] = value.DeepClone();
            }
            return into;
        }
        return patch?.DeepClone();
    }

    /// <summary>Канонические байты: без отступов, поля в порядке объявления — одинаково у хоста и клиента.</summary>
    public static byte[] Write(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            node.WriteTo(writer);
        return stream.ToArray();
    }
}
