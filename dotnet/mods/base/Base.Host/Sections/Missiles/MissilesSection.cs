using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Раздел Missiles (С7). Ракеты: шахты (<see cref="Silos"/>), пуски и полёт (<see cref="Flight"/>), перехватчики
/// (<see cref="Interceptors"/>), радары (<see cref="Radar"/>). Системы scan и launchMissiles — места в тике по
/// <see cref="Order.Tick"/>; приказы warhead, cancelWarhead, launch, abortLaunch; запросы missiles.*.
/// <para>
/// Разведка радара и вспышка ядерного взрыва — сущности <c>Reveal</c>: обзор считает движок. Что видно кому — правила
/// <c>Sighting</c> (ракета — всем, метки пуска и удара — списком, ракета-перехватчик — по точкам). Взрыв долетевшей ракеты —
/// <c>Burst</c>: бьёт ближайший бой (Combat). Долю энергии шахты, перехватчика и радара берёт у раздела Energy
/// (<see cref="Energy.Books.ShareOf"/>), готовое для сборки — со складов сети труб (<see cref="Research.Networks"/>).
/// </para>
/// </summary>
internal static class MissilesSection
{
    public static void Setup(ModBuilder mod)
    {
        Radar.Declare(Order.System(mod, "scan", new Radar()));
        Flight.Declare(Order.System(mod, "launchMissiles", new Flight()));
        Commands.Handle<WarheadOrder>(WarheadOrder.Name, (call, player, order) => Silos.Start(call, player, order.Silo));
        Commands.Handle<CancelWarheadOrder>(CancelWarheadOrder.Name, (call, player, order) => Silos.Cancel(call, player, order.Silo));
        Commands.Handle<LaunchOrder>(LaunchOrder.Name, (call, player, order) => Silos.Launch(call, player, order.Silo, order.X, order.Y));
        Commands.Handle<AbortLaunchOrder>(AbortLaunchOrder.Name, (call, player, order) => Silos.Abort(call, player, order.Silo));
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(MissilesQueries.WarheadLack, ctx =>
        {
            var args = Args<TargetArgs>(ctx);
            ctx.Reply(Silos.Lack(Call.Of(ctx), args.Player, args.Target) ?? "");
        });
        mod.Query(MissilesQueries.InterceptorLack, ctx => ctx.Reply(Interceptors.Lack(Call.Of(ctx), Args<EntityArgs>(ctx).Entity) ?? ""));
        mod.Query(MissilesQueries.LaunchLeft, ctx => ctx.Reply(Silos.Left(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
