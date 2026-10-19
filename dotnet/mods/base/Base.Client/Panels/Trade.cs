using Base.Shared.Orders;
using Base.Shared.Rules;

namespace Base.Client;

/// <summary>Предложение продажи: что лежит в хранилищах игрока и почём; Here — сколько из этого в сети самого космопорта.</summary>
internal sealed record TradeOffer(string Resource, double Available, double Here, double Price);

/// <summary>Закупка с орбиты: цена, место в складе космопорта, тема, без которой не купить (null — можно).</summary>
internal sealed record TradePurchase(string Resource, double Price, double Room, string? Research);

/// <summary>Открытая заявка: Flight — готовность полёта (null — корабль ещё грузится).</summary>
internal sealed record TradeOrder(string Resource, double Wanted, double Delivered, double Price, double? Flight, bool Buy);

/// <summary>Корабль с подарком в пути: кому, сколько денег, как далеко улетел. Груз знает только хост.</summary>
internal sealed record TradeTransfer(uint To, double Credits, List<Stack> Goods, double Flight);

/// <summary>Продажа, если выбран свой готовый космопорт (trade в HudState).</summary>
internal sealed record TradeState(uint Port, List<TradeOffer> Offers, List<TradePurchase> Purchases, TradeOrder? Order, TradeTransfer? Transfer);

/// <summary>
/// Подарок союзнику, который игрок набирает у космопорта: кому, сколько денег и какого груза. Editing — что правится
/// сейчас (null — список груза): груз или деньги; Amount — набираемое число (FerryDraft в commands.ts).
/// </summary>
internal sealed record FerryDraft(uint To, string Name, double Credits, IReadOnlyDictionary<string, double> Goods, FerryEditing? Editing, double Amount)
{
    public static FerryDraft For(uint to, string name) => new(to, name, 0, new Dictionary<string, double>(), null, 0);
}

/// <summary>Что правят в подарке: груз Good или (Good = null) деньги.</summary>
internal sealed record FerryEditing(string? Good);

/// <summary>Сделка в пределах возможного (dealOf).</summary>
internal sealed record DealView(string Resource, double Amount, double Price, double Max, double Room);

/// <summary>Страницы космопорта в сетке команд: продажа, закупка, подарок кораблём (торговая часть commandsOf).</summary>
internal static class TradePages
{
    /// <summary>Кнопки продажи и закупки — начало нижнего ряда; подарок — сверху.</summary>
    private const int TradeCell = 8, FerryCell = 0, Demolish = 10, Back = 11, Cancel = 9, ListSize = 8;

    /// <summary>Шаги количества в сделке и с какого числа она начинается.</summary>
    private static readonly int[] DealSteps = [10, 50, 100];
    private const int DealStart = 50;

    /// <summary>Шаги подарка: груз — десятками, деньги — сотнями.</summary>
    private static readonly int[] GoodSteps = DealSteps, CreditSteps = [100, 500, 1000];

    /// <summary>
    /// Сделка в пределах возможного: количество — не больше max (продажа — сколько есть в запасе, закупка — на сколько
    /// хватает кредитов и места в трюме). null — такого ресурса космопорт сейчас не предлагает.
    /// </summary>
    public static DealView? DealOf(TradeState trade, double credits, bool buying, Deal deal)
    {
        var purchase = trade.Purchases.FirstOrDefault(item => item.Resource == deal.Resource);
        var offer = trade.Offers.FirstOrDefault(item => item.Resource == deal.Resource);
        double price;
        if (buying)
        {
            if (purchase is null)
                return null;
            price = purchase.Price;
        }
        else
        {
            if (offer is null)
                return null;
            price = offer.Price;
        }
        var room = purchase?.Room ?? 0;
        var max = buying ? Math.Max(0, Math.Min(room, Math.Floor(credits / price))) : offer!.Available;
        var amount = Math.Max(0, Math.Min(max, Math.Floor(double.IsFinite(deal.Amount) ? deal.Amount : 0)));
        return new DealView(deal.Resource, amount, price, max, room);
    }

    public static void Fill(HudState state, string page, Slot?[] slots, GridActions actions, Tr t, Deal? deal, FerryDraft? ferry)
    {
        var trade = state.Trade!;
        var credits = state.Credits;
        var send = actions.Send;
        var port = trade.Port;
        void Open(string next) => actions.Open(next);
        void Top(IEnumerable<Slot> items)
        {
            var i = 0;
            foreach (var slot in items.Take(ListSize))
                slots[i++] = slot;
        }
        // Космопорт один, и корабль у него один: пока в пути заказ или подарок, ни то, ни другое не начать.
        var busy = trade.Order is not null || trade.Transfer is not null;
        var allies = state.Players.Where(row => !row.Own && row.Ally).ToList();

        IEnumerable<Slot> GoodsOf(FerryDraft draft) =>
            (state.Stock?.Items ?? []).Select(stack => new Slot
            {
                Label = $"{t("goods." + stack.Resource)} ×{Commands.N(stack.Amount)}",
                Good = stack.Resource,
                Title = t("grid.ferryAmount"),
                Run = () => actions.Draft(draft with { Editing = new FerryEditing(stack.Resource), Amount = Math.Floor(draft.Goods.GetValueOrDefault(stack.Resource)) }),
            });

        if (page == "root")
        {
            slots[TradeCell] = new Slot
            {
                Label = t("grid.sell"),
                Building = "spaceport",
                Active = trade.Order is not null,
                Hint = actions.Step?.Sell == true && trade.Order is null,
                Title = trade.Order is not null ? t("grid.sellingTip") : t("grid.sellTip"),
                Run = () => Open("sell"),
            };
            slots[TradeCell + 1] = new Slot
            {
                Label = t("grid.buy"),
                Building = "spaceport",
                Disabled = trade.Order is not null,
                Title = trade.Order is not null ? t("grid.buyBusyTip") : t("grid.buyTip"),
                Run = () => Open("buy"),
            };
            // Подарок союзнику: деньги и груз кораблём. Набранное ждёт возвращения на страницу подарка.
            slots[FerryCell] = new Slot
            {
                Label = t("grid.give"),
                Building = "spaceport",
                Active = ferry is not null,
                Disabled = busy || allies.Count < 1,
                Title = busy ? t("grid.giveBusyTip") : allies.Count > 0 ? t("grid.giveTip") : t("grid.noAllies"),
                Run = () => Open(ferry is not null ? "ferryGoods" : "ally"),
            };
        }
        else if (page == "ally")
        {
            // Кому корабль: союзник на ячейку, клик — набор подарка ему.
            Top(allies.Select(row => new Slot
            {
                Label = row.Name,
                Active = ferry?.To == row.Player,
                Title = t("grid.ferryWho"),
                Run = () =>
                {
                    actions.Draft(FerryDraft.For(row.Player, row.Name));
                    Open("ferryGoods");
                },
            }));
            slots[Back] = new Slot { Label = t("common.back"), Run = () => Open("root") };
        }
        else if (page == "ferryMore" && ferry is { Editing: null })
        {
            // Хвост списка груза, который не влез на первую страницу.
            var i = 0;
            foreach (var slot in GoodsOf(ferry).Skip(ListSize))
                slots[i++] = slot;
            slots[Back] = new Slot { Label = t("common.back"), Run = () => Open("ferryGoods") };
        }
        else if (page is "ferryGoods" or "ferryMore" && ferry is not null)
        {
            if (ferry!.Editing is null)
            {
                var goods = GoodsOf(ferry).ToList();
                Top(goods);
                // Под список восемь ячеек: девятый груз — в свободную ячейку нижнего ряда, а если их больше — там «Ещё».
                if (goods.Count == ListSize + 1)
                    slots[Demolish] = goods[ListSize];
                else if (goods.Count > ListSize + 1)
                    slots[Demolish] = new Slot { Label = t("grid.more"), Title = t("grid.moreTip"), Run = () => Open("ferryMore") };
                var empty = ferry.Credits <= 0 && ferry.Goods.Count == 0;
                slots[TradeCell] = new Slot
                {
                    Label = $"{t("grid.ferrySend")}: {ferry.Name}",
                    Building = "spaceport",
                    Disabled = empty || busy,
                    Title = busy ? t("grid.giveBusyTip") : empty ? t("grid.ferryEmpty") : t("grid.giveTip"),
                    Run = () =>
                    {
                        send(new FerryOrder { Port = port, To = ferry.To, Credits = ferry.Credits, Goods = new Dictionary<string, double>(ferry.Goods) });
                        actions.Draft(null);
                        Open("root");
                    },
                };
                slots[TradeCell + 1] = new Slot
                {
                    Label = ferry.Credits > 0 ? $"{t("grid.ferryCredits")} ×{Commands.N(ferry.Credits)}" : t("grid.ferryCredits"),
                    Title = t("grid.ferryAmount"),
                    Run = () => actions.Draft(ferry with { Editing = new FerryEditing(null), Amount = ferry.Credits }),
                };
                slots[Back] = new Slot { Label = t("common.back"), Run = () => Open("root") };
            }
            else
            {
                // Количество: верхний ряд — прибавить, второй — убавить, «Всё» — сколько есть, «Добавить» — в корабль.
                var good = ferry.Editing.Good;
                var max = good is not null
                    ? Math.Min(state.Stock?.Items.FirstOrDefault(stack => stack.Resource == good).Amount ?? 0, TradeRules.TransferCapacity)
                    : credits;
                var loaded = good is not null ? Math.Floor(ferry.Goods.GetValueOrDefault(good)) : ferry.Credits;
                var steps = good is not null ? GoodSteps : CreditSteps;
                void Set(double next) => actions.Draft(ferry with { Amount = Math.Max(0, Math.Min(max, next)) });
                for (var i = 0; i < steps.Length; i++)
                {
                    var step = steps[i];
                    slots[i] = new Slot { Label = $"+{step}", Disabled = ferry.Amount >= max, Run = () => Set(ferry.Amount + step) };
                    slots[i + 4] = new Slot { Label = $"−{step}", Disabled = ferry.Amount <= 0, Run = () => Set(ferry.Amount - step) };
                }
                slots[steps.Length] = new Slot { Label = t("grid.all"), Active = ferry.Amount >= max, Title = $"×{Commands.N(max)}", Run = () => Set(max) };
                slots[TradeCell] = new Slot
                {
                    Label = $"{t("grid.ferryAdd")} ×{Commands.N(ferry.Amount)}",
                    Good = good,
                    Disabled = ferry.Amount == loaded,
                    Title = t("grid.ferryAmount"),
                    Run = () =>
                    {
                        // Ноль убирает груз из корабля: так набранное и правят, и снимают.
                        if (good is null)
                        {
                            actions.Draft(ferry with { Credits = ferry.Amount, Editing = null, Amount = 0 });
                            return;
                        }
                        var goods = new Dictionary<string, double>(ferry.Goods);
                        if (ferry.Amount > 0)
                            goods[good] = ferry.Amount;
                        else
                            goods.Remove(good);
                        actions.Draft(ferry with { Goods = goods, Editing = null, Amount = 0 });
                    },
                };
                slots[Back] = new Slot
                {
                    Label = t("common.back"),
                    Run = () =>
                    {
                        actions.Draft(ferry with { Editing = null, Amount = 0 });
                        Open("ferryGoods");
                    },
                };
            }
        }
        else if (page == "ferryGoods")
            slots[Back] = new Slot { Label = t("common.back"), Run = () => Open("root") };
        else if (trade.Order is null && page is "buy" or "sell")
        {
            var buying = page == "buy";
            var picked = deal is null ? null : DealOf(trade, credits, buying, deal);
            if (picked is null)
            {
                // Сначала — что продать или купить: ячейка на ресурс. Количество — на следующем шаге.
                Slot Entry(string resource, double price, string? research, double? here)
                {
                    var limit = DealOf(trade, credits, buying, new Deal(resource, DealStart))!;
                    return new Slot
                    {
                        Label = buying ? t("goods." + resource) : $"{t("goods." + resource)} ×{Commands.N(limit.Max)}",
                        Good = resource,
                        Disabled = limit.Max < 1 || research is not null,
                        Title = research is not null ? t("grid.needsResearch", "name", t("research." + research))
                            : limit.Max >= 1 ? buying ? t("grid.buyPrice", "price", Commands.N(price))
                                : here is { } at && at < limit.Max ? t("grid.sellFar", "price", Commands.N(price), "here", Commands.N(at)) : t("grid.sellPrice", "price", Commands.N(price))
                            : limit.Room < 1 && buying ? t("grid.noRoom") : t("grid.noCredits"),
                        Run = () => actions.Pick(new Deal(resource, limit.Amount)),
                    };
                }
                Top(buying
                    ? trade.Purchases.Select(item => Entry(item.Resource, item.Price, item.Research, null))
                    : trade.Offers.Select(item => Entry(item.Resource, item.Price, null, item.Here)));
                slots[Back] = new Slot { Label = t("common.back"), Run = () => Open("root") };
            }
            else
            {
                // Верхний ряд — прибавить, второй — убавить; «Всё» — сколько есть в запасе или на сколько хватает кредитов и трюма.
                var (resource, amount, price, max, _) = picked;
                void Set(double next) => actions.Pick(new Deal(resource, Math.Max(0, Math.Min(max, next))));
                for (var i = 0; i < DealSteps.Length; i++)
                {
                    var step = DealSteps[i];
                    slots[i] = new Slot { Label = $"+{step}", Disabled = amount >= max, Run = () => Set(amount + step) };
                    slots[i + 4] = new Slot { Label = $"−{step}", Disabled = amount <= 0, Run = () => Set(amount - step) };
                }
                slots[DealSteps.Length] = new Slot { Label = t("grid.all"), Active = amount >= max, Title = $"×{Commands.N(max)}", Run = () => Set(max) };
                slots[TradeCell] = new Slot
                {
                    Label = $"{t(buying ? "grid.buy" : "grid.sell")} ×{Commands.N(amount)}",
                    Good = resource,
                    Cost = amount * price,
                    Gain = !buying,
                    Disabled = amount < 1,
                    Title = t(buying ? "grid.buyPrice" : "grid.sellPrice", "price", Commands.N(price)),
                    Run = () =>
                    {
                        send(buying ? new BuyOrder { Port = port, Resource = resource, Amount = amount } : new SellOrder { Port = port, Resource = resource, Amount = amount });
                        actions.Pick(null);
                    },
                };
                slots[Back] = new Slot { Label = t("common.back"), Run = () => actions.Pick(null) };
            }
        }
        else if (trade.Order is { } order)
        {
            if (order.Flight is null)
                slots[Cancel] = new Slot
                {
                    Label = order.Delivered > 0 ? t("grid.send") : t("grid.dropSale"),
                    Title = order.Delivered > 0 ? t("grid.sendTip") : null,
                    Run = () => send(new CloseSaleOrder { Port = port }),
                };
            slots[Back] = new Slot { Label = t("common.back"), Run = () => Open("root") };
        }
    }
}
