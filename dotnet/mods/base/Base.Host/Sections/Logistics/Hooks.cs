using Base.Host.Kernel;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Что логистике нужно от других разделов: каждый ставит своё в Setup. Не поставили — логистика обходится без этого
/// (сказано у каждого). Состояние чужих разделов, лежащее в компонентах (Trade, Assembly, Batch, Off), логистика читает
/// сама.
/// </summary>
internal static class Hooks
{
    /// <summary>
    /// Сырьё нынешней темы исследований игрока и головной темы плана, сколько ещё потратить (researchNeeds, С6): груз →
    /// количество. Нет — лаборатории ничего не заказывают и отдают всё.
    /// </summary>
    public static Func<Call, uint, IReadOnlyList<(string Good, double Amount)>>? ResearchNeeds { get; set; }
}
