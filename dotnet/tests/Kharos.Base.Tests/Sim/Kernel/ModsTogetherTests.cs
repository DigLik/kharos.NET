using Kharos.Base.Scenario;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Host;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Base.Tests.Kernel;

/// <summary>Моды hello и base в одной партии (папка модов по умолчанию у Desktop): оба должны подняться вместе.</summary>
public sealed class ModsTogetherTests
{
    [Theory(DisplayName = "hello и base вместе: партия поднимается, игрок входит, тик идёт, в каком бы порядке ни лежали моды")]
    [InlineData(true)]
    [InlineData(false)]
    public void HelloAndBaseLoadTogether(bool helloFirst)
    {
        Assert.SkipUnless(Sim.Unavailable(Sim.DefaultMode) is null, Sim.Unavailable(Sim.DefaultMode) ?? "");
        var hello = ModPackage.Read(Path.Combine(Sim.DotnetRoot, "mods", "hello"));
        var game = ModPackage.Read(Path.Combine(Sim.DotnetRoot, "mods", "base"));
        var log = new List<string>();
        using var host = new KHost(GameContent.Load(helloFirst ? [hello, game] : [game, hello]),
            new KHostOptions { Game = "test", ModMode = Sim.DefaultMode, Log = log.Add, Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 10_000 } });
        host.Start();
        Assert.True(host.Join(1));
        for (var tick = 0; tick < 5; tick++)
            host.Step();
        Assert.DoesNotContain(log, line => line.Contains("нет компонента"));
    }

    [Theory(DisplayName = "hello и base вместе на клиенте: оба client.wasm поднимаются на схеме хоста")]
    [InlineData(true)]
    [InlineData(false)]
    public void HelloAndBaseLoadTogetherOnClient(bool helloFirst)
    {
        Assert.SkipUnless(BaseSession.Built && File.Exists(Path.Combine(Sim.DotnetRoot, "mods", "hello", "client.wasm")), BaseSession.NotBuilt);
        var hello = ModPackage.Read(Path.Combine(Sim.DotnetRoot, "mods", "hello"));
        var game = ModPackage.Read(Path.Combine(Sim.DotnetRoot, "mods", "base"));
        var content = GameContent.Load(helloFirst ? [hello, game] : ModPackage.Discover(Path.Combine(Sim.DotnetRoot, "mods")));
        var options = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 5000 };
        var log = new List<string>();
        using var local = LocalGame.Start(() => new KHost(content, new KHostOptions { Log = log.Add }), step: TimeSpan.FromMilliseconds(5));
        using var client = new ClientGame(content, local.Client, new ClientGameOptions { ModuleOptions = options, Log = log.Add });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var frame = 0; frame < 20; frame++)
        {
            client.Step(new ClientInput { Width = 1280, Height = 720, Time = clock.Elapsed.TotalSeconds });
            Thread.Sleep(10);
        }
        Assert.Empty(client.Errors);
        Assert.DoesNotContain(log, line => line.Contains("нет компонента"));
    }

    [Theory(DisplayName = "программы кладут рядом моды вместе с schema/*.json: без неё хост base не знает своих компонентов (нет компонента Building)")]
    [InlineData("Kharos.Desktop")]
    [InlineData("Kharos.Server")]
    public void AppsShipModSchema(string app)
    {
        var project = File.ReadAllText(Path.Combine(Sim.DotnetRoot, "apps", app, app + ".csproj"));
        Assert.Contains(@"schema\*.json", project);
        // Что объявлено в schema/ мода base, то и должно лететь с программой.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Sim.DotnetRoot, "mods", "base", "schema"), "*.json"));
    }
}
