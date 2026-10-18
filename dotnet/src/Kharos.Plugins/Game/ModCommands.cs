using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;

namespace Kharos.Plugins.Game;

/// <summary>Запись буфера команд (как CommandOp движка).</summary>
public enum ModCommandOp : byte
{
    /// <summary>Новая сущность.</summary>
    Spawn = ModAbi.OpSpawn,
    /// <summary>Убрать сущность.</summary>
    Destroy = ModAbi.OpDestroy,
    /// <summary>Добавить компонент.</summary>
    Add = ModAbi.OpAdd,
    /// <summary>Поменять поля.</summary>
    Set = ModAbi.OpSet,
    /// <summary>Снять компонент.</summary>
    Remove = ModAbi.OpRemove,
    /// <summary>Событие обработчикам.</summary>
    Emit = ModAbi.OpEmit,
    /// <summary>Сигнал клиентам.</summary>
    Signal = ModAbi.OpSignal,
    /// <summary>Канал слоя-сетки в прямоугольнике.</summary>
    Layer = ModAbi.OpLayer,
    /// <summary>Предмет слоя: положить или убрать.</summary>
    Item = ModAbi.OpItem,
    /// <summary>Правка земли.</summary>
    Land = ModAbi.OpLand,
    /// <summary>Сигнал списку игроков.</summary>
    SignalTo = ModAbi.OpSignalTo,
    /// <summary>Сигнал тем, кто видит точку.</summary>
    SignalAt = ModAbi.OpSignalAt,
}

/// <summary>Значение поля в Add/Set: метка (ValueTag движка) и само значение.</summary>
public readonly record struct ModFieldValue(byte Field, byte Tag, double Number, uint Id, string? Text, byte[]? Bytes);

/// <summary>Разобранная запись буфера команд — для тестов, журнала и проверки; движок применяет байты сам.</summary>
public sealed record ModCommand(ModCommandOp Op, uint Entity, int Component, IReadOnlyList<ModFieldValue> Fields, string? Name, byte[]? Payload)
{
    /// <summary>SignalTo: игроки.</summary>
    public uint[]? Players { get; init; }

    /// <summary>Layer и Item: номер слоя и владелец (0 — мир).</summary>
    public int Layer { get; init; } = -1;

    /// <summary>Layer и Item: владелец слоя (0 — мир).</summary>
    public uint Owner { get; init; }

    /// <summary>Layer — канал, Item — режим (ModAbi.Item*), Land — вид (ModAbi.Land*).</summary>
    public byte Mode { get; init; }

    /// <summary>Место: тайл (Layer, Land) или точка (Item, SignalAt).</summary>
    public double X { get; init; }

    /// <summary>Место по y.</summary>
    public double Y { get; init; }

    /// <summary>Layer: прямоугольник.</summary>
    public uint W { get; init; }

    /// <summary>Layer: прямоугольник.</summary>
    public uint H { get; init; }

    /// <summary>Числа: значение Layer, поля Item, вершина Land (px, py, radius).</summary>
    public double[] Values { get; init; } = [];

    /// <summary>Land: байты тайла (terrain, biome, tier, flags, dunes) или биом вершины.</summary>
    public byte[] Tile { get; init; } = [];
}

/// <summary>Буфер команд не разобран: обрыв, незнакомая запись или метка.</summary>
public sealed class ModCommandFormatException(string message, int offset) : FormatException($"команда @{offset}: {message}")
{
    /// <summary>Смещение записи.</summary>
    public int Offset { get; } = offset;
}

/// <summary>
/// Разбор буфера команд мода. Формат — ровно буфер команд движка (Kharos.Engine.World.CommandBuffer), здесь — без
/// зависимости от движка: только синтаксис, без проверки схемы и номеров (это делает Commands.Validate движка).
/// </summary>
public static class ModCommands
{
    /// <summary>Все записи по порядку.</summary>
    public static List<ModCommand> Decode(ReadOnlySpan<byte> buffer)
    {
        var commands = new List<ModCommand>();
        var at = 0;
        while (at < buffer.Length)
            commands.Add(Next(buffer, ref at));
        return commands;
    }

    /// <summary>Сколько записей (с проверкой синтаксиса).</summary>
    public static int Count(ReadOnlySpan<byte> buffer)
    {
        var count = 0;
        var at = 0;
        while (at < buffer.Length)
        {
            Next(buffer, ref at);
            count++;
        }
        return count;
    }

    private static ModCommand Next(ReadOnlySpan<byte> buffer, ref int at)
    {
        var start = at;
        var op = (ModCommandOp)Byte(buffer, ref at, start);
        switch (op)
        {
            case ModCommandOp.Spawn:
            case ModCommandOp.Destroy:
                return new ModCommand(op, Varint(buffer, ref at, start), -1, [], null, null);
            case ModCommandOp.Add:
            case ModCommandOp.Set:
            {
                var entity = Varint(buffer, ref at, start);
                var component = (int)Varint(buffer, ref at, start);
                var count = Byte(buffer, ref at, start);
                if (count > ModAbi.MaxFields)
                    throw new ModCommandFormatException($"{count} полей", start);
                var fields = new ModFieldValue[count];
                for (var i = 0; i < count; i++)
                    fields[i] = Value(buffer, ref at, start);
                return new ModCommand(op, entity, component, fields, null, null);
            }
            case ModCommandOp.Remove:
            {
                var entity = Varint(buffer, ref at, start);
                return new ModCommand(op, entity, (int)Varint(buffer, ref at, start), [], null, null);
            }
            case ModCommandOp.Emit:
            case ModCommandOp.Signal:
            {
                var name = Encoding.UTF8.GetString(Blob(buffer, ref at, start));
                var payload = Blob(buffer, ref at, start).ToArray();
                return new ModCommand(op, 0, -1, [], name, payload);
            }
            case ModCommandOp.SignalTo:
            {
                var name = Encoding.UTF8.GetString(Blob(buffer, ref at, start));
                var payload = Blob(buffer, ref at, start).ToArray();
                var count = Varint(buffer, ref at, start);
                if (count > buffer.Length - at)
                    throw new ModCommandFormatException($"{count} игроков", start);
                var players = new uint[count];
                for (var i = 0; i < players.Length; i++)
                    players[i] = Varint(buffer, ref at, start);
                return new ModCommand(op, 0, -1, [], name, payload) { Players = players };
            }
            case ModCommandOp.SignalAt:
            {
                var name = Encoding.UTF8.GetString(Blob(buffer, ref at, start));
                var payload = Blob(buffer, ref at, start).ToArray();
                return new ModCommand(op, 0, -1, [], name, payload) { X = F64(buffer, ref at, start), Y = F64(buffer, ref at, start) };
            }
            case ModCommandOp.Layer:
            {
                var layer = (int)Varint(buffer, ref at, start);
                var owner = Varint(buffer, ref at, start);
                var channel = Byte(buffer, ref at, start);
                var (x, y) = (SVarint(buffer, ref at, start), SVarint(buffer, ref at, start));
                var (w, h) = (Varint(buffer, ref at, start), Varint(buffer, ref at, start));
                return new ModCommand(op, 0, -1, [], null, null) { Layer = layer, Owner = owner, Mode = channel, X = x, Y = y, W = w, H = h, Values = [F64(buffer, ref at, start)] };
            }
            case ModCommandOp.Item:
            {
                var layer = (int)Varint(buffer, ref at, start);
                var owner = Varint(buffer, ref at, start);
                var mode = Byte(buffer, ref at, start);
                var (x, y) = (F64(buffer, ref at, start), F64(buffer, ref at, start));
                var values = new double[mode == ModAbi.ItemPush ? Byte(buffer, ref at, start) : 0];
                for (var i = 0; i < values.Length; i++)
                    values[i] = F64(buffer, ref at, start);
                return new ModCommand(op, 0, -1, [], null, null) { Layer = layer, Owner = owner, Mode = mode, X = x, Y = y, Values = values };
            }
            case ModCommandOp.Land:
            {
                var kind = Byte(buffer, ref at, start);
                var (x, y) = (SVarint(buffer, ref at, start), SVarint(buffer, ref at, start));
                if (kind == ModAbi.LandTile)
                    return new ModCommand(op, 0, -1, [], null, null) { Mode = kind, X = x, Y = y, Tile = Take(buffer, ref at, 5, start).ToArray() };
                var biome = Byte(buffer, ref at, start);
                return new ModCommand(op, 0, -1, [], null, null)
                {
                    Mode = kind, X = x, Y = y, Tile = [biome], Values = [F64(buffer, ref at, start), F64(buffer, ref at, start), F64(buffer, ref at, start)],
                };
            }
            default:
                throw new ModCommandFormatException($"незнакомая запись 0x{(byte)op:x2}", start);
        }
    }

    private static ModFieldValue Value(ReadOnlySpan<byte> buffer, ref int at, int start)
    {
        var field = Byte(buffer, ref at, start);
        var tag = Byte(buffer, ref at, start);
        switch (tag)
        {
            case ModAbi.ValueNumber:
                return new ModFieldValue(field, tag, BinaryPrimitives.ReadDoubleLittleEndian(Take(buffer, ref at, 8, start)), 0, null, null);
            case ModAbi.ValueFalse:
            case ModAbi.ValueTrue:
                return new ModFieldValue(field, tag, tag == ModAbi.ValueTrue ? 1 : 0, 0, null, null);
            case ModAbi.ValueStringId:
                return new ModFieldValue(field, tag, 0, Varint(buffer, ref at, start), null, null);
            case ModAbi.ValueStringText:
                return new ModFieldValue(field, tag, 0, 0, Encoding.UTF8.GetString(Blob(buffer, ref at, start)), null);
            case ModAbi.ValueBytes:
                return new ModFieldValue(field, tag, 0, 0, null, Blob(buffer, ref at, start).ToArray());
            default:
                throw new ModCommandFormatException($"незнакомое значение 0x{tag:x2}", start);
        }
    }

    private static byte Byte(ReadOnlySpan<byte> buffer, ref int at, int start) => Take(buffer, ref at, 1, start)[0];

    private static double F64(ReadOnlySpan<byte> buffer, ref int at, int start) => BinaryPrimitives.ReadDoubleLittleEndian(Take(buffer, ref at, 8, start));

    private static int SVarint(ReadOnlySpan<byte> buffer, ref int at, int start)
    {
        var raw = Varint(buffer, ref at, start);
        return (int)(raw >> 1) ^ -(int)(raw & 1);
    }

    private static uint Varint(ReadOnlySpan<byte> buffer, ref int at, int start)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var part = Byte(buffer, ref at, start);
            if (shift == 28 && part > 0x0f)
                throw new ModCommandFormatException("varint длиннее u32", start);
            value |= (uint)(part & 0x7f) << shift;
            if (part < 0x80)
                return value;
        }
        throw new ModCommandFormatException("varint длиннее u32", start);
    }

    private static ReadOnlySpan<byte> Blob(ReadOnlySpan<byte> buffer, ref int at, int start)
    {
        var length = Varint(buffer, ref at, start);
        return Take(buffer, ref at, checked((int)Math.Min(length, int.MaxValue)), start);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> buffer, ref int at, int count, int start)
    {
        if (count > buffer.Length - at)
            throw new ModCommandFormatException("буфер обрывается", start);
        var part = buffer.Slice(at, count);
        at += count;
        return part;
    }
}
