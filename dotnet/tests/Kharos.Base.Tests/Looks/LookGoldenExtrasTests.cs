using System.Security.Cryptography;
using System.Text.Json;
using Base.Client;
using Kharos.Client.Game;
using Kharos.Client.Looks;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Base.Tests;

/// <summary>
/// Б-З без wasm: значки грузов и тревог, эффекты, следы, дропы, покрытие и портреты (Art/Icons.cs, Effects.cs, Paving.cs) —
/// потоком облика SDK через клиентский холст, кадр за кадром против art-more.json (TS). То же сверяет
/// LookGoldenMoreTests.EffectsIconsAndPavingMatchTsByteForByte на client.wasm.
/// </summary>
public sealed class LookGoldenExtrasTests
{
    private static string Hash(ReadOnlySpan<byte> pixels) => Convert.ToHexStringLower(SHA256.HashData(pixels))[..16];

    private static IReadOnlyList<Mount> MountsOf(string unit)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BaseGame.DotnetRoot, "mods", "base", "data", "units.json")));
        if (!document.RootElement.GetProperty(unit).TryGetProperty("mounts", out var list))
            return [];
        return [.. list.EnumerateArray().Select(m => new Mount(m.GetProperty("turret").GetString()!, m.GetProperty("along").GetDouble(), m.GetProperty("across").GetDouble()))];
    }

    [Fact]
    public void ExtraLooksMatchTsByteForByte()
    {
        var golden = GoldenMore.Load();
        var built = ArtExtras.All(MountsOf).ToDictionary(l => l.Name, l => Look.Build(0, LookStream.Decode(l.Spec.Encode())), StringComparer.Ordinal);
        var problems = new List<string>();
        var covered = 0;
        foreach (var (name, expected) in golden.Frames)
        {
            if (name.StartsWith("buildings/", StringComparison.Ordinal))
                continue;
            var hashes = expected.FrameHashes;
            List<byte[]> images;
            if (expected.Sizes is not null)
            {
                // Кадры разных размеров лежат облик на размер: decals/scorch/<сторона>.
                images = [];
                foreach (var side in expected.Sizes.Split(' ').Select(s => s.Split('x')[0]).Distinct())
                    images.AddRange(built[$"{name}/{side}"].Images);
            }
            else if (name.StartsWith("portraits/", StringComparison.Ordinal))
            {
                var look = built[name];
                var (pixels, width, height) = look.Portrait ?? throw new InvalidOperationException($"{name}: нет портрета");
                if ((width, height, Hash(pixels)) != (expected.Portrait!.Width, expected.Portrait.Height, expected.Portrait.Hash))
                    problems.Add($"{name}: портрет {width}×{height} не совпал с TS {expected.Portrait.Width}×{expected.Portrait.Height}");
                covered++;
                continue;
            }
            else
                images = built.TryGetValue(name, out var found) ? found.Images : throw new InvalidOperationException($"{name}: облика нет");
            if (images.Count != hashes.Length)
            {
                problems.Add($"{name}: кадров {images.Count}, в TS {hashes.Length}");
                continue;
            }
            for (var i = 0; i < hashes.Length; i++)
                if (Hash(images[i]) != hashes[i])
                    problems.Add($"{name}: кадр {i} не совпал с TS");
            covered += hashes.Length;
        }
        Assert.Empty(problems);
        TestContext.Current.SendDiagnosticMessage($"Б-З: {covered} кадров и портретов совпали с TS до байта");
    }
}
