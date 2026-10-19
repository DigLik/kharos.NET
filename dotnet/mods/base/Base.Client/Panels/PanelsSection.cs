using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Раздел Panels: торговля и подарок (космопорт), игроки и союзы, защита, поражение, «начать заново».</summary>
internal sealed class PanelsSection : Section
{
    // TODO(К3б): Panels/Trade.cs, Panels/Gift.cs, Panels/Players.cs; сигналы ally, defeated, sheltered, player_respawned. Обработчик — client.Ui("base.panels", Frame).
    public override void Setup(ClientBuilder client)
    {
    }

    public override void Frame(ClientContext c)
    {
    }
}
