using Base.Shared.Data;
using Base.Shared.Orders;
using Kharos.Mod.Sdk.Client;
using MemoryPack;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Панель игрока — перенос createHud из gui/hud.ts в объёме К3а: верхняя полоса (<see cref="TopBar"/>), нижняя панель
/// (<see cref="Bar"/>: мини-карта, сведения, сетка команд), подсказка по месторождению под указателем. Своё состояние —
/// здесь: страница сетки и набираемый заказ сбрасываются, когда выбрано другое. Клавиши сетки (T…,) и R (окно
/// исследований) — тоже здесь; поле ввода в фокусе забирает их раньше (UI-ядро).
/// </summary>
internal sealed class Hud(BaseState state)
{
    private readonly Minimap _minimap = new(state);
    private Bar? _bar;
    private HudParts? _parts;
    private HudReader? _reader;
    private BaseTables? _tables;
    private string _page = "root";
    private Deal? _deal;
    private FerryDraft? _ferry;
    private string _selectionKey = "";

    /// <summary>Последнее состояние и сетка (для тестов и звука нажатий).</summary>
    public HudState? Last { get; private set; }

    public void Draw(ClientContext c)
    {
        if (_reader is null)
        {
            _tables = BaseTables.Load(name => c.TryData(name, out var bytes) ? bytes.ToArray() : null);
            _parts = new HudParts(c);
            _reader = new HudReader(c, _parts, _tables, HudRules.Of(c, _tables), state.P.Deposits);
            _minimap.Tables = _tables;
            _bar = new Bar(state, _minimap);
        }
        Tr t = (key, args) => c.Text(key, args);
        var hud = _reader.Read(Shared.Selection(c), HudShared.Read(c));
        hud.Editor = (c.Shell & ShellMode.Editor) != 0;
        var selection = Shared.Selection(c);
        PlayersPanel.Of(c).Augment(c, _reader, _parts!, _tables!, hud, selection);
        Last = hud;

        // Выбрано другое — страница сетки и набираемый заказ сбрасываются (кроме закладки и протяжки).
        var key = $"{hud.Building}:{string.Join(",", hud.Units.Select(unit => unit.Type + unit.Count))}";
        if (key != _selectionKey)
        {
            _selectionKey = key;
            if (hud.Construction is not { Placing: not null } && hud.Construction is not { Paving: not null })
                _page = "root";
            _deal = null;
            _ferry = null;
        }
        var actions = Actions(c);
        var slots = hud.Selected ? Commands.Of(hud, _page, actions, _tables!, t, _deal, _ferry) : new Slot?[Commands.Size];
        c.Shared[PanelsFeed.Key] = Feed(hud, c);

        // Снизу вверх: нижняя панель, полоса, подсказки.
        _bar!.Draw(new Bar.Frame(c, hud, slots, _tables!, t, _parts!, _reader.Heads()));
        TopBar.Draw(c, hud, t);
        HoverTip(c, t);
        Keys(c, slots);
    }

    /// <summary>Что HUD отдаёт панелям на кадр: состояние, сделка, подарок, страница и действия поля количества.</summary>
    private PanelsFeed Feed(HudState hud, ClientContext c) => new()
    {
        State = hud,
        Deal = _deal,
        Ferry = _ferry,
        Page = _page,
        SetAmount = amount =>
        {
            if (_deal is { } deal)
                _deal = deal with { Amount = amount };
        },
        Confirm = () =>
        {
            if (hud.Trade is { Order: null } trade && _deal is { } deal && TradePages.DealOf(trade, hud.Credits, _page == "buy", deal) is { Amount: >= 1 } view)
            {
                Send(c, _page == "buy"
                    ? new BuyOrder { Port = trade.Port, Resource = view.Resource, Amount = view.Amount }
                    : new SellOrder { Port = trade.Port, Resource = view.Resource, Amount = view.Amount });
                _deal = null;
            }
        },
    };

    /// <summary>Клавиши сетки команд (T, Y, U, I, G, H, J, K, B, N, M, «,»); R — окно исследований — ловит К4.</summary>
    private static void Keys(ClientContext c, Slot?[] slots)
    {
        var input = c.Input;
        if (input.Ctrl || input.Alt || input.Keys.Contains("ctrl"))
            return;
        for (var i = 0; i < Commands.Size; i++)
            if (input.Hits.Contains(Commands.Keys[i]) && slots[i] is { Disabled: false } slot)
                slot.Run();
    }

    /// <summary>Что сетка делает с игрой: приказы — хосту, режимы наведения — управлению через c.Shared.</summary>
    private GridActions Actions(ClientContext c) => new()
    {
        Send = order => Send(c, order),
        Place = building =>
        {
            if (building is not null)
                Modes.Place(c, building);
            else
                c.Shared.Remove(Modes.PlacingKey);
        },
        Pave = tool =>
        {
            if (tool is not null)
                Modes.Pave(c, tool);
            else
                c.Shared.Remove(Modes.PavingKey);
        },
        Route = start => HudShared.Set(c, HudShared.Route, start ? 0 : null),
        Serve = start => HudShared.Set(c, HudShared.Serve, start ? 0 : null),
        Carry = pick => HudShared.Set(c, HudShared.Carry, pick),
        Patrol = start => HudShared.Set(c, HudShared.Patrol, start),
        Aim = silo => HudShared.Set(c, HudShared.Aim, silo),
        // Место разворота выбирают, как место здания: режим ведёт постановка (К2, Controls/Placing).
        Deploy = mcv =>
        {
            if (mcv is { } unit)
                Modes.Deploy(c, unit);
            else
                c.Shared.Remove(Modes.DeployingKey);
        },
        Open = page =>
        {
            _page = page;
            _deal = null;
        },
        Pick = deal => _deal = deal,
        Draft = ferry => _ferry = ferry,
        Step = c.Shared.TryGetValue(StepsSection.CurrentKey, out var step) ? step as StepSpec : null,
        IsSeen = key => Seen.Is(c, key),
        MarkSeen = key => Seen.Mark(c, key),
        Tech = () => HudShared.Set(c, HudShared.Tech, true),
    };

    /// <summary>Приказ хосту: имя и MemoryPack по типу приказа.</summary>
    public static void Send(ClientContext c, object order)
    {
        var (name, bytes) = order switch
        {
            ProduceOrder o => (ProduceOrder.Name, MemoryPackSerializer.Serialize(o)),
            CancelProductionOrder o => (CancelProductionOrder.Name, MemoryPackSerializer.Serialize(o)),
            DeployOrder o => (DeployOrder.Name, MemoryPackSerializer.Serialize(o)),
            CancelDeployOrder o => (CancelDeployOrder.Name, MemoryPackSerializer.Serialize(o)),
            PackOrder o => (PackOrder.Name, MemoryPackSerializer.Serialize(o)),
            StanceOrder o => (StanceOrder.Name, MemoryPackSerializer.Serialize(o)),
            ServeOrder o => (ServeOrder.Name, MemoryPackSerializer.Serialize(o)),
            FilterOrder o => (FilterOrder.Name, MemoryPackSerializer.Serialize(o)),
            ReserveOrder o => (ReserveOrder.Name, MemoryPackSerializer.Serialize(o)),
            WantOrder o => (WantOrder.Name, MemoryPackSerializer.Serialize(o)),
            SellOrder o => (SellOrder.Name, MemoryPackSerializer.Serialize(o)),
            BuyOrder o => (BuyOrder.Name, MemoryPackSerializer.Serialize(o)),
            CloseSaleOrder o => (CloseSaleOrder.Name, MemoryPackSerializer.Serialize(o)),
            FerryOrder o => (FerryOrder.Name, MemoryPackSerializer.Serialize(o)),
            GiftOrder o => (GiftOrder.Name, MemoryPackSerializer.Serialize(o)),
            ScuttleOrder o => (ScuttleOrder.Name, MemoryPackSerializer.Serialize(o)),
            WorkOrder o => (WorkOrder.Name, MemoryPackSerializer.Serialize(o)),
            UpgradeOrder o => (UpgradeOrder.Name, MemoryPackSerializer.Serialize(o)),
            CancelUpgradeOrder o => (CancelUpgradeOrder.Name, MemoryPackSerializer.Serialize(o)),
            CancelResearchOrder o => (CancelResearchOrder.Name, MemoryPackSerializer.Serialize(o)),
            WarheadOrder o => (WarheadOrder.Name, MemoryPackSerializer.Serialize(o)),
            CancelWarheadOrder o => (CancelWarheadOrder.Name, MemoryPackSerializer.Serialize(o)),
            AbortLaunchOrder o => (AbortLaunchOrder.Name, MemoryPackSerializer.Serialize(o)),
            CancelBuildOrder o => (CancelBuildOrder.Name, MemoryPackSerializer.Serialize(o)),
            DemolishOrder o => (DemolishOrder.Name, MemoryPackSerializer.Serialize(o)),
            _ => throw new ArgumentException($"HUD: неизвестный приказ {order.GetType().Name}"),
        };
        c.Send(name, bytes);
    }

    /// <summary>
    /// Месторождение под указателем (hover в hoverTips): что в нём и сколько осталось — каким игрок его знает (в тумане —
    /// с последнего раза, никогда не видел — «остаток неизвестен»). Правее и ниже указателя, у правого края — левее.
    /// </summary>
    private void HoverTip(ClientContext c, Tr t)
    {
        var input = c.Input;
        if (!input.Inside || input.OverUi || float.IsNaN(input.X))
            return;
        if (KnownReserve.Spot(c, state.P.Deposits, Math.Floor(input.WorldX) + 0.5, Math.Floor(input.WorldY) + 0.5) is not { } spot)
            return;
        var ui = c.Ui;
        var anchor = input.X > ui.Width - 260 ? new Anchor(Right: ui.Width - input.X + 16, Top: input.Y + 16) : new Anchor(Left: input.X + 16, Top: input.Y + 16);
        ui.Box(new Style { Id = "hover", Anchor = anchor, Skin = Skin.Panel, Padding = (8, 12), Direction = Direction.Column, Gap = 2, Passive = true }, () =>
            ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
            {
                Res(c, spot.Kind);
                ui.Text(t("hud.depositTip", "name", t("goods." + spot.Kind)));
                var left = spot.Left is { } known ? Math.Floor(known) : (double?)null;
                ui.Text(left is null ? t("hud.leftUnknown") : left > 0 ? t("hud.left", "amount", Commands.N(left.Value)) : t("hud.depleted"), new TextStyle { Color = Dim });
            }));
    }
}
