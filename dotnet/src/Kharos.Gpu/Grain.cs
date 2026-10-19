namespace Kharos.Gpu;

/// <summary>
/// Зерно земли — <c>fract(sin(dot(mod(texel, 1024), (12.9898, 78.233))) · 43758.5453)</c> из terrain.frag, посчитанное
/// на процессоре так, как его считает SwiftShader (эталоны кадров TS — Chrome на SwiftShader, docs/dotnet-n6.md, Н6.4).
/// Аргумент синуса доходит до ~10⁵, и малейшая разница в синусе меняет дробную часть целиком: на видеокартах и WARP
/// зерно своё, сверка с TS по пикселям расходится на каждом текселе земли. sin у SwiftShader (ShaderCore.cpp, Sin и
/// Sin5): x₂ = fma(x, −1/2π, ¼), z = ¼ − |x₂ − round(x₂)|, многочлен A·z⁵ + B·z³ + C·z через fma. Скалярное умножение —
/// без слияния. Таблица 1024² лежит в окне земли под тайлами: f32 битами в RGBA8 (<see cref="Renderer"/>).
/// </summary>
public static class Grain
{
    public const int Size = 1024;

    private static readonly Lazy<byte[]> _bytes = new(Build);

    /// <summary>Таблица: строка за строкой, 4 байта f32 на тексель.</summary>
    public static ReadOnlySpan<byte> Bytes => _bytes.Value;

    /// <summary>Зерно текселя (tx, ty) мира.</summary>
    public static float At(int tx, int ty)
    {
        float a = tx & (Size - 1), b = ty & (Size - 1);
        var product = b * 78.233f;
        var dot = a * 12.9898f + product;
        var value = Sin(dot) * 43758.5453f;
        return value - MathF.Floor(value);
    }

    /// <summary>sin у SwiftShader (ShaderCore.cpp).</summary>
    public static float Sin(float x)
    {
        const float q = 0.25f;
        var twoPi = 2 * 3.1415926535f;
        var pi2 = 1 / twoPi;
        var x2 = MathF.FusedMultiplyAdd(x, -pi2, q);
        var z = q - MathF.Abs(x2 - MathF.Round(x2, MidpointRounding.ToEven));
        return Sin5(z);
    }

    private static float Sin5(float x)
    {
        var a = (float)((36288 - 20736 * Math.Sqrt(3)) / 5);
        var b = (float)(288 * Math.Sqrt(3) - 540);
        var c = (float)((47 - 9 * Math.Sqrt(3)) / 5);
        var x2 = x * x;
        return MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(a, x2, b), x2, c) * x;
    }

    private static byte[] Build()
    {
        var bytes = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                BitConverter.TryWriteBytes(bytes.AsSpan((y * Size + x) * 4), At(x, y));
        return bytes;
    }
}
