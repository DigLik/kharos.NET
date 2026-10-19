using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Генератор материи (matter, 2×2) — перенос matter из buildingArt.ts.</summary>
internal static class Matter
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Камера синтеза с пульсирующим ядром в кольце огней, катушки по углам и короткая лента, по которой готовый слиток
    /// уезжает в приёмный люк.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _) =>
    {
        Slab(g, 0, 1, 32, 31, 3, Steel);

        // Лента от камеры к люку у нижнего края: за цикл по ней проезжает один слиток.
        g.Rect(10, 18, 12, 10, Ink);
        g.Rect(11, 19, 10, 8, Iron[0]);
        for (var y = 20; y < 27; y += 2)
            g.Rect(11, y, 10, 1, Iron[1]);
        var ingot = 17 + (int)Math.Floor(t * Frames) % 8;
        g.Rect(13, ingot, 6, 3, Ink);
        g.Rect(13, ingot, 6, 2, Gold3[1]);
        g.Rect(13, ingot, 3, 1, Gold3[2]);
        g.Rect(13, ingot + 2, 6, 1, Gold3[0]);
        g.Rect(9, 25, 14, 4, Ink);
        g.Rect(10, 26, 12, 2, Dark);
        g.Rect(10, 26, 12, 1, Rust[1]);

        // Камера синтеза выше двора и выступает над основанием.
        Slab(g, 3, -5, 26, 26, 5, Iron);
        g.Circle(16, 6, 10, Ink);
        g.Circle(16, 6, 9, Dark);
        g.Ring(16, 6, 8, 1.5, Steel[2]);
        // Огни кольца бегут навстречу друг другу к ядру: энергия стекается внутрь.
        const int lights = 8;
        for (var i = 0; i < lights; i++)
        {
            var angle = (double)i / lights * Turn;
            var x = Round(16 + Math.Cos(angle) * 8);
            var lightY = Round(6 + Math.Sin(angle) * 8);
            Bulb(g, x - 1, lightY - 1, Chase(t * 2, i % 4 / 4.0));
        }
        // Ядро: золотое, когда материя готова, и гаснет к следующей порции.
        var glow = Pulse(t * 2);
        Lamp(g, 16, 6, 4, 0.35 + glow * 0.65);
        g.Circle(16, 6, 2, Gold3[glow > 0.5 ? 1 : 0]);
        if (glow > 0.8)
            g.Rect(15, 5, 1, 1, Gold3[2]);

        // Катушки по нижним углам.
        foreach (var (x, offset) in (ReadOnlySpan<(double, double)>)[(4, 0), (28, 0.5)])
        {
            Tower(g, x, 26, 2, 4, Steel);
            Bulb(g, x - 1, 20, Pulse(t * 2, offset));
        }
    });
}
