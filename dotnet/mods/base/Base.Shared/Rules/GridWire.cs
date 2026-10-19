using System.Buffers.Binary;

namespace Base.Shared.Rules;

/// <summary>
/// Энергия для клиента (schema/energy.json: Grid, Grids). Хост считает сети (Books) и в конце тика пишет: зданию сети —
/// Grid (номер сети среди сетей хозяина с 1, скрыт от чужих; биты состояния — всем, как powerStates и loadedPlants в
/// TS видит каждый), сущности счёта — Grids.nets (@net owner): по записи <see cref="NetSize"/> байт на сеть, в порядке
/// сетей (главное здание — первой):
/// <code>flags:u8 (1 перегруз, 2 есть главное здание) crowd:u8 produced demand peak stored income: i32 тысячные</code>
/// Пишется только изменившееся: в спокойной партии — ноль байт.
/// </summary>
public static class GridWire
{
    /// <summary>Биты Grid.state.</summary>
    public const int Overload = 1, Starved = 2, Loaded = 4, Coreless = 8;

    /// <summary>Байт на сеть в Grids.nets.</summary>
    public const int NetSize = 22;

    private const byte FlagOverload = 1, FlagCore = 2;

    /// <summary>Хозяйство одной сети: выработка с отданным накопителями, спрос сейчас и в работе, отданное, доход, число тесных.</summary>
    public readonly record struct Net(bool Overload, bool Core, int Crowd, double Produced, double Demand, double Peak, double Stored, double Income);

    public static byte[] Pack(IReadOnlyList<Net> nets)
    {
        var bytes = new byte[nets.Count * NetSize];
        for (var i = 0; i < nets.Count; i++)
        {
            var net = nets[i];
            var span = bytes.AsSpan(i * NetSize, NetSize);
            span[0] = (byte)((net.Overload ? FlagOverload : 0) | (net.Core ? FlagCore : 0));
            span[1] = (byte)Math.Clamp(net.Crowd, 0, 255);
            Milli(span[2..], net.Produced);
            Milli(span[6..], net.Demand);
            Milli(span[10..], net.Peak);
            Milli(span[14..], net.Stored);
            Milli(span[18..], net.Income);
        }
        return bytes;
    }

    public static List<Net> Unpack(ReadOnlySpan<byte> bytes)
    {
        var nets = new List<Net>(bytes.Length / NetSize);
        for (var at = 0; at + NetSize <= bytes.Length; at += NetSize)
        {
            var span = bytes.Slice(at, NetSize);
            nets.Add(new Net((span[0] & FlagOverload) != 0, (span[0] & FlagCore) != 0, span[1],
                Milli(span[2..]), Milli(span[6..]), Milli(span[10..]), Milli(span[14..]), Milli(span[18..])));
        }
        return nets;
    }

    private static void Milli(Span<byte> span, double value) =>
        BinaryPrimitives.WriteInt32LittleEndian(span, (int)Math.Clamp(Math.Round(value * 1000), int.MinValue, int.MaxValue));

    private static double Milli(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt32LittleEndian(span) / 1000.0;
}

/// <summary>Почему лаборатория стоит или замедлилась (LabLack) — числом в Research.cause: строку мира клиент не читает.</summary>
public static class LabLack
{
    public const int None = 0, Idle = 1, Off = 2, Power = 3, Credits = 4, Material = 5;

    /// <summary>Строка lack хоста → число; груз — <see cref="Material"/>.</summary>
    public static int Of(string? lack) => lack switch
    {
        null or "" => None,
        "idle" => Idle,
        "off" => Off,
        "power" => Power,
        "credits" => Credits,
        _ => Material,
    };

    /// <summary>Число → строка lack; груз — его имя (good), без имени — null.</summary>
    public static string? Name(int cause, string? good) => cause switch
    {
        Idle => "idle",
        Off => "off",
        Power => "power",
        Credits => "credits",
        Material => good,
        _ => null,
    };
}
