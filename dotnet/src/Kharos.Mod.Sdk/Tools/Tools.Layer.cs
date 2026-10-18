using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 18 Layer: канал слоя-сетки в прямоугольнике (ModAbi.ToolLayer). Движок — Д3.
public static partial class Tools
{
    /// <summary>Значения канала слоя-сетки в прямоугольнике w×h с (x, y) построчно; owner 0 — слой мира.</summary>
    public static void Layer(LayerRef layer, uint owner, int channel, int x, int y, int w, int h, Span<double> into)
    {
        Begin().U32((uint)layer.Id).U32(owner).U32((uint)channel).I32(x).I32(y).U32((uint)w).U32((uint)h);
        var output = Call(ModAbi.ToolLayer);
        for (var i = 0; i < w * h; i++)
            into[i] = BinaryPrimitives.ReadDoubleLittleEndian(output[(i * 8)..]);
    }

    /// <summary>Значение канала одного тайла.</summary>
    public static double Layer(LayerRef layer, uint owner, int channel, int x, int y)
    {
        Span<double> one = stackalloc double[1];
        Layer(layer, owner, channel, x, y, 1, 1, one);
        return one[0];
    }
}
