using Kharos.Engine.Movement;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 16 Tiles: тайлы прямоугольника — карта, местность, здания, ворота, юниты (формат — ModAbi.ToolTiles). Владелец — Д2.
public partial class ModTools
{
    private readonly HashSet<long> _unitTiles = [];

    private partial int Tiles(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var (x, y, w, h) = (r.I32(), r.I32(), r.U32(), r.U32());
        if ((ulong)w * h > MaxToolTiles)
            throw new FormatException($"Tiles: {w}×{h} больше {MaxToolTiles} тайлов");
        // Наземные юниты — по тайлу центра.
        _unitTiles.Clear();
        var positions = world.Store(Builtin.Position);
        foreach (var entity in world.Store(Builtin.Body).Entities)
        {
            var at = positions.RowOf(entity);
            if (at >= 0 && !nav.ChassisOf(entity).Air)
                _unitTiles.Add(Navigation.Key(PathSearch.Floor(positions.Column(0)[at]), PathSearch.Floor(positions.Column(1)[at])));
        }
        var into = Room((int)(w * h));
        var i = 0;
        // Обход по смещениям в long: x + w у края int (int.MaxValue) не переполняется и цикл не зацикливается;
        // тайл за пределами int — вне карты, нулевой байт.
        for (var dy = 0L; dy < h; dy++)
            for (var dx = 0L; dx < w; dx++)
            {
                var (longX, longY) = (x + dx, y + dy);
                if (longX > int.MaxValue || longY > int.MaxValue)
                {
                    into[i++] = 0;
                    continue;
                }
                var (tileX, tileY) = ((int)longX, (int)longY);
                var (terrain, _, foot) = nav.TileAt(tileX, tileY);
                byte bits = 0;
                if (nav.InBounds(tileX, tileY))
                    bits |= ModAbi.TileOnMap;
                if (TileFormat.IsPassable(terrain))
                    bits |= ModAbi.TilePassable;
                if (foot)
                    bits |= ModAbi.TileCliffFoot;
                if (TileFormat.IsBuildable(terrain) && !foot)
                    bits |= ModAbi.TileBuildable;
                var building = nav.Occupancy.At(tileX, tileY);
                if (building != 0)
                {
                    bits |= ModAbi.TileBuilding;
                    if (nav.Occupancy.IsOpen(building))
                        bits |= ModAbi.TileOpenGate;
                }
                if (_unitTiles.Contains(Navigation.Key(tileX, tileY)))
                    bits |= ModAbi.TileUnit;
                into[i++] = bits;
            }
        return 0;
    }
}
