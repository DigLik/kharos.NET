using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Перехватчик (interceptor, 2×2) — перенос interceptor из buildingArt.ts.</summary>
internal static class Interceptor
{
    /// <summary>Ёмкость пакета: вариантов облика на один больше (число заряженных труб от 0 до 4).</summary>
    public const int Capacity = 4;

    /// <summary>
    /// Бетонная плита, на поворотном станке — пакет из четырёх пусковых труб, нацеленный в небо; слева — будка с плоской
    /// антенной наведения. Вариант — сколько ракет готово: в торцах заряженных труб виден красный нос. working — есть
    /// хоть одна: лампа зелёная, у пустого — жёлтая и мигает.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, variant, working) =>
    {
        Slab(g, 0, 2, 32, 30, 3, Concrete);
        for (var x = 2; x < 30; x += 4)
        {
            g.Rect(x, 27, 2, 1, Hazard[1]);
            g.Rect(x + 2, 27, 2, 1, Hazard[0]);
        }
        // Будка наведения и антенна над ней: плоская решётка под наклоном, по ней бежит огонёк.
        Slab(g, 2, 13, 10, 12, 3, Iron);
        Windows(g, [4], 17);
        g.Rect(3, 5, 9, 8, Ink);
        g.Rect(4, 6, 7, 6, Steel[1]);
        for (var x = 5; x < 11; x += 2)
            for (var y = 7; y < 12; y += 2)
                g.Rect(x, y, 1, 1, Steel[3]);
        Bulb(g, 4 + Round(Chase(t, 0) * 5), 6, Chase(t, 0));
        // Станок и пакет труб: смотрит вверх и вправо.
        Tower(g, 21, 21, 5, 2, Iron);
        const double fromX = 18, fromY = 22, toX = 25, toY = 9;
        g.Line(fromX, fromY, toX, toY, 11, Ink);
        g.Line(fromX, fromY, toX, toY, 9, Steel[1]);
        g.Line(fromX - 2.5, fromY, toX - 2.5, toY, 2, Steel[3]);
        g.Line(fromX + 3, fromY + 0.5, toX + 3, toY + 0.5, 1.5, Steel[0]);
        // Торцы труб — квадратом два на два; заряженные — с носом ракеты, первыми тратятся верхние.
        g.Rect(toX - 5, toY - 5, 10, 10, Ink);
        g.Rect(toX - 4, toY - 4, 8, 8, Steel[2]);
        g.Rect(toX - 4, toY - 4, 8, 1, Steel[3]);
        (double X, double Y)[] ports = [(toX - 2, toY - 2), (toX + 2, toY - 2), (toX - 2, toY + 2), (toX + 2, toY + 2)];
        for (var i = 0; i < ports.Length; i++)
        {
            var (x, y) = ports[i];
            var loaded = i >= ports.Length - variant;
            g.Circle(x, y, 1.9, loaded ? Iron[3] : Dark);
            if (loaded)
            {
                g.Circle(x, y, 1.2, Fire[1]);
                g.Rect(Math.Floor(x) - 1, Math.Floor(y) - 1, 1, 1, Fire[3]);
            }
        }
        var ready = working ? 0x5fe08au : 0xe0b84au;
        g.Rect(14, 22, 3, 3, Ink);
        g.Rect(15, 23, 1, 1, ready);
        g.Light(15.5, 23.5, 1.5, working ? 0.6 : 0.2 + 0.3 * Pulse(t * 2));
    }, Capacity + 1, true);
}
