namespace Base.Shared.Queries;

// Запросы производства (Host.Query): только чтение мира — для тестов, администратора и подсказок интерфейса.

/// <summary>Имена запросов производства.</summary>
public static class ProductionQueries
{
    /// <summary>Есть ли у цеха место под готовое одной сборки (hasRoom): EntityArgs → bool; не цех — false.</summary>
    public const string HasRoom = "production.hasRoom";
}
