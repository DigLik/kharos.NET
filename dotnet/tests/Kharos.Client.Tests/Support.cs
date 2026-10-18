using System.Security.Cryptography;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Gpu;
using StbImageWriteSharp;

namespace Kharos.Client.Tests;

/// <summary>Пути репозитория: эталоны тестов и папка картинок для просмотра (dotnet/.cache/client).</summary>
internal static class Repo
{
    private static readonly Lazy<string> Dotnet = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Kharos.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException($"Kharos.slnx не найден выше {AppContext.BaseDirectory}");
    });

    public static string Golden(string area, string name) => Path.Combine(Dotnet.Value, "tests", "Kharos.Client.Tests", area, "golden", name);

    public static string Cache(string name)
    {
        var directory = Path.Combine(Dotnet.Value, ".cache", "client");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Картинка RGBA в PNG.</summary>
    public static void Png(string name, byte[] rgba, int width, int height)
    {
        using var stream = File.Create(Cache(name));
        new ImageWriter().WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
    }

    /// <summary>Однобайтовая картинка (атлас шрифта) в PNG: белое по альфе на чёрном.</summary>
    public static void PngGray(string name, byte[] gray, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < gray.Length; i++) (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (gray[i], gray[i], gray[i], 255);
        Png(name, rgba, width, height);
    }
}

/// <summary>Кадр ввода для тестов интерфейса.</summary>
internal static class Frames
{
    public static UiInput At(double time, float x, float y, float width = 800, float height = 600) =>
        new() { Time = time, Width = width, Height = height, Pointer = (x, y) };

    public static UiInput Off(double time, float width = 800, float height = 600) => new() { Time = time, Width = width, Height = height };
}

/// <summary>
/// Батч интерфейса на процессоре — чтобы посмотреть кадр глазами без видеокарты: четырёхугольники по шесть индексов
/// (интерфейс рисует только их), выборка ближайшим текселем, смешивание «поверх», обрезка отрезков.
/// </summary>
internal sealed class SoftRaster(int width, int height, float scale = 1)
{
    public readonly byte[] Pixels = new byte[width * height * 4];
    public byte[] FontAtlas = [];
    public byte[] Skin = Skins.Pixels();
    public LookAtlas? Sprites;
    /// <summary>Палитра своей стороны: тёмный, основной, светлый.</summary>
    public Color[] Team = [Color.Hex(0x10358f), Color.Hex(0x1f63d8), Color.Hex(0x5aa9ff)];

    public void Fill(Color color)
    {
        for (var i = 0; i < width * height; i++) (Pixels[i * 4], Pixels[i * 4 + 1], Pixels[i * 4 + 2], Pixels[i * 4 + 3]) = (color.R, color.G, color.B, 255);
    }

    private Color Sample(Paint paint, float u, float v)
    {
        static int At(float t, int size) => Math.Clamp((int)MathF.Floor(t * size), 0, size - 1);
        switch (paint.Kind)
        {
            case PaintKind.Font:
                var alpha = FontAtlas[At(v, Batch.AtlasSize) * Batch.AtlasSize + At(u, Batch.AtlasSize)];
                return new Color(255, 255, 255, alpha);
            case PaintKind.Skin:
            {
                var at = (At(v, Skins.Height) * Skins.Width + At(u, Skins.Width)) * 4;
                return new Color(Skin[at], Skin[at + 1], Skin[at + 2], Skin[at + 3]);
            }
            case PaintKind.Sprite when Sprites is { } atlas:
            {
                var at = (At(v, atlas.Height) * atlas.Width + At(u, atlas.Width)) * 4;
                var a = atlas.Pixels[at + 3];
                if (a is >= 252 and <= 254) return Team[254 - a];
                return new Color(atlas.Pixels[at], atlas.Pixels[at + 1], atlas.Pixels[at + 2], a);
            }
            case PaintKind.Minimap:
            {
                // Местность-заглушка: клетки травы и воды.
                var land = ((int)(u * 16) + (int)(v * 16)) % 5 == 0;
                return land ? Color.Hex(0x1d3b5c) : Color.Hex(0x2f5a2a);
            }
            default:
                return new Color(0, 0, 0, 0);
        }
    }

    public void Draw(Batch batch)
    {
        for (var s = 0; s < batch.Segments.Count; s++)
        {
            var segment = batch.Segments[s];
            var end = s + 1 < batch.Segments.Count ? batch.Segments[s + 1].Start : batch.Indices.Count;
            int cx0 = 0, cy0 = 0, cx1 = width, cy1 = height;
            if (segment.Clip is { } clip)
            {
                cx0 = Math.Max(0, (int)MathF.Round(clip.X * scale));
                cy0 = Math.Max(0, (int)MathF.Round(clip.Y * scale));
                cx1 = Math.Min(width, (int)MathF.Round((clip.X + clip.Width) * scale));
                cy1 = Math.Min(height, (int)MathF.Round((clip.Y + clip.Height) * scale));
            }
            for (var i = segment.Start; i + 5 < end; i += 6)
            {
                var first = (int)batch.Indices[i];
                Vertex a = batch.Vertices[first], c = batch.Vertices[first + 2];
                float x0 = a.X * scale, y0 = a.Y * scale, x1 = c.X * scale, y1 = c.Y * scale;
                var color = new Color((byte)a.Rgba, (byte)(a.Rgba >> 8), (byte)(a.Rgba >> 16), (byte)(a.Rgba >> 24));
                var fromX = Math.Max(cx0, (int)MathF.Ceiling(x0 - 0.5f));
                var toX = Math.Min(cx1, (int)MathF.Ceiling(x1 - 0.5f));
                var fromY = Math.Max(cy0, (int)MathF.Ceiling(y0 - 0.5f));
                var toY = Math.Min(cy1, (int)MathF.Ceiling(y1 - 0.5f));
                for (var py = fromY; py < toY; py++)
                    for (var px = fromX; px < toX; px++)
                    {
                        var u = a.U + (c.U - a.U) * ((px + 0.5f - x0) / (x1 - x0));
                        var v = a.V + (c.V - a.V) * ((py + 0.5f - y0) / (y1 - y0));
                        var texel = Sample(segment.Paint, u, v);
                        Blend(px, py, (byte)(texel.R * color.R / 255), (byte)(texel.G * color.G / 255), (byte)(texel.B * color.B / 255), texel.A * color.A / 255);
                    }
            }
        }
    }

    private void Blend(int x, int y, byte r, byte g, byte b, int a)
    {
        if (a == 0) return;
        var at = (y * width + x) * 4;
        Pixels[at] = (byte)((r * a + Pixels[at] * (255 - a)) / 255);
        Pixels[at + 1] = (byte)((g * a + Pixels[at + 1] * (255 - a)) / 255);
        Pixels[at + 2] = (byte)((b * a + Pixels[at + 2] * (255 - a)) / 255);
        Pixels[at + 3] = 255;
    }
}

/// <summary>Облики и панель игрока для тестов: багги, стрелок и завод, нарисованные на холсте, и HUD как в hud.luau.</summary>
internal static class Demo
{
    private const uint Ink = 0x0b111b;

    public static LookSpec Buggy => new((canvas, angle, phase) =>
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        (double, double) At(double along, double across) => (c * along - s * across, s * along + c * across);
        foreach (var (along, across) in new[] { (-6.0, -6.0), (6.0, -6.0), (-6.0, 6.0), (6.0, 6.0) })
        {
            var (x, y) = At(along + (phase % 2 == 0 ? 0 : 0.5), across);
            canvas.Circle(x, y, 2.6, Ink);
        }
        var body = new[] { At(-9, -5), At(9, -5), At(9, 5), At(-9, 5) };
        canvas.Poly([.. body.SelectMany(p => new[] { p.Item1, p.Item2 })], Canvas.Toned(0x1f63d8, 1));
        var roof = new[] { At(-4, -3.5), At(5, -3.5), At(5, 3.5), At(-4, 3.5) };
        canvas.Poly([.. roof.SelectMany(p => new[] { p.Item1, p.Item2 })], Canvas.Toned(0x5aa9ff, 2));
        var (lx, ly) = At(9, -3);
        var (rx, ry) = At(9, 3);
        canvas.Pixel(lx, ly, 0xfff1b8).Pixel(rx, ry, 0xfff1b8).Light(lx, ly, 6, 1);
    })
    { Directions = 64, Phases = 2, Step = 0.25f };

    public static LookSpec Gunner => new((canvas, angle, _) =>
    {
        canvas.Circle(0, 0, 4, Canvas.Toned(0x10358f, 0)).Circle(0, 0, 3, 0x41617f);
        canvas.Line(0, 0, Math.Cos(angle) * 10, Math.Sin(angle) * 10, 2, 0x9bb9d1);
    })
    { Directions = 64 };

    public static LookSpec Factory => new((canvas, _, phase) =>
    {
        canvas.Rect(-36, -36, 72, 72, 0x2d4560).Rect(-36, -36, 72, 2, 0x6184a3).Rect(-36, 34, 72, 2, Ink);
        canvas.Rect(-14, 6, 28, 28, Ink).Rect(-12, 8, 24, 24, Canvas.Toned(0x1f63d8, 1));
        canvas.Ring(18, -18, 10, 2, 0x9bb9d1).Circle(18, -18, 4, 0xf0c95a);
        var angle = phase / 16.0 * Look.Turn;
        canvas.Line(18 - Math.Cos(angle) * 9, -18 - Math.Sin(angle) * 9, 18 + Math.Cos(angle) * 9, -18 + Math.Sin(angle) * 9, 1.5, 0xe4f4ff);
        canvas.Rect(-30, -30, 30, 20, 0x1c2b3e).Light(-15, -20, 10, 0.6);
    })
    {
        Width = 80, Height = 80, Phases = 16, Layer = LookLayer.Building, Shadow = null,
        Portrait = new PortraitSpec(80, 80, canvas => canvas.Rect(-36, -36, 72, 72, 0x2d4560).Rect(-12, 8, 24, 24, Canvas.Toned(0x1f63d8, 1))),
    };

    public static (Look[] Looks, LookAtlas Atlas) Atlas()
    {
        Look[] looks = [Look.Build(1, Buggy), Look.Build(2, Gunner), Look.Build(3, Factory)];
        return (looks, LookAtlas.Pack(looks));
    }

    private static readonly Color Gold = Color.Parse("#f0c95a")!.Value, Bright = Color.Parse("#eef6ff")!.Value,
        Dim = Color.Parse("#8fa9c2")!.Value, Muted = Color.Parse("#6184a3")!.Value, Good = Color.Parse("#8fe0a8")!.Value,
        TeamMid = Color.Parse("#1f63d8")!.Value;

    private static TextStyle Shadowed(Color color, bool ellipsis = false) => new() { Color = color, Shadow = Skins.Ink, Lift = 1, Ellipsis = ellipsis };

    /// <summary>Панель игрока как в mods/base/ui/hud.luau: верхняя полоса, мини-карта, сведения, сетка команд 4 × 3.</summary>
    public static void Hud(Gui ui, Look[] looks, bool selected, float progress)
    {
        const float inner = 140, cellWidth = 132, gridWidth = 4 * cellWidth + 3 * 4, spacing = 16 + 8 + 8;
        ui.Box(new Style { Id = "top", Anchor = new Anchor(Left: 0, Right: 0, Top: 0), Height = 38, Skin = Skin.Panel, Padding = (4, 12), Direction = Direction.Row, Align = Align.Center, Gap = 12 }, () =>
        {
            ui.Box(new Style { Id = "credits", Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
            {
                ui.Coin();
                ui.Text("1250", new TextStyle { Font = FontName.Title, Color = Gold, Shadow = Skins.Ink, Lift = 2 });
                ui.Text("+4/с", new TextStyle { Color = Good });
            });
            ui.Tip("credits", "Кредиты");
            ui.Space(grow: 1);
            ui.Box(new Style { Id = "clock", Direction = Direction.Row, Gap = 4 }, () => ui.Text("13:45", new TextStyle { Color = Dim }));
            ui.Button("menu", Style.Button with { Padding = (2, 10), Height = 30 }, () => ui.Text("Меню", Shadowed(Gui.TextColor)));
        });
        var side = MathF.Max(inner, MathF.Min(200, ui.Width - spacing - gridWidth - (104 + 10 + 24 + 150)));
        var infoWidth = ui.Width - spacing - side - gridWidth;
        ui.Box(new Style { Id = "bar", Anchor = new Anchor(Left: 0, Right: 0, Bottom: 0), Height = inner + 16, Skin = Skin.Panel, Padding = (4, 8), Direction = Direction.Row, Align = Align.End, Gap = 8 }, () =>
        {
            ui.Box(new Style { Height = inner, Direction = Direction.Column, Justify = Align.End }, () =>
            {
                ui.Box(new Style { Id = "minimap", Width = side, Height = side, Skin = Skin.Well, Padding = 4, Clip = true }, () =>
                    ui.Box(new Style { Direction = Direction.Stack, Width = side - 8, Height = side - 8, Background = Color.Hex(0x05090f) }, () =>
                    {
                        ui.Minimap(side - 8, side - 8);
                        for (var i = 0; i < 12; i++)
                            ui.Box(new Style { Self = (Align.Start, Align.Start), Padding = (10 + i * 9, 0, 0, 12 + i * 11 % 100) }, () =>
                                ui.Box(new Style { Width = 3, Height = 3, Background = i % 3 == 0 ? Color.Hex(0xff6b5a) : Color.Hex(0x5aa9ff) }));
                    }));
            });
            ui.Box(new Style { Direction = Direction.Row, Gap = 8, Height = inner, Align = Align.Center }, () =>
            {
                if (selected) Info(ui, looks, infoWidth, inner, progress);
                else
                    ui.Box(new Style { Width = infoWidth, Height = inner, Skin = Skin.Well, Padding = (10, 12), Justify = Align.Center, Gap = 4, Clip = true }, () =>
                    {
                        foreach (var line in new[] { "Ничего не выбрано.", "Левая кнопка — выбрать, рамка — несколько юнитов, правая — приказ." })
                            ui.Text(line, new TextStyle { Color = Muted, Wrap = float.PositiveInfinity });
                    });
                Grid(ui, looks, selected, gridWidth, inner);
            });
        });
    }

    private static void Info(Gui ui, Look[] looks, float width, float height, float progress)
    {
        var text = MathF.Max(40, width - 24 - 104 - 10);
        ui.Box(new Style { Width = width, Height = height, Skin = Skin.Well, Padding = (10, 12), Direction = Direction.Row, Gap = 10 }, () =>
        {
            ui.Button("portrait", new Style { Width = 104, Height = 104, Skin = Skin.Well, Direction = Direction.Stack }, () =>
            {
                ui.Box(new Style { Self = (Align.Center, Align.Center) }, () => ui.Image(looks[2].Icon, 96, 96));
                ui.Box(new Style { Self = (Align.Start, Align.End), Width = 104, Height = 6, Background = Color.Hex(0x4fd07a) });
            });
            ui.Tip("portrait", "Показать на карте");
            ui.Box(new Style { Id = "info:details", Gap = 4, Grow = 1, MaxHeight = height - 20, Scroll = true }, () =>
            {
                ui.Box(new Style { Id = "info:title", Width = text }, () =>
                    ui.Text("Завод боевых машин", new TextStyle { Font = FontName.Title, Color = Bright, Shadow = Skins.Ink, Lift = 2, Ellipsis = true }));
                ui.Box(new Style { Skin = Skin.Well, Width = text, Height = 24, Padding = 4, Direction = Direction.Stack, Clip = true }, () =>
                {
                    ui.Box(new Style { Grow = 1, Direction = Direction.Row }, () =>
                    {
                        ui.Box(new Style { Grow = progress, Background = TeamMid });
                        ui.Box(new Style { Grow = 1 - progress });
                    });
                    ui.Box(new Style { Self = (Align.Start, Align.Center), Padding = (0, 8) }, () => ui.Text("Багги", Shadowed(Gui.TextColor, true)));
                });
                ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                {
                    for (var i = 0; i < 3; i++)
                        ui.Box(new Style { Id = $"queue:{i}", Width = 32, Height = 32, Skin = Skin.Well, Padding = 4 }, () => ui.Image(looks[0].Icon, 24, 24));
                });
                ui.Text("Правый щелчок по карте — точка сбора готовых", new TextStyle { Color = Color.Hex(0xb4c9dd), Wrap = text });
            });
        });
    }

    private static readonly string[] Keys = ["T", "Y", "U", "I", "G", "H", "J", "K", "B", "N", "M", ","];

    private static void Grid(Gui ui, Look[] looks, bool selected, float width, float height)
    {
        float cellWidth = (width - 3 * 4) / 4, cellHeight = (height - 2 * 4) / 3;
        ui.Box(new Style { Id = "commands", Gap = 4, Width = width, Height = height }, () =>
        {
            for (var row = 0; row < 3; row++)
                ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
                {
                    for (var column = 0; column < 4; column++)
                    {
                        var i = row * 4 + column;
                        if (!selected || i >= 2)
                        {
                            ui.Box(new Style { Width = cellWidth, Height = cellHeight, Skin = Skin.Well, Alpha = 0.35f });
                            continue;
                        }
                        var id = $"cell:{i}";
                        var look = looks[i];
                        var disabled = i == 1;
                        ui.Button(id, Style.Button with { Width = cellWidth, Height = cellHeight, Padding = (0, 6, 0, 4), Justify = Align.Start }, () =>
                        {
                            ui.Box(new Style { Direction = Direction.Stack }, () =>
                            {
                                ui.Box(new Style { Width = 32, Height = 32 }, () => ui.Image(look.Icon, 32, 32));
                                ui.Box(new Style { Self = (Align.Start, Align.Start), Background = Skins.Ink, Padding = (0, 2) }, () =>
                                    ui.Text(Keys[i], Shadowed(disabled ? Muted : Gold)));
                            });
                            ui.Box(new Style { Grow = 1, Clip = true }, () =>
                            {
                                ui.Box(new Style { Direction = Direction.Row, Gap = 6 }, () =>
                                    ui.Box(new Style { Grow = 1 }, () => ui.Text(i == 0 ? "Багги разведчика" : "Стрелок", Shadowed(Bright, true))));
                                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 4 }, () =>
                                {
                                    ui.Coin();
                                    ui.Text(i == 0 ? "120" : "300", Shadowed(disabled ? Color.Hex(0xffb070) : Gold));
                                });
                            });
                        }, disabled: disabled);
                        ui.Tip(id, () =>
                            ui.Box(new Style { Gap = 4, Width = 264, Align = Align.Stretch }, () =>
                            {
                                ui.Box(new Style { Direction = Direction.Row, Justify = Align.Between, Align = Align.End, Gap = 12 }, () =>
                                {
                                    ui.Text("Багги", new TextStyle { Font = FontName.Title, Color = Bright });
                                    ui.Text(Keys[i], new TextStyle { Font = FontName.Title, Color = Gold });
                                });
                                ui.Text("Быстрая машина разведки с пулемётом; дешёвая и шустрая, но хрупкая.", new TextStyle { Color = Dim, Wrap = 264 });
                            }), new Anchor(Right: 8, Bottom: height + 28));
                    }
                });
        });
    }
}
