using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Поля схемы, нужные логистике сверх <see cref="S"/>: грузовик, ручной приказ, поручение строителя, отметки, заказ,
/// заявка космопорта, пачки труб. Берутся привязкой системы haul (<see cref="Load"/> — до первого тика и приказа).
/// </summary>
internal static class L
{
    public static ComponentRef Carry = null!, Unreachable = null!, Trade = null!, Batch = null!, Off = null!;

    public static FieldRef HaulerMine, HaulerFrom, HaulerTo, HaulerResource, HaulerAmount, HaulerFull, HaulerLoading, HaulerWaiting,
        HaulerMisses, HaulerStop, HaulerFilter, HaulerPickup, HaulerSupply, HaulerTrapped, HaulerRoute, HaulerServe;

    public static FieldRef CarryFrom, CarryTo, CarryResource, CarryLeft, CarryFull;
    public static FieldRef FetchFrom, FetchTo, FetchResource, FetchAmount, FetchFull, FetchMisses;
    public static FieldRef UnreachableUntil, BeamLinks, StockReserve, BuildsSite, SiteDemolish, SiteProgress, AssemblyOn, BodyRadius;
    public static FieldRef TradeResource, TradeWanted, TradeTotal, TradeBuy;
    public static FieldRef BatchTo, BatchResource, BatchAmount, BatchArrive;

    /// <summary>Столбцы заказа станции запроса по грузам (порядок <see cref="Goods.All"/>); нет столбца — null.</summary>
    public static FieldRef?[] Wanted = [];

    /// <summary>Берёт поля схемы (привязка системы раздела).</summary>
    public static void Load(Func<string, ComponentRef> component)
    {
        Carry = component(Names.Carry.Name);
        Unreachable = component(Names.Unreachable.Name);
        Trade = component(Names.Trade.Name);
        Batch = component(Names.Batch.Name);
        Off = component(Names.Off.Name);
        var h = S.Hauler;
        HaulerMine = h[Names.Hauler.Mine];
        HaulerFrom = h[Names.Hauler.From];
        HaulerTo = h[Names.Hauler.To];
        HaulerResource = h[Names.Hauler.Resource];
        HaulerAmount = h[Names.Hauler.Amount];
        HaulerFull = h[Names.Hauler.Full];
        HaulerLoading = h[Names.Hauler.Loading];
        HaulerWaiting = h[Names.Hauler.Waiting];
        HaulerMisses = h[Names.Hauler.Misses];
        HaulerStop = h[Names.Hauler.Stop];
        HaulerFilter = h[Names.Hauler.Filter];
        HaulerPickup = h[Names.Hauler.Pickup];
        HaulerSupply = h[Names.Hauler.Supply];
        HaulerTrapped = h[Names.Hauler.Trapped];
        HaulerRoute = h[Names.Hauler.Route];
        HaulerServe = h[Names.Hauler.Serve];
        CarryFrom = Carry[Names.Carry.From];
        CarryTo = Carry[Names.Carry.To];
        CarryResource = Carry[Names.Carry.Resource];
        CarryLeft = Carry[Names.Carry.Left];
        CarryFull = Carry[Names.Carry.Full];
        FetchFrom = S.Fetch[Names.Fetch.From];
        FetchTo = S.Fetch[Names.Fetch.To];
        FetchResource = S.Fetch[Names.Fetch.Resource];
        FetchAmount = S.Fetch[Names.Fetch.Amount];
        FetchFull = S.Fetch[Names.Fetch.Full];
        FetchMisses = S.Fetch[Names.Fetch.Misses];
        UnreachableUntil = Unreachable[Names.Unreachable.Until];
        BeamLinks = S.Beam[Names.Beam.Links];
        StockReserve = S.Stock[Names.Stock.Reserve];
        BuildsSite = S.Builds[Names.Builds.Site];
        SiteDemolish = S.Site[Names.Site.Demolish];
        SiteProgress = S.Site[Names.Site.Progress];
        AssemblyOn = S.Assembly[Names.Assembly.On];
        BodyRadius = S.Body[Names.Body.Radius];
        TradeResource = Trade[Names.Trade.Resource];
        TradeWanted = Trade[Names.Trade.Wanted];
        TradeTotal = Trade[Names.Trade.Total];
        TradeBuy = Trade[Names.Trade.Buy];
        BatchTo = Batch[Names.Batch.To];
        BatchResource = Batch[Names.Batch.Resource];
        BatchAmount = Batch[Names.Batch.Amount];
        BatchArrive = Batch[Names.Batch.Arrive];
        Wanted = [.. Goods.All.Select(good => S.Wanted.Find(good))];
        Cargo.Load();
    }
}
