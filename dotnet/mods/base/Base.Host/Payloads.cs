using MemoryPack;

namespace Base.Host;

// Payload приказов игроков и сигналов клиентам — MemoryPack. Те же поля, что у таблиц пилота (args и signal в Luau);
// этот файл подключён и в тесты хоста.

/// <summary>Приказ move: свои юниты с ходовой едут к точке (группа встаёт вокруг неё спиралью).</summary>
[MemoryPackable]
public sealed partial class MoveOrder
{
    public uint[] Units { get; set; } = [];
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Приказ stop: стоять.</summary>
[MemoryPackable]
public sealed partial class StopOrder
{
    public uint[] Units { get; set; } = [];
}

/// <summary>Приказ produce: заказать юнит в здании; кредиты списываются сразу.</summary>
[MemoryPackable]
public sealed partial class ProduceOrder
{
    public uint Building { get; set; }
    public string Unit { get; set; } = "";
}

/// <summary>Приказ cancel: снять последний заказ здания с возвратом кредитов.</summary>
[MemoryPackable]
public sealed partial class CancelOrder
{
    public uint Building { get; set; }
}

/// <summary>Приказ rally: точка сбора здания.</summary>
[MemoryPackable]
public sealed partial class RallyOrder
{
    public uint Building { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Сигнал moved: приказ ехать принят.</summary>
[MemoryPackable]
public sealed partial class MovedSignal
{
    public uint Player { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Сигнал refused: заказ отклонён (reason — "credits").</summary>
[MemoryPackable]
public sealed partial class RefusedSignal
{
    public uint Player { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Сигнал ordered: заказ принят.</summary>
[MemoryPackable]
public sealed partial class OrderedSignal
{
    public uint Player { get; set; }
    public uint Building { get; set; }
    public string Unit { get; set; } = "";
}

/// <summary>Сигнал produced: юнит вышел из ворот.</summary>
[MemoryPackable]
public sealed partial class ProducedSignal
{
    public uint Player { get; set; }
    public uint Building { get; set; }
    public string Unit { get; set; } = "";
    public uint Entity { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
