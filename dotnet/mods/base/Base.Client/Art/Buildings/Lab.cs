using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Лаборатория (lab, 2×2) — перенос lab из buildingArt.ts.</summary>
internal static class Lab
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Корпус под стеклянным куполом, где висит голограмма — каркас куба, и две колбы с реактивом на пристройке. В работе
    /// голограмма вращается и светится ярче, а в колбах поднимаются пузыри; в простое каркас стоит тусклым.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        Slab(g, 0, 2, 32, 30, 3, Steel);
        // Корпус с окнами и дверью.
        Slab(g, 2, 12, 20, 16, 4, Iron);
        Windows(g, [4, 17], 19);
        g.Rect(9, 19, 6, 6, Ink);
        g.Rect(10, 20, 4, 5, Dark);
        Bulb(g, 11, 16, working ? 1 : Pulse(t));

        // Стеклянный купол с голограммой.
        uint[] glass = [0x123047, 0x1d4a6b, 0x2f6f96, 0x5aa0c8, 0xb8e2f5];
        g.Circle(12, 10, 8, Ink);
        g.Circle(12, 10, 7, glass[0]);
        g.Circle(11, 9, 6, glass[1]);
        var angle = working ? t * Turn : Turn / 8;
        (double X, double Y) Corner(int k, double lift)
        {
            var a = angle + k * Turn / 4;
            return (12 + Math.Cos(a) * 3.5, 11 + Math.Sin(a) * 1.5 - lift);
        }
        for (var k = 0; k < 4; k++)
        {
            var (x1, y1) = Corner(k, 0);
            var (x2, y2) = Corner(k + 1, 0);
            var (x3, y3) = Corner(k, 4);
            var (x4, y4) = Corner(k + 1, 4);
            g.Line(x1, y1, x2, y2, 1, working ? Team[2] : Team[0]);
            g.Line(x3, y3, x4, y4, 1, working ? Team[2] : Team[0]);
            g.Line(x1, y1, x3, y3, 1, working ? Team[1] : Team[0]);
        }
        g.Light(12, 9, 3, working ? 0.7 + 0.3 * Pulse(t * 2) : 0.15);
        g.Rect(8, 5, 2, 1, glass[4]);
        g.Rect(7, 6, 1, 2, glass[3]);

        // Пристройка с колбами: реактив светится, пока идёт работа, пузыри поднимаются и замыкаются по t.
        Slab(g, 23, 14, 8, 14, 3, Iron);
        uint[] liquid = [0x2a8f6a, 0x49d39a, 0xa8f5d0];
        foreach (var (x, offset) in (ReadOnlySpan<(int, double)>)[(24, 0), (28, 0.5)])
        {
            g.Rect(x, 7, 3, 9, Ink);
            g.Rect(x + 1, 8, 1, 7, glass[1]);
            g.Rect(x + 1, 11, 1, 4, working ? liquid[1] : liquid[0]);
            if (working)
            {
                var rise = (t * 2 + offset) % 1 * 3;
                g.Rect(x + 1, Round(13 - rise), 1, 1, liquid[2]);
            }
            g.Rect(x, 6, 3, 1, Iron[3]);
        }
        g.Light(26, 12, 2, working ? 0.5 + 0.2 * Pulse(t, 0.3) : 0);
    }, 1, true);
}
