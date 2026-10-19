namespace Base.Client;

/// <summary>
/// Граница зоны строительства пунктиром — перенос outlineOf из cursorPass.ts. Зона — круги (x, y и радиус подряд); точка
/// окружности внутри соседнего круга границей не считается. Соседей точка ищет по сетке, а не среди всех кругов: у большой
/// базы кругов сотни, и перебор каждого с каждым — десятки миллионов проверок.
/// </summary>
internal static class ZoneOutline
{
    /// <summary>Сколько штрихов на круг радиуса <see cref="ControlRadius"/>: на меньших их столько, чтобы шаг был тем же.</summary>
    public const int Dashes = 120;

    /// <summary>Радиус зоны главного здания (data/buildings.json, command.zone): по нему считается шаг точек.</summary>
    public const double ControlRadius = 12;

    /// <summary>Насколько точка должна войти в соседний круг, чтобы не считаться границей.</summary>
    private const double Inside = 0.01;

    /// <summary>Сколько штрихов у круга радиуса radius.</summary>
    public static int DashesOf(double radius, double controlRadius = ControlRadius) => (int)Math.Floor(Dashes * radius / controlRadius + 0.5);

    private static long KeyOf(double x, double y, double cell) =>
        ((long)Math.Floor(x / cell) + 32768) * 65536 + (long)Math.Floor(y / cell) + 32768;

    /// <summary>Точки внешней границы зоны, x и y подряд. zone — круги: x, y и радиус подряд.</summary>
    public static double[] Of(IReadOnlyList<double> zone, double controlRadius = ControlRadius)
    {
        var cell = 1.0;
        for (var i = 2; i < zone.Count; i += 3)
            cell = Math.Max(cell, zone[i]);
        // Сторона ячейки — самый большой радиус: круг, накрывающий точку, лежит центром в её ячейке или в соседней.
        var cells = new Dictionary<long, List<int>>();
        for (var i = 0; i + 2 < zone.Count; i += 3)
        {
            var key = KeyOf(zone[i], zone[i + 1], cell);
            if (cells.TryGetValue(key, out var list))
                list.Add(i);
            else
                cells[key] = [i];
        }
        var dots = new List<double>();
        for (var i = 0; i + 2 < zone.Count; i += 3)
        {
            var radius = zone[i + 2];
            // Точки идут с одним шагом на любом круге, поэтому на малом их меньше.
            var dashes = DashesOf(radius, controlRadius);
            for (var dash = 0; dash < dashes; dash++)
            {
                var angle = (double)dash / dashes * Math.PI * 2;
                var x = zone[i] + Math.Cos(angle) * radius;
                var y = zone[i + 1] + Math.Sin(angle) * radius;
                if (!Covered(zone, cells, cell, i, x, y))
                {
                    dots.Add(x);
                    dots.Add(y);
                }
            }
        }
        return [.. dots];
    }

    private static bool Covered(IReadOnlyList<double> zone, Dictionary<long, List<int>> cells, double cell, int self, double x, double y)
    {
        for (var dy = -cell; dy <= cell; dy += cell)
            for (var dx = -cell; dx <= cell; dx += cell)
            {
                if (!cells.TryGetValue(KeyOf(x + dx, y + dy, cell), out var list))
                    continue;
                foreach (var other in list)
                {
                    var (ox, oy) = (x - zone[other], y - zone[other + 1]);
                    if (other != self && Math.Sqrt(ox * ox + oy * oy) < zone[other + 2] - Inside)
                        return true;
                }
            }
        return false;
    }
}
