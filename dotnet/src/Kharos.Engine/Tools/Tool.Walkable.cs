using System.Buffers.Binary;
using Kharos.Engine.Movement;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 21 Walkable: может ли стоять наземный (isWalkable; формат — ModAbi.ToolWalkable). Владелец — Д1.
public partial class ModTools
{
    /// <summary>
    /// Ход и пути мира (Kharos.Engine.Movement): ими работают инструменты 16, 17, 21–28. Ставит сторона, у которой они
    /// есть (хост — EngineSystems); нет — эти инструменты отвечают ErrorUnknownId.
    /// </summary>
    public Navigation? Navigation { get; set; }

    /// <summary>Тайлов в одном ответе не больше: защита от входа на всю карту.</summary>
    private const int MaxToolTiles = 1 << 20;

    private partial int Walkable(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var flags = r.U32();
        var n = r.Count(8);
        var into = Room(n);
        for (var i = 0; i < n; i++)
        {
            var (x, y) = (r.I32(), r.I32());
            into[i] = nav.CanStand((flags & ModAbi.PlanAir) != 0, x, y, (flags & ModAbi.PlanFoot) != 0) ? (byte)1 : (byte)0;
        }
        return 0;
    }

    private void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Room(4), value);
}
