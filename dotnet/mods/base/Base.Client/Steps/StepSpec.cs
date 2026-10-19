namespace Base.Client;

/// <summary>Шаг (StepSpec в firstSteps.ts): ключ и что подсветить в сетке, пока шаг текущий.</summary>
/// <param name="Key">Ключ словаря steps.items.*.</param>
/// <param name="Building">Кнопка здания (и её раздел).</param>
/// <param name="Unit">Заказ юнита.</param>
/// <param name="Deploy">Разворот MCV.</param>
/// <param name="Research">Кнопка окна исследований на верхней полосе (тема).</param>
/// <param name="Sell">Кнопка продажи у космопорта.</param>
internal sealed record StepSpec(string Key, string? Building = null, string? Unit = null, bool Deploy = false, string? Research = null, bool Sell = false);
