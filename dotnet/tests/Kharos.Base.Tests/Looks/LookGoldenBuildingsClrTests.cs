using System.Security.Cryptography;
using Base.Client;
using Kharos.Client.Game;
using Kharos.Client.Looks;

namespace Kharos.Base.Tests;

/// <summary>
/// Здания (Б-А, Б-Б) по эталону TS art-more.json на CLR, без client.wasm: поток облика чертежа → растеризация клиента →
/// хеш кадра и портрета. Виды, у которых ещё заглушка (кадров не столько, сколько в TS), пропускаются.
/// </summary>
public sealed class LookGoldenBuildingsClrTests
{
    private static string Hash(ReadOnlySpan<byte> pixels) => Convert.ToHexStringLower(SHA256.HashData(pixels))[..16];

    /// <summary>Основание вида в тайлах по размеру кадра эталона: ширина − 32 пикселя поля, на 16.</summary>
    private static BuildingInfo InfoOf(GoldenMoreLook golden) => new((golden.Width - 32) / 16, (golden.Height - 32) / 16, BuildingKit.Role.Storage);

    private static Look Build(string name, GoldenMoreLook golden) =>
        Look.Build(0, LookStream.Decode(BuildingArt.Look(name, InfoOf(golden)).Encode()));

    [Fact]
    public void BuildingsDrawnByClrMatchTs()
    {
        var golden = GoldenMore.Load();
        var problems = new List<string>();
        var done = 0;
        var frames = 0;
        foreach (var name in BuildingArt.Names)
        {
            var expected = golden.Frames["buildings/" + name];
            var look = Build(name, expected);
            var hashes = expected.FrameHashes;
            if (look.Images.Count != hashes.Length || (look.Width, look.Height) != (expected.Width, expected.Height))
                continue;
            done++;
            frames += hashes.Length;
            for (var i = 0; i < hashes.Length; i++)
                if (Hash(look.Images[i]) != hashes[i])
                    problems.Add($"{name}: кадр {i} (вариант {i / expected.PerVariant}, кадр {i % expected.PerVariant}) не совпал с TS");
            if (look.Portrait is not { } portrait)
                problems.Add($"{name}: нет портрета");
            else if ((portrait.Width, portrait.Height) != (expected.Portrait!.Width, expected.Portrait.Height) || Hash(portrait.Pixels) != expected.Portrait.Hash)
                problems.Add($"{name}: портрет {portrait.Width}×{portrait.Height} не совпал с TS {expected.Portrait.Width}×{expected.Portrait.Height}");
        }
        TestContext.Current.SendDiagnosticMessage($"здания на CLR: {done} из {BuildingArt.Names.Length} видов, {frames} кадров сверено с TS");
        Assert.Empty(problems);
        Assert.SkipWhen(done < BuildingArt.Names.Length, $"ждёт Б-А/Б-Б: чертежей {done} из {BuildingArt.Names.Length}");
    }
}
