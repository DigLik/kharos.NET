using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Раздел Defense (С5b). Оборона: боеприпасы и снабжение войск (<see cref="Supply"/>, <see cref="Magazines"/>), щиты
/// (<see cref="Domes"/>, <see cref="ChargeShields"/>), турели в покое (<see cref="RestTurrets"/>). Системы supply,
/// restTurrets, chargeShields — места в тике по <see cref="Order.Tick"/>; запросы defense.*.
/// <para>
/// Бою (<see cref="Hooks"/> Combat) раздел даёт магазины юнитов (unitHasAmmo, spendAmmo, isEmpty) и купола щитов тика
/// (<see cref="Domes"/>). Склады, потоки и предложения сетей берёт у логистики, долю энергии — у энергии
/// (<see cref="Energy.Books.ShareOf"/>), зоны — у стройки; своих приказов у раздела нет (приказы supply и haul — Logistics).
/// </para>
/// </summary>
internal static class DefenseSection
{
    public static void Setup(ModBuilder mod)
    {
        Supply.Declare(Order.System(mod, "supply", new Supply()));
        RestTurrets.Declare(Order.System(mod, "restTurrets", new RestTurrets()));
        ChargeShields.Declare(Order.System(mod, "chargeShields", new ChargeShields()));
        Hooks.UnitHasAmmo = (call, entity, type, weapon) => Magazines.HasAmmo(call, entity, type, weapon);
        Hooks.SpendAmmo = (call, entity, type, weapon) => Magazines.Spend(call, entity, type, weapon);
        Hooks.Empty = (call, entity) => Magazines.IsEmpty(call, entity);
        Hooks.Shields = call => Domes.Of(call);
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(DefenseQueries.Magazine, ctx => ctx.Reply(Magazines.CapacityOf(Args<UnitArgs>(ctx).Unit)));
        mod.Query(DefenseQueries.ShotCost, ctx =>
        {
            var args = Args<ShotCostArgs>(ctx);
            ctx.Reply(Core.Tables.Weapons.TryGetValue(args.Weapon, out var weapon) ? Magazines.ShotCost(args.Unit, weapon.Reload) : 0);
        });
        mod.Query(DefenseQueries.Durability, ctx =>
        {
            var args = Args<BuildArgs>(ctx);
            ctx.Reply(Construction.Sites.Durability(args.Type, args.X, args.Y));
        });
        mod.Query(DefenseQueries.Empty, ctx => ctx.Reply(Magazines.IsEmpty(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
        mod.Query(DefenseQueries.Domes, ctx =>
        {
            var domes = Domes.Of(Call.Of(ctx));
            ctx.Reply(new DomeList
            {
                Items = [.. (domes?.Items ?? []).Select(dome => new DomeInfo
                    { Entity = dome.Entity, Player = dome.Player, X = dome.X, Y = dome.Y, Radius = dome.Radius, Charge = dome.Charge })],
            });
        });
        mod.Query(DefenseQueries.Absorb, ctx =>
        {
            var hits = Args<AbsorbArgs>(ctx).Hits;
            var domes = Domes.Of(Call.Of(ctx));
            var passed = hits.Select(hit => domes?.Absorb(hit.Player, hit.X, hit.Y, hit.Damage, hit.FromX, hit.FromY, hit.FromAir) ?? hit.Damage).ToArray();
            ctx.Reply(new AbsorbResult
            {
                Passed = passed,
                Domes = [.. (domes?.Items ?? []).Select(dome => new DomeState { Entity = dome.Entity, Charge = dome.Charge, Hit = dome.Hit, Down = dome.Down })],
            });
        });
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
