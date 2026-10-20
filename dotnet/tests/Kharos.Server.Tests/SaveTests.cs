using Kharos.Protocol;

namespace Kharos.Server.Tests;

/// <summary>Судьба файла сохранения: атомарная запись, чужая версия и мусор откладываются в сторону.</summary>
public class SaveTests
{
    [Fact]
    public void WriteIsAtomic()
    {
        using var temp = new Env.Temp();
        var path = temp.File("sub/world.kharos");
        Assert.Null(SaveFiles.Read(path));
        SaveFiles.Write(path, [1, 2, 3]);
        SaveFiles.Write(path, [4, 5, 6, 7]);
        // Временный файл подменил сохранение и исчез.
        Assert.Equal(["world.kharos"], Directory.GetFiles(temp.File("sub")).Select(Path.GetFileName));
        Assert.Equal([4, 5, 6, 7], SaveFiles.Read(path));
    }

    [Fact]
    public void OldFileSurvivesFailedWrite()
    {
        using var temp = new Env.Temp();
        var path = temp.File("world.kharos");
        SaveFiles.Write(path, [1, 2, 3]);
        // Упавшая запись (временный файл занят) не трогает прежнее сохранение.
        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.ThrowsAny<IOException>(() => SaveFiles.Write(path, [9, 9]));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void OtherVersionAndBrokenFilesAreMovedAside()
    {
        using var temp = new Env.Temp();
        var path = temp.File("world.kharos");
        var log = new List<string>();
        File.WriteAllBytes(path, [1]);
        SaveFiles.AsideVersion(path, 41, log.Add);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".v41"));
        Assert.Contains(log, line => line.Contains(".v41"));

        // Два негодных подряд не затирают друг друга, даже в одну миллисекунду.
        for (var i = 0; i < 2; i++)
        {
            File.WriteAllBytes(path, [2]);
            SaveFiles.AsideBroken(path, "мусор", log.Add);
        }
        Assert.Equal(2, Directory.GetFiles(temp.Path, "world.kharos.*.old").Length);
    }

    [Fact]
    public void HostRefusesOtherVersionFile()
    {
        var file = SaveFile.Pack([("META", [1, 2, 3])], SaveFile.Version + 1);
        var error = Assert.Throws<SaveVersionException>(() => SaveFile.Unpack(file));
        Assert.Equal((SaveFile.Version + 1, SaveFile.Version), (error.Found, error.Expected));
    }

    [Fact(DisplayName = "свои разделы вызывающего доходят, незнакомые не мешают")]
    public void CallerSectionsArriveAndUnknownOnesDoNotInterfere()
    {
        var file = SaveFile.Pack([("HOST", "{\"players\":{\"a\":1}}"u8.ToArray()), ("ZZZZ", "потом"u8.ToArray())]);
        var sections = SaveFile.Unpack(file);
        Assert.Equal("{\"players\":{\"a\":1}}", System.Text.Encoding.UTF8.GetString(sections["HOST"]));
        Assert.Equal("потом", System.Text.Encoding.UTF8.GetString(sections["ZZZZ"]));
    }

    // В TS «прежний JSON» читался отдельной ошибкой; в .NET прежнего формата нет: любой файл без KHSV — «не сохранение».
    [Fact(DisplayName = "не сохранение, прежний JSON и версия новее игры — ошибки")]
    public void NotASaveOldJsonAndNewerVersionAreErrors()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"tick\":1,\"units\":[]}");
        Assert.Contains("не сохранение", Assert.Throws<FormatException>(() => SaveFile.Unpack(json)).Message);
        Assert.Throws<FormatException>(() => SaveFile.Unpack([]));
        Assert.Throws<FormatException>(() => SaveFile.Unpack("KHS"u8));
        var sections = new[] { ("META", new byte[] { 1 }) };
        var older = Assert.Throws<SaveVersionException>(() => SaveFile.Unpack(SaveFile.Pack(sections, SaveFile.Version - 1)));
        Assert.Equal(SaveFile.Version - 1, older.Found);
        var newer = Assert.Throws<SaveVersionException>(() => SaveFile.Unpack(SaveFile.Pack(sections, SaveFile.Version + 1)));
        Assert.Equal((SaveFile.Version + 1, SaveFile.Version), (newer.Found, newer.Expected));
    }

    [Fact(DisplayName = "повреждённое тело — ошибка, а не мусорный мир")]
    public void CorruptBodyIsAnErrorNotAGarbageWorld()
    {
        var random = new Random(7);
        var data = new byte[20_000];
        random.NextBytes(data);
        var file = SaveFile.Pack([("META", data)]);
        Assert.ThrowsAny<Exception>(() => SaveFile.Unpack(file.AsSpan(0, file.Length >> 1)));
        var damaged = (byte[])file.Clone();
        for (var i = 6; i < damaged.Length; i += 7)
            damaged[i] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => SaveFile.Unpack(damaged));
    }
}
