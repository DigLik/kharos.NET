using System.Buffers.Binary;
using System.Text.Json;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Хост Н5: настройки партии поверх games.json, права приказов из сети, системы движка engine.*.</summary>
public sealed class HostApiTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void SettingsOverrideGameRecordAndSurviveSave()
    {
        using var host = HelloGame.Create(ModMode.Clr, settings: Json("""{ "size": 64, "fog": false, "rules": { "credits": 900 }, "land": { "seed": 11 } }"""));
        Assert.Equal(64, host.Game.Size);
        Assert.Equal(new MapBounds(-64, -64, 64, 64), host.Map);
        Assert.Null(host.Vision);
        Assert.Equal(11, host.Game.Land!.Seed);
        // Модам — запись целиком: своё из games.json (title) и настройки поверх.
        Assert.Equal("Привет", host.Game.Data.GetProperty("title").GetString());
        Assert.Equal(900, host.Game.Data.GetProperty("rules").GetProperty("credits").GetInt32());
        host.Start();
        host.Join(1);
        host.Step();
        var save = host.Save();

        using var loaded = Host.Load(HelloGame.Content(), save, new HostOptions { ModMode = ModMode.Clr });
        Assert.Equal(new MapBounds(-64, -64, 64, 64), loaded.Map);
        Assert.Equal(900, loaded.Game.Data.GetProperty("rules").GetProperty("credits").GetInt32());
        Assert.True(WorldSave.Save(host.World).AsSpan().SequenceEqual(WorldSave.Save(loaded.World)));
    }

    [Fact]
    public void LandByNameAndBadSettings()
    {
        using var host = HelloGame.Create(ModMode.Clr, settings: Json("""{ "land": "dunes" }"""));
        Assert.Equal(("dunes", 7), (host.Game.LandName, host.Game.Land!.Seed));
        Assert.Throws<HostException>(() => HelloGame.Create(ModMode.Clr, settings: Json("""{ "size": -3 }""")));
        Assert.Throws<HostException>(() => HelloGame.Create(ModMode.Clr, settings: Json("""{ "land": "nowhere" }""")));
        Assert.Throws<HostException>(() => HelloGame.Create(ModMode.Clr, settings: Json("[1]")));
    }

    [Fact]
    public void EngineSystemsAreOrderedAndReferable()
    {
        using var host = HelloGame.Create(ModMode.Clr);
        var names = host.Scheduler.Names;
        string[] engine = ["engine.paths", "engine.move", "engine.mounts", "engine.traces", "engine.vision", "engine.recallLand", "engine.recallPaving", "engine.memory"];
        Assert.Equal(engine, names.Where(name => name.StartsWith("engine.", StringComparison.Ordinal)));
        // Ссылка полным именем: система встаёт между системами движка.
        var specs = engine.Select(name => new SystemSpec { Name = name, Key = name, System = new DelegateSystem(_ => { }) }).ToList();
        specs.Insert(0, new SystemSpec { Name = "mod.after", After = ["engine.move"], Before = ["engine.mounts"], System = new DelegateSystem(_ => { }) });
        var order = new Scheduler(Fixtures.World(), specs).Names.ToList();
        Assert.Equal(order.IndexOf("engine.move") + 1, order.IndexOf("mod.after"));
    }

    [Fact]
    public void NetworkOrdersCarryRemoteAdminAndEditorRights()
    {
        using var host = HelloGame.Create(ModMode.Clr);
        host.Start();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0, AdminPassword = "ключ", ChatGap = TimeSpan.Zero });
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        var connection = session.Join(hostEnd, null, "Ева");
        var player = connection.Player;

        uint Ask()
        {
            clientEnd.Send(DeltaCodec.Order("hello.rights", []));
            session.Step();
            var rights = 0u;
            foreach (var signal in host.Scheduler.Signals)
                if (signal.Name == "hello.rights" && signal.To is [var to] && to == player)
                    rights = BinaryPrimitives.ReadUInt32LittleEndian(signal.Payload);
            while (clientEnd.TryReceive(out _))
            {
            }
            return rights;
        }

        Assert.Equal((uint)OrderRights.Remote, Ask());
        clientEnd.Send(Messages.Say("/admin ключ"));
        session.Step();
        Assert.Equal((uint)(OrderRights.Remote | OrderRights.Admin), Ask());
        clientEnd.Send(Messages.Say("/editor"));
        session.Step();
        Assert.True(connection.Editor);
        Assert.Equal((uint)(OrderRights.Remote | OrderRights.Admin | OrderRights.Editor), Ask());
        clientEnd.Send(Messages.Say("/logout"));
        session.Step();
        Assert.Equal((uint)OrderRights.Remote, Ask());
    }

    /// <summary>Мир по схеме движка без модов.</summary>
    private static class Fixtures
    {
        public static GameWorld World() => new(new Schema());
    }
}
