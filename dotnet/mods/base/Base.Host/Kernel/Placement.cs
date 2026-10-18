using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Правила места (canPlace в buildings.ts, isRoadway в paved.ts): где встаёт здание. Это же правило у разворачивания
/// MCV и правок редактора. Чужие зоны (zones.ts) знает раздел Construction: он ставит <see cref="ForeignZone"/>.
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
    public static bool CanPlace(Call call, string type, int x, int y, int gap = 0, IReadOnlySet<uint>? ignore = null)
    {
        if (Factory.Building(type) is not { } spec)
            return false;
        var (w, h) = ((int)spec.Width, (int)spec.Height);
        var sand = spec.Defense || spec.Pipe is not null;
        var count = w * h;
        Span<TileFlags> tiles = count <= 256 ? stackalloc TileFlags[count] : new TileFlags[count];
        Tools.Tiles(x, y, w, h, tiles);
        Span<double> paving = count <= 256 ? stackalloc double[count] : new double[count];
        if (S.Paving is { } layer)
            Tools.Layer(layer, 0, 0, x, y, w, h, paving);
        else
            paving.Clear();
        for (var i = 0; i < count; i++)
        {
            var bits = tiles[i];
            if ((bits & TileFlags.OnMap) == 0 || (bits & TileFlags.CliffFoot) != 0)
                return false;
            var code = (int)paving[i];
            // Готовый фундамент делает песок пригодным для любого здания; дорога — для транспорта, на ней не строят.
            if (code != 0 && (code - 2) % 2 == 1)
                return false;
            var foundation = code != 0 && (code - 2) % 2 == 0;
            if ((bits & TileFlags.Buildable) == 0 && !foundation && !(sand && Sand(x + i % w, y + i / w)))
                return false;
        }
        if (Laying(call, x, y, w, h))
            return false;
        // Чужие здания в зазоре.
        if (gap > 0)
        {
            var (gw, gh) = (w + gap * 2, h + gap * 2);
            var around = new TileFlags[gw * gh];
            Tools.Tiles(x - gap, y - gap, gw, gh, around);
            for (var i = 0; i < around.Length; i++)
                if ((around[i] & TileFlags.Building) != 0 && !Ignored(x - gap + i % gw, y - gap + i / gw, ignore))
                    return false;
        }
        else
            for (var i = 0; i < count; i++)
                if ((tiles[i] & TileFlags.Building) != 0 && !Ignored(x + i % w, y + i / w, ignore))
                    return false;
        return SiteAt(call, x - gap, y - gap, w + gap * 2, h + gap * 2) == 0;
    }

    /// <summary>Здание на тайле — из тех, что не в счёт (их переносят).</summary>
    private static bool Ignored(int x, int y, IReadOnlySet<uint>? ignore) => ignore is not null && ignore.Contains(Tools.Occupant(x, y));

    /// <summary>Песок ли тайл.</summary>
    private static bool Sand(int x, int y) => Tools.Terrain(x + 0.5, y + 0.5, out var info) && info.Terrain == 1;

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
