using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Rules;

namespace Base.Client;

/// <summary>Ячейка сетки команд (Slot в ui/commands.ts).</summary>
internal sealed class Slot
{
    public string Label { get; set; } = "";

    /// <summary>Картинка: портрет здания или юнита, значок груза или покрытия.</summary>
    public string? Building { get; set; }
    public string? Unit { get; set; }
    public string? Good { get; set; }
    public string? Pave { get; set; }

    /// <summary>Раздел, а не действие: сколько в нём открытых кнопок. Рисуется стопкой со счётчиком.</summary>
    public int? Group { get; set; }
    public double? Cost { get; set; }

    /// <summary>Cost — не трата, а доход: продажа, возврат за разбор.</summary>
    public bool Gain { get; set; }

    /// <summary>Кредитов на всю цену нет, но нажать можно: стройку оплачивают по ходу.</summary>
    public bool Short { get; set; }

    /// <summary>Новое: игрок ещё не наводил на эту кнопку стройки и не нажимал её, а у раздела — в нём есть такая (Seen).</summary>
    public bool Fresh { get; set; }

    /// <summary>Что отметить знакомым, когда на кнопку навели или её нажали: вид здания или инструмент протяжки.</summary>
    public string? Seen { get; set; }

    /// <summary>Сюда ведёт текущий первый шаг новичка (Steps): подсвечено ярче нового.</summary>
    public bool Hint { get; set; }
    public double? Power { get; set; }
    public double? Idle { get; set; }
    public List<Stack>? Materials { get; set; }
    public bool Disabled { get; set; }
    public bool Active { get; set; }

    /// <summary>Переключатель: лампа горит, пока включено (true), и гаснет красным выключенное.</summary>
    public bool? Lamp { get; set; }

    /// <summary>Пока курсор на ячейке, на карте видны границы зон строительства.</summary>
    public bool Zones { get; set; }
    public string? About { get; set; }
    public string? Title { get; set; }
    public Action Run { get; set; } = () => { };
}

/// <summary>Сделка, которую игрок набирает: груз и количество (у станции запроса — груз, чей заказ правят).</summary>
internal sealed record Deal(string Resource, double Amount);

/// <summary>Что сетка делает с игрой (Actions в ui/commands.ts): приказы хосту и режимы наведения.</summary>
internal sealed class GridActions
{
    /// <summary>Приказ хосту: объект приказа Base.Shared.Orders.</summary>
    public required Action<object> Send { get; init; }
    public Action<string?> Place { get; init; } = _ => { };
    public Action<string?> Pave { get; init; } = _ => { };
    public Action<bool> Route { get; init; } = _ => { };
    public Action<bool> Serve { get; init; } = _ => { };
    public Action<string?> Carry { get; init; } = _ => { };
    public Action<bool> Patrol { get; init; } = _ => { };
    public Action<uint?> Aim { get; init; } = _ => { };
    public Action<uint?> Deploy { get; init; } = _ => { };
    public Action<string> Open { get; init; } = _ => { };
    public Action<Deal?> Pick { get; init; } = _ => { };

    /// <summary>Набираемый подарок союзнику кораблём (К3б, Panels/Trade).</summary>
    public Action<FerryDraft?> Draft { get; init; } = _ => { };
    public Action Tech { get; init; } = () => { };

    /// <summary>Текущий первый шаг новичка (<c>c.Shared["steps.current"]</c>); null — подсказывать нечего.</summary>
    public StepSpec? Step { get; init; }

    /// <summary>Знакома ли игроку кнопка (Seen.Is); по умолчанию — знакома, подсветки «нового» нет.</summary>
    public Func<string, bool> IsSeen { get; init; } = _ => true;

    /// <summary>Отметить кнопку знакомой (Seen.Mark).</summary>
    public Action<string> MarkSeen { get; init; } = _ => { };
}

/// <summary>
/// Сетка команд 4 × 3 — перенос commandsOf из ui/commands.ts: какие кнопки у выбранного на странице page. Чистая
/// функция состояния: приказы уходят через <see cref="GridActions"/>, тексты — через <see cref="Tr"/>. Страницы торговли
/// и подарка кораблём (космопорт) — К3б.
/// </summary>
internal static class Commands
{
    /// <summary>Клавиши ячеек по порядку (GRID_KEYS): три ряда по четыре, как на клавиатуре, справа от WASD.</summary>
    public static readonly string[] Keys = ["t", "y", "u", "i", "g", "h", "j", "k", "b", "n", "m", ","];

    /// <summary>Подпись клавиши на кнопке (keyLabel).</summary>
    public static string KeyLabel(int index) => Keys[index].ToUpperInvariant();

    public const int Size = 12;

    // Постоянные места: превращение, отмена заказа, разбор, назад — так рука привыкает к ним.
    private const int Convert = 8, Cancel = 9, Demolish = 10, Upgrade = 7, Back = 11, Switch = Back, Gift = Back, Scuttle = Cancel;

    /// <summary>Сколько ячеек сверху отдаётся под списки: заказы, здания, рецепты.</summary>
    private const int List = 8;

    /// <summary>Разделы строителя: в какой странице какое здание (остальное — хозяйство).</summary>
    private static readonly Dictionary<string, string[]> Sections = new()
    {
        ["storage"] = ["metalYard", "siliconStore", "fuelTank", "khariteVault", "lithiumStore", "uraniumCask", "blockYard", "ammoBunker", "partsLocker", "rodRack", "requestStation"],
        ["industry"] = ["smelter", "siliconWorks", "distillery", "enricher", "evaporator", "uraniumMill"],
        ["production"] = ["blockPlant", "ammoPlant", "partsPlant", "rodPlant"],
        ["military"] = ["barracks", "factory", "airfield", "repairStation", "supplyDepot", "silo"],
        ["defense"] = ["wall", "gate", "turret", "rocketTurret", "cannonTurret", "laserTurret", "arcTurret", "shieldGenerator", "interceptor", "radar"],
    };

    /// <summary>Стойки по порядку кнопок.</summary>
    public static readonly string[] Stances = ["aggressive", "defensive", "hold", "passive"];

    /// <summary>Дальше скольких тайлов от своего места боец в обороне не гонится (LEASH).</summary>
    public const double Leash = 8;

    /// <summary>Цены землеройных работ (PEAK_COST, CLIFF_COST).</summary>
    public const double PeakCost = 40, CliffCost = 25;

    /// <summary>Шаги количества в заказе станции и резерве.</summary>
    private static readonly int[] Steps = [10, 50];

    public static string SectionOf(string building)
    {
        foreach (var (page, list) in Sections)
            if (Array.IndexOf(list, building) >= 0)
                return page;
        return "economy";
    }

    /// <summary>Описание здания для карточки: что делает и какие здания открывает.</summary>
    private static string AboutOf(BaseTables tables, string building, Tr t)
    {
        var opens = tables.Buildings.Where(pair => Array.IndexOf(pair.Value.Requires, building) >= 0).Select(pair => t("buildings." + pair.Key)).ToList();
        var info = t("buildingInfo." + building);
        return opens.Count > 0 ? t("grid.unlocks", "info", info, "list", string.Join(", ", opens)) : info;
    }

    /// <summary>Инструменты раздела «Покрытие» и «Логистика» с ценой.</summary>
    private static (string Tool, double Cost)[] PaveTools => [("foundation", PaveRules.FoundationCost), ("road", PaveRules.RoadCost), ("peak", PeakCost), ("cliff", CliffCost)];

    private static (string Tool, double Cost)[] PipeTools(BaseTables tables) =>
        [("pipe", tables.Buildings.GetValueOrDefault("pipe")?.Cost ?? 0), ("well", tables.Buildings.GetValueOrDefault("well")?.Cost ?? 0)];

    /// <summary>Готовое: ресурсы и изделия (WARES).</summary>
    public static IReadOnlyList<string> Wares(BaseTables tables) => [.. tables.Resources.Keys, .. tables.Products.Keys];

    /// <summary>Число, как его пишет JS String(): целое без дроби, иначе до 14 значащих цифр.</summary>
    public static string N(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("G14", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Сетка команд для выбранного: <see cref="Size"/> ячеек, пустые — null.</summary>
    public static Slot?[] Of(HudState state, string page, GridActions actions, BaseTables tables, Tr t, Deal? deal = null, FerryDraft? ferry = null)
    {
        var slots = new Slot?[Size];
        void Fill(IEnumerable<Slot> items)
        {
            var i = 0;
            foreach (var slot in items.Take(List))
                slots[i++] = slot;
        }
        var credits = state.Credits;
        var send = actions.Send;
        // Куда ведёт текущий первый шаг: его кнопка подсвечена, пока шаг не пройден.
        var hinted = actions.Step;

        if (state.Construction is { } construction)
        {
            Slot BuildingSlot(BuildOption option) => new()
            {
                Label = t("buildings." + option.Building),
                Building = option.Building,
                Cost = option.Cost,
                Power = option.Power,
                Idle = option.Idle == option.Power ? null : option.Idle,
                Materials = option.Materials,
                Active = construction.Placing == option.Building,
                Zones = true,
                About = AboutOf(tables, option.Building, t),
                Short = !option.Affordable,
                Fresh = !actions.IsSeen(option.Building),
                Seen = option.Building,
                Hint = hinted?.Building == option.Building,
                Title = option.Affordable ? null : t("grid.shortCredits"),
                Run = () =>
                {
                    actions.MarkSeen(option.Building);
                    actions.Pave(null);
                    actions.Place(construction.Placing == option.Building ? null : option.Building);
                },
            };
            // Стену и ворота тянут мышью линией, как трубу: их кнопка включает инструмент протяжки.
            Slot WallSlot(BuildOption option)
            {
                var tool = option.Building == "gate" ? "gate" : "wall";
                var active = construction.Paving?.Tool == tool;
                var slot = BuildingSlot(option);
                slot.Cost = active && construction.Paving!.Value.Tiles > 1 ? construction.Paving.Value.Cost : option.Cost;
                slot.Active = active;
                slot.Title = option.Affordable ? t("grid.wallTip") : $"{t("grid.wallTip")}. {t("grid.shortCredits")}";
                slot.Run = () =>
                {
                    actions.MarkSeen(option.Building);
                    actions.Place(null);
                    actions.Pave(active ? null : tool);
                };
                return slot;
            }
            Slot BackSlot(string title) => new()
            {
                Label = t("common.back"),
                Title = t(title),
                Run = () =>
                {
                    actions.Pave(null);
                    actions.Place(null);
                    actions.Open("root");
                },
            };
            var pipeTools = PipeTools(tables);
            if (page == "root")
            {
                // Раздел подсвечен, пока в нём есть кнопка, которую игрок ещё не нажимал.
                bool Fresh(string name) =>
                    construction.Options.Any(option => SectionOf(option.Building) == name && !actions.IsSeen(option.Building))
                    || (name is "paving" or "logistics" && (name == "paving" ? PaveTools : pipeTools).Any(tool => construction.Tools.Contains(tool.Tool) && !actions.IsSeen(tool.Tool)));
                Slot Section(string name, string? building = null, string? pave = null, int group = 0) => new()
                {
                    Label = t("grid.sections." + name),
                    Title = t("grid.sectionTips." + name),
                    Fresh = Fresh(name),
                    Hint = hinted?.Building is { } wanted && SectionOf(wanted) == name && construction.Options.Any(option => option.Building == wanted),
                    Building = building,
                    Pave = pave,
                    Group = group,
                    Run = () => actions.Open(name),
                };
                int Count(string name) => construction.Options.Count(option => SectionOf(option.Building) == name);
                // У каждого раздела своё место: закрытый оставляет пустую ячейку, кнопки не сдвигаются.
                (string Page, string Building)[] sections = [("economy", "generator"), ("storage", "metalYard"), ("industry", "smelter"), ("production", "blockPlant"), ("military", "factory"), ("defense", "wall")];
                for (var i = 0; i < sections.Length; i++)
                    slots[i] = Count(sections[i].Page) is var count and > 0 ? Section(sections[i].Page, sections[i].Building, group: count) : null;
                int Known((string Tool, double Cost)[] tools) => tools.Count(tool => construction.Tools.Contains(tool.Tool));
                slots[sections.Length] = Known(PaveTools) is var paves and > 0 ? Section("paving", pave: "road", group: paves) : null;
                var logistics = Known(pipeTools) + Count("logistics");
                slots[sections.Length + 1] = logistics > 0 ? Section("logistics", building: "pipe", group: logistics) : null;
                var removing = construction.Paving?.Tool == "remove";
                slots[Demolish] = new Slot { Label = t("grid.remove"), Pave = "remove", Active = removing, Title = t("grid.removeTip"), Run = () => actions.Pave(removing ? null : "remove") };
            }
            else if (page is "paving" or "logistics")
            {
                var tools = page == "paving" ? PaveTools : pipeTools;
                for (var i = 0; i < tools.Length; i++)
                {
                    var (tool, cost) = tools[i];
                    // Неисследованный инструмент — пустая ячейка: остальные не сдвигаются.
                    if (!construction.Tools.Contains(tool))
                        continue;
                    var active = construction.Paving?.Tool == tool;
                    var piping = tool is "pipe" or "well";
                    slots[i] = new Slot
                    {
                        Label = t("grid.tools." + tool),
                        Building = piping ? tool : null,
                        Zones = piping,
                        Pave = piping ? null : tool,
                        Cost = active && construction.Paving!.Value.Tiles > 1 ? construction.Paving.Value.Cost : cost,
                        Active = active,
                        // Трубы и колодцы — стройки, их оплачивают по ходу; покрытие и землеройные работы — сразу.
                        Short = piping && credits < cost,
                        Disabled = !piping && credits < cost,
                        Fresh = !actions.IsSeen(tool),
                        Seen = tool,
                        Title = t("grid.toolTips." + tool, "bridge", N(PaveRules.BridgeCost), "reach", N(BuildRules.UndergroundReach), "cost", N(cost)),
                        Run = () =>
                        {
                            actions.MarkSeen(tool);
                            actions.Place(null);
                            actions.Pave(active ? null : tool);
                        },
                    };
                }
                if (page == "logistics")
                {
                    var at = pipeTools.Length;
                    foreach (var option in construction.Options.Where(option => SectionOf(option.Building) == page))
                        if (at < Size)
                            slots[at++] = BuildingSlot(option);
                }
                slots[Back] = BackSlot("grid.backFromPaving");
            }
            else
            {
                var i = 0;
                foreach (var option in construction.Options.Where(option => SectionOf(option.Building) == page).Take(Back))
                    slots[i++] = option.Building is "wall" or "gate" ? WallSlot(option) : BuildingSlot(option);
                slots[Back] = BackSlot("grid.backFromPlacing");
            }
        }

        if (state.Production is { } production && page == "root")
        {
            Fill(production.Options.Select(option => new Slot
            {
                Label = t("units." + option.Unit),
                Unit = option.Unit,
                Cost = option.Cost,
                Materials = option.Materials,
                Disabled = !option.Affordable || production.Full || option.Missing.Count > 0 || option.Research is not null || option.Capped,
                Hint = hinted?.Unit == option.Unit,
                Title = option.Missing.Count > 0 ? t("grid.needs", "list", string.Join(", ", option.Missing.Select(type => t("buildings." + type))))
                    : option.Research is { } research ? t("grid.needsResearch", "name", t("research." + research))
                    : production.Full ? t("grid.queueFull")
                    : option.Capped && production.ArmyLimit is { } army ? t("grid.armyLimit", "used", N(army.Used), "limit", N(army.Limit), "supply", N(tables.Units.GetValueOrDefault(option.Unit)?.Supply ?? 0))
                    : option.Affordable ? null : t("grid.noCredits"),
                Run = () => send(new ProduceOrder { Producer = production.Producer, Unit = option.Unit }),
            }));
            if (production.Queue.Count > 0)
                slots[state.Trade is not null ? Back : Cancel] = new Slot
                {
                    Label = t("grid.cancelOrder"),
                    Title = t("grid.cancelOrderTip"),
                    Run = () => send(new CancelProductionOrder { Producer = production.Busiest }),
                };
        }

        if (state.Conversion is { } conversion)
        {
            var mcv = conversion.Kind == "deploy" ? conversion.Entity : (uint?)null;
            if (conversion.Progress is null)
                slots[Convert] = new Slot
                {
                    Label = conversion.Kind == "deploy" ? t("grid.deploy") : t("grid.pack"),
                    Building = BuildRules.Core,
                    About = conversion.Kind == "deploy" ? t("buildingInfo." + BuildRules.Core) : null,
                    Disabled = !conversion.Possible,
                    Active = conversion.Choosing,
                    Hint = mcv is not null && hinted?.Deploy == true && !conversion.Traveling,
                    Title = mcv is not null ? t("grid.deployTip") : null,
                    Run = () =>
                    {
                        if (mcv is { } unit)
                            actions.Deploy(conversion.Choosing ? null : unit);
                        else
                            send(new PackOrder { Building = conversion.Entity });
                    },
                };
            if ((conversion.Progress is not null || conversion.Traveling) && conversion.Cancel)
                slots[Cancel] = new Slot { Label = t("grid.cancel"), Run = () => send(new CancelDeployOrder { Unit = conversion.Entity }) };
        }

        if (state.Tactics is { } tactics && state.Construction is null && page == "root")
        {
            for (var i = 0; i < Stances.Length; i++)
            {
                var stance = Stances[i];
                slots[i] = new Slot
                {
                    Label = t("grid.stances." + stance),
                    Active = tactics.Stance == stance,
                    Title = t("grid.stanceTips." + stance, "leash", N(Leash)),
                    Run = () => send(new StanceOrder { Units = tactics.Units, Stance = stance }),
                };
            }
            slots[4] = new Slot
            {
                Label = t("grid.patrol"),
                Active = tactics.Picking || tactics.Patrolling > 0,
                Title = t("grid.patrolTip"),
                Run = () => actions.Patrol(!tactics.Picking),
            };
        }

        if (state.Haul is { } haul && state.Construction is null)
        {
            var units = haul.Units;
            var filter = haul.Filter;
            if (page == "root")
            {
                // Верхний ряд — режим работы: грузовик работает в одном, он и подсвечен. «Сам» снимает остальные.
                slots[0] = new Slot { Label = t("grid.truckAuto"), Active = haul.Job.Kind == "auto", Title = t("grid.truckAutoTip"), Run = () => send(new ServeOrder { Units = units, Buildings = [] }) };
                slots[1] = new Slot { Label = t("grid.carry"), Active = haul.Job.Kind == "carry" || haul.Carrying, Title = t("grid.carryTip"), Run = () => actions.Carry(haul.Carrying ? null : "source") };
                slots[2] = new Slot
                {
                    Label = haul.Routing is { } stops ? t("grid.routeStops", "stops", N(stops)) : t("grid.route"),
                    Active = haul.Job.Kind == "route" || haul.Routing is not null,
                    Title = t("grid.routeTip"),
                    Run = () => actions.Route(haul.Routing is null),
                };
                slots[3] = new Slot
                {
                    Label = haul.Serving is { } count ? t("grid.serveCount", "count", N(count)) : t("grid.serve"),
                    Active = haul.Job.Kind == "serve" || haul.Serving is not null,
                    Title = t("grid.serveTip"),
                    Run = () => actions.Serve(haul.Serving is null),
                };
                slots[4] = new Slot
                {
                    Label = filter.Count > 0 ? t("grid.filterCount", "count", N(filter.Count)) : t("grid.filter"),
                    Active = filter.Count > 0,
                    Title = t("grid.filterTip"),
                    Run = () => actions.Open("filter"),
                };
                if (filter.Count > 0)
                    slots[5] = new Slot { Label = t("grid.all"), Title = t("grid.allTip"), Run = () => send(new FilterOrder { Units = units, Goods = [] }) };
            }
            else if (page == "filter")
            {
                // Руда в фильтре — вся сразу: по видам её различает месторождение, а не игрок.
                bool Has(IEnumerable<string> goods) => goods.All(filter.Contains);
                void Toggle(IReadOnlyCollection<string> goods)
                {
                    var next = Has(goods) ? filter.Where(good => !goods.Contains(good)).ToArray() : [.. filter, .. goods.Where(good => !filter.Contains(good))];
                    send(new FilterOrder { Units = units, Goods = next });
                }
                var wares = Wares(tables);
                for (var i = 0; i < wares.Count && i < Size; i++)
                {
                    var ware = wares[i];
                    slots[i] = new Slot { Label = t("goods." + ware), Good = ware, Active = Has([ware]), Title = t("grid.carryOrNot"), Run = () => Toggle([ware]) };
                }
                var ores = tables.Ores.Keys.ToArray();
                if (wares.Count < Size && ores.Length > 0)
                    slots[wares.Count] = new Slot { Label = t("grid.ore"), Good = ores[0], Active = Has(ores), Title = t("grid.oreTip"), Run = () => Toggle(ores) };
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Open("root") };
            }
        }

        if (state.Reserve is { } reserve && state.Construction is null && state.Haul is null)
        {
            var (buildings, amount, capacity) = (reserve.Buildings, reserve.Amount, reserve.Capacity);
            void Set(double next) => send(new ReserveOrder { Buildings = buildings, Amount = Math.Max(0, Math.Min(capacity, next)) });
            if (page == "root")
                slots[0] = new Slot
                {
                    Label = amount > 0 ? t("grid.reserveCount", "amount", N(amount)) : t("grid.reserve"),
                    Active = amount > 0,
                    Title = t("grid.reserveTip"),
                    Run = () => actions.Open("reserve"),
                };
            else if (page == "reserve")
            {
                // Верхний ряд — прибавить, второй — убавить: по 10 и по 50.
                for (var i = 0; i < Steps.Length; i++)
                {
                    var step = Steps[i];
                    slots[i] = new Slot { Label = $"+{step}", Disabled = amount >= capacity, Title = t("grid.reserveCount", "amount", N(amount)), Run = () => Set(amount + step) };
                    slots[i + 4] = new Slot { Label = $"−{step}", Disabled = amount <= 0, Title = t("grid.reserveCount", "amount", N(amount)), Run = () => Set(amount - step) };
                }
                slots[2] = new Slot { Label = t("grid.reserveAll"), Active = amount >= capacity, Title = t("grid.reserveAllTip"), Run = () => Set(capacity) };
                slots[Cancel] = new Slot { Label = t("grid.reserveNone"), Active = amount <= 0, Title = t("grid.reserveNoneTip"), Run = () => Set(0) };
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Open("root") };
            }
        }

        if (state.Request is { } request && state.Construction is null && state.Haul is null)
        {
            var (buildings, wanted, capacity) = (request.Buildings, request.Wanted, request.Capacity);
            var wares = Wares(tables);
            if (page == "root")
            {
                var count = wanted.Count(pair => pair.Value > 0);
                slots[0] = new Slot
                {
                    Label = count > 0 ? t("grid.requestCount", "count", N(count)) : t("grid.request"),
                    Active = count > 0,
                    Title = t("grid.requestTip"),
                    Run = () => actions.Open("request"),
                };
            }
            else if (page == "request" && deal is null)
            {
                // Сначала — какой груз: ячейка на каждый, с заказанным количеством. Количество — на следующем шаге.
                for (var i = 0; i < wares.Count && i < Size; i++)
                {
                    var ware = wares[i];
                    var amount = wanted.GetValueOrDefault(ware);
                    slots[i] = new Slot
                    {
                        Label = amount > 0 ? $"{t("goods." + ware)} ×{N(amount)}" : t("goods." + ware),
                        Good = ware,
                        Active = amount > 0,
                        Title = t("grid.requestGoodTip"),
                        Run = () => actions.Pick(new Deal(ware, amount)),
                    };
                }
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Open("root") };
            }
            else if (page == "request" && deal is not null)
            {
                // Верхний ряд — прибавить, второй — убавить; «Всё» — сколько осталось места на складе за вычетом других заказов.
                var resource = deal.Resource;
                var amount = wanted.GetValueOrDefault(resource);
                var others = wares.Where(ware => ware != resource).Sum(ware => wanted.GetValueOrDefault(ware));
                var max = Math.Max(0, capacity - others);
                void Set(double next) => send(new WantOrder { Buildings = buildings, Resource = resource, Amount = Math.Max(0, Math.Min(max, next)) });
                var title = t("grid.requestAmount", "good", t("goods." + resource), "amount", N(amount));
                for (var i = 0; i < Steps.Length; i++)
                {
                    var step = Steps[i];
                    slots[i] = new Slot { Label = $"+{step}", Good = resource, Disabled = amount >= max, Title = title, Run = () => Set(amount + step) };
                    slots[i + 4] = new Slot { Label = $"−{step}", Good = resource, Disabled = amount <= 0, Title = title, Run = () => Set(amount - step) };
                }
                slots[2] = new Slot { Label = t("grid.all"), Active = amount >= max, Title = $"×{N(max)}", Run = () => Set(max) };
                slots[Cancel] = new Slot { Label = t("grid.requestNone"), Active = amount <= 0, Title = t("grid.requestNoneTip"), Run = () => Set(0) };
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Pick(null) };
            }
        }

        if (state.Gift is { } gift && state.Construction is null)
        {
            // Отдать юнитов союзнику: они перейдут под его управление там же, где стоят.
            var allies = state.Players.Where(row => !row.Own && row.Ally).ToList();
            if (page == "root")
                slots[Gift] = new Slot
                {
                    Label = t("grid.gift"),
                    Disabled = allies.Count < 1,
                    Title = allies.Count > 0 ? t("grid.giftTip") : t("grid.noAllies"),
                    Run = () => actions.Open("gift"),
                };
            else if (page == "gift")
            {
                Fill(allies.Select(row => new Slot
                {
                    Label = row.Name,
                    Title = t("grid.giftWho"),
                    Run = () =>
                    {
                        send(new GiftOrder { Units = gift, To = row.Player });
                        actions.Open("root");
                    },
                }));
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Open("root") };
            }
        }

        if (state.Gift is { } doomed)
        {
            // Уничтожить выбранных: сначала кнопка, потом подтверждение на своей странице — и на другой клавише.
            if (page == "root" && slots[Scuttle] is null)
                slots[Scuttle] = new Slot { Label = t("grid.scuttle"), Title = t("grid.scuttleTip"), Run = () => actions.Open("scuttle") };
            else if (page == "scuttle")
            {
                slots[0] = new Slot
                {
                    Label = t("grid.scuttleConfirm", "count", N(doomed.Length)),
                    Title = t("grid.scuttleConfirmTip"),
                    Run = () =>
                    {
                        send(new ScuttleOrder { Units = doomed });
                        actions.Open("root");
                    },
                };
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Open("root") };
            }
        }

        if (state.Assembly is { } assembly)
            slots[0] = new Slot
            {
                Label = assembly.On ? t("common.turnOff") : t("common.turnOn"),
                Good = assembly.Recipe,
                Lamp = assembly.On,
                Title = assembly.On ? t("grid.plantOffTip") : t("grid.plantOnTip"),
                Run = () => send(new WorkOrder { Building = assembly.Plant, On = !assembly.On }),
            };

        if (state.Switchable is { } switchable && page == "root" && slots[Switch] is null)
            slots[Switch] = new Slot
            {
                Label = switchable.On ? t("common.turnOff") : t("common.turnOn"),
                Lamp = switchable.On,
                Title = switchable.On ? t("grid.switchOffTip") : t("grid.switchOnTip"),
                Run = () =>
                {
                    foreach (var building in switchable.Buildings)
                        send(new WorkOrder { Building = building, On = !switchable.On });
                },
            };

        if (state.Trade is not null)
            TradePages.Fill(state, page, slots, actions, t, deal, ferry);

        if (state.Level is { } level && page == "root" && state.Construction is null)
        {
            if (level.Next is { } next)
                slots[Upgrade] = new Slot
                {
                    Label = t("grid.upgrade", "level", N(next.Level)),
                    Building = state.Building,
                    Cost = next.Cost,
                    Materials = next.Materials,
                    Disabled = next.Lack is not null,
                    Title = next.Lack == "research" ? t("grid.needsResearch", "name", t("research." + next.Topic))
                        : next.Lack is not null ? t(next.Lack == "credits" ? "grid.noCredits" : "grid.upgradeNoMaterials")
                        : t("grid.upgradeTip", "gain", t("levelGains." + level.Gain), "factor", N(next.Factor)),
                    Run = () =>
                    {
                        foreach (var building in next.Buildings)
                            send(new UpgradeOrder { Building = building, Builders = [] });
                    },
                };
            else if (level.Raising is { } raising)
                slots[Upgrade] = new Slot
                {
                    Label = t("grid.cancel"),
                    Title = t("grid.cancelUpgradeTip"),
                    Run = () =>
                    {
                        foreach (var building in raising.Buildings)
                            send(new CancelUpgradeOrder { Building = building });
                    },
                };
        }

        if (state.Research is { } labs && page == "root" && state.Construction is null)
        {
            slots[0] = new Slot { Label = t("grid.techTree"), Title = t("grid.techTreeTip"), About = t("buildingInfo.lab"), Run = actions.Tech };
            if (labs.Topic is not null)
                slots[Cancel] = new Slot { Label = t("grid.cancel"), Title = t("grid.cancelResearchTip"), Run = () => send(new CancelResearchOrder()) };
        }

        if (state.Silo is { } silo && page == "root" && state.Construction is null)
        {
            var empty = silo.Stage == "empty";
            slots[0] = new Slot
            {
                Label = t("grid.warhead"),
                Good = "rods",
                Cost = empty ? silo.Cost : null,
                Materials = empty ? silo.Materials : null,
                About = t("grid.warheadAbout"),
                Active = !empty,
                Disabled = silo.Lack is not null,
                Title = !empty ? t("grid.warheadBusy") : silo.Lack == "credits" ? t("grid.noCredits") : silo.Lack == "materials" ? t("grid.warheadNoMaterials") : t("grid.warheadTip", "time", silo.Time),
                Run = () => send(new WarheadOrder { Silo = silo.Entity }),
            };
            slots[1] = new Slot
            {
                Label = t("grid.launch"),
                About = t("grid.launchAbout"),
                Active = silo.Aiming,
                Disabled = silo.Stage is not ("ready" or "launching"),
                Title = silo.Stage == "launching" ? t("grid.launchRetarget") : silo.Stage == "ready" ? t("grid.launchTip", "seconds", N(silo.Launch)) : t("grid.launchNoWarhead"),
                Run = () => actions.Aim(silo.Aiming ? null : silo.Entity),
            };
            if (silo.Stage == "assembling")
                slots[Cancel] = new Slot { Label = t("grid.cancel"), Title = t("grid.cancelWarheadTip"), Run = () => send(new CancelWarheadOrder { Silo = silo.Entity }) };
            if (silo.Stage == "launching")
                slots[Cancel] = new Slot { Label = t("grid.abortLaunch"), Title = t("grid.abortLaunchTip"), Run = () => send(new AbortLaunchOrder { Silo = silo.Entity }) };
        }

        // Отменённую стройку уже разбирают: передумать нельзя, кнопки нет.
        if (state.Site is { Undo: false } site)
            slots[Demolish] = new Slot
            {
                Label = site.Demolish ? t("grid.cancelDemolish") : t("grid.cancelBuild"),
                Run = () => send(new CancelBuildOrder { Site = site.Entity }),
            };
        if (state.Demolish is { } demolish && page == "root")
            slots[Demolish] = new Slot
            {
                Label = t("grid.remove"),
                Cost = demolish.Refund,
                Gain = true,
                Title = t("grid.demolishTip"),
                Run = () =>
                {
                    foreach (var building in demolish.Buildings)
                        send(new DemolishOrder { Building = building, Builders = [] });
                },
            };
        return slots;
    }
}
