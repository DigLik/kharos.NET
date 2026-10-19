using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Base.Shared.Orders;
using MemoryPack;

namespace Base.Host.Kernel;

// Книга приказов: каждый приказ base — имя, класс payload, раздел-владелец, как читать его юнитов и флаг Shift.
internal static partial class Commands
{
    /// <summary>Свойства приказа в диспетчере.</summary>
    [Flags]
    public enum Flags
    {
        None = 0,
        /// <summary>С целью по номеру — только изнутри (INTERNAL в commands.ts).</summary>
        Internal = 1,
        /// <summary>С Shift встаёт в очередь юнитов (QUEUED в orders.ts).</summary>
        Queued = 2,
        /// <summary>Укладка протяжкой (lay в commands.ts): очередь и занятых строителей решает сам обработчик.</summary>
        Lay = 4,
        /// <summary>Настройка, не приказ: очередь юнитов не трогает.</summary>
        Setting = 8,
        /// <summary>Правка редактора: только с правом редактора или администратора.</summary>
        Edit = 16,
    }

    /// <summary>Приказ в книге: payload, юниты, Shift и обработчик.</summary>
    private abstract class Entry(string name, string section, Flags flags)
    {
        public string Name { get; } = name;

        /// <summary>Пакет-владелец (С1, С2b…): кто поставит обработчик.</summary>
        public string Section { get; } = section;

        public Flags Flags { get; } = flags;

        public Queuer? Queuer { get; set; }

        public bool Has(Flags flag) => (Flags & flag) != 0;

        /// <summary>Payload из байтов; не разобрался — null.</summary>
        public abstract object? Read(ReadOnlySpan<byte> payload);

        public abstract byte[] Write(object order);

        public object Copy(object order) => Read(Write(order))!;

        /// <summary>Юниты приказа (units или builders); нет — пусто.</summary>
        public abstract uint[] Units(object order);

        public abstract void SetUnits(object order, uint[] units);

        /// <summary>С Shift ли приказ.</summary>
        public abstract bool Queue(object order);

        public abstract void SetQueue(object order, bool queue);

        /// <summary>Поставил ли раздел обработчик.</summary>
        public abstract bool Handled { get; }

        /// <summary>Обработчик раздела; нет — отказ «ждёт раздел».</summary>
        public abstract bool Run(Call call, uint player, object order);
    }

    private sealed class Entry<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string name, string section, Flags flags, Func<T, uint[]>? units, Action<T, uint[]>? setUnits, Func<T, bool>? queue, Action<T, bool>? setQueue)
        : Entry(name, section, flags) where T : class
    {
        public Handler<T>? Handler { get; set; }

        public override bool Handled => Handler is not null;

        public override object? Read(ReadOnlySpan<byte> payload)
        {
            T? order;
            try
            {
                order = MemoryPackSerializer.Deserialize<T>(payload);
            }
            catch (MemoryPackSerializationException)
            {
                return null;
            }
            return order is not null && Whole(order) ? order : null;
        }

        /// <summary>
        /// Поля, что у нового приказа не null (массивы, строки, вложенная кисть): MemoryPack пропускает в них null
        /// (длина −1), а обработчики на null не рассчитаны. Такой payload — кривой, его отвергают, как TS — кривой ввод.
        /// </summary>
        private static readonly PropertyInfo[] Required = RequiredOf();

        private static PropertyInfo[] RequiredOf()
        {
            var fresh = Activator.CreateInstance<T>();
            return [.. typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && !p.PropertyType.IsValueType && p.GetValue(fresh) is not null)];
        }

        private static bool Whole(T order)
        {
            foreach (var property in Required)
                switch (property.GetValue(order))
                {
                    case null:
                        return false;
                    // Массив строк: null-строка внутри — тоже кривой ввод.
                    case object?[] items when Array.IndexOf(items, null) >= 0:
                        return false;
                }
            return true;
        }

        public override byte[] Write(object order) => MemoryPackSerializer.Serialize((T)order);

        public override uint[] Units(object order) => units?.Invoke((T)order) ?? [];

        public override void SetUnits(object order, uint[] list) => setUnits?.Invoke((T)order, list);

        public override bool Queue(object order) => queue?.Invoke((T)order) ?? false;

        public override void SetQueue(object order, bool value) => setQueue?.Invoke((T)order, value);

        public override bool Run(Call call, uint player, object order)
        {
            if (Handler is { } handler)
                return handler(call, player, (T)order);
            call.Refuse(player, Name, "ждёт " + Section);
            return false;
        }
    }

    private static readonly Dictionary<string, Entry> Book = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> Names = [];

    /// <summary>Имя приказа по классу payload.</summary>
    public static string NameOf(object order) => Names.GetValueOrDefault(order.GetType()) ?? "";

    /// <summary>Пакет, чей обработчик у приказа (С1, С2b, Б1…); нет приказа — null.</summary>
    public static string? SectionOf(string name) => Book.GetValueOrDefault(name)?.Section;

    /// <summary>Есть ли у приказа обработчик (приказ точкой разбирает само ядро).</summary>
    public static bool Handled(string name) => Book.TryGetValue(name, out var entry) && (entry.Handled || name == PointOrder.Name);

    private static void Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string name, string section, Flags flags = Flags.None, Func<T, uint[]>? units = null, Action<T, uint[]>? setUnits = null,
        Func<T, bool>? queue = null, Action<T, bool>? setQueue = null) where T : class
    {
        Book[name] = new Entry<T>(name, section, flags, units, setUnits, queue, setQueue);
        Names[typeof(T)] = name;
    }

    static Commands()
    {
        const Flags Q = Flags.Queued, I = Flags.Internal | Flags.Queued, L = Flags.Lay | Flags.Queued, E = Flags.Edit;
        // Ядро (Б1).
        Add<PointOrder>(PointOrder.Name, "Б1", Q, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<MoveOrder>(MoveOrder.Name, "Б1", Q, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<StopOrder>(StopOrder.Name, "Б1", Flags.None, o => o.Units, (o, u) => o.Units = u);
        Add<RallyOrder>(RallyOrder.Name, "Б1");
        Add<DeployOrder>(DeployOrder.Name, "Б1");
        Add<CancelDeployOrder>(CancelDeployOrder.Name, "Б1");
        Add<PackOrder>(PackOrder.Name, "Б1");
        Add<ProduceOrder>(ProduceOrder.Name, "Б1");
        Add<CancelProductionOrder>(CancelProductionOrder.Name, "Б1");
        // Стройка (С1).
        Add<BuildOrder>(BuildOrder.Name, "С1", Q, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<AssistOrder>(AssistOrder.Name, "С1", I, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<CancelBuildOrder>(CancelBuildOrder.Name, "С1");
        Add<DemolishOrder>(DemolishOrder.Name, "С1", Flags.None, o => o.Builders, (o, u) => o.Builders = u);
        Add<PaveOrder>(PaveOrder.Name, "С1", L, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<UnpaveOrder>(UnpaveOrder.Name, "С1", L, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<WallsOrder>(WallsOrder.Name, "С1", L, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<DigOrder>(DigOrder.Name, "С1", L, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        // Логистика (С2b).
        Add<HaulOrder>(HaulOrder.Name, "С2b", I, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<PickupOrder>(PickupOrder.Name, "С2b", I, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<SupplyOrder>(SupplyOrder.Name, "С2b", I, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<RouteOrder>(RouteOrder.Name, "С2b", Flags.None, o => o.Units, (o, u) => o.Units = u);
        Add<ServeOrder>(ServeOrder.Name, "С2b", Flags.None, o => o.Units, (o, u) => o.Units = u);
        Add<FilterOrder>(FilterOrder.Name, "С2b", Flags.Setting, o => o.Units, (o, u) => o.Units = u);
        Add<CarryOrder>(CarryOrder.Name, "С2b", Flags.None, o => o.Units, (o, u) => o.Units = u);
        Add<ReserveOrder>(ReserveOrder.Name, "С2b");
        Add<WantOrder>(WantOrder.Name, "С2b");
        // Трубы (С2c).
        Add<PipesOrder>(PipesOrder.Name, "С2c", L, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        Add<WellsOrder>(WellsOrder.Name, "С2c", L, o => o.Builders, (o, u) => o.Builders = u, o => o.Queue, (o, q) => o.Queue = q);
        // Торговля (С2d).
        Add<SellOrder>(SellOrder.Name, "С2d");
        Add<BuyOrder>(BuyOrder.Name, "С2d");
        Add<CloseSaleOrder>(CloseSaleOrder.Name, "С2d");
        Add<FerryOrder>(FerryOrder.Name, "С2d");
        Add<GiftOrder>(GiftOrder.Name, "С2d", Flags.None, o => o.Units, (o, u) => o.Units = u);
        // Энергия (С3).
        Add<WorkOrder>(WorkOrder.Name, "С3");
        // Бой (С5а).
        Add<AttackOrder>(AttackOrder.Name, "С5а", I, o => o.Units, (o, u) => o.Units = u, o => o.Queue, (o, q) => o.Queue = q);
        // Исследования и уровни (С6).
        Add<ResearchOrder>(ResearchOrder.Name, "С6");
        Add<DequeueOrder>(DequeueOrder.Name, "С6");
        Add<CancelResearchOrder>(CancelResearchOrder.Name, "С6");
        Add<UpgradeOrder>(UpgradeOrder.Name, "С6", Flags.None, o => o.Builders, (o, u) => o.Builders = u);
        Add<CancelUpgradeOrder>(CancelUpgradeOrder.Name, "С6");
        // Ракеты (С7).
        Add<WarheadOrder>(WarheadOrder.Name, "С7");
        Add<CancelWarheadOrder>(CancelWarheadOrder.Name, "С7");
        Add<LaunchOrder>(LaunchOrder.Name, "С7");
        Add<AbortLaunchOrder>(AbortLaunchOrder.Name, "С7");
        // Тактика (С8).
        Add<StanceOrder>(StanceOrder.Name, "С8", Flags.Setting, o => o.Units, (o, u) => o.Units = u);
        Add<PatrolOrder>(PatrolOrder.Name, "С8", Flags.None, o => o.Units, (o, u) => o.Units = u);
        // Игроки (С9).
        Add<ScuttleOrder>(ScuttleOrder.Name, "С9", Flags.None, o => o.Units, (o, u) => o.Units = u);
        Add<PingOrder>(PingOrder.Name, "С9");
        Add<SurrenderOrder>(SurrenderOrder.Name, "С9");
        Add<AllyOrder>(AllyOrder.Name, "С9");
        Add<BreakAllyOrder>(BreakAllyOrder.Name, "С9");
        Add<ExitAllyOrder>(ExitAllyOrder.Name, "С9");
        // Правки редактора (Б1).
        Add<EditPaintOrder>(EditPaintOrder.Name, "Б1", E);
        Add<EditBuildingOrder>(EditBuildingOrder.Name, "Б1", E);
        Add<EditUnitOrder>(EditUnitOrder.Name, "Б1", E);
        Add<EditDepositOrder>(EditDepositOrder.Name, "Б1", E);
        Add<EditSetDepositOrder>(EditSetDepositOrder.Name, "Б1", E);
        Add<EditRemoveDepositOrder>(EditRemoveDepositOrder.Name, "Б1", E);
        Add<EditEraseOrder>(EditEraseOrder.Name, "Б1", E);
        Add<EditMoveOrder>(EditMoveOrder.Name, "Б1", E);
        Add<EditOwnerOrder>(EditOwnerOrder.Name, "Б1", E);
        Add<EditHealthOrder>(EditHealthOrder.Name, "Б1", E);
        Add<EditFacingOrder>(EditFacingOrder.Name, "Б1", E);
        Add<EditTurretOrder>(EditTurretOrder.Name, "Б1", E);
        Add<EditStockOrder>(EditStockOrder.Name, "Б1", E);
        Add<EditClearTasksOrder>(EditClearTasksOrder.Name, "Б1", E);
        Add<EditOrderOrder>(EditOrderOrder.Name, "Б1", E);
        Add<EditCreditsOrder>(EditCreditsOrder.Name, "Б1", E);
        Add<EditCameraOrder>(EditCameraOrder.Name, "Б1", E);
        Add<EditAddPlayerOrder>(EditAddPlayerOrder.Name, "Б1", E);
        Add<EditStartOrder>(EditStartOrder.Name, "Б1", E);
    }
}
