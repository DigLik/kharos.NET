using Base.Host.Kernel;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Что стройке нужно от других разделов: каждый ставит своё в Setup. Не поставили — стройка обходится без этого
/// (сказано у каждого).
/// </summary>
internal static class Hooks
{
    /// <summary>Поедет ли строитель builder сам за материалом для стройки site (willFetch, С2b); нет — не поедет.</summary>
    public static Func<Call, uint, uint, bool>? WillFetch { get; set; }

    /// <summary>Груз склада сущности — на землю (dropCargo, С2а); нет — груз пропадает вместе с ней.</summary>
    public static Action<Call, uint>? DropCargo { get; set; }

    /// <summary>Вернуть игроку закупку с орбиты космопорта (refundPurchase, С2d): игрок, порт.</summary>
    public static Action<Call, uint, uint>? RefundPurchase { get; set; }

    /// <summary>Отменить улучшение здания с возвратом (cancelUpgrade, С6): игрок, здание.</summary>
    public static Func<Call, uint, uint, bool>? CancelUpgrade { get; set; }

    /// <summary>Электростанции зон, где потребителей больше, чем потянули бы и целые (overbuiltPlants, С3); нет — таких нет.</summary>
    public static Func<Call, IReadOnlySet<uint>>? Overbuilt { get; set; }

    /// <summary>
    /// Доля энергии зоны, с которой работает потребитель (powerSupply, С3): ремонтная станция чинит с этой долей. Нет —
    /// выключенная (Off) стоит, остальные работают в полную силу.
    /// </summary>
    public static Func<Call, uint, double>? PowerShare { get; set; }
}
