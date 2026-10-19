using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Logistics;
using Base.Host.Sections.Mining;
using Base.Shared.Rules;

namespace Base.Host.Sections.Trade;

/// <summary>
/// Космопорт (trade.ts): продажа по заявке, закупка с орбиты. Заявка — компонент Trade на космопорте: sell кладёт заявку,
/// грузовики свозят товар из хранилищ в склад порта (раздел Logistics читает Trade), и когда привезено всё, корабль улетает
/// и через <see cref="TradeRules.SellSeconds"/> приносит кредиты (система trade, <see cref="TradeFlights"/>). Закупка списывает
/// кредиты сразу, а корабль выгружает груз в склад порта. Пока корабль в пути, порт занят.
/// </summary>
internal static class Trading
{
    /// <summary>Сколько тиков летит корабль этого космопорта (flightTicks).</summary>
    public static int FlightTicks(Call call, uint port, double seconds) =>
        TradeRules.FlightTicks(seconds, call.Num(port, S.Building, S.BuildingLevel, 1), Core.Step);

    /// <summary>Продаётся ли ресурс с орбиты: уран — нет, его только добывают (isOrbital).</summary>
    public static bool IsOrbital(string resource) => Core.Tables.Resources.TryGetValue(resource, out var spec) && !spec.Mined;

    /// <summary>Какую тему игроку надо изучить, чтобы купить ресурс с орбиты; null — ничего не надо (buyResearch).</summary>
    public static string? BuyResearch(Call call, uint player, string resource)
    {
        var topic = Core.Tables.Resources.GetValueOrDefault(resource)?.Research;
        return Accounts.Researched(call, player, topic) ? null : topic;
    }

    /// <summary>Сколько стоит купить единицу ресурса с орбиты (buyPrice).</summary>
    public static double BuyPrice(string resource) => TradeRules.BuyPrice(Core.Tables.Resources.GetValueOrDefault(resource)?.Price ?? 0);

    /// <summary>Зона строительства игрока, в которой стоит это готовое здание; null — оно ни в какую не входит (zoneWith).</summary>
    public static Zones.Zone? ZoneWith(uint player, uint building)
    {
        foreach (var zone in Zones.Of(player))
            if (zone.Buildings.Contains(building))
                return zone;
        return null;
    }

    /// <summary>Хранилище ли это: его склад входит в запас игрока (isStore).</summary>
    public static bool IsStore(Call call, uint entity) => Factory.Building(call.BuildingType(entity))?.Stores == true;

    /// <summary>Что лежит в хранилищах зоны (готовое — ресурсы и изделия) и сколько в них помещается всего (stockOfZone).</summary>
    public static (Dictionary<string, double> Items, double Capacity) StockOfZone(Call call, Zones.Zone zone)
    {
        var items = new Dictionary<string, double>();
        var capacity = 0.0;
        foreach (var entity in zone.Buildings)
        {
            if (!call.Has(entity, S.Stock) || !IsStore(call, entity))
                continue;
            for (var ware = 0; ware < Cargo.Wares; ware++)
                if (Stocks.Amount(call, entity, ware) is var amount && amount > 0)
                    items[Cargo.Name(ware)] = items.GetValueOrDefault(Cargo.Name(ware)) + amount;
            capacity += call.Num(entity, S.Stock, S.StockCapacity);
        }
        return (items, capacity);
    }

    /// <summary>Запас игрока по всем его зонам (stockOf).</summary>
    public static (Dictionary<string, double> Items, double Capacity) StockOf(Call call, uint player)
    {
        var items = new Dictionary<string, double>();
        var capacity = 0.0;
        foreach (var zone in Zones.Of(player))
        {
            var stock = StockOfZone(call, zone);
            foreach (var (good, amount) in stock.Items)
                items[good] = items.GetValueOrDefault(good) + amount;
            capacity += stock.Capacity;
        }
        return (items, capacity);
    }

    /// <summary>Готовое здание игрока: не площадка и своё (isReady).</summary>
    public static bool IsReady(Call call, uint player, uint building) =>
        call.Has(building, S.Building) && !call.Has(building, S.Site) && call.IsOwn(player, building);

    /// <summary>Свой готовый космопорт игрока (isPort).</summary>
    public static bool IsPort(Call call, uint player, uint port) =>
        Factory.Building(call.BuildingType(port))?.Trades == true && IsReady(call, player, port);

    /// <summary>
    /// Идёт ли у космопорта рейс или летит подарок. Прилетевший корабль (Transfer.done) космопорт уже не занимает: в TS его
    /// снимает хост после вести о прибытии, здесь он остаётся записью и свободен.
    /// </summary>
    public static bool Busy(Call call, uint port) =>
        call.Has(port, T.Trade) || (call.Has(port, T.Transfer) && !call.Flag(port, T.Transfer, T.TransferDone));

    /// <summary>Может ли игрок продавать и покупать через это здание: это его готовый космопорт, и он не занят (canSell).</summary>
    public static bool CanSell(Call call, uint player, uint port) => IsPort(call, player, port) && !Busy(call, port);

    /// <summary>Сколько товара продажи уже на складе космопорта (deliveredTo).</summary>
    public static double Delivered(Call call, uint port)
    {
        if (!call.Has(port, T.Trade) || call.Flag(port, T.Trade, T.TradeBuy))
            return 0;
        var good = Cargo.Of(call.Num(port, T.Trade, T.TradeResource, -1));
        return Math.Min(call.Num(port, T.Trade, T.TradeWanted), Stocks.Amount(call, port, good));
    }

    /// <summary>Сколько товара заявки космопорту ещё не хватает и никто не везёт (neededBy).</summary>
    public static double NeededBy(Call call, uint port)
    {
        if (!call.Has(port, T.Trade) || call.Num(port, T.Trade, T.TradeTotal) > 0)
            return 0;
        var good = Cargo.Of(call.Num(port, T.Trade, T.TradeResource, -1));
        var incoming = Flows.Of(Pass.Of(call)).In(port, good);
        return Math.Max(0, call.Num(port, T.Trade, T.TradeWanted) - Delivered(call, port) - incoming);
    }

    /// <summary>
    /// Закупка с орбиты: amount единиц ресурса за <see cref="BuyPrice"/> каждая, кредиты списываются сразу. Покупается
    /// целое число единиц и не больше, чем помещается в склад и хватает кредитов (buy).
    /// </summary>
    public static bool Buy(Call call, uint player, uint port, string resource, double amount)
    {
        if (!IsOrbital(resource) || BuyResearch(call, player, resource) is not null)
            return false;
        if (!double.IsFinite(amount) || amount < 1 || !CanSell(call, player, port))
            return false;
        var room = call.Has(port, S.Stock) ? Stocks.Room(call, port, resource) : 0;
        var wanted = Math.Min(Math.Floor(amount), Math.Floor(room));
        if (wanted < 1 || !Accounts.Pay(call, player, wanted * BuyPrice(resource)))
            return false;
        var ticks = FlightTicks(call, port, TradeRules.BuySeconds);
        call.Add(port, T.Trade)
            .Number(T.TradeResource, Cargo.Proto(Cargo.Index(resource))).Number(T.TradeWanted, wanted)
            .Number(T.TradeLeft, ticks).Number(T.TradeTotal, ticks).Flag(T.TradeBuy, true);
        return true;
    }

    /// <summary>
    /// Разобранный космопорт: оплаченная закупка, чей корабль не прилетел, возвращает уплаченное; груз продажи лежит на
    /// складе порта и падает на землю вместе с ним (refundPurchase).
    /// </summary>
    public static void RefundPurchase(Call call, uint player, uint port)
    {
        if (!call.Has(port, T.Trade) || !call.Flag(port, T.Trade, T.TradeBuy))
            return;
        var resource = Cargo.Name(Cargo.Of(call.Num(port, T.Trade, T.TradeResource, -1)));
        Accounts.Add(call, player, call.Num(port, T.Trade, T.TradeWanted) * BuyPrice(resource));
        call.Remove(port, T.Trade);
    }

    /// <summary>
    /// Заявка на продажу до amount единиц ресурса. Товар остаётся в хранилищах, пока его не привезут: космопорт заказывает
    /// его, из своей сети он идёт по трубам, из других сетей его везут свободные грузовики. Продаётся целое число единиц и
    /// не больше, чем помещается в трюм космопорта (sell).
    /// </summary>
    public static bool Sell(Call call, uint player, uint port, string resource, double amount)
    {
        if (!Core.Tables.Resources.ContainsKey(resource))
            return false;
        if (!double.IsFinite(amount) || amount < 1 || !CanSell(call, player, port) || ZoneWith(player, port) is null)
            return false;
        var room = call.Has(port, S.Stock) ? Stocks.Amount(call, port, resource) + Stocks.Room(call, port, resource) : 0;
        var wanted = Math.Min(Math.Min(Math.Floor(amount), Math.Floor(StockOf(call, player).Items.GetValueOrDefault(resource))), Math.Floor(room));
        if (wanted < 1)
            return false;
        call.Add(port, T.Trade).Number(T.TradeResource, Cargo.Proto(Cargo.Index(resource))).Number(T.TradeWanted, wanted);
        return true;
    }

    /// <summary>Закрывает заявку раньше срока: корабль улетает с тем, что привезли; нет ничего — заявка снимается (closeSale).</summary>
    public static bool CloseSale(Call call, uint player, uint port)
    {
        if (!call.Has(port, T.Trade) || call.Num(port, T.Trade, T.TradeTotal) > 0 || call.Flag(port, T.Trade, T.TradeBuy) || !IsPort(call, player, port))
            return false;
        var delivered = Delivered(call, port);
        if (delivered < 1e-9)
            call.Remove(port, T.Trade);
        else
            call.Set(port, T.Trade).Number(T.TradeWanted, delivered);
        return true;
    }
}
