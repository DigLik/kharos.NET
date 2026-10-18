using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Правила места (canPlace в buildings.ts, isRoadway в paved.ts): где встаёт здание. Это же правило у разворачивания
/// MCV и правок редактора; само правило — общее с клиентом (Base.Shared/Rules/Placing). Чужие зоны (zones.ts) знает
/// раздел Construction: он ставит <see cref="ForeignZone"/>.
/// Покрытие — слой paving: код вида и хозяина (paveCode), (код − 2) mod 2 — 0 фундамент, 1 дорога.
/// </summary>
internal static class Placement
{
    /// <summary>Лежит ли прямоугольник w×h с (x, y) в чужой зоне строительства для игрока (inForeignZone); нет — никто не мешает.</summary>
    public delegate bool Zone(Call call, uint player, int x, int y, int w, int h);

    /// <summary>Правило чужих зон (раздел Construction); null — зон нет.</summary>
    public static Zone? ForeignZone { get; set; }

    /// <summary>Тайл на карте.</summary>
    public static bool InBounds(int x, int y) => (Tools.Tile(x, y) & TileFlags.OnMap) != 0;

    /// <summary>Прямоугольник целиком на карте.</summary>
    public static bool InBounds(int x, int y, int w, int h) => InBounds(x, y) && InBounds(x + w - 1, y + h - 1);

    /// <summary>В чужой ли зоне прямоугольник для игрока.</summary>
    public static bool InForeignZone(Call call, uint player, int x, int y, int w, int h) => ForeignZone?.Invoke(call, player, x, y, w, h) ?? false;

    /// <summary>
    /// Можно ли поставить здание вида type левым верхним тайлом в (x, y) (canPlace): основное — целиком на скале или
    /// фундаменте, оборона и трубы — ещё и на песке; не у подножия обрыва и не на дороге; на карте и без других зданий и
    /// площадок в зазоре gap.
    /// </summary>
    public static bool CanPlace(Call call, string type, int x, int y, int gap = 0, IReadOnlySet<uint>? ignore = null) =>
        Factory.Building(type) is { } spec && Placing.CanPlace(spec, x, y, gap, new HostView(call, ignore));

    /// <summary>Мир для общего правила места (Base.Shared/Rules): тайлы, покрытие и площадки — инструментами движка.</summary>
    private sealed class HostView(Call call, IReadOnlySet<uint>? ignore) : IPlaceView
    {
        public void Tiles(int x, int y, int w, int h, Span<PlaceTile> into)
        {
            var count = w * h;
            var tiles = new TileFlags[count];
            Tools.Tiles(x, y, w, h, tiles);
            var paving = new double[count];
            if (S.Paving is { } layer)
                Tools.Layer(layer, 0, 0, x, y, w, h, paving);
            var (xs, ys) = (new double[count], new double[count]);
            for (var i = 0; i < count; i++)
                (xs[i], ys[i]) = (x + i % w + 0.5, y + i / w + 0.5);
            var terrain = new TerrainInfo[count];
            Tools.Terrain(xs, ys, terrain);
            var occupants = ignore is null ? null : new uint[count];
            if (occupants is not null)
            {
                var xy = new int[count * 2];
                for (var i = 0; i < count; i++)
                    (xy[i * 2], xy[i * 2 + 1]) = (x + i % w, y + i / w);
                Tools.Occupants(xy, occupants);
            }
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
                if (occupants is not null && ignore!.Contains(occupants[i]))
                    place |= PlaceBits.Ignored;
                into[i] = new PlaceTile(place, (int)paving[i]);
            }
        }

        public bool Laying(int x, int y, int w, int h) => Placement.Laying(call, x, y, w, h);

        public bool SiteIn(int x, int y, int w, int h) => SiteAt(call, x, y, w, h) != 0;
    }

    /// <summary>Кладут ли на прямоугольник дорогу (Pave road, не снимаемую): на ней тоже не строят.</summary>
    private static bool Laying(Call call, int x, int y, int w, int h)
    {
        foreach (var entity in Tools.Near(x + w / 2.0, y + h / 2.0, Math.Max(w, h) + 1, S.Pave))
        {
            var (px, py) = call.At(entity);
            var (tx, ty) = (Math.Floor(px), Math.Floor(py));
            if (tx < x || ty < y || tx >= x + w || ty >= y + h)
                continue;
            if (call.Text(entity, S.Pave, S.PaveKind) == "road" && !call.Flag(entity, S.Pave, S.PaveRemove))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Площадка (основание без solid: стройка, к которой ещё не приступили), задевающая прямоугольник; нет — 0. Тайлов она
    /// не занимает, но место за ней закреплено.
    /// </summary>
    public static uint SiteAt(Call call, int x, int y, int w, int h)
    {
        // Самое широкое основание — 4 тайла: площадка, задевающая прямоугольник, лежит своим углом в этом круге.
        foreach (var entity in Tools.Near(x + w / 2.0, y + h / 2.0, Math.Max(w, h) + 6, S.Footprint))
        {
            if (call.Flag(entity, S.Footprint, S.FootprintSolid))
                continue;
            var (px, py) = call.At(entity);
            var (ow, oh) = (call.Num(entity, S.Footprint, S.FootprintW), call.Num(entity, S.Footprint, S.FootprintH));
            if (x < px + ow && x + w > px && y < py + oh && y + h > py)
                return entity;
        }
        return 0;
    }
}
