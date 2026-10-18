using System.Buffers.Binary;
using Kharos.Plugins.Game;

namespace Kharos.Plugins.Tests;

/// <summary>
/// Клиентский мод hello (mods/hello/client.wasm) в рантайме игровых модов: свои экспорты (kh_look, kh_bank, kh_frame)
/// вместо систем и приказов, манифест с обликами и звуками. Без собранного client.wasm тесты пропускаются.
/// </summary>
public class ClientModTests
{
    private static string ClientWasm => Path.Combine(HelloModule.DotnetRoot, "mods", "hello", "client.wasm");

    private static (GameModuleHost Host, GameModule Module) Load()
    {
        Assert.SkipUnless(File.Exists(ClientWasm), "mods/hello/client.wasm не собран: pwsh dotnet/tools/build-mods.ps1 -Mod hello");
        var host = new GameModuleHost();
        var info = HelloModule.LoadInfo();
        info.Side = "client";
        return (host, host.LoadFile("hello", ClientWasm, info));
    }

    [Fact]
    public void DeclaresLooksSoundsAndClientExports()
    {
        var (host, module) = Load();
        using var _ = host;
        using var __ = module;
        using var instance = module.CreateInstance();

        Assert.True(instance.IsClient);
        Assert.Equal(["units/rover"], instance.Manifest.Looks);
        var blip = Assert.Single(instance.Manifest.Bank);
        Assert.Equal(("hello:blip", "interface", 2), (blip.Name, blip.Bus, blip.Variants));
        Assert.Equal("hello:chime", Assert.Single(instance.Manifest.Voices).Name);
        Assert.Empty(instance.Manifest.Systems);
        Assert.All(module.Imports, i => Assert.Contains(i, GameModuleHost.AllowedImports));

        // Облик: 32 × 32, 64 направления × 2 фазы — поток команд холста.
        var look = instance.Look(0);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(look));
        Assert.True(look.Length > 10_000, $"облик — {look.Length} байт");
        Assert.Equal(-1, Assert.Throws<ModCallException>(() => instance.Look(5)).Code);

        // Запись банка: моно, 1/12 секунды.
        var sample = instance.Bank(0, 1, 48000);
        Assert.Equal(1, sample[0]);
        Assert.Equal(4000u, BinaryPrimitives.ReadUInt32LittleEndian(sample[1..]));
        Assert.Equal(1 + 4 + 4000 * 4, sample.Length);

        // Экспортов хоста у клиентского мода нет; кадр с неверным входом — код ошибки, экземпляр годен.
        Assert.Throws<ModAbiException>(() => instance.RaiseEvent(0, [], default));
        Assert.Equal(-4, Assert.Throws<ModCallException>(() => instance.Frame([1, 2, 3])).Code);
        Assert.False(instance.Faulted);
    }

    [Fact]
    public void HostModHasNoClientExports()
    {
        Assert.SkipUnless(HelloModule.Built, HelloModule.NotBuilt);
        using var host = new GameModuleHost();
        using var module = host.LoadFile("hello", HelloModule.WasmPath, HelloModule.LoadInfo());
        using var instance = module.CreateInstance();
        Assert.False(instance.IsClient);
        Assert.Empty(instance.Manifest.Looks);
        Assert.Throws<ModAbiException>(() => instance.Look(0));
        Assert.Throws<ModAbiException>(() => instance.Frame([]));
    }
}
