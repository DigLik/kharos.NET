using MemoryPack;

namespace Base.Shared.Queries;

// Запросы ядра (Host.Query: тесты, администратор, редактор) — только чтение мира. Имя — константа Name; аргументы и
// ответ — MemoryPack (ответ — простое значение (bool, double, int, string) или NameList).

/// <summary>Аргументы «про игрока».</summary>
[MemoryPackable]
public sealed partial class PlayerArgs
{
    public uint Player { get; set; }
}

/// <summary>Аргументы «про сущность».</summary>
[MemoryPackable]
public sealed partial class EntityArgs
{
    public uint Entity { get; set; }
}

/// <summary>Аргументы «игрок и вид юнита».</summary>
[MemoryPackable]
public sealed partial class UnitArgs
{
    public uint Player { get; set; }
    public string Unit { get; set; } = "";
}

/// <summary>Аргументы «здание вида Type левым верхним тайлом (X, Y), зазор Gap».</summary>
[MemoryPackable]
public sealed partial class PlaceArgs
{
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Gap { get; set; }
}

/// <summary>Аргументы «MCV Unit игрока Player развернуть на месте или (At) левым верхним тайлом (X, Y)».</summary>
[MemoryPackable]
public sealed partial class DeployArgs
{
    public uint Player { get; set; }
    public uint Unit { get; set; }
    public bool At { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Ответ-список (виды, ключи, имена): массив в корне MemoryPack в wasm (AOT) не пишется.</summary>
[MemoryPackable]
public sealed partial class NameList
{
    public string[] Items { get; set; } = [];
}

/// <summary>Имена запросов ядра.</summary>
public static class KernelQueries
{
    /// <summary>Кредиты игрока (creditsOf): PlayerArgs → double.</summary>
    public const string Credits = "kernel.credits";

    /// <summary>Награды игрока по порядку (rewardsOf): PlayerArgs → NameList.</summary>
    public const string Rewards = "kernel.rewards";

    /// <summary>Встанет ли здание (canPlace): PlaceArgs → bool.</summary>
    public const string CanPlace = "kernel.canPlace";

    /// <summary>Можно ли развернуть MCV (canDeploy, canDeployAt): DeployArgs → bool.</summary>
    public const string CanDeploy = "kernel.canDeploy";

    /// <summary>Можно ли свернуть здание (canPack): DeployArgs (Unit — здание) → bool.</summary>
    public const string CanPack = "kernel.canPack";

    /// <summary>Кого производит сущность сейчас (producibleBy): EntityArgs → NameList.</summary>
    public const string Producible = "kernel.producible";

    /// <summary>Очередь производителя по видам (Producer.queue): EntityArgs → NameList.</summary>
    public const string Queue = "kernel.queue";

    /// <summary>Занято в лимите армии (armyOf): PlayerArgs → double.</summary>
    public const string Army = "kernel.army";

    /// <summary>Не влезет ли юнит в лимит армии (overLimit): UnitArgs → bool.</summary>
    public const string OverLimit = "kernel.overLimit";

    /// <summary>Тема, которую надо исследовать для юнита (missingResearch): UnitArgs → string ("" — нечего).</summary>
    public const string MissingResearch = "kernel.missingResearch";

    /// <summary>Каких зданий для юнита у игрока нет готовыми (missingRequirements): UnitArgs → NameList.</summary>
    public const string MissingRequirements = "kernel.missingRequirements";

    /// <summary>Сколько тиков строится юнит (buildTicks): UnitArgs (Unit) → int.</summary>
    public const string BuildTicks = "kernel.buildTicks";

    /// <summary>Очередь приказов Shift сущности по именам (Orders.list): EntityArgs → NameList.</summary>
    public const string Orders = "kernel.orders";
}
