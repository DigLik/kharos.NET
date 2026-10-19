using Base.Host.Sections.Construction;

namespace Base.Host.Sections.Pipes;

/// <summary>Кратчайшие пути от одного узла сети (Routes в pipes.ts): длина до каждого узла и следующий шаг к началу.</summary>
internal sealed class Routes
{
    public readonly Dictionary<uint, double> Dist = [];
    public readonly Dictionary<uint, uint> Next = [];
}

/// <summary>
/// Граф сети труб (graphOf в pipes.ts): соседи узлов по рёбрам сети и посчитанные пути от узлов. Кэш, производный от мира
/// (Н5-3): сеть — объект кэша зон (<see cref="Zones"/>, ключ — ревизии мира), граф живёт, пока жив тот же набор сетей;
/// пересчитали зоны — графы считаются заново. Экземпляр выбросили — тот же мир даст те же графы.
/// </summary>
internal sealed class Graph
{
    private static object? _key;
    private static readonly Dictionary<Zones.Zone, Graph> Graphs = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<uint, List<(uint Other, double Length)>> _links = [];
    private readonly Dictionary<uint, Routes> _routes = [];
    private readonly HashSet<uint> _sites;

    private Graph(Zones.Zone zone)
    {
        void Add(uint a, uint b, double length)
        {
            if (!_links.TryGetValue(a, out var list))
                _links[a] = list = [];
            list.Add((b, length));
        }
        var edges = zone.Edges;
        for (var i = 0; i + 2 < edges.Count; i += 3)
        {
            Add((uint)edges[i], (uint)edges[i + 1], edges[i + 2]);
            Add((uint)edges[i + 1], (uint)edges[i], edges[i + 2]);
        }
        _sites = [.. zone.Sites];
    }

    /// <summary>Граф сети (graphOf).</summary>
    public static Graph Of(Zones.Zone zone)
    {
        var all = Zones.All();
        if (!ReferenceEquals(all, _key))
        {
            Graphs.Clear();
            _key = all;
        }
        if (!Graphs.TryGetValue(zone, out var graph))
            Graphs[zone] = graph = new Graph(zone);
        return graph;
    }

    /// <summary>Кратчайшие пути по трубам сети от узла start до всех остальных (routesFrom: Дейкстра по графу сети).</summary>
    public static Routes RoutesFrom(Zones.Zone zone, uint start) => Of(zone).From(start);

    private Routes From(uint start)
    {
        if (_routes.TryGetValue(start, out var found))
            return found;
        found = new Routes();
        var dist = found.Dist;
        var next = found.Next;
        dist[start] = 0;
        // Двоичная куча пар (длина, узел) — та же, что в TS: при равных длинах порядок обхода тот же.
        var heap = new List<(double Length, uint Node)> { (0, start) };
        void Swap(int i, int j) => (heap[i], heap[j]) = (heap[j], heap[i]);
        while (heap.Count > 0)
        {
            var (length, node) = heap[0];
            var last = heap[^1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0)
            {
                heap[0] = last;
                for (var i = 0; ;)
                {
                    var left = i * 2 + 1;
                    var right = left + 1;
                    var least = i;
                    if (left < heap.Count && heap[left].Length < heap[least].Length)
                        least = left;
                    if (right < heap.Count && heap[right].Length < heap[least].Length)
                        least = right;
                    if (least == i)
                        break;
                    Swap(i, least);
                    i = least;
                }
            }
            if (length > dist[node])
                continue;
            // Через стройку груз не идёт: она только принимает.
            if (node != start && _sites.Contains(node))
                continue;
            if (!_links.TryGetValue(node, out var links))
                continue;
            foreach (var (other, step) in links)
            {
                var total = length + step;
                if (total >= (dist.TryGetValue(other, out var known) ? known : double.PositiveInfinity))
                    continue;
                dist[other] = total;
                next[other] = node;
                heap.Add((total, other));
                for (var i = heap.Count - 1; i > 0;)
                {
                    var up = (i - 1) >> 1;
                    if (heap[up].Length <= heap[i].Length)
                        break;
                    Swap(i, up);
                    i = up;
                }
            }
        }
        _routes[start] = found;
        return found;
    }
}
