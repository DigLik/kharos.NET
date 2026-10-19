using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Ветроуловитель (windtrap, 2×2) — перенос windtrap из buildingArt.ts.</summary>
internal static class Windtrap
{
    private const double Turn = Math.PI * 2;

    /// <summary>Широкая шахта с крыльчаткой.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _) =>
    {
        Slab(g, 0, 2, 32, 30, 3, Steel);
        foreach (var x in (ReadOnlySpan<int>)[3, 25])
        {
            g.Rect(x, 5, 4, 1, Rust[1]);
            g.Rect(x, 26, 4, 1, Rust[1]);
        }

        Tower(g, 16, 17, 11, 4, Steel);
        g.Circle(16, 13, 8, Ink);
        g.Circle(16, 13, 7, Dark);
        // Четыре лопасти: четверть оборота замыкает цикл.
        var angle = t * Turn * 0.25;
        foreach (var turn in (ReadOnlySpan<double>)[angle, angle + Turn / 4])
        {
            var dx = Math.Cos(turn) * 7;
            var dy = Math.Sin(turn) * 7;
            g.Line(16 - dx, 13 - dy, 16 + dx, 13 + dy, 2, Iron[2]);
        }
        Lamp(g, 16, 13, 2, Pulse(t * 2));
    });
}
