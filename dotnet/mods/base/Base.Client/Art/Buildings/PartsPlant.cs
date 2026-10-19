using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Цех компонентов (partsPlant, 2×2) — перенос partsPlant из buildingArt.ts.</summary>
internal static class PartsPlant
{
    private const double Turn = Math.PI * 2;

    /// <summary>Чистая комната с зелёной платой, над которой ездит манипулятор и ставит микросхемы.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Parts);
        Slab(g, 0, 2, 32, 30, 3, White);

        // Плата: зелёное поле с дорожками и микросхемами.
        uint[] pcb = [0x0d3a22, 0x16603a, 0x2a8c55];
        g.Rect(3, 5, 26, 18, Ink);
        g.Rect(4, 6, 24, 16, pcb[1]);
        for (var y = 8; y < 21; y += 4)
            g.Rect(5, y, 22, 1, pcb[2]);
        for (var x = 9; x < 27; x += 6)
            g.Rect(x, 7, 1, 14, pcb[0]);
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(6, 9), (14, 9), (22, 9), (6, 16), (14, 16)])
        {
            g.Rect(x, y, 4, 3, Ink);
            g.Rect(x, y, 4, 1, 0x30363c);
        }
        // Светодиоды платы: в работе бегут, в простое тихо.
        for (var i = 0; i < 3; i++)
            Bulb(g, 22 + i % 2 * 3, 15 + i * 2, working ? Chase(t, i / 3.0) : 0);

        // Портал манипулятора: рельс поперёк, каретка ездит туда-сюда и опускает головку.
        var along = working ? 0.5 - 0.5 * Math.Cos(Turn * t) : 0;
        var hx = Round(6 + along * 18);
        g.Rect(3, 12, 26, 2, Ink);
        g.Rect(3, 12, 26, 1, White[3]);
        g.Rect(hx - 2, 10, 5, 6, Ink);
        g.Rect(hx - 1, 11, 3, 4, White[2]);
        g.Rect(hx, 12, 1, 2, working && along > 0.45 && along < 0.55 ? tint[2] : tint[0]);

        // Готовые компоненты в лотке внизу.
        g.Rect(3, 23, 26, 5, Ink);
        g.Rect(4, 24, 24, 3, White[1]);
        for (var x = 5; x < 27; x += 4)
        {
            g.Rect(x, 24, 3, 2, tint[0]);
            g.Rect(x, 24, 3, 1, tint[1]);
        }
        Lamp(g, 29, 4, 1, Pulse(t));
    }, 1, true);
}
