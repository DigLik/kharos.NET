using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Energy;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Система chargeShields (shields.ts): раз в тик щиты восстанавливаются на долю энергии своей зоны; без энергии (и
/// выключенный) купол гаснет, запас пропадает. Растёт запас не раньше rules.repairPause секунд после последнего погашенного
/// попадания; пробитый до нуля купол растёт, когда выйдет <see cref="Domes.Down"/>. Кадр — генераторы щитов (Shield,
/// Building, Proto) без площадок.
/// </summary>
internal sealed class ChargeShields : ISystem
{
    private Column _charge, _hit, _down, _proto;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Shield.Name, Names.Building.Name, Names.Proto.Name).Without(Names.Site.Name).Write(Names.Shield.Name);

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _charge = binding.Column(Names.Shield.Name, Names.Shield.Charge);
        _hit = binding.Column(Names.Shield.Name, Names.Shield.Hit);
        _down = binding.Column(Names.Shield.Name, Names.Shield.Down);
        _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
    }

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        var charge = ctx.Column(_charge);
        var hit = ctx.Column(_hit);
        var down = ctx.Column(_down);
        var protos = ctx.Column(_proto);
        var entities = ctx.Entities;
        var tick = (double)ctx.Tick;
        var pause = Core.Rules.RepairPause / Core.Step;
        for (var row = 0; row < ctx.Count; row++)
        {
            var share = Books.ShareOf(call, entities[row]);
            if (share <= 0)
            {
                charge[row] = 0;
                continue;
            }
            if (tick < down[row] || (hit[row] >= 0 && tick - hit[row] < pause))
                continue;
            if (Arms.Building((int)protos[row])?.Spec.Shield is not { } shield)
                continue;
            if (charge[row] < shield.Capacity)
                charge[row] = Math.Min(shield.Capacity, charge[row] + shield.Regen * share * Core.Step);
        }
    }
}
