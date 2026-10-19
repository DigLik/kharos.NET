using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using ConstructionHooks = Base.Host.Sections.Construction.Hooks;
using LogisticsHooks = Base.Host.Sections.Logistics.Hooks;

namespace Base.Host.Sections.Research;

/// <summary>
/// Раздел Research (С6). Исследования и уровни зданий: план и прогресс игрока (<see cref="Knowledge"/>, компонент Science
/// на сущности счёта), выбор темы при простое без сырья (<see cref="Picks"/>), лаборатории (<see cref="Labs"/>, система
/// research), темы-открытия (<see cref="Triggers"/>, система triggers), склады сети здания (<see cref="Networks"/>),
/// уровни зданий (<see cref="Upgrades"/>). Приказы research, dequeue, cancelResearch, upgrade, cancelUpgrade; запросы
/// research.*. Дерево технологий, общее с клиентом, — <see cref="ResearchRules"/>.
/// <para>
/// Ставит крючки: нужды лабораторий логистике (<see cref="LogisticsHooks.ResearchNeeds"/>), отмену улучшения при
/// разборе стройке (<see cref="ConstructionHooks.CancelUpgrade"/>); готовой лаборатории — компонент Research.
/// </para>
/// </summary>
internal static class ResearchSection
{
    public static void Setup(ModBuilder mod)
    {
        // Добыча шахт в TS — в haul, после лабораторий: накопанное в этот тик лаборатория возьмёт только в следующий.
        Order.System(mod, "research", new Labs())
            .Query(Names.Research.Name, Names.Owner.Name).Without(Names.Site.Name).Write(Names.Research.Name)
            .Table(Names.Stock.Name, Names.Building.Name).Before("mining/extract");
        Order.System(mod, "triggers", new Triggers())
            .Query(Names.Player.Name).Every(Triggers.Ticks)
            .Table(Names.Building.Name, Names.Proto.Name, Names.Owner.Name, Names.Position.Name)
            .Table(Names.Site.Name);
        // Лаборатория ведёт исследования с той минуты, как готова: компонент состояния — сразу (в TS — с первого тика).
        Factory.OnBuilding((call, entity, type, _) =>
        {
            if (Factory.Building(type)?.Researches == true && !call.Has(entity, R.Research))
                call.Add(entity, R.Research).Number(R.ResearchSpeed, 0).Text(R.ResearchLack, "");
        });
        Commands.Handle<ResearchOrder>(ResearchOrder.Name, (call, player, order) => Knowledge.PlanResearch(call, player, order.Topic, order.Front));
        Commands.Handle<DequeueOrder>(DequeueOrder.Name, (call, player, order) => Knowledge.Dequeue(call, player, order.Topic));
        Commands.Handle<CancelResearchOrder>(CancelResearchOrder.Name, (call, player, _) => Knowledge.Cancel(call, player));
        Commands.Handle<UpgradeOrder>(UpgradeOrder.Name, (call, player, order) => Upgrades.Order(call, player, order.Building, order.Builders));
        Commands.Handle<CancelUpgradeOrder>(CancelUpgradeOrder.Name, (call, player, order) => Upgrades.Cancel(call, player, order.Building));
        LogisticsHooks.ResearchNeeds = (call, player) => Picks.Needs(call, player);
        ConstructionHooks.CancelUpgrade = Upgrades.Cancel;
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(ResearchQueries.Done, ctx =>
        {
            var call = Call.Of(ctx);
            ctx.Reply(new NameList { Items = [.. Knowledge.Done(call, Accounts.Find(call, Args<PlayerArgs>(ctx).Player))] });
        });
        mod.Query(ResearchQueries.Plan, ctx =>
        {
            var call = Call.Of(ctx);
            ctx.Reply(new NameList { Items = [.. Knowledge.Plan(call, Accounts.Find(call, Args<PlayerArgs>(ctx).Player))] });
        });
        mod.Query(ResearchQueries.Progress, ctx =>
        {
            var call = Call.Of(ctx);
            var args = Args<TopicArgs>(ctx);
            ctx.Reply(Knowledge.ProgressOf(call, Accounts.Find(call, args.Player), args.Topic));
        });
        mod.Query(ResearchQueries.Has, ctx =>
        {
            var args = Args<TopicArgs>(ctx);
            ctx.Reply(Accounts.Researched(Call.Of(ctx), args.Player, args.Topic.Length > 0 ? args.Topic : null));
        });
        mod.Query(ResearchQueries.Current, ctx => ctx.Reply(Picks.Current(Call.Of(ctx), Args<PlayerArgs>(ctx).Player) ?? ""));
        mod.Query(ResearchQueries.Price, ctx =>
        {
            var price = ResearchRules.Price(Knowledge.Topics, Args<TopicArgs>(ctx).Topic, Core.Rules);
            ctx.Reply(new TopicPriceInfo
            {
                Cost = price.Cost,
                Goods = [.. price.Materials.Select(item => item.Good)],
                Amounts = [.. price.Materials.Select(item => item.Amount)],
                Seconds = price.Seconds,
            });
        });
        mod.Query(ResearchQueries.Needs, ctx =>
        {
            var needs = Picks.Needs(Call.Of(ctx), Args<PlayerArgs>(ctx).Player);
            ctx.Reply(new AmountList { Goods = [.. needs.Select(need => need.Good)], Amounts = [.. needs.Select(need => need.Amount)] });
        });
        mod.Query(ResearchQueries.UpgradeLack, ctx =>
        {
            var args = Args<TargetArgs>(ctx);
            ctx.Reply(Upgrades.Lack(Call.Of(ctx), args.Player, args.Target) ?? "");
        });
        mod.Query(ResearchQueries.Upgradable, ctx =>
        {
            var args = Args<TargetArgs>(ctx);
            ctx.Reply(Upgrades.IsUpgradable(Call.Of(ctx), args.Player, args.Target));
        });
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
