using Kharos.Client.Game;
using Kharos.Client.Looks;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Облик из потока клиентского мода (kh_look): команды холста SDK, проигранные на холсте клиента, дают те же кадры,
/// огни и портрет, что те же вызовы прямо на C#, — до байта.
/// </summary>
public sealed class LookReplayTests
{
    /// <summary>Общий вид двух холстов: тест рисует одно и то же на обоих.</summary>
    private interface IPaint
    {
        void Rect(double x, double y, double w, double h, uint color);
        void Circle(double x, double y, double r, uint color);
        void Ring(double x, double y, double r, double width, uint color);
        void Line(double ax, double ay, double bx, double by, double width, uint color);
        void Poly(double[] coords, uint color);
        void Pixel(double x, double y, uint color);
        void Translate(double dx, double dy);
        void Light(double x, double y, double size, double level);
    }

    private sealed class Sdk(S.Canvas c) : IPaint
    {
        public void Rect(double x, double y, double w, double h, uint color) => c.Rect(x, y, w, h, color);
        public void Circle(double x, double y, double r, uint color) => c.Circle(x, y, r, color);
        public void Ring(double x, double y, double r, double width, uint color) => c.Ring(x, y, r, width, color);
        public void Line(double ax, double ay, double bx, double by, double width, uint color) => c.Line(ax, ay, bx, by, width, color);
        public void Poly(double[] coords, uint color) => c.Poly(coords, color);
        public void Pixel(double x, double y, uint color) => c.Pixel(x, y, color);
        public void Translate(double dx, double dy) => c.Translate(dx, dy);
        public void Light(double x, double y, double size, double level) => c.Light(x, y, size, level);
    }

    private sealed class Direct(Canvas c) : IPaint
    {
        public void Rect(double x, double y, double w, double h, uint color) => c.Rect(x, y, w, h, color);
        public void Circle(double x, double y, double r, uint color) => c.Circle(x, y, r, color);
        public void Ring(double x, double y, double r, double width, uint color) => c.Ring(x, y, r, width, color);
        public void Line(double ax, double ay, double bx, double by, double width, uint color) => c.Line(ax, ay, bx, by, width, color);
        public void Poly(double[] coords, uint color) => c.Poly(coords, color);
        public void Pixel(double x, double y, uint color) => c.Pixel(x, y, color);
        public void Translate(double dx, double dy) => c.Translate(dx, dy);
        public void Light(double x, double y, double size, double level) => c.Light(x, y, size, level);
    }

    /// <summary>Кадр со всеми командами холста: поворот, фаза, тона стороны, дробные координаты, сдвиг, огни.</summary>
    private static void Paint(IPaint p, double angle, int phase)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        (double X, double Y) At(double along, double across) => (c * along - s * across, s * along + c * across);
        p.Rect(-15.5, -12.25, 31, 3.3, 0x2d4560);
        foreach (var (along, across) in new[] { (-7.0, -6.5), (7.0, -6.5), (-7.0, 6.5), (7.0, 6.5) })
        {
            var (x, y) = At(along + phase * 0.37, across);
            p.Circle(x, y, 2.7, 0x0b111b);
        }
        var body = new[] { At(-10, -5.2), At(10.4, -5.2), At(10.4, 5.2), At(-10, 5.2), At(-12, 0) };
        p.Poly([.. body.SelectMany(point => new[] { point.X, point.Y })], S.Canvas.Toned(0x1f63d8, 1));
        p.Ring(At(3, 0).X, At(3, 0).Y, 3.6, 1.4, S.Canvas.Toned(0x5aa9ff, 2));
        p.Line(At(-9, 0).X, At(-9, 0).Y, At(14, 0.5).X, At(14, 0.5).Y, 1.7, S.Canvas.Toned(0x10358f, 0));
        p.Translate(0.5, -0.25);
        p.Pixel(At(10, -3).X, At(10, -3).Y, 0xfff1b8);
        p.Pixel(At(10, 3).X, At(10, 3).Y, 0xfff1b8);
        p.Light(At(10, -3).X, At(10, -3).Y, 4 + phase, 0.25 * (phase + 1));
        p.Translate(-0.5, 0.25);
        p.Rect(14, 13, 40, 2, 0x9bb9d1);
    }

    private static void Portrait(IPaint p)
    {
        p.Rect(-12, -10, 24, 20, 0x2d4560);
        p.Circle(0, 0, 6, S.Canvas.Toned(0x1f63d8, 1));
        p.Translate(3, 2);
        p.Ring(0, 0, 4, 1, 0xe4f4ff);
    }

    [Fact]
    public void ReplayEqualsDirectDrawingByteForByte()
    {
        var sdk = new S.LookSpec((canvas, angle, phase) => Paint(new Sdk(canvas), angle, phase))
        {
            Width = 40, Height = 36, Origin = (18.5, 20), Anchor = (1, -2.5), Directions = 16, Phases = 3, Step = 0.2f, Fps = 6,
            Layer = S.LookLayer.Building, Shadow = (3, 0.35f), Memory = true, Icon = 4,
            Lamps = [new S.Lamp(9, -3, 3), new S.Lamp(9, 3, 2.5f)], Beam = new S.Beam(Along: 9, Length: 48, Near: 3, Spread: 0.3f, Level: 0.8f),
            Portrait = new S.PortraitSpec(30, 28, canvas => Portrait(new Sdk(canvas))) { Origin = (14, 15) },
        };
        var direct = new LookSpec((canvas, angle, phase) => Paint(new Direct(canvas), angle, phase))
        {
            Width = 40, Height = 36, Origin = (18.5, 20), Anchor = (1, -2.5), Directions = 16, Phases = 3, Step = 0.2f, Fps = 6,
            Layer = LookLayer.Building, Shadow = (3, 0.35f), Memory = true, Icon = 4,
            Lamps = [new Lamp(9, -3, 3), new Lamp(9, 3, 2.5f)], Beam = new Beam(Along: 9, Length: 48, Near: 3, Spread: 0.3f, Level: 0.8f),
            Portrait = new PortraitSpec(30, 28, canvas => Portrait(new Direct(canvas))) { Origin = (14, 15) },
        };

        var stream = sdk.Encode();
        var decoded = LookStream.Decode(stream);
        var replayed = Look.Build(7, decoded);
        var expected = Look.Build(7, direct);

        Assert.Equal(48, replayed.Images.Count);
        Assert.Equal(expected.Images.Count, replayed.Images.Count);
        for (var i = 0; i < expected.Images.Count; i++)
            Assert.True(expected.Images[i].AsSpan().SequenceEqual(replayed.Images[i]), $"кадр {i}");
        for (var i = 0; i < expected.FrameLights.Count; i++)
            Assert.Equal(expected.FrameLights[i], replayed.FrameLights[i]);
        Assert.True(expected.Portrait!.Value.Pixels.AsSpan().SequenceEqual(replayed.Portrait!.Value.Pixels));
        Assert.Equal((expected.Portrait.Value.Width, expected.Portrait.Value.Height), (replayed.Portrait.Value.Width, replayed.Portrait.Value.Height));
        Assert.Equal(expected.Offset, replayed.Offset);
        Assert.Equal(expected.Shadow, replayed.Shadow);
        Assert.Equal((direct.Layer, direct.Memory, direct.Icon, direct.Step, direct.Fps, direct.Beam), (decoded.Layer, decoded.Memory, decoded.Icon, decoded.Step, decoded.Fps, decoded.Beam));
        Assert.Equal(direct.Lamps, decoded.Lamps);
        // Сам кадр не пуст: тоны стороны (альфа 252..254) и огни есть.
        Assert.Contains(replayed.Images[5].Where((_, i) => i % 4 == 3), alpha => alpha is >= 252 and <= 254);
        Assert.NotEmpty(replayed.FrameLights[5]);
    }

    [Fact]
    public void RoverOfHelloPacksIntoAtlas()
    {
        var look = Look.Build(3, LookStream.Decode(Hello.HelloClient.Rover.Encode()));
        Assert.Equal(128, look.Images.Count);
        Assert.Equal(2, look.Spec.Lamps.Count);
        Assert.NotNull(look.Spec.Beam);
        var atlas = LookAtlas.Pack([look]);
        Assert.Equal(128, look.Cells.Length);
        Assert.NotNull(look.PortraitUv);
        Repo.Png("look-rover-atlas.png", atlas.Pixels, atlas.Width, atlas.Height);
    }

    [Fact]
    public void BrokenStreamIsRejected()
    {
        var stream = Hello.HelloClient.Rover.Encode();
        Assert.Throws<FormatException>(() => LookStream.Decode(stream.AsSpan(0, stream.Length - 3)));
        var bad = (byte[])stream.Clone();
        bad[^1] = 0x7f; // последняя команда — не End
        Assert.Throws<FormatException>(() => LookStream.Decode(bad));
    }
}
