using System.Buffers.Binary;
using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 19 Items: предметы слоя в круге (формат — ModAbi.ToolItems). Владелец — Д3.
public partial class ModTools
{
    /// <summary>Не больше ячеек за вызов: круг больше карты не перебирают, а берут существующие ячейки слоя.</summary>
    private const long MaxItemCells = 1 << 22;

    private partial int Items(GameWorld world, ref ToolInput r)
    {
        var layers = Source.Layers ?? throw new ArgumentException("у партии нет слоёв");
        var id = r.U32();
        var player = r.U32();
        var (x, y, radius) = (r.F64(), r.F64(), r.F64());
        var spec = LayerOf(layers, id, player, out var owner);
        if (spec.IsGrid)
            throw new ArgumentException($"слой {spec.Name}: сетка, а не предметы");
        if (!double.IsFinite(x) || !double.IsFinite(y) || !(radius >= 0) || !double.IsFinite(radius))
            throw new ArgumentException("Items: круг не из чисел");
        var cell = (double)spec.Cell;
        var (fromX, toX) = (CellIndex((x - radius) / cell), CellIndex((x + radius) / cell));
        var (fromY, toY) = (CellIndex((y - radius) / cell), CellIndex((y + radius) / cell));
        var cells = new List<(int X, int Y)>();
        var existing = layers.Cells((int)id, owner);
        if (((double)toX - fromX + 1) * ((double)toY - fromY + 1) > Math.Min(existing.Count, MaxItemCells))
        {
            // Круг шире, чем занятых ячеек: берём занятые, что попали в рамку.
            foreach (var found in existing)
                if (found.X >= fromX && found.X <= toX && found.Y >= fromY && found.Y <= toY)
                    cells.Add(found);
            cells.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        }
        else
        {
            for (var cellY = fromY; cellY <= toY; cellY++)
                for (var cellX = fromX; cellX <= toX; cellX++)
                    cells.Add((cellX, cellY));
        }
        var inside = new List<LayerItem>();
        var reach = radius * radius;
        foreach (var at in cells)
            foreach (var item in layers.Items((int)id, owner, at))
            {
                var (dx, dy) = (item.X - x, item.Y - y);
                if (dx * dx + dy * dy <= reach)
                    inside.Add(item);
            }
        U32((uint)inside.Count);
        var fields = spec.Fields.Count;
        foreach (var item in inside)
        {
            var record = Room(16 + fields * 8);
            BinaryPrimitives.WriteDoubleLittleEndian(record, item.X);
            BinaryPrimitives.WriteDoubleLittleEndian(record[8..], item.Y);
            for (var f = 0; f < fields; f++)
                BinaryPrimitives.WriteDoubleLittleEndian(record[(16 + f * 8)..], item.Fields[f]);
        }
        return 0;
    }

    private static int CellIndex(double value)
    {
        var cell = Math.Floor(value);
        return cell <= int.MinValue ? int.MinValue : cell >= int.MaxValue ? int.MaxValue : (int)cell;
    }
}
