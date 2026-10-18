using System.Numerics;
using System.Runtime.InteropServices;

namespace Kharos.Audio.Dsp;

/// <summary>Векторные операции над квантами.</summary>
internal static class Mix
{
    /// <summary>dst += src.</summary>
    public static void Add(ReadOnlySpan<float> src, Span<float> dst)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var s = MemoryMarshal.Cast<float, Vector<float>>(src);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst);
            for (var v = 0; v < s.Length && v < d.Length; v++)
                d[v] += s[v];
            i = Math.Min(s.Length, d.Length) * Vector<float>.Count;
        }
        for (; i < src.Length; i++)
            dst[i] += src[i];
    }

    /// <summary>dst = src · k.</summary>
    public static void Scale(ReadOnlySpan<float> src, Span<float> dst, float k)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var s = MemoryMarshal.Cast<float, Vector<float>>(src);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst);
            for (var v = 0; v < s.Length && v < d.Length; v++)
                d[v] = s[v] * k;
            i = Math.Min(s.Length, d.Length) * Vector<float>.Count;
        }
        for (; i < src.Length; i++)
            dst[i] = src[i] * k;
    }

    /// <summary>dst = src · gain (поэлементно).</summary>
    public static void Multiply(ReadOnlySpan<float> src, ReadOnlySpan<float> gain, Span<float> dst)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var s = MemoryMarshal.Cast<float, Vector<float>>(src);
            var g = MemoryMarshal.Cast<float, Vector<float>>(gain);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst);
            for (var v = 0; v < s.Length; v++)
                d[v] = s[v] * g[v];
            i = s.Length * Vector<float>.Count;
        }
        for (; i < src.Length; i++)
            dst[i] = src[i] * gain[i];
    }
}
