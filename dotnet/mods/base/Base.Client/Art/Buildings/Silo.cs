using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Ракетная шахта (silo, 3×3) — перенос silo из buildingArt.ts.</summary>
internal static class Silo
{
    /// <summary>Ступеней открытия створок и ступеней подъёма ракеты в открытом стволе (SILO_DOOR_STEPS, SILO_RISE_STEPS).</summary>
    public const int DoorSteps = 8, RiseSteps = 4;

    /// <summary>
    /// Бетонная плита в полосах опасности, посередине — ствол под двумя створками, слева — пульт. Вариант — ступень пуска:
    /// сначала створки расходятся (до DoorSteps − 1), потом в открытом стволе поднимается ракета. working — боеголовка
    /// готова: лампа пульта зелёная, у пустой — жёлтая.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 3, (g, t, variant, working) =>
    {
        var open = Math.Min(1, variant / (double)(DoorSteps - 1));
        var rise = Math.Max(0, variant - (DoorSteps - 1)) / (double)RiseSteps;
        Slab(g, 0, 2, 48, 46, 4, Concrete);
        for (var x = 2; x < 46; x += 4)
        {
            g.Rect(x, 4, 2, 2, Hazard[1]);
            g.Rect(x + 2, 4, 2, 2, Hazard[0]);
        }
        // Ствол: тёмный колодец с ребрами по стенкам.
        const double left = 16, top = 11, size = 22;
        g.Rect(left - 2, top - 2, size + 4, size + 4, Ink);
        g.Rect(left, top, size, size, Dark);
        for (var i = 2; i < size; i += 5)
            g.Rect(left + i, top, 1, size, Iron[0]);
        // Ракета в стволе: нос сверху — круг с красной макушкой; поднимаясь, он ближе и крупнее.
        if (open > 0.5)
        {
            var radius = 4 + rise * 3;
            const double cx = left + size / 2, cy = top + size / 2;
            g.Circle(cx, cy, radius + 1, Ink);
            g.Circle(cx, cy, radius, Iron[3]);
            g.Circle(cx - radius * 0.25, cy - radius * 0.25, radius * 0.6, Iron[4]);
            g.Circle(cx, cy, Math.Max(1, radius * 0.3), Fire[1]);
        }
        // Створки уходят под бетон в стороны.
        var shift = Round(open * (size / 2));
        void Door(double x, double w)
        {
            if (w <= 0)
                return;
            g.Rect(x, top, w, size, Steel[1]);
            g.Rect(x, top, w, 1, Steel[3]);
            for (var y = top + 3; y < top + size; y += 6)
                g.Rect(x, y, w, 2, Hazard[1]);
        }
        Door(left, size / 2 - shift);
        Door(left + size / 2 + shift, size / 2 - shift);
        if (shift == 0)
            g.Rect(left + size / 2, top, 1, size, Ink);
        // Рама ствола поверх створок.
        g.Rect(left - 2, top - 2, size + 4, 2, Concrete[2]);
        g.Rect(left - 2, top + size, size + 4, 2, Concrete[0]);
        g.Rect(left - 2, top, 2, size, Concrete[2]);
        g.Rect(left + size, top, 2, size, Concrete[0]);
        // Пульт слева: будка с окном и лампой готовности.
        Slab(g, 2, 14, 11, 16, 4, Iron);
        Windows(g, [4, 8], 18);
        var ready = working ? 0x5fe08au : 0xe0b84au;
        g.Rect(6, 24, 3, 3, Ink);
        g.Rect(7, 25, 1, 1, ready);
        g.Light(7.5, 25.5, 1.5, working ? 0.6 : 0.25 + 0.2 * Pulse(t));
        // Огни по углам ствола: в пуске бегут по кругу.
        (double X, double Y)[] corners = [(left - 3, top - 3), (left + size + 1, top - 3), (left + size + 1, top + size + 1), (left - 3, top + size + 1)];
        for (var i = 0; i < corners.Length; i++)
            Bulb(g, corners[i].X, corners[i].Y, open > 0 ? Chase(t * 2, i / 4.0) : 0.1);
        Lamp(g, 42, 40, 2, Pulse(t));
    }, DoorSteps + RiseSteps, true);
}
