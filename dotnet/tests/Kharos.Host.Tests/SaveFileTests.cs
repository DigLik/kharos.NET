using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Файл сохранения хоста целиком (packages/tests/saveFile.test.ts): мир переживает файл. Остальное — Kharos.Server.Tests/SaveTests.</summary>
public sealed class SaveFileTests
{
    [Fact(DisplayName = "мир переживает файл без потерь, и из файла продолжается тот же")]
    public void WorldSurvivesFileAndContinuesTheSame()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using var host = HelloGame.Create(seed: 5);
        host.Start();
        host.Join(1);
        for (var tick = 0; tick < 10; tick++)
            host.Step();
        var file = host.Save();

        using var loaded = Host.Load(HelloGame.Content(), file, new HostOptions { Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 } });
        Assert.Equal(WorldSave.Save(host.World), WorldSave.Save(loaded.World));
        // Из файла продолжается тот же, и туман тоже — через пересчёт обзора (раз в Vision.Ticks тиков): сохранение
        // загруженного — те же байты.
        for (var tick = 0; tick < 2 * Vision.Ticks + 1; tick++)
        {
            host.Step();
            loaded.Step();
            Assert.True(host.Vision!.Cells(1).SequenceEqual(loaded.Vision!.Cells(1)), $"обзор разошёлся на тике {host.Tick}");
        }
        Assert.Equal(WorldSave.Save(host.World), WorldSave.Save(loaded.World));
        var before = SaveFile.Unpack(host.Save());
        var after = SaveFile.Unpack(loaded.Save());
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        // Все разделы те же, и слои тоже: обзор на тик сохранения пересчитывают оба (Host.Save и загрузка).
        foreach (var (tag, data) in before)
            Assert.True(data.AsSpan().SequenceEqual(after[tag]), $"раздел {tag} после загрузки другой");
    }
}
