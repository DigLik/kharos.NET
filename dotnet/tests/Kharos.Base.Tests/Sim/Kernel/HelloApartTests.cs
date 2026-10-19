using Kharos.Base.Scenario;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Host;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Base.Tests.Kernel;

/// <summary>
/// Пример hello лежит рядом с base в dotnet/mods и грузится вместе с ним (Desktop из репозитория). Он действует только в
/// своей партии hello: в партиях base его системы, события, HUD и камера молчат, а в своей — работают как прежде.
/// </summary>
public sealed class HelloApartTests
{
    private static (GameContent Content, List<string> Log) Content()
    {
        var hello = ModPackage.Read(Path.Combine(Sim.DotnetRoot, "mods", "hello"));
        var game = ModPackage.Read(Path.Combine(Sim.DotnetRoot, "mods", "base"));
        return (GameContent.Load([game, hello]), []);
    }

    /// <summary>Сколько сущностей с ходовой hello (Drive): ровер приходит игроку при входе в партию hello.</summary>
    private static int Rovers(KHost host) => host.World.Schema.Find("Drive") is { } drive ? host.World.Store(drive).Count : 0;

    [Theory(DisplayName = "hello на хосте молчит в партиях base и работает в своей")]
    [InlineData("test", false)]
    [InlineData("skirmish", false)]
    [InlineData("hello", true)]
    public void HostActsOnlyInOwnGame(string name, bool own)
    {
        Assert.SkipUnless(Sim.Unavailable(Sim.DefaultMode) is null, Sim.Unavailable(Sim.DefaultMode) ?? "");
        var (content, log) = Content();
        using var host = new KHost(content, new KHostOptions { Game = name, ModMode = Sim.DefaultMode, Log = log.Add, Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 10_000 } });
        host.Start();
        Assert.True(host.Join(1));
        for (var tick = 0; tick < 5; tick++)
            host.Step();
        if (own)
            Assert.True(Rovers(host) > 0, "в своей партии hello не дал роверов");
        else
            Assert.Equal(0, Rovers(host));
        Assert.DoesNotContain(log, line => line.Contains("нет компонента"));
    }

    [Theory(DisplayName = "клиент hello не рисует свой HUD в партии base, а в своей — рисует")]
    [InlineData("skirmish", false)]
    [InlineData("hello", true)]
    public void ClientActsOnlyInOwnGame(string name, bool own)
    {
        Assert.SkipUnless(BaseSession.Built && File.Exists(Path.Combine(Sim.DotnetRoot, "mods", "hello", "client.wasm")), BaseSession.NotBuilt);
        var (content, log) = Content();
        var options = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 5000 };
        using var local = LocalGame.Start(() => new KHost(content, new KHostOptions { Game = name, Log = log.Add }), step: TimeSpan.FromMilliseconds(5));
        using var client = new ClientGame(content, local.Client, new ClientGameOptions { ModuleOptions = options, Log = log.Add, Fonts = Fonts.Load() });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var drawn = false;
        for (var frame = 0; frame < 60 && !(own && drawn); frame++)
        {
            client.Step(new ClientInput { Width = 1280, Height = 720, Time = clock.Elapsed.TotalSeconds });
            drawn |= client.Gui.RectOf("hello:spawn") is not null;
            Thread.Sleep(10);
        }
        Assert.Empty(client.Errors);
        Assert.Equal(own, drawn);
    }
}
