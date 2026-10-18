using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>
/// Точность чисел в сети (docs/dotnet.md §4.4): у каждого числового поля — масштаб, сколько долей единицы различимо.
/// Число уходит целым round(value × scale) — varint (zigzag, сдвинутый на бит); не конечное или слишком большое —
/// флагом 1 и f64 как есть. Масштаб 0 — точно, f64. Флаги, строки (номера) и байты — всегда точно.
/// По умолчанию: <see cref="Builtin.Position"/> — 1/256 тайла, <see cref="Builtin.Facing"/> — 1/10000 рад, прочие числа —
/// 1/1000 (как JSON компонентов в TS). Целые до 2^40 проходят точно при любом из этих масштабов.
/// <code>
/// число: varint v; v &amp; 1 = 0 — целое q = unzigzag(v &gt;&gt; 1), значение q / scale; v = 1 — f64
/// </code>
/// </summary>
public sealed class NetFormat
{
    /// <summary>Масштабы по умолчанию: место, угол, прочие числа.</summary>
    public const double PositionScale = 256, AngleScale = 10000, NumberScale = 1000;

    /// <summary>Больше этого по модулю целое не квантуется, а уходит f64.</summary>
    private const double Most = 1L << 50;

    private readonly double[][] _scales;

    /// <param name="scales">Масштаб поля по компоненту и номеру поля; у не-чисел — 0.</param>
    public NetFormat(double[][] scales) => _scales = scales;

    /// <summary>Точный формат: все числа — f64.</summary>
    public static NetFormat Exact(Schema schema) => Of(schema, 0);

    /// <summary>
    /// Формат по схеме: место и угол — своими масштабами, прочие числа — number (0 — точно); overrides —
    /// «Компонент.поле» → масштаб, сильнее всего остального.
    /// </summary>
    public static NetFormat Of(Schema schema, double number = NumberScale, IReadOnlyDictionary<string, double>? overrides = null, bool exactPlaces = false)
    {
        var scales = new double[schema.Count][];
        for (var c = 0; c < schema.Count; c++)
        {
            var def = schema[c];
            scales[c] = new double[def.Fields.Count];
            foreach (var field in def.Fields)
            {
                if (field.Kind != FieldKind.Number)
                    continue;
                var scale = exactPlaces || number == 0 ? 0 : c switch
                {
                    Builtin.Position => PositionScale,
                    Builtin.Facing => AngleScale,
                    _ => number,
                };
                if (overrides is not null && overrides.TryGetValue($"{def.Name}.{field.Name}", out var chosen))
                    scale = chosen;
                scales[c][field.Index] = scale is > 0 and < double.PositiveInfinity ? scale : 0;
            }
        }
        return new NetFormat(scales);
    }

    /// <summary>Масштаб поля; 0 — точно.</summary>
    public double Scale(int component, int field) => _scales[component][field];

    /// <summary>Значение, каким его получит реплика.</summary>
    public double Quantize(int component, int field, double value)
    {
        var scale = _scales[component][field];
        var q = value * scale;
        if (scale == 0 || !double.IsFinite(q) || Math.Abs(q) >= Most)
            return value;
        return Math.Round(q) / scale;
    }

    /// <summary>Наибольшая ошибка квантования поля (половина шага).</summary>
    public double Error(int component, int field) => _scales[component][field] is var scale && scale > 0 ? 0.5 / scale : 0;

    public void Write(WireWriter w, int component, int field, double value)
    {
        var scale = _scales[component][field];
        if (scale == 0)
        {
            w.F64(value);
            return;
        }
        var q = value * scale;
        if (!double.IsFinite(q) || Math.Abs(q) >= Most)
        {
            w.Varint(1).F64(value);
            return;
        }
        var n = (long)Math.Round(q);
        w.Varint(((ulong)((n << 1) ^ (n >> 63))) << 1);
    }

    public double Read(ref WireReader r, int component, int field)
    {
        var scale = _scales[component][field];
        if (scale == 0)
            return r.F64();
        var v = r.Varint();
        if (v == 1)
            return r.F64();
        if ((v & 1) != 0)
            throw new FormatException($"число: метка {v}");
        var z = v >> 1;
        var n = (long)(z >> 1) ^ -(long)(z & 1);
        return n / scale;
    }
}
