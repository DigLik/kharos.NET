using MemoryPack;

namespace Base.Shared.Orders;

/// <summary>Очередь приказов юнита (Orders.list, MemoryPack): по порядку, первый — следующий.</summary>
[MemoryPackable]
public sealed partial class OrderQueue
{
    public QueuedOrder[] Items { get; set; } = [];
}

/// <summary>
/// Приказ в очереди юнита (Shift; Orders.list — <see cref="OrderQueue"/>): группа — кто получит его вместе (номер
/// тик × 1024 + порядок в тике), имя приказа и его payload без списка юнитов — юнитов группы соберёт followOrders.
/// </summary>
[MemoryPackable]
public sealed partial class QueuedOrder
{
    public long Group { get; set; }
    public string Name { get; set; } = "";
    public byte[] Payload { get; set; } = [];
}
