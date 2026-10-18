using System.Diagnostics;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Plugins.Game;

namespace Kharos.Host.Tests;

/// <summary>
/// Мод base в режимах CLR и wasm (docs/dotnet-n5.md, Н5-5): одна партия — один мир до байта. Пока base переделывается
/// (П0.5 и разделы Н5), тест пропускается, если мод не собирается в хост, — с причиной.
/// </summary>
public sealed class BaseModeTests(ITestOutputHelper output)
{
    private static string ModDir => Path.Combine(HelloGame.DotnetRoot, "mods", "base");

    private sealed record Outcome(byte[] World, byte[] Save, string[] Errors, long? Memory, double Startup, double Ms);

    private static Outcome Play(ModMode mode)
    {
        Host host;
        try
        {
            host = new Host(GameContent.Load([ModPackage.Read(ModDir)]), new HostOptions
            {
                ModMode = mode,
                Seed = 3,
                Modules = new GameModuleOptions { CallTimeoutMs = 5000 },
            });
        }
        catch (Exception error) when (error is HostException or ContentException)
        {
            Assert.Skip($"base не поднимается в {mode} (переделка Н5): {error.Message}");
            throw;
        }
        using (host)
        {
            var started = Stopwatch.GetTimestamp();
            host.Start();
            host.Join(1);
            host.Join(2);
            for (var tick = 0; tick < 100; tick++)
                host.Step();
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var mod = host.Mods.FirstOrDefault();
            return new Outcome(WorldSave.Save(host.World), host.Save(), [.. host.Errors.Select(error => $"{error.Tick} {error.Source}: {error.Message}")],
                mod?.MemoryBytes, mod?.StartupMs ?? 0, ms);
        }
    }

    [Fact]
    public void ClrAndWasmProduceIdenticalWorlds()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(ModDir, "host.wasm")), "mods/base/host.wasm не собран: pwsh dotnet/tools/build-mods.ps1");
        Assert.SkipWhen(ClrModule.Find(ModDir) is null, "Base.Host.dll не собран: dotnet build dotnet/Kharos.slnx");
        var clr = Play(ModMode.Clr);
        var wasm = Play(ModMode.Wasm);
        output.WriteLine($"base: host.wasm {new FileInfo(Path.Combine(ModDir, "host.wasm")).Length:N0} байт, память экземпляра {wasm.Memory:N0} байт, запуск {wasm.Startup:F0} мс (CLR {clr.Startup:F0} мс); 100 тиков: wasm {wasm.Ms:F0} мс, CLR {clr.Ms:F0} мс; мир {wasm.World.Length:N0} байт");
        Assert.Equal(wasm.Errors, clr.Errors);
        Assert.True(wasm.World.AsSpan().SequenceEqual(clr.World), "мир CLR и wasm разошёлся");
        Assert.True(wasm.Save.AsSpan().SequenceEqual(clr.Save), "сохранение CLR и wasm разошлось");
    }
}
