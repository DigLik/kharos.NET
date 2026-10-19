using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Раздел Atmosphere: погода, сутки и осадки (client.atmosphere: weather). TODO(К2): тесты погоды.</summary>
internal sealed class AtmosphereSection : Section
{
    private Weather _weather = null!;

    public override void Setup(ClientBuilder client) => client.Atmosphere("base.atmosphere", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _weather = new Weather(state);
    }

    public override void Frame(ClientContext c)
    {
        if (Profile?.Atmosphere.Contains("weather") == true)
            _weather.Update(c);
    }
}
