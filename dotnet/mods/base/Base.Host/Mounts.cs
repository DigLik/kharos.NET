using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// mechanics/mounts: турели на носителях. Новой сущности, у вида которой есть data.mounts, ставятся турели — свои
/// сущности с <c>Attached</c> (событие движка spawned). Каждый тик после хода (<c>after drive, separate</c>) турель
/// встаёт на своё место на носителе и смотрит туда же, куда он, плюс свой угол (<c>Turret.angle</c>); турель
/// погибшего носителя исчезает. Как followCarriers в sim-ts.
/// </summary>
internal sealed class Mounts(BaseMod mod) : ISystem
{
    private Column _parent, _along, _across, _x, _y, _facing, _angle;

    /// <summary>spawned { entity, proto }: носителю по виду — его турели.</summary>
    public void OnSpawned(EventContext ctx)
    {
        var (entity, proto) = SpawnedEvent.Read(ctx.Payload);
        if (proto < 0 || Tools.ProtoName(proto) is not { } path || !mod.D.Mounts.TryGetValue(path, out var list) || list.Length == 0)
            return;
        var p = mod.P;
        if (Tools.Get(entity, p.Position) is not { } at)
            return;
        var facing = Tools.Get(entity, p.Facing);
        var owner = Tools.Get(entity, p.Owner);
        var x = at.Number(p.Position["x"]);
        var y = at.Number(p.Position["y"]);
        var angle = facing?.Number(p.Facing["angle"]) ?? 0;
        var player = owner?.Number(p.Owner["player"]) ?? 0;
        foreach (var mount in list)
            Entities.Turret(p, ctx.Commands, mount.Turret, x, y, angle, player, entity, mount);
    }

    public void Bind(SystemBinding binding)
    {
        _parent = binding.Column("Attached", "parent");
        _along = binding.Column("Attached", "along");
        _across = binding.Column("Attached", "across");
        _x = binding.Column("Position", "x");
        _y = binding.Column("Position", "y");
        _facing = binding.Column("Facing", "angle");
        _angle = binding.Column("Turret", "angle");
    }

    public void Run(SystemContext ctx)
    {
        var p = mod.P;
        var parent = ctx.Column(_parent);
        var along = ctx.Column(_along);
        var across = ctx.Column(_across);
        var x = ctx.Column(_x);
        var y = ctx.Column(_y);
        var facing = ctx.Column(_facing);
        var turret = ctx.Column(_angle);
        var placeX = p.Position["x"];
        var placeY = p.Position["y"];
        var carrierAngle = p.Facing["angle"];
        for (var i = 0; i < ctx.Count; i++)
        {
            var carrier = (uint)parent[i];
            // Носителя нет (погиб) — турель тоже.
            if (Tools.Get(carrier, p.Position) is not { } place)
            {
                ctx.Commands.Destroy(ctx.Entities[i]);
                continue;
            }
            var angle = Tools.Get(carrier, p.Facing) is { } looks ? looks.Number(carrierAngle) : 0;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            x[i] = place.Number(placeX) + along[i] * c - across[i] * s;
            y[i] = place.Number(placeY) + along[i] * s + across[i] * c;
            facing[i] = angle + turret[i];
        }
    }
}
