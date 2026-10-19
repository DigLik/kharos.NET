using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Раздел Combat (С5а). Бой (combat.ts, target.ts): цели, выстрелы, урон, гибель, взрывы и следы боя — система fight
/// (<see cref="Fight"/>, <see cref="Battle"/>), самолечение и станции — recover (<see cref="Recover"/>); приказ attack и
/// правило атаки в приказе точкой (<see cref="Attack"/>); приказ идти или стоять снимает цель; запросы combat.*.
/// <para>
/// Другим разделам: <see cref="Hooks"/> — магазины и щиты (С5b), тактика (С8), грузовики (С2b). Здания, разрушенные
/// боем, зовут <see cref="Energy.Reactors.Meltdown"/> и роняют груз (<see cref="Mining.Drops.DropCargo"/>); аварию
/// (Burst) бой разбирает в следующем тике. Стену на линии огня бой ищет по своим меткам тика, не инструментом Ray.
/// </para>
/// </summary>
internal static class CombatSection
{
    public static void Setup(ModBuilder mod)
    {
        Fight.Declare(Order.System(mod, "fight", new Fight()));
        Recover.Declare(Order.System(mod, "recover", new Recover()));
        Commands.Handle<AttackOrder>(AttackOrder.Name, (call, player, order) => Attack.Order(call, player, order.Units, order.Target));
        // Приказ идти или стоять снимает цель с юнита и его турелей, а с ней патруль и поход.
        Commands.OnRelease((call, unit) =>
        {
            Attack.Stop(call, unit);
            Attack.ClearTactics(call, unit);
        });
        Commands.Resolve(10, Attack.Resolve);
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(CombatQueries.CanAttack, ctx =>
        {
            var args = Args<TargetArgs>(ctx);
            ctx.Reply(Attack.CanAttack(Call.Of(ctx), args.Player, args.Target));
        });
        mod.Query(CombatQueries.CanFight, ctx => ctx.Reply(Attack.CanFight(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
        mod.Query(CombatQueries.Resolve, ctx =>
        {
            var args = Args<ResolveArgs>(ctx);
            var call = Call.Of(ctx);
            var units = Commands.OwnUnits(call, args.Player, args.Units).ToArray();
            var items = new List<ResolvedOrder>();
            if (units.Length > 0 && double.IsFinite(args.X) && double.IsFinite(args.Y))
                foreach (var order in Commands.ResolveOrder(call, args.Player, units, args.X, args.Y, args.Fog))
                    items.Add(Describe(order));
            ctx.Reply(new ResolvedList { Items = [.. items] });
        });
    }

    /// <summary>Приказ с целью — для ответа запроса.</summary>
    private static ResolvedOrder Describe(object order) => order switch
    {
        AttackOrder attack => new ResolvedOrder { Name = AttackOrder.Name, Units = attack.Units, Target = attack.Target },
        HaulOrder haul => new ResolvedOrder { Name = HaulOrder.Name, Units = haul.Units, Target = haul.Mine },
        PickupOrder pickup => new ResolvedOrder { Name = PickupOrder.Name, Units = pickup.Units, Target = pickup.Drop },
        SupplyOrder supply => new ResolvedOrder { Name = SupplyOrder.Name, Units = supply.Units, Target = supply.Target },
        AssistOrder assist => new ResolvedOrder { Name = AssistOrder.Name, Units = assist.Units, Target = assist.Site },
        MoveOrder move => new ResolvedOrder { Name = MoveOrder.Name, Units = move.Units, X = move.X, Y = move.Y },
        _ => new ResolvedOrder { Name = Commands.NameOf(order) },
    };

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
