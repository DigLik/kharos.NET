using Base.Shared.Data;

namespace Base.Shared.Rules;

/// <summary>Что о тайле нужно правилу места (<see cref="Placing"/>).</summary>
[Flags]
public enum PlaceBits : byte
{
    None = 0,
    /// <summary>Тайл на карте.</summary>
    OnMap = 1,
    /// <summary>Подножие обрыва: там стенка, не строят.</summary>
    CliffFoot = 2,
    /// <summary>Скала: годится любому зданию.</summary>
    Buildable = 4,
    /// <summary>Песок: годится обороне и трубам.</summary>
    Sand = 8,
    /// <summary>Тайл занят зданием.</summary>
    Building = 16,
    /// <summary>Тайл занят зданием, которое не в счёт (его переносят).</summary>
    Ignored = 32,
}

/// <summary>Тайл для правила места: биты и код готового покрытия (PaveRules.Code, 0 — нет).</summary>
public readonly record struct PlaceTile(PlaceBits Bits, int Paving);

/// <summary>Мир глазами правила места: хост читает его инструментами движка, клиент — своей копией.</summary>
public interface IPlaceView
{
    /// <summary>Тайлы прямоугольника w×h с (x, y) построчно.</summary>
    void Tiles(int x, int y, int w, int h, Span<PlaceTile> into);

    /// <summary>Кладут ли на прямоугольник дорогу (работа Pave road, не снимаемая).</summary>
    bool Laying(int x, int y, int w, int h);

    /// <summary>Задевает ли прямоугольник площадка стройки, к которой ещё не приступили.</summary>
    bool SiteIn(int x, int y, int w, int h);
}

/// <summary>
/// Правило места (canPlace в buildings.ts): основное здание — целиком на скале или фундаменте, оборона и трубы — ещё и
/// на песке; не у подножия обрыва и не на дороге (готовой или той, что кладут); на карте и без других зданий и площадок
/// в зазоре gap. Это же правило у разворачивания MCV и правок редактора.
/// </summary>
public static class Placing
{
    /// <summary>Годится ли тайл под основание здания вида spec.</summary>
    public static bool TileFits(BuildingSpec spec, PlaceTile tile)
    {
        var bits = tile.Bits;
        if ((bits & PlaceBits.OnMap) == 0 || (bits & PlaceBits.CliffFoot) != 0)
            return false;
        // Дорога — для транспорта: на ней не строят ничего, даже трубу.
        if (PaveRules.IsRoad(tile.Paving))
            return false;
        // Готовый фундамент делает песок пригодным для любого здания.
        if ((bits & PlaceBits.Buildable) != 0 || PaveRules.IsFoundation(tile.Paving))
            return true;
        return (spec.Defense || spec.Pipe is not null) && (bits & PlaceBits.Sand) != 0;
    }

    /// <summary>Можно ли поставить здание вида spec левым верхним тайлом в (x, y) с зазором gap.</summary>
    public static bool CanPlace(BuildingSpec spec, int x, int y, int gap, IPlaceView view)
    {
        var (w, h) = ((int)spec.Width, (int)spec.Height);
        if (w <= 0 || h <= 0)
            return false;
        var (gw, gh) = (w + gap * 2, h + gap * 2);
        var tiles = new PlaceTile[gw * gh];
        view.Tiles(x - gap, y - gap, gw, gh, tiles);
        for (var ty = 0; ty < h; ty++)
            for (var tx = 0; tx < w; tx++)
                if (!TileFits(spec, tiles[(ty + gap) * gw + tx + gap]))
                    return false;
        if (view.Laying(x, y, w, h))
            return false;
        foreach (var tile in tiles)
            if ((tile.Bits & PlaceBits.Building) != 0 && (tile.Bits & PlaceBits.Ignored) == 0)
                return false;
        // Площадка, которую ещё не начали строить, тайлов не занимает, но место за ней закреплено.
        return !view.SiteIn(x - gap, y - gap, gw, gh);
    }
}
