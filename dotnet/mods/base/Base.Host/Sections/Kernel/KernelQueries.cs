using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;
using Queries = Base.Shared.Queries.KernelQueries;

namespace Base.Host.Sections.Kernel;

/// <summary>Запросы ядра (Base.Shared/Queries/KernelQueries.cs): только чтение — для тестов, администратора, редактора.</summary>
internal static class KernelQueriesSetup
{
    public static void Setup(ModBuilder mod)
    {
        mod.Query(Queries.Credits, ctx => ctx.Reply(Accounts.Credits(Call.Of(ctx), Args<PlayerArgs>(ctx).Player)));
        mod.Query(Queries.Rewards, ctx => ctx.Reply(List(Accounts.Rewards(Call.Of(ctx), Args<PlayerArgs>(ctx).Player))));
        mod.Query(Queries.CanPlace, ctx =>
        {
            var args = Args<PlaceArgs>(ctx);
            ctx.Reply(Placement.CanPlace(Call.Of(ctx), args.Type, args.X, args.Y, args.Gap));
        });
        mod.Query(Queries.CanDeploy, ctx =>
        {
            var args = Args<DeployArgs>(ctx);
            var call = Call.Of(ctx);
            ctx.Reply(args.At ? Conversion.CanDeployAt(call, args.Player, args.Unit, args.X, args.Y) : Conversion.CanDeploy(call, args.Player, args.Unit));
        });
        mod.Query(Queries.CanPack, ctx =>
        {
            var args = Args<DeployArgs>(ctx);
            ctx.Reply(Conversion.CanPack(Call.Of(ctx), args.Player, args.Unit));
        });
        mod.Query(Queries.Producible, ctx => ctx.Reply(List(Production.Producible(Call.Of(ctx), Args<EntityArgs>(ctx).Entity))));
        mod.Query(Queries.Queue, ctx => ctx.Reply(List(Production.Queue(Call.Of(ctx), Args<EntityArgs>(ctx).Entity))));
        mod.Query(Queries.Army, ctx => ctx.Reply(Production.Army(Call.Of(ctx), Args<PlayerArgs>(ctx).Player)));
        mod.Query(Queries.OverLimit, ctx =>
        {
            var args = Args<UnitArgs>(ctx);
            ctx.Reply(Production.OverLimit(Call.Of(ctx), args.Player, args.Unit));
        });
        mod.Query(Queries.MissingResearch, ctx =>
        {
            var args = Args<UnitArgs>(ctx);
            ctx.Reply(Production.MissingResearch(Call.Of(ctx), args.Player, args.Unit) ?? "");
        });
        mod.Query(Queries.MissingRequirements, ctx =>
        {
            var args = Args<UnitArgs>(ctx);
            ctx.Reply(List(Production.MissingRequirements(Call.Of(ctx), args.Player, args.Unit)));
        });
        mod.Query(Queries.BuildTicks, ctx => ctx.Reply(Production.BuildTicks(Args<UnitArgs>(ctx).Unit)));
        mod.Query(Queries.Orders, ctx => ctx.Reply(List(Commands.QueueOf(Call.Of(ctx), Args<EntityArgs>(ctx).Entity).Select(item => item.Name))));
    }

    private static NameList List(IEnumerable<string> items) => new() { Items = [.. items] };

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
