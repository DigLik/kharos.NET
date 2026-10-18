using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;
using Queries = Base.Shared.Queries.ConstructionQueries;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Раздел Construction (С1). Стройка: площадки и строители, ремонт, разбор, покрытие, землеройные работы, стены и
/// ворота, зоны строительства (кэш по ревизии мира — <see cref="Zones"/>). Системы gates и construct; приказы build,
/// assist, cancelBuild, demolish, pave, unpave, walls, dig; правило чужих зон для ядра (Placement.ForeignZone);
/// запросы construction.*. Что нужно от других разделов — <see cref="Hooks"/>.
/// </summary>
internal static class ConstructionSection
{
    public static void Setup(ModBuilder mod)
    {
        ConstructionOrders.Setup();
        Placement.ForeignZone = (_, player, x, y, w, h) => Zones.InForeignZone(player, x, y, w, h);
        Factory.OnBuilding(Gates.Equip);
        Order.System(mod, "gates", new Gates())
            .Query(Names.Gate.Name, Names.Footprint.Name, Names.Position.Name, Names.Owner.Name).Without(Names.Site.Name)
            .Write(Names.Gate.Name, Names.Footprint.Name).Every(Gates.CheckTicks);
        // Таблицы — по порядку Construct.*Table.
        Order.System(mod, "construct", new Construct())
            .Query(Names.Repair.Name, Names.Position.Name, Names.Owner.Name).Write(Names.Repair.Name)
            .Table(Names.Site.Name, Names.Position.Name, Names.Owner.Name)
            .Table(Names.Pave.Name, Names.Position.Name, Names.Owner.Name)
            .Table(Names.Dig.Name, Names.Position.Name, Names.Owner.Name)
            .Table(Names.Health.Name, Names.Position.Name, Names.Owner.Name)
            .Table(Names.Upgrade.Name, Names.Position.Name, Names.Owner.Name);
        SetupQueries(mod);
    }

    private static void SetupQueries(ModBuilder mod)
    {
        mod.Query(Queries.CanBuild, ctx =>
        {
            var args = Args<BuildArgs>(ctx);
            ctx.Reply(Sites.CanBuild(Call.Of(ctx), args.Player, args.Type, args.X, args.Y));
        });
        mod.Query(Queries.Unlocked, ctx =>
        {
            var args = Args<BuildArgs>(ctx);
            ctx.Reply(Sites.Unlocked(Call.Of(ctx), args.Player, args.Type));
        });
        mod.Query(Queries.CanPave, ctx =>
        {
            var args = Args<BuildArgs>(ctx);
            ctx.Reply(Paving.CanPave(Call.Of(ctx), args.Player, args.Type, args.X, args.Y));
        });
        mod.Query(Queries.CanDig, ctx =>
        {
            var args = Args<BuildArgs>(ctx);
            ctx.Reply(Earthworks.CanDig(Call.Of(ctx), args.Player, args.Type, args.X, args.Y));
        });
        mod.Query(Queries.FreeCredits, ctx => ctx.Reply(Sites.FreeCredits(Call.Of(ctx), Args<PlayerArgs>(ctx).Player)));
        mod.Query(Queries.WallStroke, ctx =>
        {
            var args = Args<StrokeArgs>(ctx);
            ctx.Reply(new FlagList { Items = Sites.WallStroke(Call.Of(ctx), args.Player, args.Tiles, args.Gate ? "gate" : "wall") });
        });
        mod.Query(Queries.Zones, ctx => ctx.Reply(List(Zones.Of(Args<PlayerArgs>(ctx).Player))));
        mod.Query(Queries.Grids, ctx => ctx.Reply(List(Zones.GridsOf(Args<PlayerArgs>(ctx).Player))));
        mod.Query(Queries.BuildSpeed, ctx =>
        {
            var args = Args<BuildArgs>(ctx);
            ctx.Reply(Factory.Building(args.Type) is null ? 0.0 : Paving.BuildSpeed(args.Type, args.X, args.Y));
        });
        mod.Query(Queries.AwaitsMaterials, ctx => ctx.Reply(Sites.AwaitsMaterials(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
        mod.Query(Queries.SiteAt, ctx =>
        {
            var args = Args<TileArgs>(ctx);
            ctx.Reply(Sites.SiteAt(Call.Of(ctx), args.X, args.Y));
        });
        mod.Query(Queries.PaveAt, ctx =>
        {
            var args = Args<TileArgs>(ctx);
            ctx.Reply(Paving.At(Call.Of(ctx), args.X, args.Y));
        });
        mod.Query(Queries.ShortOfCredits, ctx => ctx.Reply(new EntityList { Items = [.. Sites.ShortOfCredits(Call.Of(ctx), Args<PlayerArgs>(ctx).Player)] }));
    }

    private static ZoneList List(IReadOnlyList<Zones.Zone> zones) => new()
    {
        Items = [.. zones.Select(zone => new ZoneInfo { Buildings = [.. zone.Buildings], Sites = [.. zone.Sites], Circles = [.. zone.Circles] })],
    };

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
