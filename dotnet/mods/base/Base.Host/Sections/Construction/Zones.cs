using Base.Host.Kernel;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Зоны строительства — они же сети труб (zones.ts): связная группа готовых зданий и труб игрока. Здания связаны
/// трубой или стоят вплотную стороной к стороне, но только с таким же зданием; колодец — ещё и с ближайшим своим
/// колодцем по прямой не дальше UNDERGROUND_REACH. Зону сети составляют круги её зданий. Энергосети — сети, слитые
/// по касанию основания с кругом.
/// <para>
/// Кэш, производный от мира (docs/dotnet-n5.md, Н5-3): считается по миру до вызова и помнится в экземпляре мода с
/// ключом — ревизиями состава и мест (<see cref="Tools.Revision"/>). Экземпляр выбросили — пересчёт по тому же миру
/// даст то же. Записи этого же вызова (новое здание) кэш не видит до следующего вызова.
/// </para>
/// </summary>
internal static class Zones
{
    /// <summary>Сеть: круги x, y, радиус подряд; здания и трубы (главное — первым); подключённые стройки; рёбра a, b, длина подряд.</summary>
    public sealed class Zone
    {
        public List<double> Circles { get; } = [];
        public List<uint> Buildings { get; } = [];
        public List<uint> Sites { get; } = [];
        public List<double> Edges { get; } = [];
    }

    /// <summary>Здание в кэше: место, размер, вид, хозяин.</summary>
    private readonly record struct Member(uint Entity, int X, int Y, int W, int H, string Type, uint Player);

    private static (ulong World, ulong Positions) _key = (ulong.MaxValue, ulong.MaxValue);
    private static Dictionary<uint, List<Zone>> _zones = [];
    private static Dictionary<uint, Zone> _index = [];
    private static Dictionary<uint, List<Zone>>? _grids;
    private static Dictionary<uint, Zone>? _gridIndex;

    private static readonly List<Zone> None = [];

    private static readonly (int Dx, int Dy)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Сети труб всех игроков (allZones): у каждого — сначала сети с главным зданием.</summary>
    public static IReadOnlyDictionary<uint, List<Zone>> All()
    {
        Refresh();
        return _zones;
    }

    /// <summary>Сети игрока (zonesOf).</summary>
    public static IReadOnlyList<Zone> Of(uint player) => All().GetValueOrDefault(player) ?? None;

    /// <summary>Все круги зон игрока подряд (zoneOf).</summary>
    public static List<double> CirclesOf(uint player)
    {
        var circles = new List<double>();
        foreach (var zone in Of(player))
            circles.AddRange(zone.Circles);
        return circles;
    }

    /// <summary>Сеть здания, трубы или подключённой стройки (networkOf); нет — null.</summary>
    public static Zone? NetworkOf(uint entity)
    {
        Refresh();
        return _index.GetValueOrDefault(entity);
    }

    /// <summary>Энергосети всех игроков (allGrids).</summary>
    public static IReadOnlyDictionary<uint, List<Zone>> Grids()
    {
        Refresh();
        if (_grids is null)
        {
            _grids = [];
            foreach (var (player, list) in _zones)
                _grids[player] = MergeTouching(list);
        }
        return _grids;
    }

    /// <summary>Энергосети игрока (gridsOf).</summary>
    public static IReadOnlyList<Zone> GridsOf(uint player) => Grids().GetValueOrDefault(player) ?? None;

    /// <summary>Энергосеть здания или трубы (gridOf); нет — null.</summary>
    public static Zone? GridOf(uint entity)
    {
        var grids = Grids();
        if (_gridIndex is null)
        {
            _gridIndex = [];
            foreach (var list in grids.Values)
                foreach (var grid in list)
                    foreach (var member in grid.Buildings)
                        _gridIndex[member] = grid;
        }
        return _gridIndex.GetValueOrDefault(entity);
    }

    /// <summary>Задевает ли основание чужую зону (inForeignZone): там, где зоны двух игроков перекрываются, не строит ни один.</summary>
    public static bool InForeignZone(uint player, int x, int y, int w, int h)
    {
        foreach (var (other, zones) in All())
        {
            if (other == player)
                continue;
            foreach (var zone in zones)
                if (BuildRules.TouchesCircles(zone.Circles, x, y, w, h))
                    return true;
        }
        return false;
    }

    /// <summary>Забыть посчитанное (resetZones): следующий вызов пересчитает.</summary>
    public static void Reset() => _key = (ulong.MaxValue, ulong.MaxValue);

    private static void Refresh()
    {
        var revision = Tools.Revision();
        var key = (revision.World, revision.Positions);
        if (key == _key)
            return;
        _key = key;
        _grids = null;
        _gridIndex = null;
        Compute();
    }

    private static void Compute()
    {
        // Здания мира: место, размер, вид, хозяин, стройка ли — пакетом.
        var entities = Core.All(S.Building);
        var values = new double[entities.Length * 5];
        var has = new bool[entities.Length * 5];
        Tools.Columns(entities, [(S.Position, S.PositionX), (S.Position, S.PositionY), (S.Proto, S.ProtoId), (S.Owner, S.OwnerPlayer), (S.Site, Refs.R.SiteDemolish)], values, has);
        var n = entities.Length;
        var occupancy = new Dictionary<(int, int), Member>();
        var all = new List<Member>(n);
        // Входят в сети: готовые здания и трубы с хозяином (не стройки, не изолированные).
        var members = new Dictionary<uint, Member>();
        for (var i = 0; i < n; i++)
        {
            var type = Core.ProtoName((int)values[2 * n + i], "buildings");
            if (type is null || Factory.Building(type) is not { } spec)
                continue;
            var member = new Member(entities[i], (int)values[i], (int)values[n + i], (int)spec.Width, (int)spec.Height, type, (uint)values[3 * n + i]);
            all.Add(member);
            for (var ty = member.Y; ty < member.Y + member.H; ty++)
                for (var tx = member.X; tx < member.X + member.W; tx++)
                    occupancy[(tx, ty)] = member;
            if (has[3 * n + i] && !has[4 * n + i] && !spec.Isolated)
                members[member.Entity] = member;
        }
        var parent = new Dictionary<uint, uint>();
        uint Find(uint entity)
        {
            var root = entity;
            while (parent.TryGetValue(root, out var up))
                root = up;
            while (entity != root)
            {
                var next = parent[entity];
                parent[entity] = root;
                entity = next;
            }
            return root;
        }
        var edges = new List<double>();
        void Link(uint a, uint b, double length)
        {
            edges.Add(a);
            edges.Add(b);
            edges.Add(length);
            var (one, two) = (Find(a), Find(b));
            if (one != two)
                parent[two] = one;
        }
        Member? At(int x, int y) => occupancy.TryGetValue((x, y), out var found) ? found : null;
        string? PipeOf(Member member) => Factory.Building(member.Type)!.Pipe;
        bool Joins(Member? other, uint player) => other is { } found && members.TryGetValue(found.Entity, out var joined) && joined.Player == player;

        foreach (var member in all)
        {
            var kind = Factory.Building(member.Type)!.Pipe;
            if (kind is null || !members.ContainsKey(member.Entity))
                continue;
            foreach (var (dx, dy) in Sides)
            {
                var other = At(member.X + dx, member.Y + dy);
                if (!Joins(other, member.Player))
                    continue;
                // Две соседние трубы видят друг друга дважды: ребро — от меньшей.
                if (PipeOf(other!.Value) is not null && other.Value.Entity < member.Entity)
                    continue;
                Link(member.Entity, other.Value.Entity, 1);
            }
            if (kind != "well")
                continue;
            // Подземный отрезок — к ближайшему своему колодцу вправо и вниз: влево и вверх его найдёт тот колодец.
            foreach (var (dx, dy) in ((int, int)[])[(1, 0), (0, 1)])
                for (var step = 2; step <= BuildRules.UndergroundReach; step++)
                {
                    var other = At(member.X + dx * step, member.Y + dy * step);
                    if (!Joins(other, member.Player) || PipeOf(other!.Value) != "well")
                        continue;
                    Link(member.Entity, other.Value.Entity, step);
                    break;
                }
        }

        // Здания вплотную: стык — от меньшего к большему, только у зданий одного вида.
        foreach (var member in all)
        {
            if (!members.ContainsKey(member.Entity) || Factory.Building(member.Type)!.Pipe is not null)
                continue;
            foreach (var other in Neighbours(member, At))
            {
                if (other.Entity < member.Entity || !Joins(other, member.Player) || PipeOf(other) is not null || other.Type != member.Type)
                    continue;
                var length = Math.Abs(other.X + other.W / 2.0 - member.X - member.W / 2.0) + Math.Abs(other.Y + other.H / 2.0 - member.Y - member.H / 2.0);
                Link(member.Entity, other.Entity, Math.Max(1, BuildRules.Round(length)));
            }
        }

        var byRoot = new Dictionary<uint, Zone>();
        var result = new Dictionary<uint, List<Zone>>();
        var players = new List<uint>();
        foreach (var member in all)
        {
            if (!members.ContainsKey(member.Entity))
                continue;
            var root = Find(member.Entity);
            if (!byRoot.TryGetValue(root, out var zone))
            {
                byRoot[root] = zone = new Zone();
                if (!result.TryGetValue(member.Player, out var list))
                {
                    result[member.Player] = list = [];
                    players.Add(member.Player);
                }
                list.Add(zone);
            }
            var spec = Factory.Building(member.Type)!;
            zone.Circles.Add(member.X + member.W / 2.0);
            zone.Circles.Add(member.Y + member.H / 2.0);
            zone.Circles.Add(BuildRules.ZoneRadius(spec));
            if (member.Type == BuildRules.Core)
                zone.Buildings.Insert(0, member.Entity);
            else
                zone.Buildings.Add(member.Entity);
        }
        for (var i = 0; i + 2 < edges.Count; i += 3)
        {
            var zone = byRoot[Find((uint)edges[i])];
            zone.Edges.Add(edges[i]);
            zone.Edges.Add(edges[i + 1]);
            zone.Edges.Add(edges[i + 2]);
        }

        // Стройка входит в сеть первой трубы, подведённой к её основанию сбоку, или здания того же вида вплотную.
        var sites = Core.All(S.Site);
        foreach (var site in sites)
        {
            if (Tools.Has(site, S.Building) || !Tools.Has(site, S.Stock) || Tools.Get(site, S.Site) is not { } work || work.Number(Refs.R.SiteDemolish) != 0)
                continue;
            if (Tools.Get(site, S.Proto) is not { } proto || Core.ProtoName((int)proto.Number(S.ProtoId), "buildings") is not { } type || Factory.Building(type) is not { } spec)
                continue;
            var (owner, position) = (Tools.Get(site, S.Owner), Tools.Get(site, S.Position));
            if (owner is null || position is null)
                continue;
            var player = (uint)owner.Number(S.OwnerPlayer);
            var (x, y, w, h) = ((int)position.Number(S.PositionX), (int)position.Number(S.PositionY), (int)spec.Width, (int)spec.Height);
            Zone? zone = null;
            void Touch(int tx, int ty)
            {
                var other = At(tx, ty);
                if (!Joins(other, player))
                    return;
                if (PipeOf(other!.Value) is null && other.Value.Type != type)
                    return;
                var found = byRoot[Find(other.Value.Entity)];
                if (zone is not null && zone != found)
                    return;
                if (zone is null)
                    found.Sites.Add(site);
                zone = found;
                zone.Edges.Add(site);
                zone.Edges.Add(other.Value.Entity);
                zone.Edges.Add(1);
            }
            for (var tx = x; tx < x + w; tx++)
            {
                Touch(tx, y - 1);
                Touch(tx, y + h);
            }
            for (var ty = y; ty < y + h; ty++)
            {
                Touch(x - 1, ty);
                Touch(x + w, ty);
            }
        }
        _coreOf = [.. all.Where(member => member.Type == BuildRules.Core).Select(member => member.Entity)];
        _bases = all.ToDictionary(member => member.Entity, member => (member.X, member.Y, member.W, member.H));
        // Сети с главным зданием — первыми (устойчиво: остальной порядок прежний).
        _zones = [];
        foreach (var player in players)
            _zones[player] = [.. result[player].OrderBy(zone => IsCore(zone.Buildings) ? 0 : 1)];
        _index = [];
        foreach (var zones in _zones.Values)
            foreach (var zone in zones)
            {
                foreach (var entity in zone.Buildings)
                    _index[entity] = zone;
                foreach (var entity in zone.Sites)
                    _index[entity] = zone;
            }
    }

    private static HashSet<uint> _coreOf = [];
    private static Dictionary<uint, (int X, int Y, int W, int H)> _bases = [];

    private static bool IsCore(List<uint> buildings) => buildings.Count > 0 && _coreOf.Contains(buildings[0]);

    /// <summary>Соседи по сторонам основания (neighbours): каждый один раз, по углу — не соседи.</summary>
    private static List<Member> Neighbours(Member member, Func<int, int, Member?> at)
    {
        var found = new List<Member>();
        void See(int x, int y)
        {
            if (at(x, y) is { } other && other.Entity != member.Entity && !found.Contains(other))
                found.Add(other);
        }
        for (var x = member.X; x < member.X + member.W; x++)
        {
            See(x, member.Y - 1);
            See(x, member.Y + member.H);
        }
        for (var y = member.Y; y < member.Y + member.H; y++)
        {
            See(member.X - 1, y);
            See(member.X + member.W, y);
        }
        return found;
    }

    /// <summary>Сливает сети, задевающие друг друга, в энергосети (mergeTouching): порядок — как у сетей.</summary>
    private static List<Zone> MergeTouching(List<Zone> zones)
    {
        var parent = Enumerable.Range(0, zones.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        bool Touches(Zone zone, List<double> circles)
        {
            foreach (var entity in zone.Buildings)
            {
                var (x, y, w, h) = _bases[entity];
                for (var j = 0; j + 2 < circles.Count; j += 3)
                    if (BuildRules.LinkGap(circles[j], circles[j + 1], x, y, w, h) <= circles[j + 2])
                        return true;
            }
            return false;
        }
        for (var a = 0; a < zones.Count; a++)
            for (var b = a + 1; b < zones.Count; b++)
            {
                if (Find(a) == Find(b))
                    continue;
                if (Touches(zones[a], zones[b].Circles) || Touches(zones[b], zones[a].Circles))
                    parent[Find(b)] = Find(a);
            }
        var merged = new Dictionary<int, Zone>();
        var order = new List<int>();
        for (var i = 0; i < zones.Count; i++)
        {
            var root = Find(i);
            var zone = zones[i];
            if (!merged.TryGetValue(root, out var grid))
            {
                merged[root] = grid = new Zone();
                order.Add(root);
                grid.Circles.AddRange(zone.Circles);
                grid.Buildings.AddRange(zone.Buildings);
                grid.Sites.AddRange(zone.Sites);
                continue;
            }
            grid.Circles.AddRange(zone.Circles);
            // Главное здание — первым в энергосети.
            if (IsCore(zone.Buildings) && !IsCore(grid.Buildings))
                grid.Buildings.InsertRange(0, zone.Buildings);
            else
                grid.Buildings.AddRange(zone.Buildings);
            grid.Sites.AddRange(zone.Sites);
        }
        return [.. order.Select(root => merged[root])];
    }
}
