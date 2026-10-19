using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Pipes;

/// <summary>
/// Прокладка труб (piping.ts): протяжка наземной трубы, пары колодцев, замена трубы колодцем и обратно, и трассы, которыми
/// редактор и тесты соединяют здания без стройки (connectBuilding, connectAll).
/// <para>
/// Сети — кэш зон (<see cref="Zones"/>): записей этого же вызова он не видит, поэтому <see cref="ConnectAll"/> за вызов
/// соединяет одну сеть (правка edit.connectAll повторяется, пока есть что соединять).
/// </para>
/// </summary>
internal static class Piping
{
    /// <summary>Дальше скольких тайлов <see cref="Route"/> трассу не ищет (ROUTE_REACH).</summary>
    public const int RouteReach = 16;

    public const string Pipe = "pipe", Well = "well";

    private static readonly (int Dx, int Dy)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    // ===== Своя труба в тайле =====

    /// <summary>Вид своей трубы в тайле (pipe или well): готовой, строящейся или заложенной, не под разбором; нет — null.</summary>
    private static string? KindAt(Call call, uint player, int x, int y)
    {
        var entity = Paving.PipeAt(call, player, x, y);
        return entity == 0 ? null : call.BuildingType(entity);
    }

    /// <summary>
    /// Своя труба другого вида в тайле (pipeSwap): наземная там, куда кладут колодец, и колодец там, куда кладут наземную.
    /// Новая ложится вместо неё. Нет — 0.
    /// </summary>
    public static uint Swap(Call call, uint player, string kind, int x, int y)
    {
        var other = KindAt(call, player, x, y);
        return other is not null && other != kind ? Paving.PipeAt(call, player, x, y) : 0;
    }

    /// <summary>Стоит ли в тайле свой колодец — готовый, строящийся или заложенный (wellAt).</summary>
    public static bool WellAt(Call call, uint player, int x, int y) => KindAt(call, player, x, y) == Well;

    /// <summary>
    /// Убирает трубу, вместо которой кладут другую, сразу (replacePipe): не начатую и недостроенную — с возвратом
    /// уплаченного, готовую — с возвратом как за разбор.
    /// </summary>
    private static void Replace(Call call, uint player, uint entity)
    {
        if (call.Has(entity, S.Site))
        {
            Sites.ScrapSite(call, player, entity);
            return;
        }
        Accounts.Add(call, player, Sites.RefundOf(call.BuildingType(entity)!));
        call.Destroy(entity);
    }

    // ===== Протяжка =====

    /// <summary>
    /// Какие тайлы протянутой трубы вида kind можно заложить (pipeStroke): место годится под трубу или там своя труба
    /// другого вида (её заменят), и оно не в чужой зоне. Своя зона трубе не нужна. Кредиты не считаются; без темы вида
    /// не годится ни один тайл.
    /// </summary>
    public static bool[] Stroke(Call call, uint player, ReadOnlySpan<int> tiles, string kind = Pipe)
    {
        var taken = new HashSet<(int, int)>();
        var allowed = new bool[tiles.Length / 2];
        var known = Factory.Building(kind) is { } spec && Accounts.Researched(call, player, spec.Research);
        for (var i = 0; i < allowed.Length; i++)
        {
            var (x, y) = (tiles[i * 2], tiles[i * 2 + 1]);
            var ok = known && !taken.Contains((x, y))
                && (Placement.CanPlace(call, Pipe, x, y) || Swap(call, player, kind, x, y) != 0)
                && !Zones.InForeignZone(player, x, y, 1, 1);
            if (ok)
                taken.Add((x, y));
            allowed[i] = ok;
        }
        return allowed;
    }

    /// <summary>
    /// Закладывает протянутую наземную трубу (orderPipes) и посылает к ней строителей. Платят по ходу стройки, поэтому
    /// кладут и без кредитов. Куда нельзя — не кладут; свой колодец на её пути заменяется трубой. Возвращает, сколько
    /// заложено.
    /// </summary>
    public static int OrderPipes(Call call, uint player, int[] tiles, IReadOnlyList<uint> builders)
    {
        var allowed = Stroke(call, player, tiles.AsSpan(0, Math.Min(tiles.Length & ~1, PaveRules.Limit * 2)));
        uint first = 0;
        var count = 0;
        for (var i = 0; i < allowed.Length; i++)
        {
            if (!allowed[i])
                continue;
            var (x, y) = (tiles[i * 2], tiles[i * 2 + 1]);
            var swap = Swap(call, player, Pipe, x, y);
            if (swap != 0)
                Replace(call, player, swap);
            var site = Sites.PlaceSite(call, player, Pipe, x, y);
            if (first == 0)
                first = site;
            count++;
        }
        if (first != 0)
            Sites.AssignBuilders(call, player, first, builders);
        return count;
    }

    /// <summary>Годятся ли тайлы концами подземного отрезка (isWellPair): два тайла на одной прямой, от 2 до UNDERGROUND_REACH.</summary>
    public static bool IsWellPair(ReadOnlySpan<int> tiles)
    {
        if (tiles.Length != 4)
            return false;
        var (ax, ay, bx, by) = (tiles[0], tiles[1], tiles[2], tiles[3]);
        var length = Math.Abs(bx - ax) + Math.Abs(by - ay);
        return (ax == bx || ay == by) && length >= 2 && length <= BuildRules.UndergroundReach;
    }

    /// <summary>Каким концам пары колодцев уже стоит свой колодец (wellsKept): такой конец не закладывают заново.</summary>
    public static bool[] WellsKept(Call call, uint player, ReadOnlySpan<int> tiles)
    {
        var kept = new bool[tiles.Length / 2];
        for (var i = 0; i < kept.Length; i++)
            kept[i] = tiles.Length == 4 && WellAt(call, player, tiles[i * 2], tiles[i * 2 + 1]);
        return kept;
    }

    /// <summary>
    /// Закладывает колодцы и посылает к ним строителей (orderWells): один (tiles — его x, y) или пару — концы подземного
    /// отрезка. Каждый — где можно положить трубу; своя наземная труба в этом тайле заменяется колодцем. Конец пары, где
    /// свой колодец уже стоит, остаётся как есть. Пару кладут целиком или никак. Возвращает, заложено ли.
    /// </summary>
    public static bool OrderWells(Call call, uint player, int[] tiles, IReadOnlyList<uint> builders)
    {
        if (tiles.Length != 2 && !IsWellPair(tiles))
            return false;
        var kept = WellsKept(call, player, tiles);
        var fits = Stroke(call, player, tiles, Well);
        if (kept.All(item => item) || fits.Where((ok, i) => !ok && !kept[i]).Any())
            return false;
        uint first = 0;
        for (var i = 0; i < kept.Length; i++)
        {
            if (kept[i])
                continue;
            var (x, y) = (tiles[i * 2], tiles[i * 2 + 1]);
            var swap = Swap(call, player, Well, x, y);
            if (swap != 0)
                Replace(call, player, swap);
            var site = Sites.PlaceSite(call, player, Well, x, y);
            if (first == 0)
                first = site;
        }
        Sites.AssignBuilders(call, player, first, builders);
        return true;
    }

    /// <summary>
    /// С какими своими колодцами связался бы колодец в тайле (wellPartners): ближайший по прямой в каждую сторону, не
    /// дальше UNDERGROUND_REACH и не вплотную. Заложенные колодцы тоже в счёте — ready у них false.
    /// </summary>
    public static List<(int X, int Y, bool Ready)> WellPartners(Call call, uint player, int x, int y)
    {
        var sites = new HashSet<(int, int)>();
        foreach (var site in Core.All(S.Site, S.Owner))
            if (call.IsOwn(player, site) && call.BuildingType(site) == Well && !call.Flag(site, S.Site, Refs.R.SiteDemolish))
            {
                var (sx, sy) = call.At(site);
                sites.Add(((int)sx, (int)sy));
            }
        var found = new List<(int, int, bool)>();
        foreach (var (dx, dy) in Sides)
            for (var step = 2; step <= BuildRules.UndergroundReach; step++)
            {
                var (tx, ty) = (x + dx * step, y + dy * step);
                var other = Tools.Occupant(tx, ty);
                var ready = other != 0 && call.BuildingType(other) == Well && call.IsOwn(player, other) && !call.Has(other, S.Site);
                if (!ready && !sites.Contains((tx, ty)))
                    continue;
                found.Add((tx, ty, ready));
                break;
            }
        return found;
    }

    // ===== Трассы без стройки =====

    /// <summary>
    /// Трасса наземной трубы от основания (x, y, w, h) до узла сети игрока (pipeRoute): тайлы x, y подряд, от основания к
    /// сети. Пусто — к основанию уже подходит своя труба; null — сети ближе ROUTE_REACH нет. Узел сети — готовая труба или
    /// колодец, готовое здание сети или заложенная своя труба. joins — какую сеть годится брать; без него — любую.
    /// </summary>
    public static List<(int X, int Y)>? Route(Call call, uint player, int x, int y, int w, int h, Func<Zones.Zone, bool>? joins = null)
    {
        bool Inside(int tx, int ty) => tx >= x && tx < x + w && ty >= y && ty < y + h;
        // Заложенные свои трубы: к ним подключаются, как к готовым.
        var sites = new HashSet<(int, int)>();
        foreach (var site in Core.All(S.Site, S.Owner))
            if (call.IsOwn(player, site) && !call.Flag(site, S.Site, Refs.R.SiteDemolish) && Factory.Building(call.BuildingType(site))?.Pipe is not null)
            {
                var (sx, sy) = call.At(site);
                sites.Add(((int)sx, (int)sy));
            }
        var nodes = new Dictionary<(int, int, bool), bool>();
        bool Node(int tx, int ty, bool pipeOnly)
        {
            if (Inside(tx, ty))
                return false;
            if (sites.Contains((tx, ty)))
                return joins is null;
            if (nodes.TryGetValue((tx, ty, pipeOnly), out var known))
                return known;
            var other = Tools.Occupant(tx, ty);
            var ok = other != 0 && call.IsOwn(player, other) && !call.Has(other, S.Site)
                && (!pipeOnly || Factory.Building(call.BuildingType(other))?.Pipe is not null)
                && Zones.NetworkOf(other) is { } zone && (joins is null || joins(zone));
            nodes[(tx, ty, pipeOnly)] = ok;
            return ok;
        }
        // К основанию уже подведена своя труба.
        for (var tx = x; tx < x + w; tx++)
            if (Node(tx, y - 1, true) || Node(tx, y + h, true))
                return [];
        for (var ty = y; ty < y + h; ty++)
            if (Node(x - 1, ty, true) || Node(x + w, ty, true))
                return [];

        bool Free(int tx, int ty) => !Inside(tx, ty) && !sites.Contains((tx, ty)) && Placement.CanPlace(call, Pipe, tx, ty);
        var previous = new Dictionary<(int, int), int>();
        var queue = new List<(int X, int Y, int Length)>();
        void Begin(int tx, int ty)
        {
            if (previous.ContainsKey((tx, ty)) || !Free(tx, ty))
                return;
            previous[(tx, ty)] = -1;
            queue.Add((tx, ty, 1));
        }
        for (var tx = x; tx < x + w; tx++)
        {
            Begin(tx, y - 1);
            Begin(tx, y + h);
        }
        for (var ty = y; ty < y + h; ty++)
        {
            Begin(x - 1, ty);
            Begin(x + w, ty);
        }
        for (var at = 0; at < queue.Count; at++)
        {
            var (tx, ty, length) = queue[at];
            if (Sides.Any(side => Node(tx + side.Dx, ty + side.Dy, false)))
            {
                // Назад по цепочке — от сети к основанию; трасса нужна в обратном порядке.
                var back = new List<(int X, int Y)>();
                for (var i = at; i >= 0; i = previous[(queue[i].X, queue[i].Y)])
                    back.Add((queue[i].X, queue[i].Y));
                back.Reverse();
                return back;
            }
            if (length >= RouteReach)
                continue;
            foreach (var (dx, dy) in Sides)
            {
                if (previous.ContainsKey((tx + dx, ty + dy)) || !Free(tx + dx, ty + dy))
                    continue;
                previous[(tx + dx, ty + dy)] = at;
                queue.Add((tx + dx, ty + dy, length + 1));
            }
        }
        return null;
    }

    /// <summary>
    /// Подключает готовое здание к ближайшей другой своей сети готовой трубой, без стройки (connectBuilding): так ставят
    /// здания в обход стройки редактор и тесты. Возвращает уложенные трубы; пусто — сети рядом нет или труба уже подходит.
    /// </summary>
    public static List<uint> ConnectBuilding(Call call, uint building)
    {
        var player = call.OwnerOf(building);
        if (player == 0 || !call.Has(building, S.Building) || call.BuildingType(building) is not { } type || Factory.Building(type) is not { } spec
            || spec.Pipe is not null || spec.Isolated)
            return [];
        var own = Zones.NetworkOf(building);
        var (x, y) = call.At(building);
        var tiles = Route(call, player, (int)x, (int)y, (int)spec.Width, (int)spec.Height, zone => !ReferenceEquals(zone, own)) ?? [];
        return [.. tiles.Select(tile => Factory.PlaceBuilding(call, Pipe, tile.X, tile.Y, player))];
    }

    /// <summary>
    /// Соединяет трубой одну сеть игрока с первой — с главным зданием (шаг connectAll): готовые трубы сразу, без стройки.
    /// Сеть, до которой трассы не нашлось, остаётся отдельной. Возвращает, соединилось ли: тогда стоит позвать снова
    /// следующим вызовом — кэш сетей увидит новые трубы только там.
    /// </summary>
    public static bool ConnectAll(Call call, uint player)
    {
        var zones = Zones.Of(player);
        if (zones.Count < 2)
            return false;
        var main = zones[0];
        foreach (var zone in zones.Skip(1))
        {
            var entity = zone.Buildings.FirstOrDefault(item => Factory.Building(call.BuildingType(item))?.Pipe is null, zone.Buildings[0]);
            if (Factory.Building(call.BuildingType(entity)) is not { } spec)
                continue;
            var (x, y) = call.At(entity);
            var tiles = Route(call, player, (int)x, (int)y, (int)spec.Width, (int)spec.Height, other => ReferenceEquals(other, main));
            if (tiles is not { Count: > 0 })
                continue;
            foreach (var (tx, ty) in tiles)
                Factory.PlaceBuilding(call, Pipe, tx, ty, player);
            return true;
        }
        return false;
    }
}
