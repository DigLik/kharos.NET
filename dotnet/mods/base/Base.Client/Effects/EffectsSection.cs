using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Effects: эффекты мира (client.effects: traces — колея, Effects/Traces.cs). TODO(К1): Effects/Combat.cs,
/// Missiles.cs, Links.cs, Flow.cs, Power.cs, Wrecks.cs, Drops.cs; TODO(К2): Effects/Cursor.cs.
/// </summary>
internal sealed class EffectsSection : Section
{
    private Traces _traces = null!;

    public override void Setup(ClientBuilder client) => client.Effects("base.effects", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _traces = new Traces(state);
    }

    public override void Frame(ClientContext c)
    {
        if (Profile?.Effects.Contains("traces") == true)
            _traces.Update(c);
    }
}
