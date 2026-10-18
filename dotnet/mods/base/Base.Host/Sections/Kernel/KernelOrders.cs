using Base.Host.Kernel;
using Base.Shared.Orders;

namespace Base.Host.Sections.Kernel;

/// <summary>Приказы ядра (run в commands.ts): move, stop, rally, deploy, cancelDeploy, pack, produce, cancelProduction.</summary>
internal static class KernelOrders
{
    public static void Setup()
    {
        Commands.Handle<MoveOrder>(MoveOrder.Name, Move);
        Commands.Handle<StopOrder>(StopOrder.Name, Stop);
        Commands.Handle<RallyOrder>(RallyOrder.Name, (call, player, order) => Production.Rally(call, player, order.Building, order.X, order.Y));
        Commands.Handle<DeployOrder>(DeployOrder.Name, Deploy);
        Commands.Handle<CancelDeployOrder>(CancelDeployOrder.Name, (call, player, order) => Conversion.CancelDeploy(call, player, order.Unit));
        Commands.Handle<PackOrder>(PackOrder.Name, Pack);
        Commands.Handle<ProduceOrder>(ProduceOrder.Name, (call, player, order) => Production.Order(call, player, order.Producer, order.Unit));
        Commands.Handle<CancelProductionOrder>(CancelProductionOrder.Name, (call, player, order) => Production.Cancel(call, player, order.Producer));
    }

    /// <summary>Свои юниты идут к тайлу: приказ снимает строителя со стройки, грузовик — с маршрута, бойца — с цели.</summary>
    private static bool Move(Call call, uint player, MoveOrder order)
    {
        var units = Commands.OwnUnits(call, player, order.Units);
        if (units.Count == 0)
            return false;
        foreach (var unit in units)
            Commands.Free(call, unit);
        Moves.Group(call, units, order.X, order.Y);
        // Агрессивные идут походом (раздел Tactics).
        Commands.Went(call, player, units);
        return true;
    }

    /// <summary>Свои юниты бросают дела и путь и встают, где стоят (очередь забывает accept).</summary>
    private static bool Stop(Call call, uint player, StopOrder order)
    {
        var units = Commands.OwnUnits(call, player, order.Units);
        if (units.Count == 0)
            return false;
        foreach (var unit in units)
        {
            Commands.Free(call, unit);
            if (!call.Has(unit, S.Converting))
                call.Remove(unit, S.Path);
        }
        return true;
    }

    /// <summary>Развернуть MCV: с местом — ехать туда и развернуться там, без него — на месте.</summary>
    private static bool Deploy(Call call, uint player, DeployOrder order)
    {
        if (order.At)
            return Conversion.OrderDeploy(call, player, order.Unit, order.X, order.Y);
        if (!Conversion.CanDeploy(call, player, order.Unit))
            return false;
        Conversion.Start(call, order.Unit, Conversion.DeploySeconds);
        return true;
    }

    /// <summary>Свернуть своё главное здание в MCV.</summary>
    private static bool Pack(Call call, uint player, PackOrder order)
    {
        if (!Conversion.CanPack(call, player, order.Building))
            return false;
        Conversion.Start(call, order.Building, Conversion.PackSeconds);
        return true;
    }
}
