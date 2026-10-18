using MemoryPack;

namespace Base.Shared.Orders;

// Приказы исследований и уровней (Research). Темы — имена записей research.json.

/// <summary>research: тема в план вместе с неоткрытыми предшественниками; Front — в начало плана.</summary>
[MemoryPackable]
public sealed partial class ResearchOrder
{
    public const string Name = "research";
    public string Topic { get; set; } = "";
    public bool Front { get; set; }
}

/// <summary>dequeue: убрать тему из плана вместе с темами, которым она нужна.</summary>
[MemoryPackable]
public sealed partial class DequeueOrder
{
    public const string Name = "dequeue";
    public string Topic { get; set; } = "";
}

/// <summary>cancelResearch: очистить план; начатые темы помнят прогресс.</summary>
[MemoryPackable]
public sealed partial class CancelResearchOrder
{
    public const string Name = "cancelResearch";
}

/// <summary>upgrade: следующий уровень своего здания; цена списывается сразу.</summary>
[MemoryPackable]
public sealed partial class UpgradeOrder
{
    public const string Name = "upgrade";
    public uint Building { get; set; }
    public uint[] Builders { get; set; } = [];
}

/// <summary>cancelUpgrade: отменить улучшение с возвратом цены.</summary>
[MemoryPackable]
public sealed partial class CancelUpgradeOrder
{
    public const string Name = "cancelUpgrade";
    public uint Building { get; set; }
}
