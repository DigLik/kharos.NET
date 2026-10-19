using System.Text;
using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Host;
using Kharos.Protocol;

namespace Kharos.Client.Tests.Shell;

/// <summary>
/// Порт packages/tests/storage.test.ts: слоты сохранений оболочки поверх хранилища клиента. Мир — файл сохранения
/// хоста («KHSV») с разделом META, как его пишет <see cref="HostSave"/>: на сторону 256 и местность по умолчанию.
/// </summary>
public sealed class StorageTests
{
    /// <summary>Сохранение мира 256² с генератором по умолчанию (зерно 1337), как createSim(...).save() у TS.</summary>
    private static byte[] Save(ulong tick = 40) => new HostSave
    {
        Game = "skirmish", Seed = 3, Dt = 0.05, Tick = tick, World = [1, 2, 3], Layers = [], Players = [0, 0],
        Settings = Encoding.UTF8.GetBytes("""{"size":256,"land":{"seed":1337}}"""),
    }.Encode();

    private static readonly long DefaultSeed = new Kharos.Engine.Terrain.GeneratorConfig().Seed;

    [Fact(DisplayName = "слоты: новый пуст, запись описывает его, удалённый не воскресает")]
    public void SlotsNewEmptyWriteDescribesDeletedStaysDeleted()
    {
        var slots = new SaveSlots(new MemoryStorage());
        var slot = slots.Create("Первая", 256, 7);
        Assert.Equal(["Первая"], slots.List().Select(other => other.Name));
        Assert.Null(slots.Load(slot.Id));
        Assert.True(slots.List()[0].Empty);

        Assert.True(slots.Store(slot.Id, Save()));
        Assert.Equal(256, SaveInfo.Read(slots.Load(slot.Id)!).Map().Size);
        Assert.Equal(DefaultSeed, slots.List()[0].Seed);
        Assert.Equal(40UL, slots.List()[0].Tick);
        Assert.False(slots.List()[0].Empty);
        slots.StoreCamera(slot.Id, new CameraSave(1, 2, 32));
        Assert.Equal(new CameraSave(1, 2, 32), slots.LoadCamera(slot.Id));

        slots.Delete(slot.Id);
        Assert.False(slots.Store(slot.Id, Save()));
        Assert.Empty(slots.List());
        Assert.Null(slots.Load(slot.Id));
        Assert.Null(slots.LoadCamera(slot.Id));
    }

    [Fact(DisplayName = "сохранения прежнего формата (JSON) не переносятся: их слоты пропадают, пустые слоты остаются")]
    public void JsonSavesAreDroppedEmptySlotsStay()
    {
        var storage = new MemoryStorage();
        static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);
        storage.Set(SaveSlots.LegacyKey, Json("""{"version":19,"tick":5}"""));
        storage.Set(SaveSlots.LegacyCameraKey, Json("""{"x":5,"y":6,"zoom":20}"""));
        storage.Set(SaveSlots.IndexKey, Json("""
            [
              { "id": "old", "name": "Старая", "created": 1, "updated": 1, "tick": 5, "size": 256, "seed": 1 },
              { "id": "empty", "name": "Пустая", "created": 1, "updated": 1, "tick": 0, "size": 256, "seed": 1 }
            ]
            """));
        storage.Set(SaveSlots.WorldKey("old"), Json("""{"version":19,"tick":5}"""));
        storage.Set(SaveSlots.CameraKey("old"), Json("""{"x":1,"y":1,"zoom":20}"""));

        var slots = new SaveSlots(storage);
        Assert.Equal(["empty"], slots.List().Select(slot => slot.Id));
        Assert.Equal([SaveSlots.IndexKey], storage.Keys.Order());
    }

    [Fact(DisplayName = "файл сохранения переносится в новый слот, чужой файл — ошибка")]
    public void SaveFileImportsIntoNewSlotForeignFileFails()
    {
        var slots = new SaveSlots(new MemoryStorage());
        var slot = slots.Create("Перенос", 256, 1);
        slots.Store(slot.Id, Save());
        var file = slots.Export(slot.Id);
        Assert.NotNull(file);
        var copy = slots.Import(file);
        Assert.NotEqual(slot.Id, copy.Id);
        Assert.Equal("Перенос", copy.Name);
        Assert.Equal(SaveFile.Unpack(slots.Load(slot.Id)!), SaveFile.Unpack(slots.Load(copy.Id)!));
        Assert.ThrowsAny<FormatException>(() => slots.Import(Encoding.UTF8.GetBytes("""{"version":19}""")));
    }

    [Fact]
    public void FileStorageKeepsKeysAsFilesAndListsThemBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "kharos-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileStorage(root);
            storage.Set("prefs/base/steps", [1, 2]);
            storage.Set("slots/ab/world", [3]);
            storage.Set("weird/ключ с пробелом/..", [4]);
            storage.Set("engine/audio/volume", [5]);
            storage.Set("engine/audio/volume", []);
            Assert.Equal([1, 2], storage.Get("prefs/base/steps"));
            Assert.Equal([4], storage.Get("weird/ключ с пробелом/.."));
            Assert.Null(storage.Get("engine/audio/volume"));
            Assert.Null(storage.Get("nothing/here"));
            Assert.Equal(["prefs/base/steps", "slots/ab/world", "weird/ключ с пробелом/.."], storage.Keys.Order(StringComparer.Ordinal));
            // Ключ не выходит из папки хранилища.
            Assert.StartsWith(storage.Root, Path.GetFullPath(storage.PathOf("../../x")));

            // Слоты на файлах — те же правила.
            var slots = new SaveSlots(new FileStorage(root));
            var slot = slots.Create("На диске", 256, 9);
            slots.Store(slot.Id, Save(80));
            var again = new SaveSlots(new FileStorage(root)).List().Single();
            Assert.Equal(("На диске", 80UL), (again.Name, again.Tick));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrefsRememberLastGameAndRecentServers()
    {
        var storage = new MemoryStorage();
        var prefs = new ShellPrefs(storage);
        var slots = new SaveSlots(storage);
        Assert.Null(prefs.Last(slots));
        var slot = slots.Create("Последняя", 512, 3);
        prefs.RememberLast(new SlotLaunch(slot), 10);
        Assert.Equal(slot.Id, Assert.IsType<SlotLaunch>(prefs.Last(slots)).Slot.Id);
        // Удалённый слот не подменяется другим: продолжать нечего.
        slots.Delete(slot.Id);
        Assert.Null(prefs.Last(slots));

        prefs.RememberLast(new ServerLaunch("ws://a:1", "Ник"), 20);
        prefs.RememberLast(new ServerLaunch("ws://b:2", "Ник"), 30);
        prefs.RememberLast(new ServerLaunch("ws://a:1", "Другой", "секрет"), 40);
        Assert.Equal(["ws://a:1", "ws://b:2"], prefs.Servers().Select(server => server.Url));
        Assert.Equal(new ServerLaunch("ws://a:1", "Другой"), prefs.Last(slots));
        Assert.DoesNotContain("секрет", Encoding.UTF8.GetString(storage.Get(ShellPrefs.LastKey)!));

        prefs.SetLang("de");
        prefs.SetKey(ShellKey.Menu, "f11");
        var again = new ShellPrefs(storage);
        Assert.Equal("de", again.Lang);
        Assert.Equal("f11", again.KeyOf(ShellKey.Menu));
        // Клавиша, занятая другим действием, меняется с ним местами.
        Assert.Equal("f10", again.KeyOf(ShellKey.Fullscreen));
    }
}
