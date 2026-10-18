using MemoryPack;

namespace Base.Shared.Signals;

// Сигналы клиентам (адресно, SignalTo/SignalAt): заготовки, поля доопределяют разделы (Н5.4).

/// <summary>refused: приказ отклонён; Reason — ключ сообщения (раздел host словаря), Order — имя приказа.</summary>
[MemoryPackable]
public sealed partial class RefusedSignal
{
    public const string Name = "refused";
    public string Order { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>unit: событие юнита для звука и подсказок (готов, атакован, ...): Kind — ключ, место — тайлы.</summary>
[MemoryPackable]
public sealed partial class UnitSignal
{
    public const string Name = "unit";
    public string Kind { get; set; } = "";
    public uint Entity { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>ping: метка союзника в точке.</summary>
[MemoryPackable]
public sealed partial class PingSignal
{
    public const string Name = "ping";
    public uint Player { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
