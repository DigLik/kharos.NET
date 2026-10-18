namespace Base.Shared.Data;

/// <summary>
/// Правила мира (rules.json, запись default = DEFAULT_RULES в packages/data/src/rules.ts без правил местности — они в
/// moves.json): числа, которые можно менять на ходу.
/// </summary>
public sealed class RulesSpec
{
    public string? About { get; set; }
    public double RepairSpeed { get; set; } = 2;
    public double RepairCost { get; set; } = 0.5;
    public double RepairPause { get; set; } = 3;
    public bool TechTree { get; set; } = true;
    public double OfflineIncome { get; set; } = 0.2;
    public double OfflineMinutes { get; set; } = 30;
    public double Protection { get; set; } = 10;
    public double ResearchScale { get; set; } = 1;
    public double ResearchGrowth { get; set; } = 1.6;
    public double ResearchSlow { get; set; } = 1;
    public double UpgradeScale { get; set; } = 1;
    public double DepositRenewal { get; set; } = 15;
    public double ArmyLimit { get; set; } = 600;
    public bool UnitAmmo { get; set; } = true;
}

/// <summary>
/// Ходовая (moves.json, docs/movement.md М1): слой, доли скорости по видам земли (rock, sand, swamp, brine — болото
/// солончаков, toxic — болото красных пустошей, cliff — подножие обрыва, mountain; нет — 0, нельзя), урон местности в
/// секунду, множители покрытия (нет — покрытие безразлично), нижний предел доли.
/// </summary>
public sealed class ChassisSpec
{
    public string? About { get; set; }
    public string Layer { get; set; } = "ground";
    public Dictionary<string, double> Terrain { get; set; } = [];
    public Dictionary<string, double> Harm { get; set; } = [];
    public Dictionary<string, double> Paved { get; set; } = [];
    public double Slowest { get; set; } = 0.05;
    public string Steer { get; set; } = "pivot";
}

/// <summary>Где появляется игрок (host.ts): отступы, попытки, сколько скалы вокруг, запасной круг.</summary>
public sealed class SpawnSpec
{
    public double Apart { get; set; } = 96;
    public double Tries { get; set; } = 400;
    public double Area { get; set; } = 6;
    public double Rock { get; set; } = 100;
    public double Margin { get; set; } = 16;
    public double Radius { get; set; } = 24;
    public double Slots { get; set; } = 8;
}

/// <summary>
/// Партия (games.json). size, fog, land, remember читает движок (GameContent.Game); start — давать ли вошедшему
/// стартовый набор (credits и units по порядку), sandbox — готовая база (sandbox.ts).
/// </summary>
public sealed class GameSpec
{
    public string? About { get; set; }
    public string? Title { get; set; }
    public int? Size { get; set; }
    public bool Fog { get; set; } = true;
    public string? Land { get; set; }
    public bool Start { get; set; } = true;
    public bool Sandbox { get; set; }
    public double Credits { get; set; }
    public string[] Units { get; set; } = [];
    public SpawnSpec Spawn { get; set; } = new();
    public string[] Remember { get; set; } = [];
}
