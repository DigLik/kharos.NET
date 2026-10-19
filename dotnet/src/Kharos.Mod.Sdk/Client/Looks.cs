using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk.Client;

/// <summary>Слой рисования облика: на земле, наземные, здания, летающие. Турель — слоем выше носителя.</summary>
public enum LookLayer : byte { Floor = 0, Ground = 1, Building = 3, Air = 5 }

/// <summary>Фара: вдоль, поперёк, радиус — в пикселях спрайта.</summary>
public readonly record struct Lamp(float Along, float Across, float Radius);

/// <summary>Луч фар: откуда вдоль корпуса, длина, ближняя полуширина, расширение, яркость.</summary>
public readonly record struct Beam(float Along = 8, float Length = 60, float Near = 4, float Spread = 0.28f, float Level = 1);

/// <summary>Кадр облика: холст, угол направления в радианах, фаза.</summary>
public delegate void LookDraw(Canvas canvas, double angle, int phase);

/// <summary>
/// Ячейка облика с вариантами (ABI клиента 2): холст, вариант, угол, фаза. С <see cref="LookSpec.Working"/> фазы
/// <see cref="LookSpec.Phases"/>..2 × Phases − 1 — кадры «в работе».
/// </summary>
public delegate void LookCellDraw(Canvas canvas, int variant, double angle, int phase);

/// <summary>Портрет для панелей: рисуется один раз и обрезается по закрашенному.</summary>
public sealed record PortraitSpec(int Width, int Height, Action<Canvas> Draw)
{
    /// <summary>Начало координат чертежа; null — центр.</summary>
    public (double X, double Y)? Origin { get; init; }
}

/// <summary>
/// Облик вида (look пилота, LookSpec клиента): кадр, начало, точка привязки, направления × фазы, слой, тень, огни,
/// портрет. Draw рисует кадр на холсте; клиент проигрывает записанное на своём холсте и пакует кадры в атлас.
/// </summary>
public sealed record LookSpec(LookDraw Draw)
{
    public int Width { get; init; } = 32;
    public int Height { get; init; } = 32;
    /// <summary>Начало координат чертежа в кадре; null — центр.</summary>
    public (double X, double Y)? Origin { get; init; }
    /// <summary>Точка чертежа, которая стоит на месте сущности.</summary>
    public (double X, double Y) Anchor { get; init; }
    public int Directions { get; init; } = 1;
    public int Phases { get; init; } = 1;
    /// <summary>Кадров в секунду анимации; у ходовых — Step, тайлов пути на кадр.</summary>
    public float Fps { get; init; } = 8;
    public float? Step { get; init; }
    public LookLayer Layer { get; init; } = LookLayer.Ground;
    /// <summary>Тень: сдвиг в пикселях и непрозрачность; null — без тени.</summary>
    public (float Offset, float Opacity)? Shadow { get; init; } = (2, 0.4f);
    /// <summary>Видно, где разведано, а не только в обзоре.</summary>
    public bool Memory { get; init; }
    public IReadOnlyList<Lamp> Lamps { get; init; } = [];
    public Beam? Beam { get; init; }
    /// <summary>Направление кадра значка.</summary>
    public int Icon { get; init; }
    public PortraitSpec? Portrait { get; init; }

    /// <summary>
    /// Вариантов облика (ABI клиента 2, LookVariants): стена — 16, ворота — 17, дверь шахты — 8 × 4. Рисует их
    /// <see cref="DrawCell"/>; 1 — без вариантов.
    /// </summary>
    public int Variants { get; init; } = 1;

    /// <summary>Второй набор фаз «в работе» (LookWorking, framesOf в buildingsPass.ts): фазы удваиваются.</summary>
    public bool Working { get; init; }

    /// <summary>Ячейка с вариантом; null — <see cref="LookSpec.Draw"/> (вариант не передаётся).</summary>
    public LookCellDraw? DrawCell { get; init; }

    /// <summary>Облик потоком (ClientAbi, «Поток облика») — то, что отдаёт kh_look; для сверки вне wasm.</summary>
    public byte[] Encode()
    {
        using var w = new ByteWriter();
        Write(w);
        return w.Written.ToArray();
    }

    /// <summary>Записывает облик потоком (ClientAbi, «Поток облика»): метаданные и команды холста всех кадров.</summary>
    internal void Write(ByteWriter w)
    {
        var directions = Math.Max(Directions, 1);
        var phases = Math.Max(Phases, 1);
        var variants = Math.Max(Variants, 1);
        var frames = Working ? phases * 2 : phases;
        byte flags = 0;
        if (Origin is not null) flags |= ClientAbi.LookOrigin;
        if (Step is not null) flags |= ClientAbi.LookStep;
        if (Shadow is not null) flags |= ClientAbi.LookShadow;
        if (Memory) flags |= ClientAbi.LookMemory;
        if (Beam is not null) flags |= ClientAbi.LookBeam;
        if (Portrait is not null) flags |= ClientAbi.LookPortrait;
        if (variants > 1) flags |= ClientAbi.LookVariants;
        if (Working) flags |= ClientAbi.LookWorking;
        w.U16(checked((ushort)Width)).U16(checked((ushort)Height)).U8(flags);
        if (Origin is { } origin) w.F64(origin.X).F64(origin.Y);
        w.F64(Anchor.X).F64(Anchor.Y).U16(checked((ushort)directions)).U16(checked((ushort)phases)).F32(Fps);
        if (Step is { } step) w.F32(step);
        if (variants > 1) w.U16(checked((ushort)variants));
        w.U8((byte)Layer);
        if (Shadow is { } shadow) w.F32(shadow.Offset).F32(shadow.Opacity);
        w.U16(checked((ushort)Icon)).U16(checked((ushort)Lamps.Count));
        foreach (var lamp in Lamps) w.F32(lamp.Along).F32(lamp.Across).F32(lamp.Radius);
        if (Beam is { } beam) w.F32(beam.Along).F32(beam.Length).F32(beam.Near).F32(beam.Spread).F32(beam.Level);
        var canvas = new Canvas(w);
        if (Portrait is { } portrait)
        {
            w.U16(checked((ushort)portrait.Width)).U16(checked((ushort)portrait.Height)).U8(portrait.Origin is null ? (byte)0 : (byte)1);
            if (portrait.Origin is { } at) w.F64(at.X).F64(at.Y);
            portrait.Draw(canvas);
            w.U8(ClientAbi.CanvasEnd);
        }
        for (var variant = 0; variant < variants; variant++)
            for (var phase = 0; phase < frames; phase++)
                for (var direction = 0; direction < directions; direction++)
                {
                    var angle = (double)direction / directions * Canvas.Turn;
                    if (DrawCell is { } cell)
                        cell(canvas, variant, angle, phase);
                    else
                        Draw(canvas, angle, phase);
                    w.U8(ClientAbi.CanvasEnd);
                }
    }
}

/// <summary>
/// Холст облика (canvas пилота, Pixmap TS): тот же API, что Canvas клиента, но команды записываются, а рисует клиент —
/// до байта как C#. Координаты — от начала чертежа; цвет — 0xRRGGBB, цвет стороны — <see cref="Toned"/>.
/// </summary>
public sealed class Canvas
{
    /// <summary>Полный оборот, радианы.</summary>
    public const double Turn = Math.PI * 2;

    private readonly ByteWriter _out;

    internal Canvas(ByteWriter output) => _out = output;

    /// <summary>Цвет с меткой тона tone (0 — тёмный, 1 — основной, 2 — светлый): его перекрашивает палитра стороны.</summary>
    public static uint Toned(uint color, int tone) => color + (uint)(tone + 1) * 0x1000000;

    /// <summary>Прямоугольник: левый верхний угол (x, y), размер width × height.</summary>
    public Canvas Rect(double x, double y, double width, double height, uint color)
    {
        _out.U8(ClientAbi.CanvasRect).F64(x).F64(y).F64(width).F64(height).U32(color);
        return this;
    }

    public Canvas Circle(double centerX, double centerY, double radius, uint color)
    {
        _out.U8(ClientAbi.CanvasCircle).F64(centerX).F64(centerY).F64(radius).U32(color);
        return this;
    }

    /// <summary>Окружность линией толщиной width.</summary>
    public Canvas Ring(double centerX, double centerY, double radius, double width, uint color)
    {
        _out.U8(ClientAbi.CanvasRing).F64(centerX).F64(centerY).F64(radius).F64(width).U32(color);
        return this;
    }

    /// <summary>Отрезок толщиной width с прямыми торцами.</summary>
    public Canvas Line(double fromX, double fromY, double toX, double toY, double width, uint color)
    {
        _out.U8(ClientAbi.CanvasLine).F64(fromX).F64(fromY).F64(toX).F64(toY).F64(width).U32(color);
        return this;
    }

    /// <summary>Многоугольник: x1, y1, x2, y2, …; чётно-нечётное правило.</summary>
    public Canvas Poly(ReadOnlySpan<double> coords, uint color)
    {
        var count = coords.Length / 2;
        _out.U8(ClientAbi.CanvasPoly).U16(checked((ushort)count));
        for (var i = 0; i < count * 2; i++)
            _out.F64(coords[i]);
        _out.U32(color);
        return this;
    }

    /// <summary>Один пиксель чертежа, в котором лежит (x, y).</summary>
    public Canvas Pixel(double x, double y, uint color)
    {
        _out.U8(ClientAbi.CanvasPixel).F64(x).F64(y).U32(color);
        return this;
    }

    /// <summary>Сдвиг начала координат.</summary>
    public Canvas Translate(double dx, double dy)
    {
        _out.U8(ClientAbi.CanvasTranslate).F64(dx).F64(dy);
        return this;
    }

    /// <summary>Огонь кадра в точке (x, y): size — радиус лампы в пикселях, level — яркость 0..1.</summary>
    public Canvas Light(double x, double y, double size, double level)
    {
        _out.U8(ClientAbi.CanvasLight).F64(x).F64(y).F64(size).F64(level);
        return this;
    }
}
