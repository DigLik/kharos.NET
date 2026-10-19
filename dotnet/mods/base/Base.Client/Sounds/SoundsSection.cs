using Kharos.Mod.Sdk.Client;

namespace Base.Client.Sounds;

/// <summary>
/// Раздел Sounds: банк сэмплов base (<see cref="SoundBank"/>) — перенос mods/base/sounds; кадра у банка нет.
/// </summary>
internal sealed class SoundsSection : Section
{
    public override void Setup(ClientBuilder client) => SoundBank.Register(client);

    public override void Frame(ClientContext c)
    {
    }
}
