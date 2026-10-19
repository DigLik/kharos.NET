using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Склад снабжения (supplyDepot, 3×2) — перенос supplyDepot из buildingArt.ts.</summary>
internal static class SupplyDepot
{
    private const double Turn = Math.PI * 2;

    /// <summary>Штабель ящиков, площадка погрузки в полосах опасности и поворотный кран с излучателем луча.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 2, (g, t, _) =>
    {
        var tint = Accent(GoodColor.Ammo);
        Slab(g, 0, 1, 48, 31, 3, Iron);
        // Площадка погрузки справа: жёлто-чёрная рамка, внутри — бетон.
        g.Rect(24, 4, 21, 22, Ink);
        g.Rect(25, 5, 19, 20, Iron[2]);
        for (var i = 0; i < 19; i += 4)
        {
            g.Rect(25 + i, 5, 2, 1, Hazard[1]);
            g.Rect(25 + i, 24, 2, 1, Hazard[1]);
        }
        for (var i = 0; i < 20; i += 4)
        {
            g.Rect(25, 5 + i, 1, 2, Hazard[1]);
            g.Rect(43, 5 + i, 1, 2, Hazard[1]);
        }
        g.Rect(27, 7, 15, 16, Iron[1]);
        // Штабель ящиков слева, три ряда по три.
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var x = 3 + col * 6;
                var y = 4 + row * 7;
                g.Rect(x, y, 6, 6, Ink);
                g.Rect(x + 1, y + 1, 4, 4, tint[1]);
                g.Rect(x + 1, y + 1, 4, 1, tint[2]);
                g.Rect(x + 2, y + 3, 2, 1, tint[0]);
            }
        }
        // Поворотный кран между штабелем и площадкой: стрела ходит над площадкой, на конце — излучатель луча.
        var angle = -Math.PI / 4 + Math.Sin(Turn * t) * 0.6;
        const double baseX = 22, baseY = 15;
        var tipX = baseX + Math.Cos(angle) * 12;
        var tipY = baseY + Math.Sin(angle) * 12 + 4;
        g.Line(baseX, baseY, tipX, tipY, 3, Ink);
        g.Line(baseX, baseY, tipX, tipY, 1, Hazard[1]);
        Tower(g, baseX, baseY + 2, 3, 2, Steel);
        g.Circle(tipX, tipY, 2.2, Ink);
        g.Circle(tipX, tipY, 1.4, 0x5ff2d0);
        g.Light(Round(tipX), Round(tipY), 2, 0.4 + 0.4 * Pulse(t * 2));
        // Огни погрузки по углам площадки бегут по кругу.
        (double X, double Y)[] corners = [(26, 6), (42, 6), (42, 22), (26, 22)];
        for (var i = 0; i < corners.Length; i++)
            Bulb(g, corners[i].X - 1, corners[i].Y - 1, Chase(t, (double)i / corners.Length));
    });
}
