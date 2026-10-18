using Kharos.Client.Ui;

namespace Kharos.Client.Tests.Ui;

/// <summary>Текстура скина: цвета рамок по skin.ts и отпечаток.</summary>
public sealed class SkinTests
{
    private static Color At(byte[] pixels, int x, int y)
    {
        var at = (y * Skins.Width + x) * 4;
        return new Color(pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
    }

    [Fact]
    public void FramesAndCoin()
    {
        var pixels = Skins.Pixels();
        Assert.Equal(Skins.Width * Skins.Height * 4, pixels.Length);
        // Панель: срезанный угол, обводка чернилами, светлая фаска, заклёпка, заливка, тёмная фаска.
        Assert.Equal(new Color(0, 0, 0, 0), At(pixels, 0, 0));
        Assert.Equal(Skins.Ink, At(pixels, 1, 0));
        Assert.Equal(Color.Hex(0x41617f), At(pixels, 1, 1));
        Assert.Equal(Color.Hex(0x6184a3), At(pixels, 2, 2));
        Assert.Equal(Color.Hex(0x0e1825), At(pixels, 5, 5));
        Assert.Equal(Skins.Ink, At(pixels, 10, 10));
        // Утопленное окно — второй кадр; основная кнопка с наведением — восьмой.
        Assert.Equal(Color.Hex(0x070d15), At(pixels, 12 + 5, 5));
        Assert.Equal(Color.Hex(0x3a7fe8), At(pixels, 7 * 12 + 5, 5));
        // Монета: блик, золото, тень, прозрачный угол.
        Assert.Equal(Color.Hex(0xfff1b8), At(pixels, Skins.Coin + 2, 2));
        Assert.Equal(Color.Hex(0xf0c95a), At(pixels, Skins.Coin + 3, 4));
        Assert.Equal(Color.Hex(0xc9962b), At(pixels, Skins.Coin + 5, 3));
        Assert.Equal(new Color(0, 0, 0, 0), At(pixels, Skins.Coin, 0));
        Assert.Equal(new Color(0, 0, 0, 0), At(pixels, Skins.Coin + 7, 9));
    }

    [Fact]
    public void Deterministic()
    {
        var pixels = Skins.Pixels();
        Assert.Equal(pixels, Skins.Pixels());
        // Увеличенно ×8 — для просмотра.
        const int zoom = 8;
        var big = new byte[pixels.Length * zoom * zoom];
        for (var y = 0; y < Skins.Height * zoom; y++)
            for (var x = 0; x < Skins.Width * zoom; x++)
                pixels.AsSpan((y / zoom * Skins.Width + x / zoom) * 4, 4).CopyTo(big.AsSpan((y * Skins.Width * zoom + x) * 4));
        Repo.Png("skin.png", big, Skins.Width * zoom, Skins.Height * zoom);
        Assert.Equal(Sha, Repo.Sha256(pixels));
    }

    private const string Sha = "7b3d83171e7b121ad83d9a46ddd53fffaa916d2d67a0c90642ed3a7ce90a183d";
}
