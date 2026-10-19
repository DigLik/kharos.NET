using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Production;

/// <summary>
/// Раздел Production (С4). Производство: цеха собирают изделия из сырья (<see cref="Assembling"/>, система assemble — место
/// в тике из <see cref="Order.Tick"/>). Заказ сырья цехом (inputsOf), его вывоз и выключатель (приказ work, флаг
/// Assembly.on) — у Logistics и Energy; юниты на заводах — Kernel/Production. Запросы production.*.
/// </summary>
internal static class ProductionSection
{
    public static void Setup(ModBuilder mod)
    {
        Order.System(mod, "assemble", new Assembling.Assemble()).Query(Names.Assembly.Name, Names.Stock.Name, Names.Proto.Name).Without(Names.Site.Name);
        mod.Query(ProductionQueries.HasRoom, ctx =>
        {
            var call = Call.Of(ctx);
            var entity = Args<EntityArgs>(ctx).Entity;
            ctx.Reply(Assembling.RecipeOf(call, entity) is { } recipe && call.Has(entity, S.Stock) && Assembling.HasRoom(call, entity, recipe.Product, recipe.Spec));
        });
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
