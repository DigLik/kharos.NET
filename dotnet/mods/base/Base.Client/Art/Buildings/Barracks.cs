using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Казармы (barracks, 3×2) — перенос barracks из buildingArt.ts.</summary>
internal static class Barracks
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Два спальных корпуса с плоскими крышами, между ними штаб со знаменем, перед ними плац с разметкой строя. По плацу
    /// ходит часовой.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 2, (g, t, _) =>
    {
        Slab(g, 0, 2, 48, 30, 3, Steel);

        // Плац: песчаная площадка с точками строя.
        g.Rect(3, 19, 42, 9, 0x6b5a3e);
        g.Rect(3, 19, 42, 1, 0x4d4030);
        for (var x = 7; x < 44; x += 5)
            foreach (var y in (ReadOnlySpan<int>)[22, 25])
                g.Rect(x, y, 1, 1, 0x8f7a55);
        // Часовой ходит вдоль плаца туда и обратно.
        var walk = Round(6 + 34 * (0.5 - 0.5 * Math.Cos(Turn * t)));
        g.Rect(walk, 22, 3, 3, Ink);
        g.Rect(walk + 1, 23, 1, 1, Team[2]);

        // Спальные корпуса по бокам: крыши с вентиляцией.
        foreach (var x in (ReadOnlySpan<int>)[2, 31])
        {
            Slab(g, x, 3, 15, 15, 3, Iron);
            g.Rect(x + 2, 6, 11, 1, Iron[0]);
            g.Rect(x + 2, 10, 11, 1, Iron[0]);
            g.Rect(x + 6, 7, 3, 2, Iron[3]);
        }
        // Штаб посередине со знаменем команды.
        Slab(g, 17, 1, 14, 17, 3, Steel);
        g.Rect(19, 3, 10, 9, Team[1]);
        g.Rect(19, 3, 10, 2, Team[2]);
        g.Rect(23, 6, 2, 3, Team[3]);
        g.Rect(21, 13, 6, 2, Ink);
        Bulb(g, 18, 15, Pulse(t));
        Bulb(g, 28, 15, Pulse(t, 0.5));
    });
}
