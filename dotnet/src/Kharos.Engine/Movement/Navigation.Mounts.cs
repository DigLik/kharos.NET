using Kharos.Engine.Numerics;
using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

public sealed partial class Navigation
{
    private readonly List<uint> _mounted = [];
    private readonly List<uint> _orphans = [];

    /// <summary>
    /// Раз в тик, после хода (<c>followCarriers</c> в turrets.ts): сущности на носителях (Mount.parent) встают на свои
    /// места — along вперёд и across вправо от центра носителя; Facing — поворот носителя плюс Mount.angle. Центр здания
    /// (Footprint) — середина основания, смотрит оно под 0; юнита — Position и Facing. Носителя нет — сущность уходит
    /// вместе со всем, что держится на ней. Mount.parent = 0 — ни на ком: не трогается.
    /// </summary>
    public void FollowCarriers()
    {
        var mounts = World.Store(Builtin.Mount);
        _mounted.Clear();
        _mounted.AddRange(mounts.Entities);
        _orphans.Clear();
        var positionX = World.Schema[Builtin.Position].Fields[0].Index;
        var positionY = World.Schema[Builtin.Position].Fields[1].Index;
        var positions = World.Store(Builtin.Position);
        var facings = World.Store(Builtin.Facing);
        var footprints = World.Store(Builtin.Footprint);
        foreach (var entity in _mounted)
        {
            var row = mounts.RowOf(entity);
            if (row < 0 || !positions.Contains(entity))
                continue;
            var parent = mounts.RawColumn(F.MountParent)[row];
            if (parent == 0)
                continue;
            var carrier = parent >= 1 && parent <= uint.MaxValue && parent == Math.Floor(parent) ? (uint)parent : 0;
            var at = carrier == 0 ? -1 : positions.RowOf(carrier);
            if (at < 0)
            {
                _orphans.Add(entity);
                continue;
            }
            double cx = positions.RawColumn(0)[at], cy = positions.RawColumn(1)[at], facing = 0;
            var footprint = footprints.RowOf(carrier);
            if (footprint >= 0)
            {
                cx += footprints.RawColumn(F.FootprintW)[footprint] / 2;
                cy += footprints.RawColumn(F.FootprintH)[footprint] / 2;
            }
            else if (facings.RowOf(carrier) is var turn && turn >= 0)
            {
                facing = facings.RawColumn(0)[turn];
            }
            var (along, across) = (mounts.RawColumn(F.MountAlong)[row], mounts.RawColumn(F.MountAcross)[row]);
            // Как mountedAt: тот же порядок действий — тот же результат до последнего знака.
            var (cos, sin) = (DetMath.Cos(facing), DetMath.Sin(facing));
            World.SetNumber(entity, Builtin.Position, positionX, cx + along * cos - across * sin);
            World.SetNumber(entity, Builtin.Position, positionY, cy + along * sin + across * cos);
            if (facings.Contains(entity))
                World.SetNumber(entity, Builtin.Facing, 0, Wrap(facing + mounts.RawColumn(F.MountAngle)[row]));
        }
        // Состав мира меняется после обхода.
        foreach (var orphan in _orphans)
            Commands.DestroyWithMounts(World, orphan);
    }
}
