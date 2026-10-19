using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Топливные баки (fuelTank) — перенос из buildingArt.ts: два круглых бака сверху, обвязка труб и задвижка.</summary>
internal static class FuelTank
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Fuel);
        Slab(g, 0, 1, 32, 31, 3, Steel);
        // Труба с обводкой: горизонтальная, длиной 26, осью по строке 26.
        g.Rect(3, 25, 26, 3, Ink);
        g.Rect(3, 26, 26, 1, Rust[1]);
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(10, 11), (22, 19)])
        {
            g.Circle(x, y, 8, Ink);
            g.Circle(x, y, 7, Iron[2]);
            g.Circle(x - 1, y - 1, 5, Iron[3]);
            // Плавающая крыша: кольцо цвета груза, по нему ходит блик.
            g.Ring(x, y, 4, 1, tint[1]);
            var angle = KitB.Turn * t;
            g.Rect(KitB.Round(x + Math.Cos(angle) * 4), KitB.Round(y + Math.Sin(angle) * 4), 1, 1, tint[2]);
            g.Circle(x, y, 1, Ink);
        }
        // Задвижка между баками.
        g.Rect(14, 13, 4, 4, Ink);
        g.Rect(15, 14, 2, 2, Hazard[1]);
        Bulb(g, 26, 4, Pulse(t));
    });
}
