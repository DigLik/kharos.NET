namespace Base.Shared.Rules;

/// <summary>
/// Укрепление (fortify.ts): постройка в осях обороны — U от главного здания к врагу, V поперёк фронта. Главное здание —
/// в начале осей и занимает |U|, |V| ≤ 1; схема поворачивается к нужной стороне уже при постановке.
/// </summary>
public readonly record struct Piece(string Type, int U, int V);

/// <summary>Случайная оборона перед главным зданием (fortification): схема и набор турелей.</summary>
public static class Fortify
{
    /// <summary>Наборы турелей по весам: оборона то ровная, то с упором в пулемёты, ракеты или пушки.</summary>
    private static readonly (string Type, int Weight)[][] Mixes =
    [
        [("turret", 1), ("rocketTurret", 1), ("cannonTurret", 1)],
        [("turret", 3), ("rocketTurret", 1)],
        [("rocketTurret", 3), ("cannonTurret", 1)],
        [("cannonTurret", 2), ("turret", 1)],
        [("turret", 1), ("cannonTurret", 1)],
    ];

    /// <summary>Схема: какие клетки занять; turret — турель по набору обороны.</summary>
    private delegate List<Piece> Layout(Func<double> random, Func<string> turret, Func<double, double> cos, Func<double, double> sin);

    /// <summary>Схемы по порядку LAYOUTS: line, arc, bastions, wedge, depth.</summary>
    private static readonly Layout[] Layouts = [Line, Arc, Bastions, Wedge, Depth];

    /// <summary>
    /// Случайная оборона: схема и набор турелей; каждая клетка — один раз (где схема ставит две постройки в одно место,
    /// остаётся первая), рядом с главным зданием (|U|, |V| ≤ 2) — ничего. cos и sin — тригонометрия того, кто зовёт
    /// (в моде — DetMath); нет — Math.
    /// </summary>
    public static List<Piece> Fortification(Func<double> random, Func<double, double>? cos = null, Func<double, double>? sin = null)
    {
        var layout = Layouts[(int)Math.Floor(random() * Layouts.Length)];
        var pieces = layout(random, Picker(random, Mixes[(int)Math.Floor(random() * Mixes.Length)]), cos ?? Math.Cos, sin ?? Math.Sin);
        var taken = new HashSet<(int, int)>();
        return [.. pieces.Where(piece => !(Math.Abs(piece.U) <= 2 && Math.Abs(piece.V) <= 2) && taken.Add((piece.U, piece.V)))];
    }

    private static int Between(Func<double> random, int min, int max) => min + (int)Math.Floor(random() * (max - min + 1));

    private static Func<string> Picker(Func<double> random, (string Type, int Weight)[] mix)
    {
        var total = mix.Sum(entry => entry.Weight);
        return () =>
        {
            var roll = random() * total;
            foreach (var (type, weight) in mix)
                if ((roll -= weight) < 0)
                    return type;
            return mix[0].Type;
        };
    }

    /// <summary>Math.round из JS.</summary>
    private static int Round(double value) => (int)Math.Floor(value + 0.5);

    /// <summary>Прямая стена с одним-двумя проходами и ряд турелей за ней.</summary>
    private static List<Piece> Line(Func<double> random, Func<string> turret, Func<double, double> cos, Func<double, double> sin)
    {
        var pieces = new List<Piece>();
        var half = Between(random, 6, 10);
        var front = Between(random, 5, 7);
        int[] gates = random() < 0.5 ? [0] : [-(int)Math.Ceiling(half / 2.0), (int)Math.Ceiling(half / 2.0)];
        var gateWidth = Between(random, 1, 2);
        for (var v = -half - 1; v <= half + 1; v++)
        {
            if (gates.Any(gate => Math.Abs(v - gate) < gateWidth))
                continue;
            pieces.Add(new Piece("wall", front + 2, v));
        }
        var spacing = Between(random, 2, 4);
        for (var v = -half + 1; v <= half - 1; v += spacing)
            pieces.Add(new Piece(turret(), front, v));
        return pieces;
    }

    /// <summary>Дуга стены вокруг главного здания со стороны врага, с воротами посередине; турели — внутренним кольцом.</summary>
    private static List<Piece> Arc(Func<double> random, Func<string> turret, Func<double, double> cos, Func<double, double> sin)
    {
        var pieces = new List<Piece>();
        var radius = Between(random, 7, 10);
        var spread = 0.9 + random() * 0.6;
        var seen = new HashSet<(int, int)>();
        void Ring(double r, double step, Action<int, int, double> place)
        {
            for (var angle = -spread; angle <= spread; angle += step)
            {
                var (u, v) = (Round(cos(angle) * r), Round(sin(angle) * r));
                if (!seen.Add((u, v)))
                    continue;
                place(u, v, angle);
            }
        }
        var gate = 1.2 / radius;
        Ring(radius, 0.5 / radius, (u, v, angle) =>
        {
            if (Math.Abs(angle) > gate)
                pieces.Add(new Piece("wall", u, v));
        });
        Ring(radius - 2, Between(random, 2, 3) / (double)(radius - 2), (u, v, _) => pieces.Add(new Piece(turret(), u, v)));
        return pieces;
    }

    /// <summary>Бастионы: гнёзда из пары-тройки турелей, прикрытые стеной спереди и с боков; между ними — открыто.</summary>
    private static List<Piece> Bastions(Func<double> random, Func<string> turret, Func<double, double> cos, Func<double, double> sin)
    {
        var pieces = new List<Piece>();
        var count = Between(random, 2, 4);
        var gap = Between(random, 8, 11);
        var front = Between(random, 5, 8);
        for (var i = 0; i < count; i++)
        {
            var v0 = Round((i - (count - 1) / 2.0) * gap);
            var wide = random() < 0.5;
            int[] guns = wide ? [-1, 0, 1] : [0, 1];
            foreach (var dv in guns)
                pieces.Add(new Piece(turret(), front, v0 + dv));
            var left = v0 + guns[0] - 1;
            var right = v0 + guns[^1] + 1;
            for (var v = left; v <= right; v++)
                pieces.Add(new Piece("wall", front + 1, v));
            pieces.Add(new Piece("wall", front, left));
            pieces.Add(new Piece("wall", front, right));
            // Иногда гнездо глубже: вторая линия турелей позади.
            if (random() < 0.4)
                pieces.Add(new Piece(turret(), front - 1, v0));
        }
        return pieces;
    }

    /// <summary>Клин: стена углом к врагу, остриё — с воротами; турели вдоль обеих сторон клина.</summary>
    private static List<Piece> Wedge(Func<double> random, Func<string> turret, Func<double, double> cos, Func<double, double> sin)
    {
        var pieces = new List<Piece>();
        var tip = Between(random, 9, 12);
        var half = Between(random, 7, 10);
        var slope = 0.5 + random() * 0.5;
        var spacing = Between(random, 2, 3);
        for (var v = -half; v <= half; v++)
        {
            var u = Round(tip - Math.Abs(v) * slope);
            if (Math.Abs(v) > 1)
                pieces.Add(new Piece("wall", u, v));
            if (Math.Abs(v) >= 2 && Math.Abs(v) % spacing == 0)
                pieces.Add(new Piece(turret(), u - 2, v));
        }
        return pieces;
    }

    /// <summary>Две линии: дальняя — редкие турели без стены, ближняя — стена с турелями.</summary>
    private static List<Piece> Depth(Func<double> random, Func<string> turret, Func<double, double> cos, Func<double, double> sin)
    {
        var pieces = new List<Piece>();
        var half = Between(random, 6, 8);
        var outer = Between(random, 11, 13);
        for (var v = -half; v <= half; v += Between(random, 3, 5))
            pieces.Add(new Piece(turret(), outer, v));
        var inner = Between(random, 5, 6);
        for (var v = -half; v <= half; v++)
            if (Math.Abs(v) > 1)
                pieces.Add(new Piece("wall", inner + 1, v));
        for (var v = -half + 1; v <= half - 1; v += 3)
            pieces.Add(new Piece(turret(), inner - 1, v));
        return pieces;
    }
}
