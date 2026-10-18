using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 18 Layer: значения канала слоя-сетки в прямоугольнике (формат — ModAbi.ToolLayer). Владелец — Д3.
public partial class ModTools
{
    /// <summary>Слой с этим именем, если им объявлена земля, читается из <see cref="Land"/>: каналы — хранимые байты тайла.</summary>
    public const string LandLayer = "land";

    private partial int Layer(GameWorld world, ref ToolInput r)
    {
        var layers = Source.Layers ?? throw new ArgumentException("у партии нет слоёв");
        var id = r.U32();
        var player = r.U32();
        var channel = r.U32();
        var (x, y) = (r.I32(), r.I32());
        var (w, h) = (r.U32(), r.U32());
        var spec = LayerOf(layers, id, player, out var owner);
        if (!spec.IsGrid)
            throw new ArgumentException($"слой {spec.Name}: предметы, а не сетка");
        if (channel >= (uint)spec.Channels.Count)
            throw new ArgumentException($"слой {spec.Name}: нет канала {channel}");
        if ((ulong)w * h > ModAbi.MaxLayerRect || (long)x + w > int.MaxValue || (long)y + h > int.MaxValue)
            throw new ArgumentException($"слой {spec.Name}: прямоугольник {w}×{h}");
        var values = Room((int)(w * h) * 8);
        var at = 0;
        var land = spec.Name == LandLayer && owner.IsWorld ? Source.Land : null;
        for (var tileY = 0; tileY < h; tileY++)
            for (var tileX = 0; tileX < w; tileX++, at += 8)
            {
                var (tx, ty) = (x + tileX, y + tileY);
                var value = land is null ? layers.Get((int)id, owner, tx, ty, (int)channel) : LandByte(land, tx, ty, (int)channel);
                System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(values[at..], value);
            }
        return 0;
    }

    /// <summary>Канал слоя «land»: хранимый байт тайла (тип, биом, байт вершины, рельеф).</summary>
    private static double LandByte(Land land, int x, int y, int channel)
    {
        var tile = land.Stored(x, y);
        return channel switch { 0 => tile.Terrain, 1 => tile.Biome, 2 => tile.Peak, _ => tile.Relief };
    }

    /// <summary>
    /// Слой по номеру и владелец по номеру игрока (0 — мир): сторона владельца — как у слоя; не то — исключение
    /// (ошибка входа, ErrorBadInput).
    /// </summary>
    private static LayerSpec LayerOf(LayerSet layers, uint id, uint player, out LayerOwner owner)
    {
        if (id >= (uint)layers.Count)
            throw new ArgumentException($"нет слоя №{id}");
        var spec = layers.Spec((int)id);
        if ((spec.Scope == LayerScope.Player) != (player != 0))
            throw new ArgumentException(player == 0 ? $"слой {spec.Name} — у игроков, а не у мира" : $"слой {spec.Name} — у мира, а не у игроков");
        owner = player == 0 ? LayerOwner.World : LayerOwner.Player(player);
        return spec;
    }
}
