using MemoryPack;

namespace Base.Shared.Orders;

// Приказы добычи. Игроку они не нужны: брошенный груз появляется от разрушения и разбора зданий (раздел Mining).

/// <summary>
/// edit.drop: бросить груз на тайл точки (X, Y) — к дропу, что там лежит, или новым (dropItems). Задана Entity — вместо этого
/// бросить на землю под ней весь груз этой сущности (dropCargo). Право редактора.
/// </summary>
[MemoryPackable]
public sealed partial class EditDropOrder
{
    public const string Name = "edit.drop";
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Груз → сколько (имена из resources.json, products.json, ores.json).</summary>
    public Dictionary<string, double> Items { get; set; } = [];

    /// <summary>Сущность, чей груз бросить (dropCargo); 0 — бросить Items.</summary>
    public uint Entity { get; set; }
}
