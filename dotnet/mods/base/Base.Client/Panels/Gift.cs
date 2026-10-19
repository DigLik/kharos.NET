using Kharos.Mod.Sdk;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk.Client;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>Космопорт из реплики: заявка (Trade), корабль с подарком (Transfer), запас порта (Stock) — trade в readHud.</summary>
internal static class TradeReader
{
    public static void Read(ClientContext c, HudReader reader, HudParts parts, BaseTables tables, HudState hud, uint[] selection)
    {
        hud.Trade = null;
        ComponentRef trade, transfer;
        try
        {
            trade = c.Component("Trade");
            transfer = c.Component("Transfer");
        }
        catch (KeyNotFoundException)
        {
            return;
        }
        // Один свой готовый космопорт среди выбранного.
        uint port = 0;
        foreach (var entity in selection)
            if (c.Alive(entity) && c.Mine(entity) && reader.BuildingType(entity) is { } type && tables.Buildings.GetValueOrDefault(type)?.Trades == true
                && parts.Site is { } site && !c.Has(entity, site))
            {
                port = entity;
                break;
            }
        if (port == 0)
            return;
        var done = reader.Science().Done;
        var items = hud.Stock?.Items ?? [];
        var offers = new List<TradeOffer>();
        foreach (var (resource, spec) in tables.Resources)
        {
            var have = items.FirstOrDefault(stack => stack.Resource == resource).Amount;
            if (have >= 1)
                offers.Add(new TradeOffer(resource, have, have, spec.Price));
        }
        var stock = c.Get(port, parts.Stock!);
        var room = Math.Max(0, HudParts.Num(stock, parts.Stock, "capacity") - reader.Items(stock).Values.Sum());
        var purchases = new List<TradePurchase>();
        foreach (var (resource, spec) in tables.Resources)
            if (!spec.Mined)
                purchases.Add(new TradePurchase(resource, TradeRules.BuyPrice(spec.Price), Math.Floor(room), spec.Research is { } topic && !done.Contains(topic) ? topic : null));
        TradeOrder? order = null;
        if (c.Has(port, trade) && c.Get(port, trade) is { } values)
        {
            var good = reader.PathOf((int)HudParts.Num(values, trade, "resource", -1)) is { } path ? path[(path.IndexOf('/') + 1)..] : "";
            var buy = HudParts.Num(values, trade, "buy") != 0;
            var wanted = Math.Round(HudParts.Num(values, trade, "wanted"));
            var total = HudParts.Num(values, trade, "total");
            var left = HudParts.Num(values, trade, "left");
            var price = tables.Resources.GetValueOrDefault(good)?.Price ?? 0;
            order = new TradeOrder(good, wanted, buy ? 0 : Math.Floor(Math.Min(wanted, reader.Items(stock).GetValueOrDefault(good))), buy ? TradeRules.BuyPrice(price) : price,
                total > 0 ? Math.Round((1 - left / total) * 100) / 100 : null, buy);
        }
        TradeTransfer? ferry = null;
        if (c.Has(port, transfer) && c.Get(port, transfer) is { } ship && HudParts.Num(ship, transfer, "done") == 0)
        {
            var total = HudParts.Num(ship, transfer, "total");
            ferry = new TradeTransfer((uint)HudParts.Num(ship, transfer, "to"), Math.Floor(HudParts.Num(ship, transfer, "credits")), [],
                total > 0 ? Math.Round(Math.Min(1, 1 - HudParts.Num(ship, transfer, "left") / total) * 100) / 100 : 1);
        }
        hud.Trade = new TradeState(port, offers, purchases, order, ferry);
    }
}

/// <summary>Сведения о космопорте над нижней панелью (bottomBar в bar.ts): заявка, корабль, набираемая сделка, подарок.</summary>
internal static class TradeInfo
{
    public static void Draw(ClientContext c, PanelsFeed feed, Tr t, float bottom)
    {
        var hud = feed.State;
        var trade = hud.Trade;
        if (trade is null && feed.Ferry is null)
            return;
        var ui = c.Ui;
        var left = 12f + (ui.Width >= TopBar.Narrow ? Bar.MinimapSide(ui.Width) + 12 : 0);
        ui.Box(new Style { Id = "trade", Anchor = new Anchor(Left: left, Bottom: bottom + 8), Skin = Skin.Panel, Padding = (8, 12), Direction = Direction.Column, Gap = 4, Width = 360 }, () =>
        {
            if (trade is not null)
                Status(c, feed, trade, t);
            if (feed.Ferry is { } draft)
            {
                var parts = draft.Goods.Select(pair => $"{t("goods." + pair.Key)} ×{Commands.N(Math.Floor(pair.Value))}").ToList();
                if (draft.Credits > 0)
                    parts.Add($"{Commands.N(draft.Credits)} {t("hud.cr")}");
                ui.Text(t("hud.ferryDraft", "name", draft.Name, "list", parts.Count > 0 ? string.Join(", ", parts) : t("common.empty")), new TextStyle { Color = Soft, Wrap = 336 });
            }
        });
    }

    private static void Status(ClientContext c, PanelsFeed feed, TradeState trade, Tr t)
    {
        var ui = c.Ui;
        var hud = feed.State;
        var dealing = trade.Order is null && trade.Transfer is null && feed.Deal is { } deal && feed.Page is "sell" or "buy"
            ? TradePages.DealOf(trade, hud.Credits, feed.Page == "buy", feed.Deal!)
            : null;
        if (trade.Order is { } order)
        {
            var name = t("goods." + order.Resource);
            Widgets.Bar(ui, order.Flight ?? order.Delivered / Math.Max(1, order.Wanted),
                order.Buy ? t("hud.buying", "name", name, "wanted", Commands.N(order.Wanted), "paid", Commands.N(order.Wanted * order.Price))
                : order.Flight is not null ? t("hud.shipping", "name", name, "wanted", Commands.N(order.Wanted), "total", Commands.N(order.Wanted * order.Price))
                : t("hud.hauling", "name", name, "delivered", Commands.N(order.Delivered), "wanted", Commands.N(order.Wanted)), 336);
        }
        else if (trade.Transfer is { } ship)
        {
            var who = hud.Players.FirstOrDefault(row => row.Player == ship.To)?.Name ?? "";
            Widgets.Bar(ui, ship.Flight, t("hud.ferrying", "name", who, "percent", Commands.N(Math.Round(ship.Flight * 100)) + "%"), 336);
        }
        else if (dealing is not null)
        {
            var buying = feed.Page == "buy";
            ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
            {
                ResRun(c, dealing.Resource, dealing.Amount, style: new TextStyle { Color = Soft });
                ui.Text($"{t(buying ? "grid.buy" : "grid.sell")}: ", new TextStyle { Color = Soft });
                ui.Field("deal", Commands.N(dealing.Amount), new FieldOptions { Digits = true, MaxLength = 6 }, new Style { Width = 72 });
                if (ui.Edited("deal") is { } text)
                {
                    var amount = Math.Max(0, Math.Min(dealing.Max, Math.Floor(double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var typed) ? typed : 0)));
                    if (amount != dealing.Amount)
                        feed.SetAmount(amount);
                }
            });
            ui.Text(buying
                ? t("hud.dealBuy", "total", Commands.N(dealing.Amount * dealing.Price), "room", Commands.N(dealing.Room - dealing.Amount))
                : t("hud.dealSell", "total", Commands.N(dealing.Amount * dealing.Price), "left", Commands.N(dealing.Max - dealing.Amount)),
                new TextStyle { Color = Soft, Wrap = 336 });
        }
        else
            ui.Text((trade.Offers.Count > 0 ? t("hud.sellHint") : t("hud.nothingToSell")) + t("hud.buyHint"), new TextStyle { Color = Soft, Wrap = 336 });
    }
}
