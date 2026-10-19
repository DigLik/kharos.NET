using Base.Client.Music;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Music: музыка меню (audio/music.ts) — этап Music мода, шина music движка. Пока открыто меню, играют аккорды и
/// мелодия (<see cref="MenuMusic"/>); в самой партии играет фон (раздел Audio): тревога и бой меняют его пульс и гул.
/// Щелчки и касания кнопок меню звучат из оболочки.
/// </summary>
internal sealed class MusicSection : Section
{
    public override void Setup(ClientBuilder client) => client.Music("base.music", new MenuMusic().Update);

    public override void Frame(ClientContext c)
    {
    }
}
