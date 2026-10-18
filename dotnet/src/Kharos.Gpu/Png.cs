using StbImageWriteSharp;

namespace Kharos.Gpu;

/// <summary>Снимки кадров в PNG.</summary>
public static class Png
{
    /// <summary>RGBA8, строки сверху вниз.</summary>
    public static void Write(string path, byte[] rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("мало пикселей", nameof(rgba));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        new ImageWriter().WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
    }
}
