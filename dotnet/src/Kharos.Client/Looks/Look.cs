using Kharos.Client.Ui;

namespace Kharos.Client.Looks;

/// <summary>
/// Слой рисования: что лежит на земле (руда), наземные юниты под зданиями, летающие — над всеми. Турель — слоем выше
/// своего носителя.
/// </summary>
public enum LookLayer : byte { Floor = 0, Ground = 1, Building = 3, Air = 5 }

/// <summary>Фара: вдоль, поперёк, радиус — в пикселях спрайта.</summary>
public readonly record struct Lamp(float Along, float Across, float Radius);

/// <summary>Луч фар: откуда вдоль корпуса, длина, ближняя полуширина, расширение, яркость.</summary>
public readonly record struct Beam(float Along = 8, float Length = 60, float Near = 4, float Spread = 0.28f, float Level = 1);

/// <summary>Портрет для панелей: рисуется один раз и обрезается по закрашенному.</summary>
public sealed record PortraitSpec(int Width, int Height, Action<Canvas> Draw)
{
    /// <summary>Начало координат чертежа; null — центр.</summary>
    public (double X, double Y)? Origin { get; init; }
}

/// <summary>
/// Чертёж облика вида — поля таблицы <c>look</c> из atlas.rs. Draw(холст, угол в радианах, фаза) рисует кадр; пока он
/// на C#, позже его поставит мод клиента через ABI.
/// </summary>
public sealed record LookSpec(Action<Canvas, double, int> Draw)
{
    /// <summary>Кадр в пикселях.</summary>
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
    /// <summary>Видно, где разведано, а не только в обзоре (руда).</summary>
    public bool Memory { get; init; }
    public IReadOnlyList<Lamp> Lamps { get; init; } = [];
    public Beam? Beam { get; init; }
    /// <summary>Направление кадра значка.</summary>
    public int Icon { get; init; }
    public PortraitSpec? Portrait { get; init; }

    /// <summary>Вариантов (ABI клиента 2, LookVariants): стена — 16, ворота — 17; рисует их <see cref="DrawCell"/>.</summary>
    public int Variants { get; init; } = 1;

    /// <summary>Второй набор фаз «в работе» (LookWorking): фазы удваиваются — сначала покой, затем работа.</summary>
    public bool Working { get; init; }

    /// <summary>Ячейка с вариантом: холст, вариант, угол, фаза (среди удвоенных); null — <see cref="Draw"/>.</summary>
    public Action<Canvas, int, double, int>? DrawCell { get; init; }
}

/// <summary>Огонь кадра после растеризации: смещение от места сущности в тайлах, радиус в пикселях, яркость.</summary>
public readonly record struct FrameLight(float X, float Y, float Size, float Level);

/// <summary>Облик вида: кадры по направлениям и фазам (до упаковки — RGBA, после — места в атласе) и всё для рендера.</summary>
public sealed class Look
{
    public const double Turn = Math.PI * 2;
    /// <summary>Пикселей спрайта на тайл.</summary>
    public const float Tile = 16;

    /// <summary>
    /// Номер облика: у облика вида — номер вида (Proto), у свободного облика мода (значок, эффект, покрытие — не вид
    /// контента) — номер после всех видов (<see cref="Game.ClientGame.LookId"/>). Им облик назван в записях Sprite, Icon,
    /// ImageLook.
    /// </summary>
    public int Proto { get; }

    /// <summary>Имя облика «раздел/имя», как его объявил мод (пусто — облик собран вне партии).</summary>
    public string Name { get; }

    public LookSpec Spec { get; }
    public int Width => Spec.Width;
    public int Height => Spec.Height;
    public int Directions { get; }
    public int Phases { get; }
    /// <summary>Вариантов облика (1 — без вариантов).</summary>
    public int Variants { get; }
    /// <summary>Фаз в варианте: <see cref="Phases"/>, с <see cref="LookSpec.Working"/> — вдвое больше.</summary>
    public int Frames { get; }
    /// <summary>Левый верхний угол кадра от места сущности, в тайлах.</summary>
    public (float X, float Y) Offset { get; }
    /// <summary>Тень: сдвиг в тайлах и непрозрачность.</summary>
    public (float Offset, float Opacity) Shadow { get; }
    /// <summary>Кадры RGBA по порядку вариант × фаза × направление — до упаковки.</summary>
    public List<byte[]> Images { get; } = [];
    /// <summary>Места кадров в атласе — после упаковки.</summary>
    public Uv[] Cells { get; internal set; } = [];
    public List<FrameLight[]> FrameLights { get; } = [];
    /// <summary>Портрет до упаковки: обрезанная картинка RGBA и её размер.</summary>
    public (byte[] Pixels, int Width, int Height)? Portrait { get; internal set; }
    public Uv? PortraitUv { get; internal set; }
    public (float Width, float Height) PortraitSize { get; }

    private Look(int proto, LookSpec spec, string name)
    {
        Proto = proto;
        Name = name;
        Spec = spec;
        Directions = Math.Max(spec.Directions, 1);
        Phases = Math.Max(spec.Phases, 1);
        Variants = Math.Max(spec.Variants, 1);
        Frames = spec.Working ? Phases * 2 : Phases;
        var (originX, originY) = spec.Origin ?? (spec.Width / 2.0, spec.Height / 2.0);
        Offset = ((float)(-(originX + spec.Anchor.X) / 16), (float)(-(originY + spec.Anchor.Y) / 16));
        Shadow = spec.Shadow is { } shadow ? (shadow.Offset / Tile, shadow.Opacity) : (0, 0);
        for (var variant = 0; variant < Variants; variant++)
            for (var phase = 0; phase < Frames; phase++)
                for (var direction = 0; direction < Directions; direction++)
                {
                    var canvas = new Canvas(spec.Width, spec.Height) { OriginX = originX, OriginY = originY };
                    var angle = (double)direction / Directions * Turn;
                    if (spec.DrawCell is { } cell)
                        cell(canvas, variant, angle, phase);
                    else
                        spec.Draw(canvas, angle, phase);
                    Images.Add(canvas.Data);
                    FrameLights.Add([.. canvas.Lights.Select(light => new FrameLight((float)((light.X - spec.Anchor.X) / 16), (float)((light.Y - spec.Anchor.Y) / 16), (float)light.Size, (float)light.Level))]);
                }
        if (spec.Portrait is { } portrait) Portrait = Crop(portrait);
        PortraitSize = Portrait is { } cropped ? (cropped.Width, cropped.Height) : (spec.Width, spec.Height);
    }

    /// <summary>Растеризует облик номер proto (имя name): Directions × Phases кадров и портрет.</summary>
    public static Look Build(int proto, LookSpec spec, string name = "") => new(proto, spec, name);

    /// <summary>Портрет: рисуется один раз и обрезается по закрашенному (toCanvas в portraits.ts).</summary>
    private static (byte[], int, int) Crop(PortraitSpec spec)
    {
        int width = spec.Width, height = spec.Height;
        var (originX, originY) = spec.Origin ?? (width / 2.0, height / 2.0);
        var canvas = new Canvas(width, height) { OriginX = originX, OriginY = originY };
        spec.Draw(canvas);
        var data = canvas.Data;
        int left = width, top = height, right = 0, bottom = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (data[(y * width + x) * 4 + 3] != 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
        if (right <= left) return (new byte[4], 1, 1);
        int w = right - left, h = bottom - top;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            data.AsSpan(((top + y) * width + left) * 4, w * 4).CopyTo(pixels.AsSpan(y * w * 4));
        return (pixels, w, h);
    }

    /// <summary>Номер кадра направления angle (радианы) в фазе phase.</summary>
    public int Cell(double angle, int phase)
    {
        var direction = 0;
        if (Directions > 1)
        {
            var turns = Math.Round(angle / Turn * Directions, MidpointRounding.AwayFromZero) % Directions;
            direction = (int)(turns < 0 ? turns + Directions : turns);
        }
        return phase % Phases * Directions + direction;
    }

    /// <summary>
    /// Номер кадра варианта variant, фазы phase (покоя или, при working и <see cref="LookSpec.Working"/>, «в работе») и
    /// направления angle (ABI клиента 2).
    /// </summary>
    public int Cell(double angle, int phase, int variant, bool working = false)
    {
        var frame = phase % Phases + (working && Frames > Phases ? Phases : 0);
        return (Math.Clamp(variant, 0, Variants - 1) * Frames + frame) * Directions + Cell(angle, 0);
    }

    /// <summary>Размер кадра в тайлах.</summary>
    public (float Width, float Height) Tiles => (Width / Tile, Height / Tile);

    /// <summary>Значок для интерфейса (после упаковки): портрет, если он есть, иначе кадр направления <see cref="LookSpec.Icon"/>.</summary>
    public Icon Icon => PortraitUv is { } uv
        ? new Icon(uv, PortraitSize.Width, PortraitSize.Height)
        : new Icon(Cells[Cell((double)Spec.Icon / Directions * Turn, 0)], Width, Height);
}

/// <summary>Общий атлас обликов: пиксели RGBA и размер.</summary>
public sealed record LookAtlas(byte[] Pixels, int Width, int Height)
{
    public const int MaxWidth = 4096;

    /// <summary>
    /// Кадры всех обликов полками по строкам, от высоких к низким, портреты — отдельной полкой снизу (pack в atlas.rs).
    /// Места кадров — в <see cref="Look.Cells"/>, портретов — в <see cref="Look.PortraitUv"/>; картинки до упаковки
    /// освобождаются.
    /// </summary>
    public static LookAtlas Pack(IReadOnlyList<Look> looks)
    {
        const int width = MaxWidth;
        // Устойчиво: при равной высоте — в порядке обликов.
        var order = Enumerable.Range(0, looks.Count).OrderByDescending(index => looks[index].Height).ToArray();
        var places = looks.Select(_ => new List<(int X, int Y)>()).ToArray();
        int x = 0, y = 0, row = 0;
        foreach (var index in order)
        {
            var look = looks[index];
            for (var i = 0; i < look.Images.Count; i++)
            {
                if (x + look.Width > width) (x, y, row) = (0, y + row, 0);
                places[index].Add((x, y));
                x += look.Width;
                row = Math.Max(row, look.Height);
            }
        }
        // Портреты — отдельной полкой снизу.
        (x, y, row) = (0, y + row, 0);
        var portraits = new (int X, int Y)?[looks.Count];
        for (var index = 0; index < looks.Count; index++)
        {
            if (looks[index].Portrait is not { } portrait) continue;
            if (x + portrait.Width > width) (x, y, row) = (0, y + row, 0);
            portraits[index] = (x, y);
            x += portrait.Width;
            row = Math.Max(row, portrait.Height);
        }
        var height = Math.Max(y + row, 1);
        var pixels = new byte[width * height * 4];
        Uv UvOf(int px, int py, int w, int h) => new((float)px / width, (float)py / height, (float)(px + w) / width, (float)(py + h) / height);
        void Copy(byte[] image, int px, int py, int w, int h)
        {
            for (var line = 0; line < h; line++)
                image.AsSpan(line * w * 4, w * 4).CopyTo(pixels.AsSpan(((py + line) * width + px) * 4));
        }
        for (var index = 0; index < looks.Count; index++)
        {
            var look = looks[index];
            look.Cells = [.. places[index].Select(place => UvOf(place.X, place.Y, look.Width, look.Height))];
            for (var i = 0; i < look.Images.Count; i++) Copy(look.Images[i], places[index][i].X, places[index][i].Y, look.Width, look.Height);
            look.Images.Clear();
            if (look.Portrait is { } portrait && portraits[index] is { } at)
            {
                Copy(portrait.Pixels, at.X, at.Y, portrait.Width, portrait.Height);
                look.PortraitUv = UvOf(at.X, at.Y, portrait.Width, portrait.Height);
                look.Portrait = null;
            }
        }
        return new LookAtlas(pixels, width, height);
    }
}
