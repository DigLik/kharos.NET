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
}

/// <summary>Значение поля в Add/Set: метка (ValueTag движка) и само значение.</summary>
public readonly record struct ModFieldValue(byte Field, byte Tag, double Number, uint Id, string? Text, byte[]? Bytes);

/// <summary>Разобранная запись буфера команд — для тестов, журнала и проверки; движок применяет байты сам.</summary>
public sealed record ModCommand(ModCommandOp Op, uint Entity, int Component, IReadOnlyList<ModFieldValue> Fields, string? Name, byte[]? Payload);

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
