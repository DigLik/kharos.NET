using MemoryPack;

namespace Base.Shared.Queries;

// Запросы добычи (Host.Query): только чтение мира — для тестов, администратора, редактора.

/// <summary>Аргументы «месторождение с левым верхним тайлом (X, Y)».</summary>
[MemoryPackable]
public sealed partial class DepositArgs
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Смена месторождений: тики, к которым ждут новых, и сколько мест под них уже перепробовано.</summary>
[MemoryPackable]
public sealed partial class RenewalInfo
{
    public ulong[] Due { get; set; } = [];
    public int Spawned { get; set; }
}

/// <summary>Имена запросов добычи.</summary>
public static class MiningQueries
{
    /// <summary>Сколько осталось в месторождении (reserveLeft): DepositArgs → double; месторождения нет — 0.</summary>
    public const string Reserve = "mining.reserve";

    /// <summary>Вид месторождения под тайлом (depositAt(...).kind): DepositArgs → string; нет — "".</summary>
    public const string Kind = "mining.kind";

    /// <summary>Какая руда под шахтой (mineOre): EntityArgs → string; нет — "".</summary>
    public const string Ore = "mining.ore";

    /// <summary>Очередь смены выработанных (DepositLayer.due и spawned): без аргументов → RenewalInfo.</summary>
    public const string Renewal = "mining.renewal";
}
