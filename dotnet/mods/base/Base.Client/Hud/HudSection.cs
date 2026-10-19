using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Hud: панель игрока (client.ui: hud) — верхняя полоса (TopBar), нижняя панель (Bar: мини-карта, сведения,
/// сетка команд Commands), состояние из реплики (HudReader), остаток месторождений (KnownReserve). Hud/Lacks.cs — К1.
/// </summary>
internal sealed class HudSection : Section
{
    private Hud _hud = null!;

    public override void Setup(ClientBuilder client) => client.Ui("base.ui", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _hud = new Hud(state);
    }

    public override void Frame(ClientContext c)
    {
        if (Profile?.Ui.Contains("hud") == true)
            _hud.Draw(c);
    }
}
