using Hello;
using Kharos.Client.Game;
using Kharos.Content;
using Kharos.Host;
using Kharos.Plugins.Game;
using HostGame = Kharos.Host.Host;

namespace Kharos.Client.Tests.Game;

/// <summary>Доля тика в кадре (packages/tests/loop.test.ts, «кадр — после тиков и с долей тика»): кадр клиента знает, какую часть тика прошёл с последней дельты.</summary>
public sealed class FrameAlphaTests
{
    [Fact]
    public void FrameCarriesFractionOfTickSinceLastDelta()
    {
        Assert.SkipUnless(HelloSession.Built, HelloSession.NotBuilt);
        var content = GameContent.Load([ModPackage.Read(HelloSession.ModDir)]);
        using var local = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = new GameModuleOptions { CallTimeoutMs = 1000 }, Seed = 1 }), manual: true);
        using var game = new ClientGame(content, local.Client, new ClientGameOptions { ModMode = HelloSession.ClientMode, ModuleOptions = new GameModuleOptions { CallTimeoutMs = 1000 } });
        var input = new ClientInput { Time = 100 };
        game.Step(input);
        Assert.True(game.Replica.Ready);
        var dt = game.Replica.Welcome!.Dt;

        // Дельта тика пришла в кадре с временем 200: с неё отсчёт доли.
        local.Advance(local.Ticks + 1);
        game.Step(input with { Time = 200 });
        Assert.Equal(0, game.Poses.Alpha, 6);
        game.Step(input with { Time = 200 + dt / 2 });
        Assert.Equal(0.5, game.Poses.Alpha, 6);
        game.Step(input with { Time = 200 + dt * 3 });
        Assert.Equal(1, game.Poses.Alpha, 6);
    }
}
