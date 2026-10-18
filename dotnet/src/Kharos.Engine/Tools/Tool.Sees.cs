using Kharos.Engine.World;

namespace Kharos.Engine.Tools;

// 15 Sees: видит ли игрок точки — по обзору хоста (IToolWorld.Sees: глаза, вспышки Reveal, общая разведка союзников
// по Team; 2 — видит, 1 — разведано, 0 — нет; без тумана — 2). Формат — ModAbi.ToolSees. Владелец — Д4.
public partial class ModTools
{
    private partial int Sees(GameWorld world, ref ToolInput r)
    {
        var player = r.U32();
        var n = r.Count(16);
        var into = Room(n);
        for (var i = 0; i < n; i++)
        {
            var (x, y) = (r.F64(), r.F64());
            into[i] = Source.Sees(player, x, y);
        }
        return 0;
    }
}
