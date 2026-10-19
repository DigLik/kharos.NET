using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Космопорт (spaceport) — перенос из buildingArt.ts: посадочная площадка с разметкой и бегущими огнями, сбоку диспетчерская башня.</summary>
internal static class Spaceport
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        Slab(g, 0, 1, 48, 47, 3, Steel);
        g.Rect(3, 4, 42, 38, Iron[0]);
        g.Rect(4, 5, 41, 37, Iron[1]);
        // Посадочный круг с крестом.
        g.Ring(22, 24, 15, 2, KitB.Gold[0]);
        g.Ring(22, 24, 8, 1, Iron[3]);
        g.Rect(21, 12, 2, 24, Iron[3]);
        g.Rect(10, 23, 24, 2, Iron[3]);
        g.Circle(22, 24, 3, Ink);
        g.Circle(22, 24, 2, KitB.Gold[1]);
        // Огни по кругу бегут к центру захода.
        const int lights = 8;
        for (var i = 0; i < lights; i++)
        {
            var angle = (double)i / lights * KitB.Turn;
            Bulb(g, KitB.Round(22 + Math.Cos(angle) * 18) - 1, KitB.Round(24 + Math.Sin(angle) * 17) - 1, Chase(t, (double)i / lights));
        }
        // Диспетчерская.
        Tower(g, 41, 12, 4, 9, Steel);
        Lamp(g, 41, 2, 2, Pulse(t * 2));
    });
}
