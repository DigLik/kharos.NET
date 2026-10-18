namespace Kharos.Gpu;

/// <summary>Камера: центр в тайлах, логических пикселей на тайл, экран в логических пикселях и физических на логический.</summary>
public record struct View(float X, float Y, float Zoom, float ScreenWidth, float ScreenHeight, float Scale)
{
    public readonly (float X, float Y) ToScreen(float x, float y) =>
        ((x - X) * Zoom + ScreenWidth / 2, (y - Y) * Zoom + ScreenHeight / 2);

    public readonly (float X, float Y) ToWorld(float x, float y) =>
        ((x - ScreenWidth / 2) / Zoom + X, (y - ScreenHeight / 2) / Zoom + Y);

    /// <summary>Видимая часть мира в тайлах с запасом margin: левый, верхний, правый, нижний край.</summary>
    public readonly (float Left, float Top, float Right, float Bottom) Bounds(float margin)
    {
        var (left, top) = ToWorld(0, 0);
        var (right, bottom) = ToWorld(ScreenWidth, ScreenHeight);
        return (left - margin, top - margin, right + margin, bottom + margin);
    }
}

/// <summary>
/// Что рисовать в кадре — перенос <c>Frame</c> из <c>apps/client/src/render.rs</c>, числа в тех же раскладках.
/// Порядок кадра: земля, следы, по слоям тень и спрайты, осадки, свет (ночью), туман, батч поверх мира, батч интерфейса.
/// </summary>
public sealed class Frame
{
    /// <summary>Текселей на тайл.</summary>
    public const float Texels = 16;
    /// <summary>Сторон в палитре: своя, чужая, союзная, ничья — по три цвета (тёмный, основной, светлый).</summary>
    public const int Teams = 4;
    /// <summary>Слоёв спрайтов: на земле, наземные, их турели, здания, турели зданий, летающие, их турели.</summary>
    public const int Layers = 7;
    /// <summary>Чисел на спрайт: левый верхний угол и размер в тайлах, uv кадра, сторона, сдвиг и сила тени, серость.</summary>
    public const int Sprite = 12;
    /// <summary>Чисел на след: отрезок, ширина, кадр атласа (ширина 0 — белый), цвет и непрозрачность, сколько осталось, плотность, зерно.</summary>
    public const int Decal = 16;

    public View View;
    /// <summary>Спрайты по слоям, <see cref="Sprite"/> чисел на каждый.</summary>
    public readonly List<float>[] SpriteLayers = [.. Enumerable.Range(0, Layers).Select(_ => new List<float>())];
    /// <summary>Следы на земле, <see cref="Decal"/> чисел на каждый.</summary>
    public readonly List<float> Decals = [];
    /// <summary>Фары: x, y, радиус в текселях, яркость.</summary>
    public readonly List<float> Spots = [];
    /// <summary>Ореолы фар, тот же формат.</summary>
    public readonly List<float> Blooms = [];
    /// <summary>Лучи: x, y, длина, яркость, cos, sin, ближняя полуширина, расширение.</summary>
    public readonly List<float> Beams = [];
    /// <summary>Освещение: 0 — ночь, 1 — полдень.</summary>
    public float Light = 1;
    /// <summary>Ветер в тайлах в секунду и путь, пройденный ветром (по модулю окна земли).</summary>
    public (float X, float Y) Wind, Drift;
    /// <summary>Осадки 0..1.</summary>
    public float Precipitation;
    /// <summary>Палитры сторон: <see cref="Teams"/> × 3 цвета RGB 0..1.</summary>
    public readonly float[] Palette = new float[Teams * 3 * 3];
    /// <summary>Секунды — анимация болот.</summary>
    public float Time;
    /// <summary>Туман войны: левый верхний угол текстуры в тайлах; null — тумана нет.</summary>
    public (float X, float Y)? Fog;
    /// <summary>Новая картинка обзора (альфа на тайл) и её размер, если обзор поменялся.</summary>
    public (byte[] Alpha, int Width, int Height)? FogUpdate;
    /// <summary>Слой поверх мира в тайлах (выделение, частицы, рисунки модов).</summary>
    public readonly Batch World = new();
    /// <summary>Интерфейс в логических пикселях.</summary>
    public readonly Batch Interface = new();

    public void Clear()
    {
        foreach (var layer in SpriteLayers) layer.Clear();
        Decals.Clear(); Spots.Clear(); Blooms.Clear(); Beams.Clear();
        FogUpdate = null;
        World.Clear(); Interface.Clear();
    }
}