namespace Base.Client;

/// <summary>Текст на языке игрока: ключ словаря и подстановки парами «имя, значение» (инструмент Text, ClientAbi 48).</summary>
internal delegate string Tr(string key, params string[] args);

/// <summary>Сколько груза: для списков в интерфейсе (Stack в game/hud.ts).</summary>
internal readonly record struct Stack(string Resource, double Amount);

/// <summary>
/// Энергия сети (GridState): выработка с отданным накопителями (stored), спрос сейчас (demand), спрос при полной
/// нагрузке (peak), перегруз.
/// </summary>
internal sealed record GridState(double Produced, double Demand, double Peak, double Stored, bool Overload);

/// <summary>Чем заняты выбранные грузовики (TruckJob): режим, здания по порядку, груз и остаток ручного приказа.</summary>
internal sealed record TruckJob(string Kind, IReadOnlyList<string> Buildings, string? Resource = null, double Left = 0);

/// <summary>Строка списка игроков: номер, ник, свой ли, в сети ли, союзник ли, глава его союза (−1 — ни с кем).</summary>
internal sealed record PlayerRow(uint Player, string Name, bool Own, bool Online, bool Ally, long Head);

/// <summary>
/// Что интерфейс игрока показывает прямо сейчас — перенос HudState из game/hud.ts в объёме HUD (К3а). Поля, что ведут
/// другие пакеты (торговля и подарок — К3б, окно исследований — К4, первые шаги и тревоги — К6), здесь не собираются.
/// </summary>
internal sealed class HudState
{
    public double Credits { get; set; }
    public double Income { get; set; }
    public string Clock { get; set; } = "";
    public bool Storm { get; set; }
    public bool Editor { get; set; }
    public GridState? Grid { get; set; }
    public GridState? Power { get; set; }
    public (List<Stack> Items, double Capacity)? Stock { get; set; }
    public List<PlayerRow> Players { get; set; } = [];

    /// <summary>Тема, которую ведут лаборатории, и её готовность (кнопка «Исследования» на верхней полосе).</summary>
    public (string Topic, double Progress)? Current { get; set; }

    /// <summary>Выбранные юниты по видам, в порядке видов.</summary>
    public List<(string Type, int Count)> Units { get; set; } = [];
    public string? Building { get; set; }
    public int Buildings { get; set; }
    public bool Mixed { get; set; }
    public double? Health { get; set; }
    public double Repair { get; set; }
    public (double Charge, double Capacity, double Down)? Shield { get; set; }
    public (double Charge, double Capacity, double Flow)? Battery { get; set; }
    public (int Armed, double Health, double? Ammo, int Empty)? Army { get; set; }
    public bool Starved { get; set; }
    public bool Relief { get; set; }
    public bool Groupable { get; set; }
    public uint[]? Gift { get; set; }
    public (string Kind, double Left)? Deposit { get; set; }

    public StoredState? Stored { get; set; }
    public ReserveState? Reserve { get; set; }
    public RequestState? Request { get; set; }
    public (string Ore, double Intake)? Refinery { get; set; }
    public AssemblyState? Assembly { get; set; }
    public SwitchState? Switchable { get; set; }
    public (double Have, double Capacity)? Ammo { get; set; }
    public (double Have, double Capacity, bool Burning)? Rods { get; set; }
    public CargoState? Cargo { get; set; }
    public HaulState? Haul { get; set; }
    public TacticsState? Tactics { get; set; }
    public SiteState? Site { get; set; }
    public DemolishState? Demolish { get; set; }
    public LevelState? Level { get; set; }
    public LabState? Research { get; set; }
    public SiloState? Silo { get; set; }
    public (double Missiles, double Capacity, bool Assembling, double Progress, double Radius)? Interceptor { get; set; }
    public ConstructionState? Construction { get; set; }
    public ConversionState? Conversion { get; set; }
    public ProductionState? Production { get; set; }

    /// <summary>Выбрано ли хоть что-то: юниты или здание.</summary>
    public bool Selected => Units.Count > 0 || Building is not null;
}

internal sealed record StoredState(List<Stack> Items, double Capacity, bool Store, bool Buffer, List<(string Resource, double Amount, double Of)>? Slots);

/// <summary>Резерв выбранных своих хранилищ: кому слать, резерв первого, наименьшая ёмкость.</summary>
internal sealed record ReserveState(uint[] Buildings, double Amount, double Capacity);

/// <summary>Заказ выбранных своих станций запроса: кому слать, заказ первой и что у неё есть, наименьшая ёмкость.</summary>
internal sealed record RequestState(uint[] Buildings, IReadOnlyDictionary<string, double> Wanted, IReadOnlyDictionary<string, double> Have, double Capacity);

internal sealed record AssemblyState(uint Plant, string Recipe, bool On, double Progress, double Have, List<Stack> Inputs, double Yield, double Seconds);

internal sealed record SwitchState(uint[] Buildings, bool On);

internal sealed record CargoState(List<Stack> Items, double Capacity, int Bound, int Busy);

/// <summary>Выбранные грузовики: кому слать, фильтр первого, режим работы; routing и serving — набор маршрута (null — не набирают).</summary>
internal sealed record HaulState(uint[] Units, IReadOnlyList<string> Filter, TruckJob Job, int? Routing = null, int? Serving = null, bool Carrying = false);

/// <summary>Тактика выбранных бойцов: кому слать, общая стойка (null — разные), сколько в патруле, выбирает ли игрок точку.</summary>
internal sealed record TacticsState(uint[] Units, string? Stance, int Patrolling, bool Picking = false);

internal sealed record SiteState(uint Entity, bool Started, double Progress, bool Demolish, bool Undo);

internal sealed record DemolishState(uint[] Buildings, double Refund);

/// <summary>Следующий уровень: кому слать, номер, множитель, цена за всех, чего не хватает (research, credits), тема.</summary>
internal sealed record LevelNext(uint[] Buildings, int Level, double Factor, double Cost, List<Stack> Materials, string? Lack, string Topic);

internal sealed record LevelState(int Level, int Max, string Gain, double Factor, LevelNext? Next, (uint[] Buildings, double Progress)? Raising);

internal sealed record LabState(int Labs, double Speed, string? Lack, string? Topic, double Progress);

internal sealed record SiloState(uint Entity, string Stage, double Progress, double Launch, double Cost, List<Stack> Materials, string Time, string? Lack, bool Aiming = false);

/// <summary>Кнопка здания у строителя: цена, по карману ли, энергия в работе и без дела, материалы.</summary>
internal sealed record BuildOption(string Building, double Cost, bool Affordable, double Power, double Idle, List<Stack> Materials);

/// <summary>Строитель среди выбранного: что закладывается, что тянется мышью, какие здания и инструменты открыты.</summary>
internal sealed record ConstructionState(string? Placing, (string Tool, double Cost, int Tiles)? Paving, List<BuildOption> Options, IReadOnlyList<string> Tools);

/// <summary>Превращение: MCV разворачивается (deploy) или главное здание сворачивается (pack).</summary>
internal sealed record ConversionState(string Kind, uint Entity, bool Possible, double? Progress, bool Choosing, bool Traveling, bool Cancel);

internal sealed record ProduceOption(string Unit, double Cost, bool Affordable, List<Stack> Materials, IReadOnlyList<string> Missing, string? Research, bool Capped);

internal sealed record ProductionState(uint Producer, uint Busiest, int Count, List<int> Queue, double Progress, bool Full, bool IsBuilding, bool Rally,
    List<ProduceOption> Options, (double Used, double Limit)? ArmyLimit);
