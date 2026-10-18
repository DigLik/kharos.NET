using System.Diagnostics;
using Kharos.Client.Game;
using Kharos.Client.Net;
using Kharos.Client.Tests.Game;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Plugins.Game;
using Kharos.Protocol;
using HostGame = Kharos.Host.Host;

namespace Kharos.Client.Tests.Net;

/// <summary>Доставка модов: клиент с пустым кэшем получает пакеты хоста (без host.wasm), собирает из них партию и играет.</summary>
public sealed class ContentDeliveryTests
{
    private static string ModDir(string name) => Path.GetFullPath(Path.Combine(HelloSession.ModDir, "..", name));

    [Theory]
    [InlineData("hello", 1024 * 1024)]
    [InlineData("base", 0, Skip = "ждёт Б1: старт партии base на модели TS")]
    public void EmptyCacheDownloadsModsAndGameRuns(string name, int sendRate)
    {
        var dir = ModDir(name);
        Assert.SkipUnless(File.Exists(Path.Combine(dir, "host.wasm")) && File.Exists(Path.Combine(dir, "client.wasm")), $"mods/{name} не собран: pwsh dotnet/tools/build-mods.ps1");
        var content = GameContent.Load([ModPackage.Read(dir)]);
        var modules = new GameModuleOptions { CallTimeoutMs = 1000 };
        var cache = Path.Combine(Path.GetTempPath(), $"kharos-cache-{Guid.NewGuid():n}");
        try
        {
            using (var local = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = modules, Seed = 1 }), session: new HostSessionOptions { SendRate = sendRate, Local = true }))
            {
                var clock = Stopwatch.StartNew();
                var connector = GameConnector.Connect(local.Client, new ContentCache(cache), TimeSpan.FromSeconds(60));
                Assert.Equal((1, 0), (connector.Downloaded, connector.FromCache));
                Assert.Equal(connector.Expected, connector.Received);
                Assert.Equal(content.Checksum, connector.Content!.Checksum);
                var package = Assert.Single(connector.Content.Mods);
                Assert.Null(package.HostWasm);
                Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "client.wasm")), package.ClientWasm);
                Assert.Single(Directory.GetFiles(cache, "*.khmp"));
                if (sendRate > 0)
                {
                    // Пакет шёл кусками по бюджету, а не разом.
                    Assert.True(clock.Elapsed.TotalSeconds > connector.Expected / (double)sendRate * 0.5, $"{connector.Expected} байт за {clock.Elapsed.TotalSeconds:0.00} с");
                }

                var log = new List<string>();
                using var game = new ClientGame(connector, new ClientGameOptions { ModuleOptions = modules, Log = log.Add });
                Assert.Single(game.Mods);
                var input = new ClientInput { Width = 1280, Height = 720 };
                var owned = 0;
                for (var frame = 0; frame < 600 && owned == 0; frame++)
                {
                    game.Step(input with { Time = clock.Elapsed.TotalSeconds });
                    var world = game.Replica.World!;
                    owned = world.Store(Builtin.Owner).Column(0).ToArray().Count(player => player == game.Replica.Player);
                    Thread.Sleep(10);
                }
                Assert.True(owned > 0, $"своих сущностей нет; журнал: {string.Join(" | ", log.TakeLast(5))}");
                Assert.True(game.Stats.Sprites > 0 || name == "base", "кадр без спрайтов");
                Assert.DoesNotContain(game.Mods, mod => mod.Disabled);
            }

            // Второй вход — из кэша, без скачивания.
            using var again = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = modules, Seed = 1 }));
            var cached = GameConnector.Connect(again.Client, new ContentCache(cache), TimeSpan.FromSeconds(60));
            Assert.Equal((0, 1), (cached.Downloaded, cached.FromCache));
            Assert.Equal(content.Checksum, cached.Content!.Checksum);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                foreach (var file in Directory.GetFiles(cache))
                    File.Delete(file);
                Directory.Delete(cache);
            }
        }
    }

    [Fact]
    public void LocalModsWithSameHashAreNotDownloaded()
    {
        Assert.SkipUnless(HelloSession.Built, HelloSession.NotBuilt);
        var package = ModPackage.Read(HelloSession.ModDir);
        var content = GameContent.Load([package]);
        using var local = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = new GameModuleOptions { CallTimeoutMs = 1000 }, Seed = 1 }));
        var connector = GameConnector.Connect(local.Client, cache: null, TimeSpan.FromSeconds(30), local: [package]);
        Assert.Equal((0, 0, 0L), (connector.Downloaded, connector.FromCache, connector.Expected));
        Assert.Same(package, connector.Content!.Mods[0]);
    }

    [Fact]
    public void RefusalReachesTheClient()
    {
        var (host, client) = MemoryTransport.Pair();
        host.Send(Messages.Refused("Неверный пароль", password: true));
        host.Dispose();
        var connector = new GameConnector(client, cache: null);
        var error = Assert.Throws<ClientException>(() => connector.Wait(TimeSpan.FromSeconds(5)));
        Assert.Contains("Неверный пароль", error.Message);
        Assert.True(connector.PasswordNeeded);

        (host, client) = MemoryTransport.Pair();
        host.Send(Messages.Refused("Вас отключили"));
        host.Dispose();
        Assert.SkipUnless(HelloSession.Built, HelloSession.NotBuilt);
        var content = GameContent.Load([ModPackage.Read(HelloSession.ModDir)]);
        error = Assert.Throws<ClientException>(() => new ClientGame(content, client));
        Assert.Contains("Вас отключили", error.Message);
    }
}
