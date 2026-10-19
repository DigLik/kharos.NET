using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Система restTurrets (turrets.ts): турель без цели и без работы поворачивается туда же, куда смотрит её носитель, —
/// свободные башни едут стволом вперёд, а не туда, куда стреляли в последний раз. Кадр — все турели (Mount, Proto); у кого
/// есть цель стрельбы (Armed.target) или работы (Repair.target) — побочными таблицами.
/// </summary>
internal sealed class RestTurrets : ISystem
{
    private Column _angle, _proto, _armedTarget, _repairTarget;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Mount.Name, Names.Proto.Name).Write(Names.Mount.Name)
        .Table(Names.Armed.Name)
        .Table(Names.Repair.Name);

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _angle = binding.Column(Names.Mount.Name, Names.Mount.Angle);
        _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
        _armedTarget = binding.Table(0).Column(Names.Armed.Name, Names.Armed.Target);
        _repairTarget = binding.Table(1).Column(Names.Repair.Name, Names.Repair.Target);
    }

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var angles = ctx.Column(_angle);
        var protos = ctx.Column(_proto);
        var entities = ctx.Entities;
        var busy = new HashSet<uint>();
        var armed = ctx.Table(0);
        var targets = armed.Column(_armedTarget);
        for (var row = 0; row < armed.Count; row++)
            if (targets[row] != 0)
                busy.Add(armed.Entities[row]);
        var repair = ctx.Table(1);
        targets = repair.Column(_repairTarget);
        for (var row = 0; row < repair.Count; row++)
            if (targets[row] != 0)
                busy.Add(repair.Entities[row]);
        for (var row = 0; row < ctx.Count; row++)
        {
            if (angles[row] == 0 || busy.Contains(entities[row]) || Arms.Turret((int)protos[row]) is not { } turret)
                continue;
            angles[row] = Battle.TurnToward(angles[row], 0, turret.Turn * Core.Step);
        }
    }
}
