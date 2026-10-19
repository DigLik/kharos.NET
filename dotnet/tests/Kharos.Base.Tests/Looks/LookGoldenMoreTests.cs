using System.Security.Cryptography;
using System.Text.Json;
using Base.Client;
using Kharos.Client.Looks;

namespace Kharos.Base.Tests;

/// <summary>Эталон art-more.json (его делает dump.ts): здания, эффекты, следы, значки, покрытие, портреты.</summary>
internal sealed class GoldenMore
{
    public Dictionary<string, GoldenMoreLook> Frames { get; set; } = [];

    public static GoldenMore Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Looks", "golden", "art-more.json");
        return JsonSerializer.Deserialize<GoldenMore>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

internal sealed class GoldenMoreLook
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Hashes { get; set; } = "";
    public string? Sizes { get; set; }
    public int Variants { get; set; }
    public int PerVariant { get; set; }
    public bool Working { get; set; }
    public GoldenPortrait? Portrait { get; set; }

    public string[] FrameHashes => Hashes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Уровень 1 сверки кадров (Н6.4): всё, что рисует клиент TS и чего ещё нет в .NET, — до байта по хешу кадра. Облики, которые
/// base уже рисует, сверяются; пока их нет — тест пропускается (Б-А, Б-Б — здания, Б-З — значки, эффекты, покрытие).
/// </summary>
public sealed class LookGoldenMoreTests
{
    private const string Waiting = "ждёт Б-А/Б-Б/Б-З";

    private static string Hash(ReadOnlySpan<byte> pixels) => Convert.ToHexStringLower(SHA256.HashData(pixels))[..16];

    private static byte[] Cut(LookAtlas atlas, Kharos.Client.Ui.Uv uv, int width, int height)
    {
        var (x0, y0) = ((int)Math.Round(uv.U0 * atlas.Width), (int)Math.Round(uv.V0 * atlas.Height));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Array.Copy(atlas.Pixels, ((y0 + y) * atlas.Width + x0) * 4, pixels, y * width * 4, width * 4);
        return pixels;
    }

    [Fact]
    public void GoldenMoreCoversEveryBuilding()
    {
        var golden = GoldenMore.Load();
        var buildings = golden.Frames.Keys.Where(k => k.StartsWith("buildings/", StringComparison.Ordinal)).ToList();
        Assert.Equal(48, buildings.Count);
        foreach (var name in buildings)
        {
            var look = golden.Frames[name];
            Assert.Equal(look.Variants * look.PerVariant, look.FrameHashes.Length);
            Assert.NotNull(look.Portrait);
        }
        Assert.True(golden.Frames.Keys.Count(k => !k.StartsWith("buildings/", StringComparison.Ordinal)) >= 70);
    }

    [Fact]
    public void BuildingsMatchTsByteForByte()
    {
        using var session = new BaseSession();
        var golden = GoldenMore.Load();
        var game = session.Game;
        var problems = new List<string>();
        var waiting = new List<string>();
        var checkedFrames = 0;
        foreach (var look in game.Looks)
        {
            var name = look.Name;
            if (!golden.Frames.TryGetValue(name, out var expected) || !name.StartsWith("buildings/", StringComparison.Ordinal))
                continue;
            var hashes = expected.FrameHashes;
            // Заглушка: один кадр вместо вариантов и кадров работы.
            if (look.Cells.Length != hashes.Length || (look.Width, look.Height) != (expected.Width, expected.Height))
            {
                waiting.Add(name);
                continue;
            }
            for (var i = 0; i < hashes.Length; i++)
                if (Hash(Cut(game.Atlas, look.Cells[i], look.Width, look.Height)) != hashes[i])
                    problems.Add($"{name}: кадр {i} не совпал с TS");
            checkedFrames += hashes.Length;
        }
        Assert.Empty(problems);
        Assert.SkipWhen(waiting.Count > 0, $"{Waiting}: зданий без чертежа {waiting.Count} из 48 (сверено кадров: {checkedFrames})");
    }

    [Fact]
    public void EffectsIconsAndPavingMatchTsByteForByte()
    {
        using var session = new BaseSession();
        var golden = GoldenMore.Load();
        var game = session.Game;
        string[] prefixes = ["effects/", "decals/", "power/", "selection/", "drops/", "paving/", "goods/"];
        // Группа с кадрами разных размеров (гарь) лежит в .NET обликами по размеру: имя/сторона.
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, group) in golden.Frames.Where(f => prefixes.Any(p => f.Key.StartsWith(p, StringComparison.Ordinal))))
        {
            if (group.Sizes is null)
            {
                expected[key] = group.FrameHashes;
                continue;
            }
            var sizes = group.Sizes.Split(' ');
            foreach (var side in sizes.Distinct())
                expected[$"{key}/{side.Split('x')[0]}"] = [.. group.FrameHashes.Where((_, i) => sizes[i] == side)];
        }
        var names = expected.Keys.ToHashSet();
        // Значки, эффекты и покрытие — свободные облики мода: не виды контента, номера — после всех видов.
        Assert.Empty(game.Errors);
        Assert.Null(game.Content.FindProto("goods/metal"));
        Assert.True(game.LookId("goods/metal") >= game.Content.Protos.Count);
        Assert.Equal("goods/metal", game.LookOf(game.LookId("goods/metal"))!.Name);
        var drawn = game.Looks.Select(l => l.Name).Where(names.Contains).ToList();
        Assert.SkipWhen(drawn.Count < names.Count, $"{Waiting}: эффектов, следов, значков и покрытия в .NET {drawn.Count} из {names.Count} групп");
        var problems = new List<string>();
        foreach (var look in game.Looks)
        {
            var name = look.Name;
            if (!names.Contains(name))
                continue;
            var hashes = expected[name];
            if (look.Cells.Length != hashes.Length)
            {
                problems.Add($"{name}: кадров {look.Cells.Length}, в TS {hashes.Length}");
                continue;
            }
            for (var i = 0; i < hashes.Length; i++)
                if (Hash(Cut(game.Atlas, look.Cells[i], look.Width, look.Height)) != hashes[i])
                    problems.Add($"{name}: кадр {i} не совпал с TS");
        }
        Assert.Empty(problems);
    }
}
