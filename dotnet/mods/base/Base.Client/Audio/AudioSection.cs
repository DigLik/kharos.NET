using Base.Client.Audio;
using Base.Shared.Signals;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Audio: голоса и обработчики звука — перенос mods/base/audio (docs/dotnet.md, Р9). Обработчики идут в порядке
/// пилота (client.audio партии skirmish): интерфейс, моторы, работа зданий, звуки мира (WorldEvents) и боя (Battle),
/// фон с подложкой и эхом, ветер полёта; банк сэмплов — раздел Sounds, музыка — раздел Music. Обстановку фона (тревога,
/// бой, успехи) ведёт Mood: Battle и WorldEvents пишут, фон читает.
/// </summary>
internal sealed class AudioSection : Section
{
    public override void Setup(ClientBuilder client)
    {
        var mood = new Mood();
        var teams = new Teams();
        var events = new WorldEvents(mood, teams, () => State?.P.Paving);
        client.Signal(RefusedSignal.Name, (c, _) => c.Sound("deny", 0.3f));
        client.Signal(DefeatedSignal.Name, events.OnDefeated);
        client.Signal(ShelteredSignal.Name, events.OnSheltered);
        client.Voice(FlightAudio.Voice, FlightAudio.Build().Graph);
        client.Audio("base.interface", new InterfaceAudio().Update);
        client.Audio("base.motors", new MotorsAudio().Update);
        client.Audio("base.machines", new MachinesAudio().Update);
        client.Audio("base.events", events.Update);
        client.Audio("base.battle", new Battle(mood, teams).Update);
        client.Audio("base.ambience", new AmbienceAudio(mood: mood).Update);
        client.Audio("base.flight", new FlightAudio().Update);
    }

    public override void Frame(ClientContext c)
    {
    }
}
