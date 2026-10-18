using Base.Client.Audio;
using Base.Client.Sounds;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Звук base: банк сэмплов, голоса и обработчики кадра — перенос mods/base/sounds и mods/base/audio (docs/dotnet.md, Р9).
/// Обработчики звука идут в порядке пилота (client.audio партии skirmish): интерфейс, моторы, работа зданий, фон с
/// подложкой и эхом, ветер полёта. Музыки у base нет: подложка — часть фона.
/// </summary>
public static class AudioSetup
{
    public static void Register(ClientBuilder client)
    {
        SoundBank.Register(client);
        client.Voice(FlightAudio.Voice, FlightAudio.Build().Graph);
        client.Audio("base.interface", new InterfaceAudio().Update);
        client.Audio("base.motors", new MotorsAudio().Update);
        client.Audio("base.machines", new MachinesAudio().Update);
        client.Audio("base.ambience", new AmbienceAudio().Update);
        client.Audio("base.flight", new FlightAudio().Update);
    }
}
