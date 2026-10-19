using Base.Host.Kernel;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Construction.Refs;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Землеройные работы (earthworks.ts): строители срывают горы и сглаживают обрывы. Гору срывают целиком — на её месте
/// скала того же яруса, что вокруг; обрыв — по тайлу кромки: кромка становится пологой. Работа — Position, Dig, Owner;
/// плата сразу при заказе; правка карты — командой Land (игроки узнают её, увидев место). Открывает тема earthworks.
/// </summary>
internal static class Earthworks
{
    public const string Peak = "peak", Cliff = "cliff";

    /// <summary>Цена срытия горы за каждый её тайл и сглаживания тайла кромки (PEAK_COST, CLIFF_COST).</summary>
    public const double PeakCost = 40, CliffCost = 25;

    /// <summary>Тема землеройных работ (DIG_RESEARCH).</summary>
    public const string Topic = "earthworks";

    private const byte CliffBit = 4;
    private const int DuneShift = 5;

    private static readonly (int Dx, int Dy)[] Around = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    /// <summary>Хранимые байты тайла из слоя land: местность, биом (основной), рельеф.</summary>
    private static (int Terrain, byte Biome, int Relief) Bytes(int x, int y)
    {
        if (S.Land is not { } land)
            return (2, 0, 1);
        var terrain = (int)Tools.Layer(land, 0, 0, x, y);
        var biome = (byte)Tools.Layer(land, 0, 1, x, y);
        var relief = (int)Tools.Layer(land, 0, 3, x, y);
        // Биом хранится в двух старших битах байта (младшие — примесь соседнего).
        return (terrain, (byte)(biome >> 6), relief);
    }

    /// <summary>Ярус тайла; у гор — −1.</summary>
    private static int TierAt(int x, int y)
    {
        var (terrain, _, relief) = Bytes(x, y);
        return terrain == 3 ? -1 : relief & 3;
    }

    /// <summary>Вершина горы тайла; не гора — null.</summary>
    private static (double X, double Y, double Radius)? PeakOf(int x, int y) =>
        Tools.Peak(x, y, out var px, out var py, out var radius) ? (px, py, radius) : null;

    /// <summary>Тайлы горы с вершиной peak (peakTiles), по строкам.</summary>
    public static List<(int X, int Y)> PeakTiles((double X, double Y, double Radius) peak)
    {
        var tiles = new List<(int, int)>();
        var span = (int)Math.Ceiling(peak.Radius + 1);
        for (var y = (int)Math.Floor(peak.Y) - span; y <= (int)Math.Floor(peak.Y) + span; y++)
            for (var x = (int)Math.Floor(peak.X) - span; x <= (int)Math.Floor(peak.X) + span; x++)
                if (Placement.InBounds(x, y) && PeakOf(x, y) is { } other && other.X == peak.X && other.Y == peak.Y)
                    tiles.Add((x, y));
        return tiles;
    }

    /// <summary>Кромка ли обрыва тайл (isCliffEdge): обрывистая, а рядом есть тайл ниже — подножие.</summary>
    public static bool IsCliffEdge(int x, int y)
    {
        if (!Placement.InBounds(x, y))
            return false;
        var (terrain, _, relief) = Bytes(x, y);
        if (terrain == 3 || (relief & CliffBit) == 0)
            return false;
        var tier = relief & 3;
        foreach (var (dx, dy) in Around)
        {
            var other = TierAt(x + dx, y + dy);
            if (other >= 0 && other < tier)
                return true;
        }
        return false;
    }

    /// <summary>Тайлы кромки, которые сгладит рамка (cliffEdgesIn): кромки в рамке и кромки над подножиями в ней.</summary>
    public static List<(int X, int Y)> CliffEdgesIn(ReadOnlySpan<int> tiles)
    {
        var found = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();
        for (var i = 0; i + 1 < tiles.Length; i += 2)
        {
            var (x, y) = (tiles[i], tiles[i + 1]);
            if (IsCliffEdge(x, y) && seen.Add((x, y)))
                found.Add((x, y));
            var tier = TierAt(x, y);
            foreach (var (dx, dy) in Around)
                if (TierAt(x + dx, y + dy) > tier && IsCliffEdge(x + dx, y + dy) && seen.Add((x + dx, y + dy)))
                    found.Add((x + dx, y + dy));
        }
        return found;
    }

    /// <summary>Работа, которая уже идёт на тайле: кромку сглаживают или гору тайла срывают (digAt); нет — 0.</summary>
    public static uint At(Call call, int x, int y)
    {
        var peak = PeakOf(x, y);
        IEnumerable<uint> digs = Tools.Near(x + 0.5, y + 0.5, 8, R.Dig);
        if (call.Writes)
            digs = digs.Concat(call.Stage.Spawned);
        foreach (var entity in digs)
        {
            if (!call.Has(entity, R.Dig))
                continue;
            if (call.Text(entity, R.Dig, R.DigKind) == Cliff)
            {
                if (call.At(entity) == (x, y))
                    return entity;
            }
            else if (peak is { } top && call.Num(entity, R.Dig, R.DigPeakX) == top.X && call.Num(entity, R.Dig, R.DigPeakY) == top.Y)
                return entity;
        }
        return 0;
    }

    /// <summary>Можно ли игроку начать работу kind на тайле (canDig): исследовано, не в чужой зоне, есть что делать.</summary>
    public static bool CanDig(Call call, uint player, string kind, int x, int y)
    {
        if (kind is not (Peak or Cliff) || !Placement.InBounds(x, y) || !Accounts.Researched(call, player, Topic))
            return false;
        if (kind == Peak ? PeakOf(x, y) is null : !IsCliffEdge(x, y))
            return false;
        if (At(call, x, y) != 0)
            return false;
        return !Zones.InForeignZone(player, x, y, 1, 1);
    }

    /// <summary>Работы рамки (digsIn): на гору — одна, на тайл кромки — своя; место, вершина, цена.</summary>
    private static List<(int X, int Y, double PeakX, double PeakY, double Cost)> Jobs(Call call, uint player, string kind, ReadOnlySpan<int> tiles)
    {
        var jobs = new List<(int, int, double, double, double)>();
        if (kind == Cliff)
        {
            foreach (var (x, y) in CliffEdgesIn(tiles))
                if (CanDig(call, player, kind, x, y))
                    jobs.Add((x, y, 0, 0, CliffCost));
            return jobs;
        }
        var seen = new HashSet<(double, double)>();
        for (var i = 0; i + 1 < tiles.Length; i += 2)
        {
            if (PeakOf(tiles[i], tiles[i + 1]) is not { } peak || seen.Contains((peak.X, peak.Y)) || !CanDig(call, player, kind, tiles[i], tiles[i + 1]))
                continue;
            seen.Add((peak.X, peak.Y));
            jobs.Add(((int)Math.Floor(peak.X), (int)Math.Floor(peak.Y), peak.X, peak.Y, PeakTiles(peak).Count * PeakCost));
        }
        return jobs;
    }

    /// <summary>Заказывает работы рамки, платя за каждую сразу (orderDigs); первая — строителям. Ничего — 0.</summary>
    public static uint Order(Call call, uint player, string kind, ReadOnlySpan<int> tiles)
    {
        uint first = 0;
        foreach (var job in Jobs(call, player, kind, tiles))
        {
            if (!Accounts.Pay(call, player, job.Cost))
                continue;
            var entity = call.Spawn();
            call.Add(entity, S.Position).Number(S.PositionX, job.X).Number(S.PositionY, job.Y);
            call.Add(entity, R.Dig).Text(R.DigKind, kind).Number(R.DigCost, job.Cost).Number(R.DigPeakX, job.PeakX).Number(R.DigPeakY, job.PeakY);
            call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
            if (first == 0)
                first = entity;
        }
        return first;
    }

    /// <summary>Отменяет свою работу на тайле с возвратом (cancelDig); было ли что отменять.</summary>
    public static bool Cancel(Call call, uint player, int x, int y)
    {
        var entity = At(call, x, y);
        if (entity == 0 || !call.IsOwn(player, entity))
            return false;
        Accounts.Add(call, player, call.Num(entity, R.Dig, R.DigCost));
        call.Destroy(entity);
        return true;
    }

    /// <summary>Над какими тайлами идёт работа (digArea); делать уже нечего — null.</summary>
    public static (int X, int Y, int W, int H)? Area(Call call, uint entity)
    {
        if (!call.Has(entity, R.Dig) || !call.Has(entity, S.Position))
            return null;
        var (px, py) = call.At(entity);
        var (x, y) = ((int)px, (int)py);
        if (call.Text(entity, R.Dig, R.DigKind) == Cliff)
            return IsCliffEdge(x, y) ? (x, y, 1, 1) : null;
        if (PeakOf(x, y) is not { } peak || peak.X != call.Num(entity, R.Dig, R.DigPeakX) || peak.Y != call.Num(entity, R.Dig, R.DigPeakY))
            return null;
        var tiles = PeakTiles(peak);
        if (tiles.Count == 0)
            return null;
        var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        foreach (var (tx, ty) in tiles)
        {
            left = Math.Min(left, tx);
            top = Math.Min(top, ty);
            right = Math.Max(right, tx + 1);
            bottom = Math.Max(bottom, ty + 1);
        }
        return (left, top, right - left, bottom - top);
    }

    /// <summary>Самый частый ярус соседей горы (tierAround): скала ложится вровень с плато вокруг.</summary>
    private static int TierAround(List<(int X, int Y)> tiles)
    {
        var counts = new int[4];
        var own = tiles.ToHashSet();
        foreach (var (x, y) in tiles)
            foreach (var (dx, dy) in Around)
            {
                if (own.Contains((x + dx, y + dy)) || !Placement.InBounds(x + dx, y + dy))
                    continue;
                var tier = TierAt(x + dx, y + dy);
                if (tier >= 0)
                    counts[tier]++;
            }
        var best = 1;
        for (var tier = 0; tier < counts.Length; tier++)
            if (counts[tier] > counts[best])
                best = tier;
        return best;
    }

    /// <summary>Работа сделана (finishDig): гора — скалой, кромка — пологой; сущность работы исчезает.</summary>
    public static void Finish(Call call, uint entity)
    {
        if (Area(call, entity) is not null)
        {
            var (px, py) = call.At(entity);
            var (x, y) = ((int)px, (int)py);
            if (call.Text(entity, R.Dig, R.DigKind) == Cliff)
            {
                var (terrain, biome, relief) = Bytes(x, y);
                call.Commands.LandTile(x, y, new LandEdit((byte)terrain, biome, (byte)(relief & 3), false, (byte)((relief >> DuneShift) & 3)));
            }
            else if (PeakOf(x, y) is { } peak)
            {
                var tiles = PeakTiles(peak);
                var tier = TierAround(tiles);
                foreach (var (tx, ty) in tiles)
                    call.Commands.LandTile(tx, ty, new LandEdit(2, Bytes(tx, ty).Biome, (byte)tier));
            }
        }
        call.Destroy(entity);
    }
}
