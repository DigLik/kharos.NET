using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Мир глазами постановки (placing.ts, zones.ts на клиенте): таблицы зданий, правило места <see cref="Placing"/> из
/// Base.Shared над репликой (инструменты тайлов, слоя покрытия, занятости), зоны строительства по готовым зданиям, шахта,
/// что липнет к месторождению. Хост всё равно проверит приказ: здесь — подсказка красным и зелёным.
/// </summary>
internal sealed class PlaceWorld(BaseState state)
{
    /// <summary>С какого расстояния от указателя до центра месторождения шахта сама встаёт на него, в тайлах.</summary>
    private const double Snap = 3;

    /// <summary>Сторона месторождения, тайлов (DEPOSIT_SIZE).</summary>
    public const int DepositSize = 2;

    private BaseTables? _tables;
    private readonly Dictionary<int, (string Type, BuildingSpec Spec)?> _kinds = [];
    private (ulong World, ulong Positions) _zonesKey = (ulong.MaxValue, ulong.MaxValue);
    private Dictionary<uint, List<double>> _zones = [];
    private uint _zonesFor = uint.MaxValue;

    private Parts P => state.P;

    /// <summary>Таблицы data/*.json (здания, правила); читаются раз.</summary>
    public BaseTables Tables(ClientContext c)
    {
        if (_tables is not null)
            return _tables;
        var tables = BaseTables.Load(name => c.TryData(name, out var bytes) ? bytes.ToArray() : null);
        return _tables = tables;
    }

    /// <summary>Запись здания по имени вида («generator»); нет — null.</summary>
    public BuildingSpec? Spec(ClientContext c, string type) => Tables(c).Buildings.GetValueOrDefault(type);

    /// <summary>Вид здания у сущности (по Proto: «buildings/…»); не здание — null.</summary>
    public (string Type, BuildingSpec Spec)? KindOf(ClientContext c, uint entity) => KindOf(c, c.Proto(entity));

    /// <summary>Вид здания по номеру Proto.id; не здание — null.</summary>
    public (string Type, BuildingSpec Spec)? KindOf(ClientContext c, int proto)
    {
        if (proto < 0)
            return null;
        if (!_kinds.TryGetValue(proto, out var kind))
        {
            kind = null;
            if (c.ProtoPath(proto) is { } path && path.StartsWith("buildings/", StringComparison.Ordinal))
            {
                var type = path["buildings/".Length..];
                if (Spec(c, type) is { } spec)
                    kind = (type, spec);
            }
            _kinds[proto] = kind;
        }
        return kind;
    }

    // ------------------------------------------------------------------ слои

    private readonly Dictionary<string, LayerRef?> _layers = [];

    /// <summary>
    /// Слой по имени. Клиентскому SDK схему слоёв при загрузке не отдают (ClientRuntime.LoadCore собирает WorldSchema без
    /// info.Layers, поэтому <c>ctx.Layer</c> у клиента пуст), поэтому слой берётся из подготовленной Parts, а нет — собирается
    /// по data/layers.json: номер слоя у движка — место записи в файле, остальное — оттуда же.
    /// </summary>
    private LayerRef? Layer(ClientContext c, string name, LayerRef? known)
    {
        if (known is not null)
            return known;
        if (_layers.TryGetValue(name, out var cached))
            return cached;
        LayerRef? layer = null;
        var id = 0;
        foreach (var (key, record) in Tables(c).Layers)
        {
            if (key == name)
            {
                layer = new LayerRef(name, id, record.Scope == "player", (int)(record.Cell ?? 0), record.Channels.Length, [.. record.Fields.Keys]);
                break;
            }
            id++;
        }
        return _layers[name] = layer;
    }

    /// <summary>Слой готового покрытия.</summary>
    public LayerRef? PavingLayer(ClientContext c) => Layer(c, "paving", P.Paving);

    /// <summary>Слой месторождений: предметы «вид, остаток».</summary>
    public LayerRef? DepositsLayer(ClientContext c) => Layer(c, "deposits", P.Deposits);

    // ------------------------------------------------------------------ зоны

    /// <summary>
    /// Круги зон строительства каждого игрока (x, y, радиус подряд): все готовые здания с хозяином, кроме изолированных
    /// (стены, ворота) и снимаемых; в TS зону игрока составляют круги его сетей, а границу рисуют по всем сразу. Считается
    /// заново, только когда сменились состав зданий и их места.
    /// </summary>
    public IReadOnlyDictionary<uint, List<double>> Zones(ClientContext c)
    {
        var revision = Tools.Revision();
        if ((revision.World, revision.Positions) == _zonesKey && _zonesFor == c.Player)
            return _zones;
        _zonesKey = (revision.World, revision.Positions);
        _zonesFor = c.Player;
        _zones = [];
        var buildings = c.Each(P.Building, P.Owner, P.Position);
        if (buildings.Length == 0)
            return _zones;
        var n = buildings.Length;
        var values = new double[n * 4];
        var has = new bool[n * 4];
        Tools.Columns(buildings, [(P.Owner, P.Owner["player"]), (P.Position, P.Position["x"]), (P.Position, P.Position["y"]), (P.Proto, P.Proto["id"])], values, has);
        var sites = new bool[n];
        Tools.Has(buildings, P.Site, sites);
        for (var i = 0; i < n; i++)
        {
            if (sites[i] || KindOf(c, (int)values[3 * n + i]) is not { } kind || kind.Spec.Isolated)
                continue;
            var player = (uint)values[i];
            if (!_zones.TryGetValue(player, out var circles))
                _zones[player] = circles = [];
            circles.Add(values[n + i] + kind.Spec.Width / 2);
            circles.Add(values[2 * n + i] + kind.Spec.Height / 2);
            circles.Add(BuildRules.ZoneRadius(kind.Spec));
        }
        return _zones;
    }

    /// <summary>Круги зон игрока; нет зданий — пусто.</summary>
    public IReadOnlyList<double> CirclesOf(ClientContext c, uint player) => Zones(c).TryGetValue(player, out var circles) ? circles : [];

    /// <summary>Круги зон всех остальных игроков, которых видит клиент.</summary>
    public List<double> ForeignCircles(ClientContext c, uint player)
    {
        var all = new List<double>();
        foreach (var (other, circles) in Zones(c))
            if (other != player)
                all.AddRange(circles);
        return all;
    }

    /// <summary>Задевает ли основание w×h с (x, y) чужую зону (inForeignZone).</summary>
    public bool InForeignZone(ClientContext c, uint player, int x, int y, int w, int h)
    {
        foreach (var (other, circles) in Zones(c))
            if (other != player && BuildRules.TouchesCircles(circles, x, y, w, h))
                return true;
        return false;
    }

    // ------------------------------------------------------------------ месторождения

    /// <summary>
    /// Ближайшее к точке месторождение не дальше reach тайлов (от точки до его центра): левый верхний тайл и запас; нет — null.
    /// </summary>
    public (int X, int Y, double Reserve)? DepositNear(ClientContext c, double x, double y, double reach)
    {
        if (DepositsLayer(c) is not { } layer)
            return null;
        (int, int, double)? best = null;
        var bestDistance = reach;
        foreach (var item in Tools.Items(layer, 0, x, y, reach + DepositSize))
        {
            var distance = Math.Sqrt((item.X + DepositSize / 2.0 - x) * (item.X + DepositSize / 2.0 - x) + (item.Y + DepositSize / 2.0 - y) * (item.Y + DepositSize / 2.0 - y));
            if (distance <= bestDistance)
            {
                best = ((int)item.X, (int)item.Y, item.Fields.Length > 1 ? item.Fields[1] : 0);
                bestDistance = distance;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ место

    /// <summary>
    /// Где встанет здание, которое игрок сейчас выбирает: основание серединой под тайлом (tx, ty); шахта притягивается к
    /// месторождению рядом (но только к такому, которое игрок уже разведал: иначе выдала бы, где оно).
    /// </summary>
    public (int X, int Y) Where(ClientContext c, BuildingSpec spec, int tx, int ty)
    {
        int x = tx - (int)Math.Floor(spec.Width / 2), y = ty - (int)Math.Floor(spec.Height / 2);
        if (spec.Extract && (int)spec.Width == DepositSize && DepositNear(c, tx + 0.5, ty + 0.5, Snap) is { } near && c.Explored(near.X + 1, near.Y + 1))
            return (near.X, near.Y);
        return (x, y);
    }

    /// <summary>Правило места (canPlace): основание на скале или фундаменте, не у обрыва, не на дороге, зазор до других зданий.</summary>
    public bool CanPlace(ClientContext c, BuildingSpec spec, int x, int y, int gap = 0) =>
        Placing.CanPlace(spec, x, y, gap, new ClientView(c, this));

    /// <summary>В зоне ли игрока центр основания (inControl).</summary>
    public bool InControl(ClientContext c, uint player, BuildingSpec spec, double x, double y) =>
        BuildRules.InCircles(CirclesOf(c, player), x + spec.Width / 2, y + spec.Height / 2);

    /// <summary>
    /// Может ли игрок заложить здание здесь (canBuild без открытия вида и кредитов — их проверяют панели): место годится,
    /// не задевает чужую зону, лежит в своей, а шахта встаёт на месторождение, где что-то осталось.
    /// </summary>
    public bool CanBuild(ClientContext c, uint player, BuildingSpec spec, int x, int y)
    {
        if (!CanPlace(c, spec, x, y) || InForeignZone(c, player, x, y, (int)spec.Width, (int)spec.Height))
            return false;
        if (spec.Extract && (DepositNear(c, x, y, 0.5) is not { } deposit || deposit.X != x || deposit.Y != y || deposit.Reserve <= 0))
            return false;
        return !BuildRules.NeedsZone(spec) || InControl(c, player, spec, x, y);
    }

    /// <summary>Годится ли тайл покрытию (canPave без тем и кредитов): на карте, не у обрыва, не в чужой зоне, вид по местности.</summary>
    public bool CanPave(ClientContext c, uint player, string kind, int x, int y)
    {
        if (!PaveRules.Known(kind))
            return false;
        Span<TileFlags> tile = stackalloc TileFlags[1];
        Tools.Tiles(x, y, 1, 1, tile);
        if ((tile[0] & TileFlags.OnMap) == 0 || (tile[0] & TileFlags.CliffFoot) != 0)
            return false;
        var terrain = TerrainAt(x, y);
        if (kind == PaveRules.Foundation ? terrain is not (1 or 2) : terrain == 3)
            return false;
        if (PaveAt(c, x, y) != 0)
            return false;
        var laid = Laid(c, x, y);
        if (laid != PaveRules.None && (PaveRules.KindOf(laid) == kind || PaveRules.OwnerOf(laid) != player))
            return false;
        var standing = Tools.Occupant(x, y);
        if (standing == 0)
            standing = SiteAt(c, x, y, 1, 1);
        if (standing != 0 && (kind != PaveRules.Foundation || !c.Mine(standing)))
            return false;
        return !InForeignZone(c, player, x, y, 1, 1);
    }

    /// <summary>Местность тайла: 0 болото, 1 песок, 2 скала, 3 гора.</summary>
    public int TerrainAt(int x, int y) => Tools.Terrain(x + 0.5, y + 0.5, out var info) ? info.Terrain : 3;

    /// <summary>Код готового покрытия тайла (0 — нет).</summary>
    public int Laid(ClientContext c, int x, int y) => PavingLayer(c) is { } layer ? (int)Tools.Layer(layer, 0, 0, x, y) : PaveRules.None;

    /// <summary>Работа над покрытием на тайле — укладка или разбор; нет — 0.</summary>
    public uint PaveAt(ClientContext c, int x, int y)
    {
        foreach (var entity in c.Within(x, y, x + 0.99, y + 0.99, P.Pave))
            return entity;
        return 0;
    }

    /// <summary>Площадка (основание без solid: стройка, к которой ещё не приступили), задевающая прямоугольник; нет — 0.</summary>
    public uint SiteAt(ClientContext c, int x, int y, int w, int h)
    {
        // Самое широкое основание — 4 тайла: площадка, задевающая прямоугольник, лежит своим углом в окрестности.
        foreach (var entity in c.Within(x - 4, y - 4, x + w, y + h, P.Footprint))
        {
            if (c.Get(entity, P.Footprint) is not { } footprint || footprint.Flag(P.Footprint["solid"]) || c.Pose(entity) is not { } at)
                continue;
            double ow = footprint.Number(P.Footprint["w"]), oh = footprint.Number(P.Footprint["h"]);
            if (x < at.X + ow && x + w > at.X && y < at.Y + oh && y + h > at.Y)
                return entity;
        }
        return 0;
    }

    /// <summary>Мир для общего правила места (Base.Shared/Rules): тайлы, покрытие и площадки — инструментами клиента.</summary>
    private sealed class ClientView(ClientContext c, PlaceWorld world) : IPlaceView
    {
        public void Tiles(int x, int y, int w, int h, Span<PlaceTile> into)
        {
            var count = w * h;
            var tiles = new TileFlags[count];
            Tools.Tiles(x, y, w, h, tiles);
            var paving = new double[count];
            if (world.PavingLayer(c) is { } layer)
                Tools.Layer(layer, 0, 0, x, y, w, h, paving);
            var (xs, ys) = (new double[count], new double[count]);
            for (var i = 0; i < count; i++)
                (xs[i], ys[i]) = (x + i % w + 0.5, y + i / w + 0.5);
            var terrain = new TerrainInfo[count];
            Tools.Terrain(xs, ys, terrain);
            for (var i = 0; i < count; i++)
            {
                var bits = tiles[i];
                var place = PlaceBits.None;
                if ((bits & TileFlags.OnMap) != 0)
                    place |= PlaceBits.OnMap;
                if ((bits & TileFlags.CliffFoot) != 0)
                    place |= PlaceBits.CliffFoot;
                if ((bits & TileFlags.Buildable) != 0)
                    place |= PlaceBits.Buildable;
                if (terrain[i].Terrain == 1)
                    place |= PlaceBits.Sand;
                if ((bits & TileFlags.Building) != 0)
                    place |= PlaceBits.Building;
                into[i] = new PlaceTile(place, (int)paving[i]);
            }
        }

        public bool Laying(int x, int y, int w, int h)
        {
            foreach (var entity in c.Within(x, y, x + w - 0.01, y + h - 0.01, world.P.Pave))
                if (c.Get(entity, world.P.Pave) is { } pave && !pave.Flag(world.P.Pave["remove"]) && Kharos.Mod.Sdk.Tools.Text((uint)pave.Number(world.P.Pave["kind"])) == PaveRules.Road)
                    return true;
            return false;
        }

        public bool SiteIn(int x, int y, int w, int h) => world.SiteAt(c, x, y, w, h) != 0;
    }
}
