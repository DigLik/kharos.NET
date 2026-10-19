using System.Text;
using Kharos.Gpu;
using StbTrueTypeSharp;

namespace Kharos.Client.Ui;

/// <summary>Шрифтовой атлас R8 <see cref="Batch.AtlasSize"/>²; левый верхний угол 4 × 4 — белый, для заливки.</summary>
public sealed class FontAtlas
{
    public const int Size = Batch.AtlasSize;
    public readonly byte[] Pixels = new byte[Size * Size];
    /// <summary>Растёт с каждой правкой: рендер знает, когда перезалить.</summary>
    public long Generation { get; private set; } = 1;

    public FontAtlas() => Clear();

    public void Clear()
    {
        Array.Clear(Pixels);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                Pixels[y * Size + x] = 255;
        Generation++;
    }

    internal void Touch() => Generation++;
}

/// <summary>
/// Лицо шрифта на stb_truetype (управляемый перенос, без платформы): метрики по hmtx и глифы с порогом альфы.
/// Масштаб — пикселей на em, как у fontdue, а не по высоте строки.
/// </summary>
internal sealed unsafe class FontFace : IDisposable
{
    private readonly StbTrueType.stbtt_fontinfo _info;
    private readonly Dictionary<int, int> _glyphs = [];
    private readonly Dictionary<int, int> _advances = [];
    private readonly Dictionary<long, int> _kerning = [];

    public FontFace(byte[] bytes)
    {
        _info = StbTrueType.CreateFont(bytes, 0) ?? throw new InvalidDataException("шрифт: не читается");
    }

    public void Dispose() => _info.Dispose();

    /// <summary>Есть ли у лица буква (не .notdef).</summary>
    public bool Has(int codepoint) => Glyph(codepoint) != 0;

    private int Glyph(int codepoint)
    {
        if (!_glyphs.TryGetValue(codepoint, out var glyph))
            _glyphs[codepoint] = glyph = StbTrueType.stbtt_FindGlyphIndex(_info, codepoint);
        return glyph;
    }

    private float Scale(float px) => StbTrueType.stbtt_ScaleForMappingEmToPixels(_info, px);

    /// <summary>Шаг пера в пикселях при размере px (advance_width у fontdue: hmtx × px / em, в f32).</summary>
    public float Advance(int codepoint, float px)
    {
        var glyph = Glyph(codepoint);
        if (!_advances.TryGetValue(glyph, out var advance))
        {
            int width, bearing;
            StbTrueType.stbtt_GetGlyphHMetrics(_info, glyph, &width, &bearing);
            _advances[glyph] = advance = width;
        }
        return advance * Scale(px);
    }

    /// <summary>Поправка пары букв по GPOS (у Ark Pixel — −1 пиксель кегля у пар вроде AV, To) в пикселях при размере px.</summary>
    public float Kern(int left, int right, float px)
    {
        var pair = ((long)Glyph(left) << 32) | (uint)Glyph(right);
        if (!_kerning.TryGetValue(pair, out var units))
            _kerning[pair] = units = StbTrueType.stbtt_GetGlyphKernAdvance(_info, (int)(pair >> 32), (int)(uint)pair);
        return units * Scale(px);
    }

    /// <summary>
    /// Глиф при размере px: ширина, высота, левый край и верх от базовой линии (вниз — плюс), пиксели 0/255 по порогу
    /// альфы (threshold = false — покрытие как есть, для проверок).
    /// </summary>
    public (int Width, int Height, int Left, int Top, byte[] Pixels) Rasterize(int codepoint, float px, bool threshold = true)
    {
        var glyph = Glyph(codepoint);
        var scale = Scale(px);
        int x0, y0, x1, y1;
        StbTrueType.stbtt_GetGlyphBitmapBox(_info, glyph, scale, scale, &x0, &y0, &x1, &y1);
        int w = Math.Max(x1 - x0, 0), h = Math.Max(y1 - y0, 0);
        var pixels = new byte[w * h];
        if (w > 0 && h > 0)
        {
            fixed (byte* output = pixels)
                StbTrueType.stbtt_MakeGlyphBitmap(_info, output, w, h, w, scale, scale, glyph);
            // Порог альфы: буквы пиксельного шрифта без ореола.
            if (threshold)
            for (var i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] >= 128 ? (byte)255 : (byte)0;
        }
        return (w, h, x0, y0, pixels);
    }
}

/// <summary>
/// Текст интерфейса — перенос <c>Fonts</c> из gui.rs: глифы в физических пикселях кладутся в атлас по требованию,
/// ширины строк — в логических, с округлением вверх, как measureText в TS.
/// </summary>
public sealed class Fonts : IDisposable
{
    private readonly record struct Glyph(Uv Uv, float Width, float Height, float Left, float Top);

    internal readonly FontFace[] Faces;
    private readonly Dictionary<(int Face, int Px, int Codepoint), Glyph> _glyphs = [];
    private readonly Dictionary<(FontName, string), float> _widths = [];
    private int _cursorX = 6, _cursorY, _row = 4;

    public FontAtlas Atlas { get; } = new();
    /// <summary>Физических пикселей на логический.</summary>
    public float Scale { get; private set; } = 1;

    /// <summary>
    /// Кернинг пар по GPOS. Выключен: пилот (fontdue в gui.rs) его не применяет, и кадры должны совпасть с пилотом;
    /// браузерный TS-клиент кернинг применял.
    /// </summary>
    public bool Kerning
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _widths.Clear();
        }
    }

    /// <summary>Лица: 0 — Ark Pixel 12, 1 — Ark Pixel 10, 2 (если есть) — запасной Unifont.</summary>
    public Fonts(params byte[][] faces) => Faces = [.. faces.Select(bytes => new FontFace(bytes))];

    /// <summary>Номер запасного лица: чего нет в Ark Pixel, рисует Unifont (как «Kharos Unifont» в fonts.css TS).</summary>
    private const int Fallback = 2;

    /// <summary>
    /// Увеличение Unifont рядом с лицом (size-adjust в fonts.css): у Ark Pixel 12 кегль 12 даёт настоящие 16 точек
    /// Unifont (4/3), у Ark Pixel 10 — 1,6.
    /// </summary>
    private static float FallbackScale(int face) => face == 0 ? 4f / 3f : 1.6f;

    /// <summary>Каким лицом и в каком кегле рисовать букву: своё лицо, а без буквы — запасное, если оно её знает.</summary>
    private (int Face, float Size) Pick(int face, float size, int codepoint) =>
        Faces.Length > Fallback && !Faces[face].Has(codepoint) && Faces[Fallback].Has(codepoint) ? (Fallback, size * FallbackScale(face)) : (face, size);

    /// <summary>Ark Pixel 12 и 10 и запасной Unifont из ресурсов сборки.</summary>
    public static Fonts Load() => new(Resource("ark-pixel-12px.otf"), Resource("ark-pixel-10px.otf"), Resource("unifont-symbols.otf"));

    private static byte[] Resource(string name)
    {
        using var stream = typeof(Fonts).Assembly.GetManifestResourceStream("Kharos.Client.Fonts." + name)
            ?? throw new InvalidOperationException($"нет ресурса шрифта {name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        foreach (var face in Faces) face.Dispose();
    }

    /// <summary>Лицо, размер, высота строки и верх букв (FONTS в text.ts).</summary>
    public static (int Face, float Size, float Line, float Ascent) Spec(FontName font) => font switch
    {
        FontName.Small => (1, 10, 14, 11),
        FontName.Title => (1, 20, 28, 22),
        FontName.Logo => (1, 40, 56, 44),
        _ => (0, 12, 16, 13),
    };

    public static float Line(FontName font) => Spec(font).Line;

    /// <summary>Новый масштаб экрана: глифы растеризуются заново.</summary>
    public void SetScale(float scale)
    {
        if (MathF.Abs(scale - Scale) <= 0.001f) return;
        Scale = scale;
        _glyphs.Clear();
        Atlas.Clear();
        (_cursorX, _cursorY, _row) = (6, 0, 4);
    }

    private Glyph GetGlyph(int face, int px, int codepoint)
    {
        if (_glyphs.TryGetValue((face, px, codepoint), out var cached)) return cached;
        var (w, h, left, top, bitmap) = Faces[face].Rasterize(codepoint, px);
        const int size = FontAtlas.Size;
        if (_cursorX + w + 1 > size) (_cursorX, _cursorY, _row) = (0, _cursorY + _row + 1, 0);
        if (_cursorY + h + 1 > size)
        {
            // Атлас кончился: всё заново, глифы этого кадра растеризуются ещё раз.
            _glyphs.Clear();
            Atlas.Clear();
            (_cursorX, _cursorY, _row) = (0, 5, 0);
        }
        int x = _cursorX, y = _cursorY;
        for (var row = 0; row < h; row++)
            bitmap.AsSpan(row * w, w).CopyTo(Atlas.Pixels.AsSpan((y + row) * size + x, w));
        Atlas.Touch();
        _cursorX += w + 1;
        _row = Math.Max(_row, h);
        var glyph = new Glyph(new Uv((float)x / size, (float)y / size, (float)(x + w) / size, (float)(y + h) / size), w, h, left, top);
        _glyphs[(face, px, codepoint)] = glyph;
        return glyph;
    }

    /// <summary>Ширина строки в логических пикселях, с округлением вверх, как measureText в TS.</summary>
    public float Width(FontName font, string text)
    {
        if (_widths.TryGetValue((font, text), out var width)) return width;
        var (face, size, _, _) = Spec(font);
        var sum = 0f;
        var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var (used, px) = Pick(face, size, rune.Value);
            if (Kerning && previous >= 0 && used == face) sum += Faces[face].Kern(previous, rune.Value, size);
            sum += Faces[used].Advance(rune.Value, px);
            previous = used == face ? rune.Value : -1;
        }
        width = MathF.Ceiling(sum);
        if (_widths.Count > 4000) _widths.Clear();
        _widths[(font, text)] = width;
        return width;
    }

    /// <summary>Строка с левым верхним углом в (x, y): глифы ставятся по целым физическим пикселям.</summary>
    public void Draw(Batch batch, float x, float y, FontName font, Color color, string text, Clip? clip)
    {
        var (face, size, _, ascent) = Spec(font);
        var scale = Scale;
        var px = (int)Gui.Round(size * scale);
        var origin = Gui.Round(x * scale);
        var baseline = Gui.Round((y + ascent) * scale);
        var pen = 0f;
        var rgba = color.Rgba;
        var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var (used, usedSize) = Pick(face, size, rune.Value);
            var usedPx = used == face ? px : (int)Gui.Round(usedSize * scale);
            if (Kerning && previous >= 0 && used == face) pen += Faces[face].Kern(previous, rune.Value, px);
            previous = used == face ? rune.Value : -1;
            var advance = Faces[used].Advance(rune.Value, usedPx);
            var glyph = GetGlyph(used, usedPx, rune.Value);
            if (glyph.Width > 0)
            {
                var gx = origin + Gui.Round(pen) + glyph.Left;
                var gy = baseline + glyph.Top;
                batch.QuadClipped(gx / scale, gy / scale, (gx + glyph.Width) / scale, (gy + glyph.Height) / scale, glyph.Uv, rgba, Paint.Font, clip);
            }
            pen += advance;
        }
    }

    /// <summary>По словам в строки не шире width; длинное слово — по буквам (wrapText).</summary>
    public List<string> Wrap(FontName font, string text, float width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (Width(font, next) <= width)
            {
                line.Clear().Append(next);
                continue;
            }
            if (line.Length > 0)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            foreach (var letter in word.EnumerateRunes())
            {
                if (line.Length > 0 && Width(font, line + letter.ToString()) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                line.Append(letter.ToString());
            }
        }
        lines.Add(line.ToString());
        return lines;
    }

    /// <summary>Сокращение многоточием до width (clipText).</summary>
    public string Clip(FontName font, string text, float width)
    {
        if (Width(font, text) <= width) return text;
        var letters = text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        int low = 0, high = letters.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Width(font, string.Concat(letters.AsSpan(0, middle)).TrimEnd() + "…") <= width) low = middle;
            else high = middle - 1;
        }
        return string.Concat(letters.AsSpan(0, low)).TrimEnd() + "…";
    }
}
