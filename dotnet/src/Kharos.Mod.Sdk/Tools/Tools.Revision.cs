using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 13 Revision: ревизии мира — ключ кэша мода (ModAbi.ToolRevision).
public static partial class Tools
{
    /// <summary>
    /// Ревизии мира, мест, земли, слоёв и тик. Кэш, производный от мира (зоны, графы сетей), держат в экземпляре мода с
    /// этим ключом: экземпляр могут выбросить, но пересчёт по ключу даст то же.
    /// </summary>
    public static WorldRevision Revision()
    {
        // В вызове мода хоста мир не меняется (инструменты читают мир до вызова): ревизии — одним вызовом инструмента на
        // вызов мода, а не на каждую проверку кэша (диспетчер грузовиков проверял бы кэш зон десятки тысяч раз за тик).
        if (_inCall && _revisionCall == _call)
            return _revision;
        Begin();
        var output = Call(ModAbi.ToolRevision);
        _revision = new WorldRevision(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadUInt64LittleEndian(output[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[32..]));
        _revisionCall = _call;
        return _revision;
    }

    private static WorldRevision _revision;
    private static uint _revisionCall;

    /// <summary>Номер вызова мода хоста; вне вызова хоста (клиент) — ответы не запоминаются.</summary>
    private static uint _call;
    private static bool _inCall;

    /// <summary>Начался вызов мода хоста (система, событие, приказ, запрос): мир на время вызова неизменен.</summary>
    internal static void EnterCall()
    {
        _call++;
        _inCall = true;
    }

    /// <summary>Вызов клиентской части: реплика меняется между кадрами — ответы не запоминаются.</summary>
    internal static void LeaveCall() => _inCall = false;
}
