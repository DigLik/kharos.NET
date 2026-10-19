using Base.Shared.Signals;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Panels: торговля и подарок (космопорт: страницы сетки — <see cref="TradePages"/>, сведения — <see cref="TradeInfo"/>),
/// игроки и союзы, защита, поражение, «начать заново» (<see cref="PlayersPanel"/>). Состояние HUD раздел берёт из
/// <c>c.Shared[PanelsFeed.Key]</c>, что кладёт Hud каждый кадр; сигналы ally, defeated, sheltered, player_respawned
/// становятся строками уведомлений. Работает там же, где HUD (подача партии: ui hud).
/// </summary>
internal sealed class PanelsSection : Section
{
    public override void Setup(ClientBuilder client)
    {
        client.Signal(AllySignal.Name, (c, signal) => PlayersPanel.Of(c).OnAlly(c, signal));
        client.Signal(DefeatedSignal.Name, (c, signal) => PlayersPanel.Of(c).OnDefeated(c, signal));
        client.Signal(ShelteredSignal.Name, (c, signal) => PlayersPanel.Of(c).OnSheltered(c, signal));
        client.Signal(RespawnedSignal.Name, (c, signal) => PlayersPanel.Of(c).OnRespawned(c, signal));
        client.Ui("base.panels", Frame);
    }

    public override void Frame(ClientContext c)
    {
        if (Profile?.Ui.Contains("hud") != true || !c.Shared.TryGetValue(PanelsFeed.Key, out var value) || value is not PanelsFeed feed)
            return;
        PlayersPanel.Of(c).Draw(c, feed, (key, args) => c.Text(key, args));
    }
}
