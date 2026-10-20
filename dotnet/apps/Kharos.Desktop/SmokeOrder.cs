using System.Buffers;
using System.Globalization;
using System.Text.Json;
using MemoryPack;

namespace Kharos.Desktop;

/// <summary>
/// Приказ смоука (<c>order</c>): имя и payload — тем же путём, что приказы клиента (ClientGame.Send, как Sent у Viewer).
/// Payload — объект MemoryPack, собранный по полям по порядку: <c>"args": [{"u32[]": "@mcv"}, {"i32": 10}, {"bool": false}]</c>
/// (виды: bool, u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, str и массивы с «[]»), или готовые байты
/// <c>"payload": "base64"</c>. Значение-строка «@вид» — свои сущности этого вида (у скаляра — первая), «@вид.x» и «@вид.y» —
/// место первой из них; через «+N» и «−N» — сдвиг (<c>"@mcv.x+3"</c>). «@*» — все свои сущности.
/// </summary>
internal static class SmokeOrder
{
    /// <summary>Имя и payload из шага: поля name, args, payload — у шага или в JSON после глагола.</summary>
    public static (string Name, byte[] Payload) Build(SmokeStep step, Func<string, double[]> resolve)
    {
        var source = step.Raw;
        string? name = step.Field("name");
        JsonDocument? inline = null;
        try
        {
            if (step.Rest.StartsWith('{'))
            {
                inline = JsonDocument.Parse(step.Rest);
                source = inline.RootElement;
                name = source.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            }
            else if (step.Words.Length > 0)
                name = step.Words[0];
            if (string.IsNullOrEmpty(name))
                throw new FormatException("order: нет имени приказа");
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("payload", out var raw) && raw.ValueKind == JsonValueKind.String)
                return (name, Convert.FromBase64String(raw.GetString()!));
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
                return (name, []);
            return (name, Pack([.. args.EnumerateArray()], resolve));
        }
        finally
        {
            inline?.Dispose();
        }
    }

    /// <summary>Объект MemoryPack: заголовок с числом полей и поля по порядку.</summary>
    public static byte[] Pack(IReadOnlyList<JsonElement> args, Func<string, double[]> resolve)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var state = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, state);
        writer.WriteObjectHeader((byte)args.Count);
        foreach (var arg in args)
        {
            if (arg.ValueKind != JsonValueKind.Object)
                throw new FormatException("order: поле — объект {\"вид\": значение}");
            using var props = arg.EnumerateObject();
            if (!props.MoveNext())
                throw new FormatException("order: пустое поле");
            var (type, value) = (props.Current.Name, props.Current.Value);
            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                var item = type[..^2];
                if (item == "str")
                {
                    var words = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [value.GetString() ?? ""];
                    writer.WriteCollectionHeader(words.Length);
                    foreach (var word in words)
                        writer.WriteString(word);
                    continue;
                }
                var numbers = value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().SelectMany(v => Numbers(v, resolve)).ToArray()
                    : Numbers(value, resolve);
                writer.WriteCollectionHeader(numbers.Length);
                foreach (var number in numbers)
                    Scalar(ref writer, item, number);
                continue;
            }
            if (type == "str")
            {
                writer.WriteString(value.GetString());
                continue;
            }
            var all = Numbers(value, resolve);
            if (all.Length == 0)
                throw new FormatException($"order: нет значения для {value}");
            Scalar(ref writer, type, all[0]);
        }
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static double[] Numbers(JsonElement value, Func<string, double[]> resolve) => value.ValueKind switch
    {
        JsonValueKind.Number => [value.GetDouble()],
        JsonValueKind.True => [1],
        JsonValueKind.False => [0],
        JsonValueKind.String when value.GetString() is { } text && text.StartsWith('@') => resolve(text[1..]),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => [number],
        _ => throw new FormatException($"order: не число: {value}"),
    };

    private static void Scalar(ref MemoryPackWriter<ArrayBufferWriter<byte>> writer, string type, double value)
    {
        switch (type)
        {
            case "bool": writer.WriteUnmanaged(value != 0); break;
            case "u8": writer.WriteUnmanaged((byte)value); break;
            case "i8": writer.WriteUnmanaged((sbyte)value); break;
            case "u16": writer.WriteUnmanaged((ushort)value); break;
            case "i16": writer.WriteUnmanaged((short)value); break;
            case "u32": writer.WriteUnmanaged((uint)value); break;
            case "i32": writer.WriteUnmanaged((int)Math.Floor(value)); break;
            case "u64": writer.WriteUnmanaged((ulong)value); break;
            case "i64": writer.WriteUnmanaged((long)Math.Floor(value)); break;
            case "f32": writer.WriteUnmanaged((float)value); break;
            case "f64": writer.WriteUnmanaged(value); break;
            default: throw new FormatException($"order: нет вида поля «{type}»");
        }
    }
}
