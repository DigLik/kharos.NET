using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;
using ConstructionHooks = Base.Host.Sections.Construction.Hooks;

namespace Base.Host.Sections.Trade;

/// <summary>
/// Раздел Trade (С2d). Торговля: космопорт (<see cref="Trading"/>: sell, buy, closeSale), корабли с подарками союзникам
/// (<see cref="Ferry"/>, приказ ferry) и передача юнитов (<see cref="Gift"/>, приказ gift). Системы trade и ferry
/// (<see cref="TradeFlights"/>, <see cref="FerryFlights"/>) — места в тике по <see cref="Order.Tick"/>; запросы trade.*.
/// <para>
/// Заявка продажи — компонент Trade: её товар заказывает раздел Logistics (грузовики везут из хранилищ в космопорт), по
/// трубам — раздел Pipes (<see cref="Trading.NeededBy"/>). Груз корабля с подарком лежит в памяти мода (<see cref="Holds"/>).
/// Стройке ставит крючок <c>RefundPurchase</c>: разобранный космопорт возвращает оплаченную закупку.
/// </para>
/// </summary>
internal static class TradeSection
{
    public static void Setup(ModBuilder mod)
    {
        Order.System(mod, "trade", new TradeFlights()).Query(Names.Trade.Name, Names.Owner.Name);
        Order.System(mod, "ferry", new FerryFlights()).Query(Names.Transfer.Name);
        Commands.Handle<SellOrder>(SellOrder.Name, (call, player, order) => Trading.Sell(call, player, order.Port, order.Resource, order.Amount));
        Commands.Handle<BuyOrder>(BuyOrder.Name, (call, player, order) => Trading.Buy(call, player, order.Port, order.Resource, order.Amount));
        Commands.Handle<CloseSaleOrder>(CloseSaleOrder.Name, (call, player, order) => Trading.CloseSale(call, player, order.Port));
        Commands.Handle<FerryOrder>(FerryOrder.Name, (call, player, order) =>
            order.Goods is not null && Ferry.Send(call, player, order.Port, order.To, order.Credits, order.Goods));
        Commands.Handle<GiftOrder>(GiftOrder.Name, (call, player, order) => Gift.HandOver(call, player, order.To, order.Units) > 0);
        ConstructionHooks.RefundPurchase = Trading.RefundPurchase;
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(TradeQueries.CanSell, ctx =>
        {
            var args = Args<PortArgs>(ctx);
            ctx.Reply(Trading.CanSell(Call.Of(ctx), args.Player, args.Port));
        });
        mod.Query(TradeQueries.CanFerry, ctx =>
        {
            var args = Args<PortArgs>(ctx);
            ctx.Reply(Ferry.CanFerry(Call.Of(ctx), args.Player, args.Port));
        });
        mod.Query(TradeQueries.Transfer, ctx => ctx.Reply(TransferOf(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
        mod.Query(TradeQueries.Delivered, ctx => ctx.Reply(Trading.Delivered(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
        mod.Query(TradeQueries.Needed, ctx => ctx.Reply(Trading.NeededBy(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
        mod.Query(TradeQueries.Flight, ctx =>
        {
            var args = Args<FlightArgs>(ctx);
            ctx.Reply(Trading.FlightTicks(Call.Of(ctx), args.Port, args.Seconds));
        });
        mod.Query(TradeQueries.BuyResearch, ctx =>
        {
            var args = Args<PortArgs>(ctx);
            ctx.Reply(Trading.BuyResearch(Call.Of(ctx), args.Player, args.Good) ?? "");
        });
        mod.Query(TradeQueries.Stock, ctx =>
        {
            var (items, capacity) = Trading.StockOf(Call.Of(ctx), Args<PlayerArgs>(ctx).Player);
            ctx.Reply(new StockInfo { Goods = [.. items.Keys], Amounts = [.. items.Values], Capacity = capacity });
        });
        mod.Query(TradeQueries.Giftable, ctx =>
        {
            var args = Args<GiftArgs>(ctx);
            ctx.Reply(new EntityList { Items = [.. Gift.Giftable(Call.Of(ctx), args.Player, args.Units)] });
        });
    }

    /// <summary>Корабль космопорта с подарком и его груз.</summary>
    private static TransferInfo TransferOf(Call call, uint port)
    {
        if (!call.Has(port, T.Transfer))
            return new TransferInfo();
        var goods = Holds.Read(call).Ships.GetValueOrDefault(port);
        return new TransferInfo
        {
            Present = true,
            To = (uint)call.Num(port, T.Transfer, T.TransferTo),
            X = call.Num(port, T.Transfer, T.TransferX),
            Y = call.Num(port, T.Transfer, T.TransferY),
            Credits = call.Num(port, T.Transfer, T.TransferCredits),
            Left = call.Num(port, T.Transfer, T.TransferLeft),
            Total = call.Num(port, T.Transfer, T.TransferTotal),
            Done = call.Flag(port, T.Transfer, T.TransferDone),
            Goods = goods is null ? [] : new Dictionary<string, double>(goods),
        };
    }

    private static TArgs Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TArgs>(QueryContext ctx) where TArgs : class, new() => ctx.Read<TArgs>() ?? new TArgs();
}
