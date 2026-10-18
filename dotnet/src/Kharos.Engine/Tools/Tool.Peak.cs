using System.Buffers.Binary;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 20 Peak: вершина горы тайла (формат — ModAbi.ToolPeak). Владелец — Д3.
public partial class ModTools
{
    private partial int Peak(GameWorld world, ref ToolInput r)
    {
        var n = r.Count(8);
        var land = Source.Land;
        for (var at = 0; at < n; at++)
        {
            var (x, y) = (r.I32(), r.I32());
            var peak = land?.PeakOf(x, y);
            var into = Room(25);
            into.Clear();
            if (peak is not { } found)
                continue;
            into[0] = 1;
            BinaryPrimitives.WriteDoubleLittleEndian(into[1..], found.X);
            BinaryPrimitives.WriteDoubleLittleEndian(into[9..], found.Y);
            BinaryPrimitives.WriteDoubleLittleEndian(into[17..], found.Radius);
        }
        return 0;
    }
}
