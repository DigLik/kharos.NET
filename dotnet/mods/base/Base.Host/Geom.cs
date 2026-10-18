namespace Base.Host;

/// <summary>Геометрия, общая для правил (lib/geom.luau).</summary>
internal static class Geom
{
    public const double Turn = Math.PI * 2;

    /// <summary>Угол в (−π, π].</summary>
    public static double Wrap(double angle) => angle - Turn * Math.Floor(angle / Turn + 0.5);

    /// <summary>Места группы вокруг (x, y): квадратной спиралью через spacing, первый в центре.</summary>
    public static (double X, double Y)[] Spread(int count, double x, double y, double spacing)
    {
        var places = new (double, double)[count];
        int ring = 0, side = 0, step = 0, px = 0, py = 0;
        for (var i = 0; i < count; i++)
        {
            places[i] = (x + px * spacing, y + py * spacing);
            // Квадратная спираль: вправо, вниз, влево, вверх, каждый виток длиннее.
            if (step == 0)
            {
                ring++;
                (px, py) = (-ring, -ring);
                (side, step) = (0, ring * 8);
            }
            var edge = ring * 2;
            if (side < edge)
                px++;
            else if (side < edge * 2)
                py++;
            else if (side < edge * 3)
                px--;
            else
                py--;
            side++;
            step--;
        }
        return places;
    }
}
