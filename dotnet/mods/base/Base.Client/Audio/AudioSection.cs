using Base.Client.Audio;
using Base.Shared.Signals;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Audio: голоса и обработчики звука — перенос mods/base/audio (docs/dotnet.md, Р9). Обработчики идут в порядке
/// пилота (client.audio партии skirmish): интерфейс, моторы, работа зданий, фон с подложкой и эхом, ветер полёта; банк
/// сэмплов — раздел Sounds, музыка — раздел Music. TODO(К5): Audio/WorldEvents.cs, Audio/Battle.cs (звуки событий) —
/// обработчик client.Audio("base.events", Frame), тесты моторов, машин и фона.
/// </summary>
internal sealed class AudioSection : Section
{
    public override void Setup(ClientBuilder client)
    {
        // Хост отказал приказу (refused): короткий сигнал отказа; текст причины — в Н6.
        client.Signal(RefusedSignal.Name, (c, _) => c.Sound("deny", 0.3f));
        client.Voice(FlightAudio.Voice, FlightAudio.Build().Graph);
        client.Audio("base.interface", new InterfaceAudio().Update);
        client.Audio("base.motors", new MotorsAudio().Update);
        client.Audio("base.machines", new MachinesAudio().Update);
        client.Audio("base.ambience", new AmbienceAudio().Update);
        client.Audio("base.flight", new FlightAudio().Update);
    }

    public override void Frame(ClientContext c)
    {
    }
}
