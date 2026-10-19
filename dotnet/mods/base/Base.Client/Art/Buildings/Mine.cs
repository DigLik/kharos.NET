using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>
/// Шахта (mine, 2×2) — перенос mine из buildingArt.ts. Вариант — цвет руды: номер руды в ores.json плюс один, 0 — руда
/// неизвестна (7 вариантов). В работе шкив крутится, по тросу ходит бадья, по ленте бежит руда, над стволом поднимается
/// пыль, на бункере мигает маячок.
/// </summary>
internal static class Mine
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, Draw, working: true, variants: KitB.OreColors.Length + 1);

    private static void Draw(Canvas g, double t, bool working, int variant)
    {
        var ore = variant > 0 ? KitB.Accent(KitB.OreColors[variant - 1]) : Rust;
        Slab(g, 0, 1, 32, 31, 3, Steel);

        // Ствол шахты и лента от него к бункеру: в работе по ней идёт руда.
        g.Rect(4, 12, 14, 14, Ink);
        g.Rect(5, 13, 12, 12, Dark);
        g.Rect(5, 13, 12, 1, Iron[1]);
        g.Rect(17, 19, 7, 5, Ink);
        g.Rect(17, 20, 7, 3, Iron[0]);
        var step = (int)Math.Floor(t * Frames) % 8;
        if (working)
        {
            foreach (var at in (ReadOnlySpan<int>)[step, (step + 4) % 8])
            {
                if (at >= 6)
                    continue;
                g.Rect(17 + at, 20, 2, 2, ore[1]);
                g.Rect(17 + at, 20, 1, 1, ore[2]);
            }
        }

        // Бункер с рудой.
        Slab(g, 21, 10, 10, 18, 4, Iron);
        g.Rect(23, 12, 6, 9, Dark);
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(23, 17), (26, 16), (24, 19), (27, 19), (25, 13)])
        {
            g.Rect(x, y, 2, 2, ore[1]);
            g.Rect(x, y, 1, 1, ore[2]);
        }

        // Пыль над стволом: в работе — клубы, поднимающиеся от устья.
        if (working)
        {
            for (var i = 0; i < 3; i++)
            {
                var rise = ((int)Math.Floor(t * Frames) + i * 5) % 8;
                if (rise > 5)
                    continue;
                g.Rect(6 + i * 4 + rise % 2, 14 - rise, 2, 1, rise < 3 ? Iron[4] : Iron[3]);
            }
        }

        // Копёр: две ноги сходятся к шкиву над стволом.
        foreach (var x in (ReadOnlySpan<int>)[5, 16])
        {
            g.Line(x, 24, 11, -2, 3, Ink);
            g.Line(x, 24, 11, -2, 1.2, Iron[3]);
        }
        g.Rect(6, 10, 10, 1, Iron[2]);
        g.Circle(11, -2, 5, Ink);
        g.Circle(11, -2, 4, Iron[1]);
        // У шкива четыре спицы: четверть оборота замыкает цикл. Стоящая шахта шкив не крутит.
        var angle = working ? t * KitB.Turn * 0.25 : KitB.Turn / 8;
        foreach (var turn in (ReadOnlySpan<double>)[angle, angle + KitB.Turn / 4])
        {
            double dx = Math.Cos(turn) * 3.5, dy = Math.Sin(turn) * 3.5;
            g.Line(11 - dx, -2 - dy, 11 + dx, -2 + dy, 1.2, Iron[4]);
        }
        // Бадья на тросе — поверх копра: в работе ходит по стволу вниз и вверх, поднимая руду, стоящая — поднята.
        var swing = t * 2 % 1;
        var depth = working ? KitB.Round((swing < 0.5 ? swing * 2 : 2 - swing * 2) * 9) : 0;
        g.Rect(11, 3, 1, 7 + depth, Iron[2]);
        g.Rect(8, 9 + depth, 7, 5, Ink);
        g.Rect(9, 10 + depth, 5, 3, Iron[2]);
        g.Rect(9, 10 + depth, 5, 1, Iron[3]);
        if (working && swing >= 0.5)
            g.Rect(10, 10 + depth, 3, 1, ore[2]);

        // Маячок на бункере: в работе мигает жёлтым, стоящая шахта его гасит.
        var beacon = working ? Chase(t * 2, 0) : 0;
        g.Light(26, 9, 2, beacon * 0.8);
        g.Rect(24, 7, 5, 3, Ink);
        g.Rect(25, 8, 3, 2, beacon > 0.3 ? KitB.Gold[2] : beacon > 0 ? KitB.Gold[1] : KitB.Gold[0] & 0x5f5f5f);
        Bulb(g, 2, 6, working ? Pulse(t) : 0.08);
    }
}
