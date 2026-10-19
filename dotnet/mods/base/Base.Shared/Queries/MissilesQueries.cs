namespace Base.Shared.Queries;

// Запросы ракет (Host.Query: тесты, администратор, редактор) — только чтение мира. Аргументы — PlayerArgs, TargetArgs,
// EntityArgs из соседних файлов.

/// <summary>Имена запросов ракет.</summary>
public static class MissilesQueries
{
    /// <summary>Что мешает начать сборку боеголовки в шахте (warheadLack): TargetArgs (Player, Target — шахта) → string «busy», «credits», «materials»; пусто — ничего.</summary>
    public const string WarheadLack = "missiles.warheadLack";

    /// <summary>Что мешает перехватчику начать сборку ракеты (interceptorLack): EntityArgs → string «off», «credits», «materials»; пусто — ничего.</summary>
    public const string InterceptorLack = "missiles.interceptorLack";

    /// <summary>Сколько тиков до старта ракеты у шахты в пуске (launchLeft): EntityArgs → int; −1 — пуск не идёт.</summary>
    public const string LaunchLeft = "missiles.launchLeft";
}
