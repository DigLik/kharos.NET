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

    /// <summary>
    /// Пачки в трубах, ещё не пришедшие (С2c), для потоков вне систем логистики (приказы, запросы, крючки стройки): куда,
    /// груз (номер в Cargo), сколько. Нет — там пачек не видно (в системах логистики их даёт кадр).
    /// </summary>
    public static Func<Call, List<(uint To, int Good, double Amount)>>? Batches { get; set; }
}
