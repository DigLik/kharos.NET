namespace Kharos.Client.Ui;

/// <summary>Текстура скина — перенос paintSkins (skin.ts): десять рамок 12 × 12 подряд и монета 8 × 8, RGBA 128 × 12.</summary>
public static class Skins
{
    public const int Width = 128, Height = 12;
    /// <summary>Рамка: углы по Corner пикселей скина, пиксель скина — Scale пикселей экрана.</summary>
    public const float Corner = 4, Scale = 2, Border = Corner * Scale;
    /// <summary>Левый край монеты в текстуре.</summary>
    public const int Coin = 120;

    public static readonly Color Ink = Color.Hex(0x0b111b);
    private static readonly Color[] Steel = [Color.Hex(0x1c2b3e), Color.Hex(0x2d4560), Color.Hex(0x41617f), Color.Hex(0x6184a3), Color.Hex(0x9bb9d1)];
    private static readonly Color Deep = Color.Hex(0x0e1825), WellColor = Color.Hex(0x070d15);
    private static readonly Color[] Team = [Color.Hex(0x10358f), Color.Hex(0x1f63d8), Color.Hex(0x5aa9ff), Color.Hex(0xe4f4ff)];
    private static readonly Color[] Gold = [Color.Hex(0x5a3d0c), Color.Hex(0xc9962b), Color.Hex(0xf0c95a), Color.Hex(0xfff1b8)];
    private static readonly Color[] Red = [Color.Hex(0x4a1410), Color.Hex(0xa8382a), Color.Hex(0xff6b5a)];

    /// <summary>Пиксели текстуры скина RGBA.</summary>
    public static byte[] Pixels()
    {
        var pixels = new byte[Width * Height * 4];
        void Paint(int left, int x, int y, int w, int h, Color color)
        {
            for (var py = y; py < y + h; py++)
                for (var px = left + x; px < left + x + w; px++)
                {
                    var at = (py * Width + px) * 4;
                    (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (color.R, color.G, color.B, color.A);
                }
        }
        // Рамка со срезанными углами: обводка, фаска (светлая сверху и слева, тёмная снизу и справа), заливка, заклёпки.
        (Color Fill, Color Light, Color Dark, Color Edge, Color? Rivets)[] frames =
        [
            (Deep, Steel[2], Ink, Ink, Steel[3]),
            (WellColor, Ink, Steel[1], Steel[0], null),
            (Steel[1], Steel[3], Steel[0], Ink, null),
            (Steel[2], Steel[4], Steel[0], Ink, null),
            (Team[0], Ink, Team[2], Team[2], null),
            (Steel[0], Steel[1], Ink, Ink, null),
            (Team[1], Team[2], Team[0], Ink, null),
            (Color.Hex(0x3a7fe8), Team[3], Team[0], Ink, null),
            (Deep, Gold[2], Gold[0], Gold[0], Gold[3]),
            (Red[0], Red[2], Ink, Ink, Red[2]),
        ];
        for (var index = 0; index < frames.Length; index++)
        {
            var (fill, light, dark, edge, rivets) = frames[index];
            int left = index * 12, last = 11;
            Paint(left, 1, 0, 10, 1, edge);
            Paint(left, 1, last, 10, 1, edge);
            Paint(left, 0, 1, 1, 10, edge);
            Paint(left, last, 1, 1, 10, edge);
            Paint(left, 1, 1, 10, 10, fill);
            Paint(left, 1, 1, 10, 1, light);
            Paint(left, 1, 2, 1, 9, light);
            Paint(left, 1, last - 1, 10, 1, dark);
            Paint(left, last - 1, 1, 1, 10, dark);
            if (rivets is { } rivet)
                foreach (var (x, y) in new[] { (2, 2), (last - 2, 2), (2, last - 2), (last - 2, last - 2) })
                    Paint(left, x, y, 1, 1, rivet);
        }
        // Монета кредитов: золотой кружок с бликом и тенью.
        (int X, int Y, int W, int H, Color Color)[] coin =
        [
            (2, 0, 4, 1, Gold[0]), (2, 7, 4, 1, Gold[0]), (0, 2, 1, 4, Gold[0]), (7, 2, 1, 4, Gold[0]),
            (1, 1, 1, 1, Gold[0]), (6, 1, 1, 1, Gold[0]), (1, 6, 1, 1, Gold[0]), (6, 6, 1, 1, Gold[0]),
            (1, 2, 6, 4, Gold[2]), (2, 1, 4, 6, Gold[2]), (5, 2, 1, 4, Gold[1]), (2, 5, 4, 1, Gold[1]),
            (2, 2, 2, 1, Gold[3]), (2, 3, 1, 1, Gold[3]),
        ];
        foreach (var (x, y, w, h, color) in coin) Paint(Coin, x, y, w, h, color);
        return pixels;
    }

    /// <summary>uv куска текстуры скина в её пикселях.</summary>
    public static Uv UvOf(float x, float y, float w, float h) => new(x / Width, y / Height, (x + w) / Width, (y + h) / Height);
}
