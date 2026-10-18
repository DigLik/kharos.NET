using System.Text.Json;
using Kharos.Client.Looks;

namespace Kharos.Client.Tests.Looks;

/// <summary>Холст обликов против Pixmap из TS: эталоны пишет dotnet/tools/golden/pixmap.ts, сверка — до байта.</summary>
public sealed class CanvasTests
{
    public sealed record Case(string Name, int Width, int Height, double Ox, double Oy, JsonElement[][] Ops, string Sha256);

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var one in Cases.Value) data.Add(one.Name);
        return data;
    }

    private static readonly Lazy<Case[]> Cases = new(() =>
        JsonSerializer.Deserialize<Case[]>(File.ReadAllBytes(Repo.Golden("Looks", "pixmap.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    private static readonly Lazy<byte[]> Images = new(() => File.ReadAllBytes(Repo.Golden("Looks", "pixmap.bin")));

    /// <summary>Повторяет операции случая на холсте C#.</summary>
    private static Canvas Run(Case one)
    {
        var canvas = new Canvas(one.Width, one.Height) { OriginX = one.Ox, OriginY = one.Oy };
        foreach (var op in one.Ops)
        {
            var a = op.Skip(1).Select(value => value.GetDouble()).ToArray();
            switch (op[0].GetString())
            {
                case "rect": canvas.Rect(a[0], a[1], a[2], a[3], (uint)a[4]); break;
                case "circle": canvas.Circle(a[0], a[1], a[2], (uint)a[3]); break;
                case "ring": canvas.Ring(a[0], a[1], a[2], a[3], (uint)a[4]); break;
                case "line": canvas.Line(a[0], a[1], a[2], a[3], a[4], (uint)a[5]); break;
                case "translate": canvas.Translate(a[0], a[1]); break;
                case "pixel": canvas.Pixel(a[0], a[1], (uint)a[2]); break;
                case "poly": canvas.Poly(a.AsSpan(0, a.Length - 1), (uint)a[^1]); break;
                default: throw new InvalidDataException(op[0].GetString());
            }
        }
        return canvas;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MatchesPixmap(string name)
    {
        var offset = 0;
        foreach (var one in Cases.Value)
        {
            var size = one.Width * one.Height * 4;
            if (one.Name == name)
            {
                var expected = Images.Value.AsSpan(offset, size);
                var actual = Run(one).Data;
                for (var i = 0; i < size; i += 4)
                    Assert.True(expected.Slice(i, 4).SequenceEqual(actual.AsSpan(i, 4)),
                        $"{name}: пиксель ({i / 4 % one.Width}, {i / 4 / one.Width}) — TS {Convert.ToHexString(expected.Slice(i, 4))}, C# {Convert.ToHexString(actual.AsSpan(i, 4))}");
                Assert.Equal(one.Sha256, Repo.Sha256(actual));
                return;
            }
            offset += size;
        }
        Assert.Fail($"нет случая {name}");
    }

    [Fact]
    public void AllCasesDrawSomething()
    {
        Assert.True(Cases.Value.Length >= 50);
        foreach (var one in Cases.Value)
            Assert.Contains(Run(one).Data, value => value != 0);
    }

    [Fact]
    public void TonedAlphaMarksTeamColor()
    {
        Assert.Equal(0x011f63d8u, Canvas.Toned(0x1f63d8, 0));
        var canvas = new Canvas(3, 1).Pixel(0, 0, Canvas.Toned(0x102030, 0)).Pixel(1, 0, Canvas.Toned(0x102030, 2)).Pixel(2, 0, 0x102030);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 254, 0x10, 0x20, 0x30, 252, 0x10, 0x20, 0x30, 255 }, canvas.Data);
    }

    [Fact]
    public void LightsAndClear()
    {
        var canvas = new Canvas(4, 4).Light(1, 2, 3, 0.5).Rect(0, 0, 4, 4, 0xffffff);
        Assert.Equal([new CanvasLight(1, 2, 3, 0.5)], canvas.Lights);
        canvas.Clear();
        Assert.Empty(canvas.Lights);
        Assert.All(canvas.Data, value => Assert.Equal(0, value));
    }
}
