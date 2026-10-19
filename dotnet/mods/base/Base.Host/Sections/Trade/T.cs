using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Trade;

/// <summary>
/// Поля схемы, нужные торговле сверх <see cref="S"/>: берутся привязкой систем раздела (Bind — после Mod.Load, до
/// первого тика, приказа и запроса).
/// </summary>
internal static class T
{
    public static ComponentRef Trade = null!, Transfer = null!, Doomed = null!;
    public static FieldRef TradeResource, TradeWanted, TradeLeft, TradeTotal, TradeBuy;
    public static FieldRef TransferTo, TransferX, TransferY, TransferCredits, TransferLeft, TransferTotal, TransferDone;
    public static FieldRef HaulerResource, HaulerFull;
    public static FieldRef ArmedTarget, ArmedChase, ArmedOrdered;
    public static FieldRef TacticsPatrol, TacticsLeg, TacticsAway, TacticsResupply;

    /// <summary>Берёт поля схемы; повторный вызов той же схемы ничего не меняет.</summary>
    public static void Load(Func<string, ComponentRef> component)
    {
        Trade = component(Names.Trade.Name);
        Transfer = component(Names.Transfer.Name);
        Doomed = component(Names.Doomed.Name);
        TradeResource = Trade[Names.Trade.Resource];
        TradeWanted = Trade[Names.Trade.Wanted];
        TradeLeft = Trade[Names.Trade.Left];
        TradeTotal = Trade[Names.Trade.Total];
        TradeBuy = Trade[Names.Trade.Buy];
        TransferTo = Transfer[Names.Transfer.To];
        TransferX = Transfer[Names.Transfer.X];
        TransferY = Transfer[Names.Transfer.Y];
        TransferCredits = Transfer[Names.Transfer.Credits];
        TransferLeft = Transfer[Names.Transfer.Left];
        TransferTotal = Transfer[Names.Transfer.Total];
        TransferDone = Transfer[Names.Transfer.Done];
        HaulerResource = S.Hauler[Names.Hauler.Resource];
        HaulerFull = S.Hauler[Names.Hauler.Full];
        ArmedTarget = S.Armed[Names.Armed.Target];
        ArmedChase = S.Armed[Names.Armed.Chase];
        ArmedOrdered = S.Armed[Names.Armed.Ordered];
        TacticsPatrol = S.Tactics[Names.Tactics.Patrol];
        TacticsLeg = S.Tactics[Names.Tactics.Leg];
        TacticsAway = S.Tactics[Names.Tactics.Away];
        TacticsResupply = S.Tactics[Names.Tactics.Resupply];
    }
}
