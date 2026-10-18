using Kharos.Protocol;

namespace Kharos.Server.Tests;

/// <summary>Файл сохранения и реестр игроков (швы, которые заменит новый хост).</summary>
public class SaveTests
{
    private static SaveSnapshot Snapshot(byte fill = 7) =>
        new(Welcome.ProtocolVersion, 1234, [new PlayerRecord("alpha", 1, "Альфа"), new PlayerRecord("beta", 2, "")], Enumerable.Repeat(fill, 1000).ToArray());

    [Fact]
    public void WriteIsAtomicAndRoundTrips()
    {
        using var temp = new Env.Temp();
        var store = new FileSaveStore(temp.File("sub/world.kharos"));
        Assert.Null(store.Load(_ => { }));
        store.Write(Snapshot(1));
        store.Write(Snapshot(2));
        // Временный файл подменил сохранение и исчез.
        Assert.Equal(["world.kharos"], Directory.GetFiles(temp.File("sub")).Select(Path.GetFileName));
        var loaded = store.Load(_ => { })!;
        Assert.Equal(1234UL, loaded.Tick);
        Assert.Equal(Snapshot().Players, loaded.Players);
        Assert.Equal(Snapshot(2).World, loaded.World);
    }

    [Fact]
    public void OldFileSurvivesFailedWrite()
    {
        using var temp = new Env.Temp();
        var store = new FileSaveStore(temp.File("world.kharos"));
        store.Write(Snapshot(1));
        var before = File.ReadAllBytes(store.Path);
        // Упавшая запись (временный файл занят) не трогает прежнее сохранение.
        using (new FileStream(store.Path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.Write(Snapshot(9)));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void OtherVersionIsMovedAside()
    {
        using var temp = new Env.Temp();
        var store = new FileSaveStore(temp.File("world.kharos"));
        store.Write(Snapshot() with { Version = 41 });
        var log = new List<string>();
        Assert.Null(store.Load(log.Add));
        Assert.False(File.Exists(store.Path));
        Assert.True(File.Exists(store.Path + ".v41"));
        Assert.Contains(log, line => line.Contains(".v41"));
    }

    [Fact]
    public void UnreadableFileIsMovedAside()
    {
        using var temp = new Env.Temp();
        var store = new FileSaveStore(temp.File("world.kharos"));
        File.WriteAllText(store.Path, "{\"прежний\": \"JSON\"}");
        var log = new List<string>();
        Assert.Null(store.Load(log.Add));
        Assert.False(File.Exists(store.Path));
        Assert.Single(Directory.GetFiles(temp.Path, "world.kharos.*.old"));

        // Обрезанный файл — тоже.
        store.Write(Snapshot());
        var bytes = File.ReadAllBytes(store.Path);
        File.WriteAllBytes(store.Path, bytes[..(bytes.Length - 10)]);
        Assert.Null(store.Load(log.Add));
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*.old").Length);
    }

    [Fact]
    public void RegistryKeepsPlayersAcrossRestarts()
    {
        var registry = new PlayerRegistry();
        Assert.False(registry.Known("alpha"));
        Assert.Equal(1u, registry.Join("alpha", "Альфа"));
        Assert.True(registry.Known("alpha"));
        Assert.Equal(2u, registry.Join("beta", null));
        Assert.Equal(1u, registry.Join("alpha", null));
        // Без id — каждый раз новый номер.
        Assert.Equal(3u, registry.Join(null, "аноним"));
        Assert.Equal(4u, registry.Join("", null));
        Assert.Equal("Альфа", registry.NameOf(1));
        Assert.Equal("Игрок 2", registry.NameOf(2));

        var restored = new PlayerRegistry();
        restored.Restore(registry.Players);
        Assert.Equal(1u, restored.Join("alpha", null));
        Assert.Equal(5u, restored.Join("gamma", null));
    }
}
