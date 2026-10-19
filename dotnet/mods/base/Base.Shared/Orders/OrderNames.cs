namespace Base.Shared.Orders;

/// <summary>Имена приказов base: 48 команд TS, свои (stop, союзы) и правки редактора.</summary>
public static class OrderNames
{
    /// <summary>Приказы с целью по номеру — только изнутри (INTERNAL в commands.ts): от клиента их не берут.</summary>
    public static readonly string[] Internal = [AssistOrder.Name, HaulOrder.Name, PickupOrder.Name, SupplyOrder.Name, AttackOrder.Name];

    /// <summary>С Shift встают в очередь юнитов (QUEUED в orders.ts и протяжка).</summary>
    public static readonly string[] Queued =
    [
        PointOrder.Name, MoveOrder.Name, AttackOrder.Name, AssistOrder.Name, BuildOrder.Name, HaulOrder.Name, PickupOrder.Name,
        SupplyOrder.Name, PaveOrder.Name, PipesOrder.Name, WallsOrder.Name, WellsOrder.Name, UnpaveOrder.Name, DigOrder.Name,
    ];

    /// <summary>Команды TS по порядку объявления в commands.ts.</summary>
    public static readonly string[] Commands =
    [
        PointOrder.Name, MoveOrder.Name, RallyOrder.Name, DeployOrder.Name, CancelDeployOrder.Name, PackOrder.Name,
        ProduceOrder.Name, CancelProductionOrder.Name, BuildOrder.Name, AssistOrder.Name, HaulOrder.Name, PickupOrder.Name,
        SupplyOrder.Name, AttackOrder.Name, SellOrder.Name, BuyOrder.Name, CloseSaleOrder.Name, FerryOrder.Name, GiftOrder.Name,
        ScuttleOrder.Name, WorkOrder.Name, PingOrder.Name, CancelBuildOrder.Name, DemolishOrder.Name, UpgradeOrder.Name,
        CancelUpgradeOrder.Name, ResearchOrder.Name, DequeueOrder.Name, CancelResearchOrder.Name, WarheadOrder.Name,
        CancelWarheadOrder.Name, LaunchOrder.Name, AbortLaunchOrder.Name, RouteOrder.Name, ServeOrder.Name, FilterOrder.Name,
        CarryOrder.Name, ReserveOrder.Name, WantOrder.Name, SurrenderOrder.Name, StanceOrder.Name, PatrolOrder.Name,
        PaveOrder.Name, PipesOrder.Name, WallsOrder.Name, WellsOrder.Name, UnpaveOrder.Name, DigOrder.Name,
    ];

    /// <summary>Свои приказы .NET: stop и союзы (в TS — команды хоста ally и unally).</summary>
    public static readonly string[] Own = [StopOrder.Name, AllyOrder.Name, BreakAllyOrder.Name, ExitAllyOrder.Name];

    /// <summary>Правки редактора (EditOp).</summary>
    public static readonly string[] Edits =
    [
        EditPaintOrder.Name, EditBuildingOrder.Name, EditUnitOrder.Name, EditDepositOrder.Name, EditSetDepositOrder.Name,
        EditRemoveDepositOrder.Name, EditEraseOrder.Name, EditMoveOrder.Name, EditOwnerOrder.Name, EditHealthOrder.Name,
        EditFacingOrder.Name, EditTurretOrder.Name, EditStockOrder.Name, EditClearTasksOrder.Name, EditOrderOrder.Name,
        EditCreditsOrder.Name, EditCameraOrder.Name, EditAddPlayerOrder.Name, EditStartOrder.Name, EditPaveOrder.Name,
    ];
}
