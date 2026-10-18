namespace Kharos.Audio;

/// <summary>Слушатель — камера: центр в тайлах, масштаб (пикселей на тайл), размер экрана в пикселях.</summary>
public readonly record struct Listener(double X, double Y, double Zoom, double Width, double Height);

/// <summary>Слышимость точки мира (перенос hearing.ts и nearness/side из apps/client/src/script.rs).</summary>
public static class Hearing
{
    /// <summary>При таком масштабе громкость полная, при вдвое меньшем — вдвое тише.</summary>
    public const double NearZoom = 32;

    /// <summary>
    /// Насколько слышно то, что в (x, y): 1 — на экране вблизи, 0 — далеко за краем или с большой высоты. За краем экрана
    /// звук стихает на расстоянии reach, в долях полуширины экрана.
    /// </summary>
    public static float Nearness(in Listener listener, double x, double y, double reach = 0.6)
    {
        var halfWidth = listener.Width / 2 / listener.Zoom;
        var halfHeight = listener.Height / 2 / listener.Zoom;
        var outside = Math.Max(0, Math.Max(Math.Abs(x - listener.X) - halfWidth, Math.Abs(y - listener.Y) - halfHeight));
        return (float)(Math.Max(0, 1 - outside / (halfWidth * reach)) * Math.Min(1, listener.Zoom / NearZoom));
    }

    /// <summary>Сторона x: −1 — у левого края экрана, 1 — у правого, дальше — за ними.</summary>
    public static float Side(in Listener listener, double x) => (float)((x - listener.X) / (listener.Width / 2 / listener.Zoom));
}
