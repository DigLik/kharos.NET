using TradeState = Base.Client.TradeState;
using Base.Client;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Client.Hud.RequestButtonsTests;

namespace Kharos.Base.Tests.Client.Panels;

/// <summary>Страницы космопорта и меню юнитов в сетке команд (commandsOf): сделка, подарок кораблём, «Отдать союзнику».</summary>
public sealed class TradePanelTests
{
    private static PlayerRow Row(uint player, string name, bool own = false, string? relation = null) =>
        new(player, name, own, true, relation == "ally", -1, relation);

    private static readonly PlayerRow[] Players = [Row(1, "Вася", own: true), Row(2, "Петя", relation: "ally")];

    [Fact(DisplayName = "сделка космопорта: ресурс, потом количество шагами в пределах запаса, кредитов и трюма")]
    public void Deal()
    {
        var trade = new TradeState(7, [new TradeOffer("metal", 130, 130, 10)], [new TradePurchase("metal", 30, 200, null), new TradePurchase("fuel", 36, 0, null)], null, null);
        var state = new HudState { Trade = trade, Credits = 1000, Building = "spaceport" };
        Deal? deal = null;
        var sent = new List<object>();
        var actions = new GridActions { Send = sent.Add, Pick = next => deal = next };
        Slot?[] Grid(string page) => Commands.Of(state, page, actions, Tables.Value, T, deal);
        void Press(string page, string label) => Grid(page).First(slot => slot?.Label == label)!.Run();

        // Продажа: начинается с полусотни, «+100» упирается в запас, отправка шлёт набранное и закрывает сделку.
        Press("sell", "Металл ×130");
        Assert.Equal(new Deal("metal", 50), deal);
        Press("sell", "+100");
        Assert.Equal(130, deal!.Amount);
        Press("sell", "−10");
        var send = Grid("sell")[8]!;
        Assert.Equal("Продажа ×120", send.Label);
        Assert.Equal(1200, send.Cost);
        Assert.True(send.Gain);
        send.Run();
        var order = Assert.IsType<SellOrder>(Assert.Single(sent));
        Assert.Equal((7u, "metal", 120d), (order.Port, order.Resource, order.Amount));
        Assert.Null(deal);

        // Закупка: не больше, чем хватает кредитов; ресурс, которому нет места в трюме, не выбрать.
        Assert.True(Grid("buy")[1]!.Disabled);
        Assert.Equal("Топливо", Grid("buy")[1]!.Label);
        Press("buy", "Металл");
        Assert.Equal(new Deal("metal", 33), deal);
        Assert.Equal("+10", Grid("buy")[0]!.Label);
        Assert.True(Grid("buy")[0]!.Disabled);
        Assert.Equal("Закупка ×33", Grid("buy")[8]!.Label);
        Assert.Equal(990, Grid("buy")[8]!.Cost);
    }

    /// <summary>Панель космопорта с мирным состоянием, союзником и запасом металла.</summary>
    private sealed class Port
    {
        public readonly HudState State;
        public readonly List<object> Sent = [];
        public FerryDraft? Ferry;
        public string Page = "root";
        private readonly GridActions _actions;

        public Port(TradeTransfer? transfer, IEnumerable<PlayerRow> players, double metal = 130)
        {
            State = new HudState
            {
                Trade = new TradeState(7, [], [], null, transfer),
                Credits = 1000,
                Building = "spaceport",
                Stock = ([new Stack("metal", metal)], 500),
                Demolish = new DemolishState([7], 100),
                Players = [.. players],
            };
            _actions = new GridActions { Send = Sent.Add, Open = next => Page = next, Draft = next => Ferry = next };
        }

        public Slot?[] Grid(string at) => Commands.Of(State, at, _actions, Tables.Value, T, null, Ferry);
        public void Press(string at, string label) => Grid(at).First(slot => slot?.Label == label)!.Run();
    }

    [Fact(DisplayName = "панель космопорта: подарок союзнику набирается грузом и деньгами и уходит кораблём")]
    public void PortPanelBuildsTheGift()
    {
        // Тот, кто союз только предложил, ещё не союзник: в списке получателей его нет.
        var port = new Port(null, [Row(1, "Вася", own: true), Row(2, "Петя", relation: "ally"), Row(3, "Коля", relation: "allyAsked")]);

        // В корне космопорта — «Союзнику», и набор начинается с выбора союзника.
        Assert.Equal("Союзнику", port.Grid("root")[0]!.Label);
        Assert.Equal("Разобрать", port.Grid("root")[10]!.Label);
        port.Press("root", "Союзнику");
        Assert.Equal("ally", port.Page);
        Assert.Equal(["Петя", "Назад"], port.Grid("ally").Where(slot => slot is not null).Select(slot => slot!.Label));
        port.Press("ally", "Петя");
        Assert.Equal("ferryGoods", port.Page);
        Assert.Equal((2u, "Петя", 0d, 0d), (port.Ferry!.To, port.Ferry.Name, port.Ferry.Credits, port.Ferry.Amount));
        Assert.Empty(port.Ferry.Goods);
        Assert.Null(port.Ferry.Editing);
        // Пустой корабль не отправить.
        var send = port.Grid("ferryGoods")[8]!;
        Assert.Equal("Отправить: Петя", send.Label);
        Assert.True(send.Disabled);
        Assert.Equal("Корабль пуст: положите груз или деньги", send.Title);

        // Груз: шаги по десять и сотне, «Всё» — весь запас, «Добавить» кладёт набранное в корабль.
        port.Press("ferryGoods", "Металл ×130");
        Assert.Equal("metal", port.Ferry!.Editing!.Good);
        port.Press("ferryGoods", "+100");
        Assert.Equal(100, port.Ferry!.Amount);
        port.Press("ferryGoods", "Всё");
        Assert.Equal(130, port.Ferry!.Amount);
        port.Press("ferryGoods", "Добавить ×130");
        Assert.Equal(130, port.Ferry!.Goods["metal"]);
        Assert.Null(port.Ferry.Editing);
        Assert.Equal(0, port.Ferry.Amount);
        Assert.False(port.Grid("ferryGoods")[8]!.Disabled);

        // Деньги: сотнями, не больше счёта; подпись ячейки показывает уже набранное.
        port.Press("ferryGoods", "Деньги");
        Assert.NotNull(port.Ferry!.Editing);
        Assert.Null(port.Ferry.Editing.Good);
        port.Press("ferryGoods", "+1000");
        Assert.Equal(1000, port.Ferry!.Amount);
        port.Press("ferryGoods", "Добавить ×1000");
        Assert.Equal(1000, port.Ferry!.Credits);
        Assert.Null(port.Ferry.Editing);
        Assert.Equal("Деньги ×1000", port.Grid("ferryGoods")[9]!.Label);

        // Отправка: получатель, деньги и груз уходят командой, черновик и страница сбрасываются.
        port.Press("ferryGoods", "Отправить: Петя");
        var order = Assert.IsType<FerryOrder>(Assert.Single(port.Sent));
        Assert.Equal((7u, 2u, 1000d), (order.Port, order.To, order.Credits));
        Assert.Equal(new Dictionary<string, double> { ["metal"] = 130 }, order.Goods);
        Assert.Null(port.Ferry);
        Assert.Equal("root", port.Page);
    }

    [Fact(DisplayName = "космопорт занят или союзников нет: подарок не начать")]
    public void PortPanelBlocksWhenBusyOrAlone()
    {
        var transfer = new TradeTransfer(2, 100, [new Stack("metal", 50)], 0.5);
        var busy = new Port(transfer, Players).Grid("root")[0]!;
        Assert.Equal("Союзнику", busy.Label);
        Assert.True(busy.Disabled);
        Assert.Equal("Космопорт занят: корабль уже в пути", busy.Title);
        var lone = new Port(null, Players.Take(1)).Grid("root")[0]!;
        Assert.Equal("Союзнику", lone.Label);
        Assert.True(lone.Disabled);
        Assert.Equal("Союзников нет: предложите союз в списке по Tab", lone.Title);
    }

    [Fact(DisplayName = "панель подарка не обещает взять больше вместимости корабля")]
    public void GiftPanelRespectsShipCapacity()
    {
        var port = new Port(null, Players, metal: 700);
        port.Press("root", "Союзнику");
        port.Press("ally", "Петя");
        port.Press("ferryGoods", "Металл ×700");
        port.Press("ferryGoods", "Всё");
        Assert.Equal(500, port.Ferry!.Amount);
        Assert.Equal("×500", port.Grid("ferryGoods").First(slot => slot?.Label == "Всё")!.Title);
    }

    /// <summary>Меню юнитов: выбранные юниты (по номерам) и игроки.</summary>
    private sealed class Menu
    {
        public readonly List<object> Sent = [];
        public string Page = "root";
        private readonly HudState _state;
        private readonly GridActions _actions;

        public Menu(uint[]? units, IEnumerable<PlayerRow> players)
        {
            _state = new HudState { Units = [("truck", units?.Length ?? 0)], Gift = units, Players = [.. players] };
            _actions = new GridActions { Send = Sent.Add, Open = next => Page = next };
        }

        public Slot?[] Grid(string at) => Commands.Of(_state, at, _actions, Tables.Value, T);
        public void Press(string at, string label) => Grid(at).First(slot => slot?.Label == label)!.Run();
    }

    [Fact(DisplayName = "меню юнитов: «Отдать союзнику» ведёт к списку союзников, и клик по имени отдаёт выбранных")]
    public void UnitMenuLeadsToTheAllyList()
    {
        // Кто союз только предложил — ещё не союзник: в списке получателей его нет.
        var menu = new Menu([12, 13], [Row(1, "Вася", own: true), Row(2, "Петя", relation: "ally"), Row(3, "Коля", relation: "allyOffered")]);
        var cell = menu.Grid("root")[11]!;
        Assert.Equal("Отдать союзнику", cell.Label);
        Assert.False(cell.Disabled);
        menu.Press("root", "Отдать союзнику");
        Assert.Equal("gift", menu.Page);
        Assert.Equal(["Петя", "Назад"], menu.Grid("gift").Where(slot => slot is not null).Select(slot => slot!.Label));
        menu.Press("gift", "Петя");
        var order = Assert.IsType<GiftOrder>(Assert.Single(menu.Sent));
        Assert.Equal([12u, 13u], order.Units);
        Assert.Equal(2u, order.To);
        Assert.Equal("root", menu.Page);
    }

    [Fact(DisplayName = "меню юнитов: без выбранных юнитов отдавать нечего, а без союзников — некому")]
    public void UnitMenuHasNothingToGiveWithoutSelectionOrAllies()
    {
        // Ничего не выбрано — ячейки передачи нет вовсе.
        Assert.Null(new Menu(null, Players).Grid("root")[11]);
        // Союзников нет — ячейка есть, но недоступна.
        var cell = new Menu([12], Players.Take(1)).Grid("root")[11]!;
        Assert.Equal("Отдать союзнику", cell.Label);
        Assert.True(cell.Disabled);
        Assert.Equal("Союзников нет: предложите союз в списке по Tab", cell.Title);
    }
}
