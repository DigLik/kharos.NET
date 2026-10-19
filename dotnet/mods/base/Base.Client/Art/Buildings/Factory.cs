using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Завод (factory) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Factory
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Машинный завод, 3×3 — перенос factory из buildingArt.ts: сборочный цех под ребристой крышей с мостовым краном,
    /// ворота на юг и двор с пандусом, где стоит выкаченная машина. Вентилятор на крыше крутится, огни над воротами бегут.
    /// </summary>
    public static LookSpec Look(BuildingInfo _) => BuildingKit.Look(3, 3, (g, t, _) =>
    {
        Slab(g, 0, 2, 48, 46, 3, Steel);

        // Двор: бетон с разметкой и пандус от ворот.
        g.Rect(3, 30, 42, 14, Iron[1]);
        g.Rect(3, 30, 42, 1, Iron[0]);
        for (var x = 5; x <= 43; x += 6)
            g.Rect(x, 42, 3, 1, Hazard[1]);
        g.Rect(16, 30, 16, 12, Iron[0]);
        for (var y = 32; y <= 41; y += 3)
            g.Rect(17, y, 14, 1, Iron[2]);
        // Выкаченная машина на пандусе: корпус, гусеницы, башня.
        g.Rect(19, 33, 10, 8, Ink);
        g.Rect(19, 33, 2, 8, Iron[0]);
        g.Rect(27, 33, 2, 8, Iron[0]);
        g.Rect(21, 34, 6, 6, Team[1]);
        g.Rect(21, 34, 6, 1, Team[2]);
        g.Circle(24, 37, 2, Steel[3]);
        g.Rect(24, 31, 1, 4, Ink);

        // Цех: длинный корпус с рёбрами крыши, по которой ездит кран.
        Slab(g, 2, 0, 44, 30, 4, Iron);
        for (var x = 5; x <= 41; x += 4)
        {
            g.Rect(x, 2, 1, 22, Iron[0]);
            g.Rect(x + 1, 2, 1, 22, Iron[2]);
        }
        // Мостовой кран: балка поперёк цеха ходит вдоль него туда и обратно.
        var crane = Math.Round(8 + 28 * (0.5 - 0.5 * Math.Cos(Turn * t)), MidpointRounding.AwayFromZero);
        g.Rect(crane - 1, 2, 4, 22, Ink);
        g.Rect(crane, 2, 2, 22, Hazard[1]);
        g.Rect(crane - 1, 11, 4, 4, Iron[3]);
        // Ворота в торце цеха.
        g.Rect(16, 23, 16, 7, Ink);
        g.Rect(17, 24, 14, 6, Dark);
        for (var i = 0; i <= 6; i++)
            g.Rect(17 + i * 2, 22, 2, 1, TeamColor(Chase(t, i / 7.0)));

        // Вентилятор на крыше: у крыльчатки четыре лопасти, четверть оборота замыкает цикл.
        g.Circle(39, 8, 5, Ink);
        g.Circle(39, 8, 4, Iron[0]);
        var angle = t * Turn * 0.25;
        foreach (var turn in (ReadOnlySpan<double>)[angle, angle + Turn / 4])
        {
            double dx = Math.Cos(turn) * 3.5, dy = Math.Sin(turn) * 3.5;
            g.Line(39 - dx, 8 - dy, 39 + dx, 8 + dy, 1.2, Iron[3]);
        }
        Lamp(g, 7, 36, 2, Pulse(t));
        Lamp(g, 41, 36, 2, Pulse(t, 0.5));
    });
}
