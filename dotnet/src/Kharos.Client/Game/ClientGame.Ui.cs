using Kharos.Client.Ui;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Game;

/// <summary>Интерфейс кадра: Gui модов и мини-карта.</summary>
public sealed partial class ClientGame
{
    // Владелец — Д2 (docs/dotnet-n6.md, Н6.5): интерфейс кадра, мини-карта.

    /// <summary>Мини-карта по умолчанию — PALETTE из minimap.ts: биом за биомом, внутри — вид местности.</summary>
    public static readonly uint[] DefaultMinimap =
    [
        0xC29945, 0x63450F, 0x36331A, 0x8C6828,
        0xD1C9B3, 0x5E5C54, 0x4D8585, 0x828078,
        0xB35C29, 0x542414, 0x452112, 0x804024,
        0x857A38, 0x454021, 0x1A3621, 0x645F34,
    ];

    private uint[] _minimapPalette = DefaultMinimap;

    /// <summary>Буфер обмена полей ввода: оболочка подставляет системный (по умолчанию — в памяти).</summary>
    public IClipboard Clipboard
    {
        get => Gui.Clipboard;
        set => Gui.Clipboard = value;
    }

    /// <summary>В поле ввода набирают текст или открыто модальное окно: оболочка не отдаёт клавиши своим горячим клавишам.</summary>
    public bool CapturesKeys => Gui.CapturesKeys;

    /// <summary>Начало интерфейса кадра: ввод — Gui (места и щелчки прошлого кадра).</summary>
    private void BeginUi(in ClientInput input) => Gui.Begin(input.ToUi());

    /// <summary>Конец интерфейса кадра: раскладка и батч интерфейса в кадр рендера.</summary>
    private void FinishUi(RenderFrame frame) => Gui.Finish(frame.Interface);

    /// <summary>Мини-карта: тайл — пиксель цветом своей местности и биома (minimap вида lands, PALETTE в minimap.ts).</summary>
    private (byte[], int, int)? BuildMinimap()
    {
        if (Land is not { } land || Replica.Welcome?.Map is not { } map)
            return null;
        var (width, height) = (map.Width, map.Height);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (tx, ty) = (map.Left + x, map.Top + y);
                var terrain = (int)land.TerrainAt(tx, ty) & 3;
                var biome = (int)land.BiomeAt(tx + 0.5, ty + 0.5) & 3;
                var color = _minimapPalette[biome * 4 + terrain];
                var at = (y * width + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = ((byte)(color >> 16), (byte)(color >> 8), (byte)color, 255);
            }
        return (pixels, width, height);
    }
}
