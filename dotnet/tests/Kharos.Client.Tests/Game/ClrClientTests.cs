using Kharos.Client.Game;
using Kharos.Content;
using Kharos.Host;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;
using HostGame = Kharos.Host.Host;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Клиентский мод в режиме CLR (docs/dotnet-n6.md, Н6-6): сборка Hello.Client.dll в своей ALC вместо client.wasm,
/// рукопожатие клиентского ABI и тот же итог, что у wasm.
/// </summary>
public sealed class ClrClientTests
{
    private static string? HelloClient => ClrModule.FindClient(HelloSession.ModDir);

    private static ModLoadInfo ClientLoad() => new() { Abi = 1, Side = "client", Seed = 1, Game = "hello" };

    [Fact]
    public void ClrModuleSpeaksClientAbi()
    {
        Assert.SkipUnless(HelloSession.Built && HelloClient is not null, HelloSession.NotBuilt);
        var content = GameContent.Load([ModPackage.Read(HelloSession.ModDir)]);
        using var local = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = new GameModuleOptions { CallTimeoutMs = 1000 }, Seed = 1 }));
        using var game = new ClientGame(content, local.Client, new ClientGameOptions { ModMode = ModMode.Clr });
        var module = Assert.IsType<ClrModule>(Assert.Single(game.Mods).Module);
        // Клиентскому моду в kh_load уходит версия клиентского ABI, а не хоста; экземпляр хоста у него не делается.
        Assert.True(module.IsClient);
        Assert.Equal(2, module.LoadInfo.Abi);
        Assert.Equal(2, game.Mods[0].Manifest.Abi);
        Assert.Throws<ModAbiException>(() => module.CreateInstance());
        using var instance = module.CreateClientInstance();
        Assert.Equal(2, instance.Manifest.Abi);
        Assert.False(instance.Faulted);
        Assert.False(instance.Look(0).IsEmpty);
    }

    [Fact]
    public void HostAssemblyIsNotAClientMod()
    {
        var host = ClrModule.Find(HelloSession.ModDir);
        Assert.SkipWhen(host is null, "Hello.Host.dll не собран: dotnet build dotnet/Kharos.slnx");
        using var module = new ClrModule("hello", host!, ClientLoad());
        var error = Assert.Throws<ModAbiException>(() => module.CreateClientInstance());
        Assert.Contains("ClientExports", error.Message);
    }

    [Fact]
    public void ClrAndWasmClientsDrawTheSameLooks()
    {
        Assert.SkipUnless(HelloSession.Built && HelloClient is not null, HelloSession.NotBuilt);
        var content = GameContent.Load([ModPackage.Read(HelloSession.ModDir)]);
        ClientGame Client(ModMode mode, out LocalGame local)
        {
            local = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = new GameModuleOptions { CallTimeoutMs = 1000 }, Seed = 1 }));
            return new ClientGame(content, local.Client, new ClientGameOptions { ModMode = mode, ModuleOptions = new GameModuleOptions { CallTimeoutMs = 1000 } });
        }
        using var clr = Client(ModMode.Clr, out var clrLocal);
        using var wasm = Client(ModMode.Wasm, out var wasmLocal);
        using (clrLocal)
        using (wasmLocal)
        {
            Assert.Equal(ModMode.Clr, Assert.Single(clr.Mods).Mode);
            Assert.Equal(ModMode.Wasm, Assert.Single(wasm.Mods).Mode);
            Assert.Equal(wasm.Mods[0].Manifest.Looks, clr.Mods[0].Manifest.Looks);
            Assert.NotEmpty(clr.Looks);
            Assert.Equal((wasm.Atlas.Width, wasm.Atlas.Height), (clr.Atlas.Width, clr.Atlas.Height));
            Assert.Equal(wasm.Atlas.Pixels, clr.Atlas.Pixels);
        }
    }
}
