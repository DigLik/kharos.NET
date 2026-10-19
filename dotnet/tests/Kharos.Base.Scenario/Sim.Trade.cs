using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Signals;

namespace Kharos.Base.Scenario;

// Раздел Trade (С2d): космопорт, корабли с подарками, передача юнитов. Приказы идут «сразу», как прямые вызовы sell, buy,
// sendFerry и handOver в тестах TS; союзы — приказами ally раздела игроков (Sim.Players.cs).
public sealed partial class Sim
{
    /// <summary>
    /// Приказ игрока сразу, без тика (<see cref="OrderNow{T}"/>); возвращает, принят ли — как булево значение прямого вызова
    /// в TS. Отказ приказу приходит сигналом refused.
    /// </summary>
    public bool Now<T>(uint player, T order) where T : class
    {
        var before = Signals.Count;
        OrderNow(player, order);
        return !Signals.Skip(before).Any(signal => signal.Name == RefusedSignal.Name);
    }

    /// <summary>
    /// Союз двух игроков (offerAlliance в обе стороны): настоящие приказы ally раздела игроков (<see cref="Offer"/>); у обоих
    /// должен быть счёт (<see cref="SetCredits"/>). a — одиночка или глава: он предлагает, b принимает.
    /// </summary>
    public void Ally(uint a, uint b)
    {
        Offer(a, b);
        Offer(b, a);
    }

    /// <summary>Заявка космопорта (Trade): груз — имя, остальное — как в схеме; нет заявки — null.</summary>
    public TradeState? TradeOf(uint port) => Has(port, Names.Trade.Name)
        ? new TradeState(GoodName(Num(port, "Trade.resource")), Num(port, "Trade.wanted"), Num(port, "Trade.left"), Num(port, "Trade.total"), Num(port, "Trade.buy") != 0)
        : null;

    /// <summary>Положить заявку космопорту прямо в мир (<c>world.add(port, Trade({…}))</c>).</summary>
    public void PutTrade(uint port, string resource, double wanted, double? left = null, double? total = null, bool buy = false)
    {
        Add(port, Names.Trade.Name);
        Set(port, "Trade.resource", GoodProto(resource));
        Set(port, "Trade.wanted", wanted);
        Set(port, "Trade.left", left ?? 0);
        Set(port, "Trade.total", total ?? 0);
        Set(port, "Trade.buy", buy ? 1 : 0);
    }

    /// <summary>Корабль космопорта с подарком и его груз (<c>world.get(port, Transfer)</c>).</summary>
    public TransferInfo TransferOf(uint port) => Query<TransferInfo>(TradeQueries.Transfer, new EntityArgs { Entity = port });

    /// <summary>Может ли игрок продавать и покупать через здание (canSell).</summary>
    public bool CanSell(uint player, uint port) => Query<bool>(TradeQueries.CanSell, new PortArgs { Player = player, Port = port });

    /// <summary>Свободен ли космопорт для подарка (canFerry).</summary>
    public bool CanFerry(uint player, uint port) => Query<bool>(TradeQueries.CanFerry, new PortArgs { Player = player, Port = port });

    /// <summary>Сколько товара заявки уже привезено в космопорт (deliveredTo).</summary>
    public double Delivered(uint port) => Query<double>(TradeQueries.Delivered, new EntityArgs { Entity = port });

    /// <summary>Сколько тиков летит корабль космопорта за seconds секунд (flightTicks).</summary>
    public int FlightTicks(uint port, double seconds) => Query<int>(TradeQueries.Flight, new FlightArgs { Port = port, Seconds = seconds });

    /// <summary>Какую тему надо изучить, чтобы купить ресурс (buyResearch); не нужно — null.</summary>
    public string? BuyResearch(uint player, string resource) =>
        Query<string>(TradeQueries.BuyResearch, new PortArgs { Player = player, Good = resource }) is { Length: > 0 } topic ? topic : null;

    /// <summary>Кого из юнитов игрок может отдать (giftable).</summary>
    public uint[] Giftable(uint player, params uint[] units) => Query<EntityList>(TradeQueries.Giftable, new GiftArgs { Player = player, Units = units }).Items;

    /// <summary>Запас игрока в хранилищах его зон (stockOf): груз → сколько.</summary>
    public Dictionary<string, double> StockOf(uint player)
    {
        var info = Query<StockInfo>(TradeQueries.Stock, new PlayerArgs { Player = player });
        return info.Goods.Zip(info.Amounts).ToDictionary(pair => pair.First, pair => pair.Second);
    }

    /// <summary>Подарок союзнику через космопорт (sendFerry): принят ли приказ.</summary>
    public bool SendFerry(uint player, uint port, uint to, double credits, params (string Good, double Amount)[] goods) =>
        Now(player, new FerryOrder { Port = port, To = to, Credits = credits, Goods = goods.ToDictionary(item => item.Good, item => item.Amount) });

    /// <summary>Заявка на продажу (sell): принят ли приказ — заявка на космопорте есть уже сейчас.</summary>
    public bool Sell(uint player, uint port, string resource, double amount) =>
        Now(player, new SellOrder { Port = port, Resource = resource, Amount = amount });

    /// <summary>Закупка с орбиты (buy): принят ли приказ.</summary>
    public bool Buy(uint player, uint port, string resource, double amount) =>
        Now(player, new BuyOrder { Port = port, Resource = resource, Amount = amount });

    /// <summary>Передать юниты союзнику (handOver): принят ли приказ; сколько перешло, видно по хозяину.</summary>
    public bool HandOver(uint player, uint to, params uint[] units) => Now(player, new GiftOrder { Units = units, To = to });
}

/// <summary>Заявка космопорта (Trade): груз — имя; wanted, left, total — как в схеме; buy — закупка.</summary>
public sealed record TradeState(string Resource, double Wanted, double Left, double Total, bool Buy);
