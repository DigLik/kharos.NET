using Kharos.Mod.Sdk;
using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Signals;
using Kharos.Mod.Sdk.Client;
using MemoryPack;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Что HUD отдаёт панелям на кадр через <c>c.Shared[PanelsFeed.Key]</c>: состояние, набираемая сделка и подарок, страница
/// сетки и два действия — поставить количество сделки и подтвердить её (поле ввода в сведениях космопорта).
/// </summary>
internal sealed class PanelsFeed
{
    public const string Key = "panels.feed";

    public required HudState State { get; init; }
    public Deal? Deal { get; init; }
    public FerryDraft? Ferry { get; init; }
    public string Page { get; init; } = "root";
    public Action<double> SetAmount { get; init; } = _ => { };
    public Action Confirm { get; init; } = () => { };
}

/// <summary>
/// Игроки и союзы, защита новичка, поражение (gui/hud.ts, protectionOf, gui/screens/play.ts): союзники над панелью, список
/// игроков по Tab с кнопками союза, срок защиты, уведомления по сигналам ally, defeated, sheltered, player_respawned в
/// виде строк чата и окно поражения с кнопками «Сдаться» (пока есть что сдавать) и «Начать заново».
/// </summary>
internal sealed class PlayersPanel
{
    /// <summary>Сколько секунд живёт строка уведомления.</summary>
    public const double NoticeSeconds = 12;

    private const int MaxNotices = 6;

    private const string SharedKey = "panels.players";

    /// <summary>Панель партии: одна на клиента, лежит в общем состоянии кадра.</summary>
    public static PlayersPanel Of(ClientContext c)
    {
        if (c.Shared.TryGetValue(SharedKey, out var value) && value is PlayersPanel panel)
            return panel;
        var made = new PlayersPanel();
        c.Shared[SharedKey] = made;
        return made;
    }

    private readonly List<(string Text, Color Color, double Until)> _notices = [];
    private readonly HashSet<uint> _sheltered = [];
    private bool _roster;
    private bool _surrendering;

    /// <summary>Строки уведомлений сейчас (для тестов).</summary>
    public IReadOnlyList<string> Notices => [.. _notices.Select(notice => notice.Text)];

    /// <summary>Кто под защитой новичка по последней вести.</summary>
    public IReadOnlySet<uint> Sheltered => _sheltered;

    private static string NameOf(ClientContext c, uint player)
    {
        foreach (var row in c.Roster())
            if (row.Player == player)
                return row.Nick;
        return c.Text("server.player", "number", player.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Notice(ClientContext c, string text, Color color)
    {
        _notices.Add((text, color, c.Time + NoticeSeconds));
        if (_notices.Count > MaxNotices)
            _notices.RemoveAt(0);
    }

    // ---- Сигналы хоста.

    public void OnAlly(ClientContext c, HostSignal signal)
    {
        var ally = MemoryPackSerializer.Deserialize<AllySignal>(signal.Payload);
        if (ally is null)
            return;
        var good = ally.Kind is "allyFormed" or "allyOffered" or "allyAsked" or "allyKicked";
        var color = ally.Kind is "allyFormed" ? Ally : good ? Good : Soft;
        Notice(c, c.Text("host." + ally.Kind, "name", ally.Player == 0 ? "" : NameOf(c, ally.Player)), color);
    }

    public void OnDefeated(ClientContext c, HostSignal signal)
    {
        var defeated = MemoryPackSerializer.Deserialize<DefeatedSignal>(signal.Payload);
        if (defeated is null)
            return;
        Notice(c, $"{NameOf(c, defeated.Player)} {c.Text("host.defeated")}", Bad);
    }

    public void OnSheltered(ClientContext c, HostSignal signal)
    {
        var sheltered = MemoryPackSerializer.Deserialize<ShelteredSignal>(signal.Payload);
        if (sheltered is null)
            return;
        foreach (var player in sheltered.Players)
            if (!_sheltered.Contains(player))
                Notice(c, $"{NameOf(c, player)}: {c.Text("hud.sheltered")}", Good);
        _sheltered.Clear();
        foreach (var player in sheltered.Players)
            _sheltered.Add(player);
    }

    /// <summary>Игрока начали заново: уведомления прошлой жизни уходят, защита — как у нового.</summary>
    public void OnRespawned(ClientContext c, HostSignal signal)
    {
        _notices.Clear();
        _sheltered.Remove(c.Player);
        Notice(c, $"{NameOf(c, c.Player)} {c.Text("host.restarts")}", Soft);
    }

    // ---- Состояние для HUD.

    /// <summary>Дополняет состояние HUD: отношения и защита игроков, срок своей защиты, поражение, космопорт.</summary>
    public void Augment(ClientContext c, HudReader reader, HudParts parts, BaseTables tables, HudState hud, uint[] selection)
    {
        Relations(c, reader, hud);
        hud.Protection = ProtectionOf(c, reader);
        hud.Defeated = c.Tick > 0 && reader.Account() is not null && IsDefeated(c, reader, parts);
        TradeReader.Read(c, reader, parts, tables, hud, selection);
    }

    private static string ProtectionOf(ClientContext c, HudReader reader)
    {
        var account = reader.Account();
        var player = c.Component("Player");
        var until = HudParts.Num(account, player, "safe");
        var seconds = Math.Ceiling(Math.Max(0, until - c.Tick) * (c.TickDt > 0 ? c.TickDt : 0.05) / 10) * 10;
        return seconds > 0 ? HudReader.ClockOf(seconds) : "";
    }

    /// <summary>Проиграл ли свой игрок (isDefeated): ни одного готового здания и нет MCV.</summary>
    private static bool IsDefeated(ClientContext c, HudReader reader, HudParts parts)
    {
        if (parts.Building is { } building)
            foreach (var entity in c.Each(building, parts.Owner!))
                if (c.Mine(entity) && !(parts.Site is { } site && c.Has(entity, site)))
                    return false;
        if (parts.Unit is { } unit)
            foreach (var entity in c.Each(unit, parts.Owner!))
                if (c.Mine(entity) && reader.UnitType(entity) == "mcv")
                    return false;
        return true;
    }

    /// <summary>Союз, предложения, защита, счёт и база каждого игрока списка.</summary>
    private void Relations(ClientContext c, HudReader reader, HudState hud)
    {
        ComponentRef? diplomacy = null, player = null;
        try
        {
            diplomacy = c.Component("Diplomacy");
            player = c.Component("Player");
        }
        catch (KeyNotFoundException)
        {
        }
        var mineOffered = new List<uint>();
        var mineAsked = new List<uint>();
        if (diplomacy is not null && player is not null)
            foreach (var entity in c.Each(player))
                if (HudParts.Num(c.Get(entity, player), player, "id") == c.Player && c.Get(entity, diplomacy) is { } values)
                {
                    mineOffered = HudParts.U32s(values, diplomacy, "offered");
                    mineAsked = HudParts.U32s(values, diplomacy, "asked");
                    break;
                }
        var credits = new Dictionary<uint, double>();
        if (player is not null)
            foreach (var entity in c.Each(player))
                if (c.Get(entity, player) is { } values)
                    credits[(uint)HudParts.Num(values, player, "id")] = Math.Floor(HudParts.Num(values, player, "credits"));
        var bases = BasesOf(c, reader, hud);
        for (var i = 0; i < hud.Players.Count; i++)
        {
            var row = hud.Players[i];
            var relation = row.Own ? null : row.Ally ? "ally" : mineAsked.Contains(row.Player) ? "allyAsked" : mineOffered.Contains(row.Player) ? "allyOffered" : null;
            hud.Players[i] = row with
            {
                Relation = relation,
                Safe = _sheltered.Contains(row.Player),
                Credits = credits.GetValueOrDefault(row.Player),
                Base = bases.TryGetValue(row.Player, out var at) ? at : null,
            };
        }
    }

    /// <summary>Главные здания союзников: по ним союзник на панели ведёт камеру.</summary>
    private static Dictionary<uint, (double X, double Y)> BasesOf(ClientContext c, HudReader reader, HudState hud)
    {
        var bases = new Dictionary<uint, (double, double)>();
        var allies = hud.Players.Where(row => !row.Own && row.Ally).Select(row => row.Player).ToHashSet();
        if (allies.Count == 0)
            return bases;
        var building = c.Component("Building");
        var owner = c.Component("Owner");
        foreach (var entity in c.Each(building, owner))
        {
            if (reader.BuildingType(entity) != "command")
                continue;
            var who = (uint)HudParts.Num(c.Get(entity, owner), owner, "player");
            if (allies.Contains(who) && !bases.ContainsKey(who) && c.Pose(entity) is { } pose)
                bases[who] = (pose.X, pose.Y);
        }
        return bases;
    }

    // ---- Рисунок.

    public void Draw(ClientContext c, PanelsFeed feed, Tr t)
    {
        var hud = feed.State;
        var ui = c.Ui;
        _notices.RemoveAll(notice => notice.Until <= c.Time);
        var top = TopBar.HeightOf(ui.Width);
        var bottom = Bar.HeightOf(ui.Width);
        TradeInfo.Draw(c, feed, t, bottom);
        Allies(c, hud, bottom, t);
        NoticeColumn(c, bottom, feed.State.Trade is not null || feed.Ferry is not null);
        if (hud.Defeated && !hud.Editor)
            DefeatWindow(c, t, top);
        Roster(c, hud, top, t);
    }

    /// <summary>
    /// Вести о союзах, поражении и защите — строки там, где в TS их показывает чат (chat.draw в gui/hud.ts): над нижней
    /// панелью, правее мини-карты; над сделкой космопорта, когда она открыта. Срок защиты — в верхней полосе (TopBar).
    /// </summary>
    private void NoticeColumn(ClientContext c, float bottom, bool trade)
    {
        if (_notices.Count == 0)
            return;
        var ui = c.Ui;
        var side = Bar.MinimapSide(ui.Width);
        var left = ui.Width >= TopBar.Narrow && side > 140 ? side + 12 : 12;
        var above = trade && ui.RectOf("trade") is { } rect ? ui.Height - rect.Y + 6 : bottom + 8;
        ui.Box(new Style { Id = "notices", Anchor = new Anchor(Left: left, Bottom: above), Skin = Skin.Panel, Padding = (8, 12), Direction = Direction.Column, Gap = 3, MaxWidth = 360, Passive = true }, () =>
        {
            foreach (var (text, color, _) in _notices)
                ui.Text(text, new TextStyle { Color = color, Shadow = Ink, Wrap = 336 });
        });
    }

    /// <summary>Союзники — справа над панелью, всегда на виду: имя, глава ли он, кредиты; щелчок — камера к его базе.</summary>
    private static void Allies(ClientContext c, HudState hud, float bottom, Tr t)
    {
        var list = hud.Players.Where(row => !row.Own && row.Relation == "ally").ToList();
        if (list.Count == 0)
            return;
        var ui = c.Ui;
        ui.Box(new Style { Id = "allies", Anchor = new Anchor(Right: 12, Bottom: bottom + 8), Skin = Skin.Panel, Padding = 10, Direction = Direction.Column, Gap = 4, Align = Align.Stretch }, () =>
        {
            foreach (var row in list)
            {
                var id = $"ally:{row.Player}";
                var click = ui.Button(id, Style.Button with { Padding = (3, 12), Justify = Align.Start, Alpha = row.Online ? null : 0.45f }, () =>
                {
                    ui.Box(new Style { Width = 8, Height = 8, Background = Ally });
                    ui.Text(row.Name, Shadowed(Ally));
                    if (row.Head == row.Player)
                        ui.Text(t("hud.allyHead"), new TextStyle { Color = Gold });
                    ui.Text(Commands.N(row.Credits), new TextStyle { Color = Gold });
                });
                ui.Tip(id, t("hud.friendTip", "name", row.Name, "credits", Commands.N(row.Credits)));
                if (click is not null && row.Base is { } at)
                {
                    c.Camera.X = at.X;
                    c.Camera.Y = at.Y;
                }
            }
        });
    }

    /// <summary>Что можно сделать с этим игроком в союзе прямо сейчас: подпись и приказ (allyActions).</summary>
    public static List<(string Label, object Order)> AllyActions(PlayerRow row, PlayerRow? self)
    {
        var leads = self is not null && (self.Head < 0 || self.Head == self.Player);
        if (row.Own)
            return row.Head < 0 ? [] : [(row.Head == row.Player ? "hud.allyDissolve" : "hud.allyExit", new ExitAllyOrder())];
        var break_ = new BreakAllyOrder { To = row.Player };
        var offer = new AllyOrder { To = row.Player };
        if (row.Relation == "ally")
            return leads && row.Head == self!.Player ? [("hud.allyKick", break_)] : [];
        if (row.Relation == "allyAsked")
            return leads ? [("hud.allyAccept", offer), ("hud.allyDecline", break_)] : [("hud.allyDecline", break_)];
        if (row.Relation == "allyOffered")
            return [("hud.allyWithdraw", break_)];
        return leads ? [("hud.allyOffer", offer)] : [];
    }

    private static void SendOrder(ClientContext c, object order)
    {
        var (name, bytes) = order switch
        {
            AllyOrder o => (AllyOrder.Name, MemoryPackSerializer.Serialize(o)),
            BreakAllyOrder o => (BreakAllyOrder.Name, MemoryPackSerializer.Serialize(o)),
            ExitAllyOrder o => (ExitAllyOrder.Name, MemoryPackSerializer.Serialize(o)),
            SurrenderOrder o => (SurrenderOrder.Name, MemoryPackSerializer.Serialize(o)),
            _ => throw new ArgumentException($"Panels: неизвестный приказ {order.GetType().Name}"),
        };
        c.Send(name, bytes);
    }

    /// <summary>
    /// Список игроков — пока зажат Tab, и пока под ним мышь, когда Tab уже отпущен: так кнопки союза не исчезают из-под
    /// руки. Ушла мышь с отпущенным Tab — уходит и он.
    /// </summary>
    private void Roster(ClientContext c, HudState hud, float top, Tr t)
    {
        var ui = c.Ui;
        var held = c.Input.Keys.Contains("tab");
        if (held)
            _roster = true;
        var over = ui.Within("roster");
        if (!held && !over)
        {
            _roster = false;
            _surrendering = false;
        }
        if (!_roster || hud.Players.Count == 0)
            return;
        var self = hud.Players.FirstOrDefault(row => row.Own);
        var width = Math.Min(420, ui.Width - 32);
        ui.Box(new Style { Id = "roster", Anchor = new Anchor(CenterX: 0, Top: top + 26), Width = width, Skin = Skin.Panel, Padding = (16, 20), Direction = Direction.Column, Align = Align.Stretch }, () =>
        {
            ui.Box(new Style { Padding = (0, 0, 8, 0) }, () =>
                ui.Text(t("hud.roster", "online", hud.Players.Count(row => row.Online).ToString(System.Globalization.CultureInfo.InvariantCulture)), new TextStyle { Color = Dim }));
            foreach (var row in hud.Players)
            {
                var actions = AllyActions(row, self);
                ui.Box(new Style { Height = 1, Background = new Color(155, 185, 209, 38) });
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 16, Padding = (4, 0), Alpha = row.Own || row.Online ? null : 0.45f }, () =>
                {
                    ui.Box(new Style { Direction = Direction.Row, Gap = 8, Grow = 1 }, () =>
                    {
                        ui.Text(row.Name, new TextStyle { Color = row.Own ? Bright : Text });
                        if (row.Safe)
                            ui.Text(t("hud.sheltered"), new TextStyle { Color = Good });
                        if (row.Head == row.Player)
                            ui.Text(t("hud.allyHead"), new TextStyle { Color = Ally });
                        if (row.Relation is { } relation)
                            ui.Text(t("hud." + relation), new TextStyle { Color = relation == "ally" ? Ally : Good });
                    });
                    if (actions.Count > 0 && (row.Own || row.Online))
                        ui.Box(new Style { Direction = Direction.Row, Gap = 6 }, () =>
                        {
                            foreach (var (label, order) in actions)
                                if (ui.Button($"roster:{row.Player}:{label}", Style.Button with { Padding = (3, 8) }, () => ui.Text(t(label), Shadowed())) is not null)
                                    SendOrder(c, order);
                        });
                    ui.Text(row.Own ? t("hud.you") : row.Online ? t("hud.online") : t("hud.offline"), new TextStyle { Color = Muted });
                });
            }
            // Сдаться: сначала кнопка, потом подтверждение; сдаваться, когда нечего, незачем.
            if (!hud.Defeated && self is not null)
            {
                ui.Box(new Style { Height = 1, Background = new Color(155, 185, 209, 38) });
                ui.Box(new Style { Padding = (8, 0, 0, 0), Direction = Direction.Row }, () =>
                {
                    var label = _surrendering ? t("game.surrender") + "?" : t("game.surrender");
                    if (ui.Button("roster:surrender", Style.Button with { Skin = Skin.Danger }, () => ui.Text(label, Shadowed(Bad))) is not null)
                    {
                        if (_surrendering)
                            SendOrder(c, new SurrenderOrder());
                        _surrendering = !_surrendering;
                    }
                    if (_surrendering)
                        ui.Text(t("game.surrenderConfirm"), new TextStyle { Color = Dim, Wrap = width - 160 });
                });
            }
        });
    }

    /// <summary>Не осталось ни зданий, ни MCV: сообщение и кнопки «Начать заново» (play.ts).</summary>
    private static void DefeatWindow(ClientContext c, Tr t, float top)
    {
        var ui = c.Ui;
        ui.Box(new Style { Id = "defeat", Anchor = new Anchor(CenterX: 0, Top: 56), Skin = Skin.Panel, Padding = (20, 28), Direction = Direction.Column, Align = Align.Center, Gap = 10 }, () =>
        {
            ui.Text(t("game.defeat"), new TextStyle { Font = FontName.Title, Color = Bad });
            ui.Text(t("game.defeatServer"), new TextStyle { Wrap = 360 });
            ui.Box(new Style { Direction = Direction.Row, Gap = 8 }, () =>
            {
                if (ui.Button("defeat:again", Style.Button, () => ui.Text(t("game.startOver"), Shadowed())) is not null)
                    c.Ask(ShellAction.Respawn);
            });
        });
    }
}
