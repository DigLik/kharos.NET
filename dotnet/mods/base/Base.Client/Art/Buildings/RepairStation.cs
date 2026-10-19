using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Станция ремонта (repairStation, 3×3) — перенос repairStation из buildingArt.ts.</summary>
internal static class RepairStation
{
    private const double Turn = Math.PI * 2;

    /// <summary>Площадка с поворотным кругом, портал со сварочной головкой, будка оператора и стойка баллонов.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 3, (g, t, _) =>
    {
        Slab(g, 0, 2, 48, 46, 3, Steel);

        // Площадка: разметка по краю и поворотный круг посередине.
        g.Rect(3, 12, 32, 31, Ink);
        g.Rect(4, 13, 30, 29, Hazard[0]);
        for (var i = 0; i < 30; i += 4)
        {
            g.Rect(4 + i, 13, 2, 2, Hazard[1]);
            g.Rect(4 + i, 40, 2, 2, Hazard[1]);
        }
        for (var i = 0; i < 26; i += 4)
        {
            g.Rect(4, 15 + i, 2, 2, Hazard[1]);
            g.Rect(32, 15 + i, 2, 2, Hazard[1]);
        }
        g.Rect(7, 16, 24, 23, Iron[1]);
        g.Circle(19, 27, 10, Ink);
        g.Circle(19, 27, 9, Iron[2]);
        g.Circle(19, 27, 7, Iron[1]);
        // Круг медленно поворачивается: по нему бегут четыре метки.
        for (var i = 0; i < 4; i++)
        {
            var a = Turn * (t + i / 4.0);
            g.Rect(Round(19 + Math.Cos(a) * 8) - 1, Round(27 + Math.Sin(a) * 8) - 1, 2, 2, Iron[4]);
        }
        g.Rect(16, 24, 6, 6, Iron[3]);
        g.Rect(17, 25, 4, 4, Iron[2]);

        // Портал: две стойки и балка поперёк площадки.
        foreach (var x in (ReadOnlySpan<int>)[5, 31])
        {
            g.Rect(x - 1, 8, 4, 30, Ink);
            g.Rect(x, 9, 2, 28, Hazard[1]);
            g.Rect(x, 9, 1, 28, 0xf0d060);
        }
        g.Rect(4, 7, 30, 5, Ink);
        g.Rect(5, 8, 28, 3, Iron[3]);
        g.Rect(5, 8, 28, 1, Iron[4]);
        // Головка ездит по балке туда и обратно; трос опускает её к кругу.
        var head = Round(19 + Math.Sin(Turn * t) * 9);
        g.Rect(head - 3, 6, 6, 6, Ink);
        g.Rect(head - 2, 7, 4, 4, Steel[3]);
        g.Rect(head, 12, 1, 9, Iron[0]);
        g.Rect(head - 1, 21, 3, 3, Ink);
        g.Rect(head, 22, 1, 1, Team[3]);
        // Искры сварки вспыхивают и разлетаются вниз.
        var spark = Chase(t * 4, 0);
        g.Light(head, 23, 3, 0.3 + 0.7 * spark);
        if (spark > 0.2)
        {
            g.Rect(head - 2, 24, 1, 1, 0xfff0b0);
            g.Rect(head + 2, 25, 1, 1, 0xffd36a);
            g.Rect(head - 1, 26, 1, 1, 0xf08a2a);
        }

        // Будка оператора с окном и стойка баллонов.
        Slab(g, 36, 6, 10, 16, 4, Iron);
        g.Rect(38, 9, 6, 3, Team[1]);
        g.Rect(38, 9, 6, 1, Team[2]);
        Lamp(g, 41, 4, 1, Pulse(t * 2));
        for (var i = 0; i < 3; i++)
            Tower(g, 38 + i * 3, 38, 1, 8, i == 1 ? Brick : Steel);
        for (var i = 0; i < 3; i++)
            Bulb(g, 37, 25 + i * 4, Chase(t, i / 3.0));
    });
}
