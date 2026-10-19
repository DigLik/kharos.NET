using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Кремниевый завод (siliconWorks, 3×2) — перенос siliconWorks из buildingArt.ts.</summary>
internal static class SiliconWorks
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Дуговая печь — три электрода в тигле, между ними трещат разряды, — две камеры, где из расплава тянут кристаллы,
    /// и стопки готовых пластин.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Silicon);
        var frame = (int)Math.Floor(t * Frames);
        Slab(g, 0, 2, 48, 30, 3, White);

        // Тигель печи с расплавом.
        const double cx = 14, cy = 15;
        g.Circle(cx, cy, 12, Ink);
        g.Circle(cx, cy, 11, Iron[1]);
        g.Circle(cx - 1, cy - 1, 10, Iron[2]);
        g.Circle(cx, cy, 8, Ink);
        // Расплав кварца рыжий от жара: на нём голубые дуги видны, а цвет кремния — у кристаллов и пластин.
        g.Circle(cx, cy, 7, working ? FireColor(0.45) : 0x262a30);
        g.Circle(cx - 1, cy - 1, 4, working ? FireColor(0.7) : 0x30353c);
        // Три электрода треугольником; в работе между ними и к расплаву бьют дуги, каждые два кадра — новые.
        var electrodes = new (double X, double Y)[3];
        for (var i = 0; i < 3; i++)
        {
            var angle = Turn * (i / 3.0) - Turn / 4;
            electrodes[i] = (Round(cx + Math.Cos(angle) * 5), Round(cy + Math.Sin(angle) * 5));
        }
        if (working)
        {
            var beat = Math.Floor(frame / 2.0);
            for (var i = 0; i < 3; i++)
            {
                var (x0, y0) = electrodes[i];
                var (x1, y1) = electrodes[(i + 1) % 3];
                var bend = (Hash(beat, i) - 0.5) * 4;
                var mx = (x0 + x1) / 2 + (cx - (x0 + x1) / 2) * 0.3 + bend;
                var my = (y0 + y1) / 2 + (cy - (y0 + y1) / 2) * 0.3 - bend;
                g.Line(x0, y0, mx, my, 1.5, Hash(beat, i + 5) > 0.3 ? Arc[2] : Arc[1]);
                g.Line(mx, my, x1, y1, 1.5, Arc[2]);
                g.Rect(Round(mx), Round(my), 1, 1, Arc[2]);
            }
        }
        foreach (var (x, y) in electrodes)
        {
            g.Circle(x, y, 2, Ink);
            g.Circle(x, y, 1.5, working ? Arc[1] : Iron[4]);
        }
        g.Light(cx, cy, 6, working ? 0.6 + 0.4 * Hash(Math.Floor(frame / 2.0), 11) : 0);

        // Камеры выращивания: круглые колпаки со смотровым окном, в работе внутри светится тянущийся кристалл.
        foreach (var (x, y, offset) in (ReadOnlySpan<(double, double, double)>)[(33, 10, 0), (33, 22, 0.5)])
        {
            g.Circle(x, y, 5, Ink);
            g.Circle(x, y, 4, White[2]);
            g.Circle(x - 1, y - 1, 2, White[4]);
            g.Circle(x, y, 2, working ? tint[1] : White[0]);
            if (working)
                g.Rect(x, y - 1 + Round(Pulse(t, offset)), 1, 1, tint[2]);
        }
        // Стопки готовых пластин: тонкие диски цвета кремния.
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(42, 9), (42, 17), (42, 25)])
        {
            g.Circle(x, y, 3, Ink);
            g.Circle(x, y, 2, tint[1]);
            g.Rect(x - 1, y - 2, 2, 1, tint[2]);
        }
        Lamp(g, 26, 26, 1, Pulse(t));
    }, 1, true);
}
