using Kharos.Base.Scenario;
using Kharos.Client.Game;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>
/// Звуки боя (Audio/Battle.cs) и музыка меню (Music/MenuMusic.cs) настоящим клиентом base: выстрелы и взрывы — сущностями
/// хоста, меню — режимом оболочки во входе кадра; звук рендерится офлайн.
/// </summary>
public sealed class BattleMusicTests
{
    private static (Sim Sim, Viewer Viewer) Setup()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 800, Audio = true });
        viewer.Camera = new CameraState(0, 0, 32);
        return (sim, viewer);
    }

    private static IReadOnlyList<string> Played(Viewer viewer) => [.. viewer.Sounds.Of(Ops.Play).Select(call => call.Name!)];

    [Fact(DisplayName = "выстрел и взрыв получают голос: оружие по виду выстрела, размер взрыва — по громкости и виду")]
    public void ShotsAndBlastsGetTheirVoices()
    {
        var (sim, viewer) = Setup();
        using var _ = sim;
        viewer.Run(3);
        void Spawn(string weapon, double x, double y)
        {
            var shot = sim.World.Spawn();
            foreach (var name in (string[])["Position", "Proto", "Shot"])
                sim.Add(shot, name);
            sim.Set(shot, "Position.x", x);
            sim.Set(shot, "Position.y", y);
            sim.Set(shot, "Proto.id", sim.Host.Content.FindProto("weapons/" + weapon)!.Id);
            sim.Set(shot, "Shot.player", 2);
            foreach (var (field, value) in (ReadOnlySpan<(string, double)>)[("fromX", x), ("fromY", y), ("toX", x + 2), ("toY", y + 2), ("life", 100)])
                sim.Set(shot, "Shot." + field, value);
        }
        Spawn("rifle", 1, 1);
        viewer.Run(0.1);
        Spawn("cannon", -2, 1);
        viewer.Run(0.2);
        // Выстрелы звучат по оружию; попадания хоста добавляют свои взрывы — их здесь не считаем.
        Assert.Equal(["rifle", "cannon"], Played(viewer).Where(name => name is "rifle" or "cannon"));
        var explosions = () => Played(viewer).Count(name => name is "blast" or "bigBlast");
        var before = explosions();
        var blast = sim.World.Spawn();
        sim.Add(blast, "Position");
        sim.Add(blast, "Blast");
        sim.Set(blast, "Position.x", 2);
        sim.Set(blast, "Position.y", 2);
        sim.Set(blast, "Blast.size", 0.8);
        sim.Set(blast, "Blast.life", 100);
        viewer.Run(0.3);
        Assert.Equal(before + 1, explosions());
        var heard = viewer.Sounds.Of(Ops.Play).Last(call => call.Name is "blast" or "bigBlast");
        Assert.Equal("blast", heard.Name);
        Assert.True(heard.Values[0] > 1, "взрыв громче выстрела");
        // Далеко за краем экрана — тишина.
        var all = Played(viewer).Count;
        Spawn("rifle", 50, 50);
        viewer.Run(0.3);
        Assert.Equal(all, Played(viewer).Count);
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "музыка меню: играет на шине music, пока открыто меню, и стихает, когда его закрыли")]
    public void MenuMusicPlaysOnTheMusicBusWhileTheMenuIsOpen()
    {
        var (sim, viewer) = Setup();
        using var _ = sim;
        List<SoundCall> Music() => [.. viewer.Sounds.Of(Ops.Voice).Where(call => call.Values[0] == (float)Kharos.Audio.Bus.Music)];
        viewer.Run(1);
        Assert.Empty(Music());
        viewer.Input = viewer.Input with { Shell = 1 /* ClientAbi.ShellMenu */ };
        viewer.Run(3);
        var bed = Assert.Single(Music(), call => float.IsNaN(call.Values[2]));
        Assert.True(Music().Count > 1, "ноты планируются вперёд");
        Assert.NotNull(viewer.Game.Mods[0].Sounds[bed.Voice]);
        // Музыка слышна: вывод не тише, чем у одного фона без неё.
        Assert.Contains(viewer.Output.Skip(2 * Kharos.Audio.AudioFormat.SampleRate), value => Math.Abs(value) > 1e-5f);
        viewer.Input = viewer.Input with { Shell = 0 };
        viewer.Run(2);
        Assert.True(viewer.Game.Mods[0].Sounds[bed.Voice] is null or { IsFinished: true });
        var notes = Music().Count;
        viewer.Run(2);
        Assert.Equal(notes, Music().Count);
        Assert.Empty(viewer.Log);
    }
}
