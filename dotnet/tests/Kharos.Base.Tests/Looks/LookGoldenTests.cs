using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Base.Client;
using Kharos.Client.Game;
using Kharos.Client.Looks;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Base.Tests;

/// <summary>Эталон графики (golden/art.json, его делает dump.ts из TS-клиента): хеш на кадр и образцы.</summary>
internal sealed class GoldenArt
{
    public Dictionary<string, GoldenLook> Frames { get; set; } = [];

    public static GoldenArt Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Looks", "golden", "art.json");
        return JsonSerializer.Deserialize<GoldenArt>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

internal sealed class GoldenLook
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Hashes { get; set; } = "";
    public GoldenSample? Sample { get; set; }
    public double[][][]? Lights { get; set; }
    public GoldenPortrait? Portrait { get; set; }
    public string? Gait { get; set; }
    public double[][]? Lamps { get; set; }
    public double[]? Beam { get; set; }
    public GoldenTrace? Trace { get; set; }

    [JsonIgnore]
    public string[] FrameHashes => Hashes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

internal sealed class GoldenSample
{
    public int Index { get; set; }
    public string Rgba { get; set; } = "";
}

internal sealed class GoldenPortrait
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Hash { get; set; } = "";
}

internal sealed class GoldenTrace
{
    public double[] Sides { get; set; } = [];
    public double Width { get; set; }
}

/// <summary>
/// Пиксельная сверка графики base с TS-клиентом (docs/dotnet.md, Н3: «кадры совпадают»). Облики берутся из настоящего
/// client.wasm через клиент партии (kh_look → холст клиента → атлас) и сравниваются с эталоном TS кадр за кадром, до
/// байта; чертежи всех юнитов и турелей, которых пока нет в данных, — тем же путём потока облика, но без wasm.
/// </summary>
public sealed class LookGoldenTests
{
    private static string Hash(ReadOnlySpan<byte> pixels) => Convert.ToHexStringLower(SHA256.HashData(pixels))[..16];

    /// <summary>Пиксели кадра из атласа по uv.</summary>
    private static byte[] Cut(LookAtlas atlas, Kharos.Client.Ui.Uv uv, int width, int height)
    {
        var (x0, y0) = ((int)Math.Round(uv.U0 * atlas.Width), (int)Math.Round(uv.V0 * atlas.Height));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Array.Copy(atlas.Pixels, ((y0 + y) * atlas.Width + x0) * 4, pixels, y * width * 4, width * 4);
        return pixels;
    }

    private static string Describe(string name, GoldenLook golden, int frame, byte[] actual)
    {
        var message = $"{name}: кадр {frame} не совпал с TS";
        if (golden.Sample is { } sample && sample.Index == frame)
        {
            var expected = Convert.FromBase64String(sample.Rgba);
            var diffs = Enumerable.Range(0, expected.Length / 4).Where(i => !expected.AsSpan(i * 4, 4).SequenceEqual(actual.AsSpan(i * 4, 4))).ToList();
            message += $"; пикселей отличается {diffs.Count}" + (diffs.Count > 0 ? $", первый ({diffs[0] % golden.Width}, {diffs[0] / golden.Width})" : "");
        }
        return message;
    }

    /// <summary>Сверяет все кадры, портрет и огни облика look с эталоном; возвращает список расхождений.</summary>
    private static List<string> Compare(string name, GoldenLook golden, Look look, LookAtlas? atlas, List<byte[]>? images = null)
    {
        var problems = new List<string>();
        var hashes = golden.FrameHashes;
        var count = images?.Count ?? look.Cells.Length;
        if (count != hashes.Length)
            return [$"{name}: кадров {count}, в TS {hashes.Length}"];
        if ((look.Width, look.Height) != (golden.Width, golden.Height))
            return [$"{name}: кадр {look.Width}×{look.Height}, в TS {golden.Width}×{golden.Height}"];
        for (var i = 0; i < count; i++)
        {
            var pixels = images is not null ? images[i] : Cut(atlas!, look.Cells[i], look.Width, look.Height);
            if (Hash(pixels) != hashes[i])
                problems.Add(Describe(name, golden, i, pixels));
        }
        if (golden.Portrait is { } portrait)
        {
            byte[] pixels;
            int width, height;
            if (images is not null)
                (pixels, width, height) = look.Portrait ?? throw new InvalidOperationException($"{name}: нет портрета");
            else
            {
                (width, height) = ((int)look.PortraitSize.Width, (int)look.PortraitSize.Height);
                pixels = Cut(atlas!, look.PortraitUv ?? throw new InvalidOperationException($"{name}: нет портрета в атласе"), width, height);
            }
            if ((width, height) != (portrait.Width, portrait.Height) || Hash(pixels) != portrait.Hash)
                problems.Add($"{name}: портрет {width}×{height} не совпал с TS {portrait.Width}×{portrait.Height}");
        }
        if (golden.Lights is { } lights)
        {
            var (ax, ay) = look.Spec.Anchor;
            for (var i = 0; i < lights.Length; i++)
            {
                var expected = lights[i].Select(l => new FrameLight((float)((l[0] - ax) / 16), (float)((l[1] - ay) / 16), (float)l[2], (float)l[3])).ToArray();
                if (!expected.SequenceEqual(look.FrameLights[i]))
                    problems.Add($"{name}: огни кадра {i} не совпали с TS");
            }
        }
        return problems;
    }

    [Fact]
    public void WasmLooksMatchTsFramesByteForByte()
    {
        using var session = new BaseSession();
        var golden = GoldenArt.Load();
        var game = session.Game;
        var checkedFrames = 0;
        var names = new List<string>();
        var problems = new List<string>();
        foreach (var look in game.Looks)
        {
            var name = game.Content.Protos[look.Proto].Path;
            names.Add(name);
            Assert.True(golden.Frames.TryGetValue(name, out var expected), $"нет эталона для {name}");
            problems.AddRange(Compare(name, expected, look, game.Atlas));
            checkedFrames += look.Cells.Length;
        }
        // Что объявил мод: юнит, турель, здание и шесть месторождений.
        Assert.Equal(["buildings/factory", "deposits/fuel", "deposits/kharite", "deposits/lithium", "deposits/metal", "deposits/silicon", "deposits/uranium", "turrets/gunner", "units/buggy"], names.Order(StringComparer.Ordinal));
        Assert.Equal(256 + 64 + 16 + 6, checkedFrames);
        Assert.Empty(problems);
        Assert.Empty(game.Errors);
        TestContext.Current.SendDiagnosticMessage($"base client.wasm: {checkedFrames} кадров в {names.Count} обликах совпали с TS до байта");
        BaseSession.Png("look-atlas.png", game.Atlas.Pixels, game.Atlas.Width, game.Atlas.Height);
    }

    [Fact]
    public void WasmLookSettingsMatchPilot()
    {
        using var session = new BaseSession();
        var game = session.Game;
        Look LookOf(string path) => game.LookOf(session.Content.FindProto(path)!.Id)!;

        var buggy = LookOf("units/buggy").Spec;
        Assert.Equal((32, 32, 64, 4), (buggy.Width, buggy.Height, buggy.Directions, buggy.Phases));
        Assert.Equal(1 / 16f, buggy.Step);
        Assert.Equal((LookLayer.Ground, 56), (buggy.Layer, buggy.Icon));
        Assert.Equal((2f, 0.4f), buggy.Shadow);
        Assert.Equal([new Lamp(7, -2, 2), new Lamp(7, 2, 2)], buggy.Lamps);
        Assert.Equal(new Beam(10, 60, 4, 0.28f, 1), buggy.Beam);

        var gunner = LookOf("turrets/gunner").Spec;
        Assert.Equal((64, 1, (1f, 0.4f)), (gunner.Directions, gunner.Phases, gunner.Shadow));
        Assert.Empty(gunner.Lamps);

        var factory = LookOf("buildings/factory");
        Assert.Equal((80, 80, 1, 16), (factory.Width, factory.Height, factory.Directions, factory.Phases));
        Assert.Equal((16.0, 16.0), factory.Spec.Origin);
        Assert.Equal((24.0, 24.0), factory.Spec.Anchor);
        Assert.Equal((LookLayer.Building, 8f, (3f, 0.4f)), (factory.Spec.Layer, factory.Spec.Fps, factory.Spec.Shadow));

        var oil = LookOf("deposits/fuel").Spec;
        Assert.Equal((LookLayer.Floor, true, (double)16), (oil.Layer, oil.Memory, oil.Anchor.X));
        Assert.Null(oil.Shadow);
        Assert.Equal((0.0, 0.0), oil.Origin);
    }

    [Fact]
    public void AllUnitAndTurretArtMatchTs()
    {
        var golden = GoldenArt.Load();
        var problems = new List<string>();
        var frames = 0;
        foreach (var unit in UnitArt.Units)
        {
            var spec = UnitArt.Look(unit.Name, () => unit.Name == "buggy" ? [new Mount("gunner", -0.16, 0)] : []);
            var look = Look.Build(0, LookStream.Decode(Encode(spec)));
            problems.AddRange(Compare("units/" + unit.Name, golden.Frames["units/" + unit.Name], look, null, look.Images));
            frames += look.Images.Count;
        }
        foreach (var (name, _) in UnitArt.Turrets)
        {
            var look = Look.Build(0, LookStream.Decode(Encode(UnitArt.TurretLook(name))));
            problems.AddRange(Compare("turrets/" + name, golden.Frames["turrets/" + name], look, null, look.Images));
            frames += look.Images.Count;
        }
        Assert.Empty(problems);
        Assert.Equal(14 * 256 + 4 * 64 + 8 * 64, frames);
        TestContext.Current.SendDiagnosticMessage($"{UnitArt.Units.Count} юнитов и {UnitArt.Turrets.Count} турелей: {frames} кадров совпали с TS до байта");
    }

    [Fact]
    public void UnitTablesMatchTs()
    {
        var golden = GoldenArt.Load();
        foreach (var unit in UnitArt.Units)
        {
            var expected = golden.Frames["units/" + unit.Name];
            Assert.Equal(expected.Gait, unit.Gait.ToString().ToLowerInvariant());
            Assert.Equal(expected.Lamps!.Select(l => new S.Lamp((float)l[0], (float)l[1], (float)l[2])), unit.Lamps);
            var b = expected.Beam!;
            Assert.Equal(new S.Beam((float)b[0], (float)b[1], (float)b[2], (float)b[3], (float)b[4]), unit.Beam);
            Assert.Equal(expected.Trace?.Sides, unit.Trace?.Sides);
            Assert.Equal(expected.Trace?.Width, unit.Trace?.Width);
            Assert.Equal(unit.Gait == Gait.Legs, unit.Trace?.Legs ?? false);
        }
    }

    [Fact]
    public void DepositKindsAreMatchedOneToOne()
    {
        var golden = GoldenArt.Load();
        var problems = new List<string>();
        foreach (var kind in DepositArt.Types)
        {
            var look = Look.Build(0, LookStream.Decode(Encode(DepositArt.Look(kind))));
            problems.AddRange(Compare("deposits/" + kind, golden.Frames["deposits/" + kind], look, null, look.Images));
        }
        Assert.Empty(problems);
    }

    /// <summary>Поток облика SDK (то, что отдаёт kh_look).</summary>
    private static byte[] Encode(S.LookSpec spec) => spec.Encode();
}
