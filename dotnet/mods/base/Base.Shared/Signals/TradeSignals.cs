using MemoryPack;

namespace Base.Shared.Signals;

// Вести о кораблях и подарках (адресно, SignalTo): в TS их шлёт host.ts по самой сущности корабля.

/// <summary>
/// ferry: корабль с подарком — вышел (sent, отправителю), летит (coming, получателю), прибыл (arrived, отправителю),
/// выгрузил (got, получателю). From и To — игроки.
/// </summary>
[MemoryPackable]
public sealed partial class FerrySignal
{
    public const string Name = "ferry";
    public const string Sent = "sent", Coming = "coming", Arrived = "arrived", Got = "got";
    public string Kind { get; set; } = "";
    public uint From { get; set; }
    public uint To { get; set; }
    public uint Port { get; set; }
    public double Credits { get; set; }
}

/// <summary>gift: юниты перешли союзнику — отправителю (sent) и получателю (got); Count — сколько.</summary>
[MemoryPackable]
public sealed partial class GiftSignal
{
    public const string Name = "gift";
    public const string Sent = "sent", Got = "got";
    public string Kind { get; set; } = "";
    public uint From { get; set; }
    public uint To { get; set; }
    public int Count { get; set; }
}
