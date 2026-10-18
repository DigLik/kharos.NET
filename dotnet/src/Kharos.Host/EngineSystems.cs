using Kharos.Engine.Movement;
using Kharos.Engine.World;

namespace Kharos.Host;

/// <summary>
/// Системы движка в порядке тика (docs/dotnet-n5.md, Н5.2): на них ссылаются моды в After/Before полными именами
/// <c>engine.*</c> (ключ — то же полное имя: короткие имена модов с ними не путаются). Порядок между собой — как в
/// sim.ts; встают после систем модов, если те не попросили иначе. Пока не сделанные — пустые места: пакеты Д1–Д4
/// ставят сюда свои системы (каждая — в своём методе).
/// </summary>
internal static class EngineSystems
{
    public const string Paths = "engine.paths";
    public const string Move = "engine.move";
    public const string Mounts = "engine.mounts";
    public const string Traces = "engine.traces";
    public const string Vision = Host.VisionSystem;
    public const string RecallLand = "engine.recallLand";
    public const string RecallPaving = "engine.recallPaving";
    public const string Memory = "engine.memory";

    /// <summary>Все системы движка по порядку.</summary>
    public static IEnumerable<SystemSpec> Create(Host host) =>
    [
        Spec(Paths, PlanPaths(host)),
        Spec(Move, MoveUnits(host)),
        Spec(Mounts, FollowCarriers(host)),
        Spec(Traces, FadeTraces(host)),
        Spec(Vision, UpdateVision(host)),
        Spec(RecallLand, RememberLand(host)),
        Spec(RecallPaving, RememberPaving(host)),
        Spec(Memory, RememberBuildings(host)),
    ];

    private static SystemSpec Spec(string name, ISystem system) => new() { Name = name, Key = name, System = system };

    /// <summary>Пустое место: система ещё не сделана.</summary>
    private static readonly ISystem Placeholder = new DelegateSystem(_ => { });

    /// <summary>planPaths (Д1): пути упёршимся, ехавшим напрямую, в норме тика.</summary>
    private static ISystem PlanPaths(Host host)
    {
        var navigation = NavigationOf(host);
        return new DelegateSystem(_ => navigation.PlanPaths());
    }

    /// <summary>moveUnits (Д1): ход, объезд, расталкивание, перепрокладка.</summary>
    private static ISystem MoveUnits(Host host)
    {
        var navigation = NavigationOf(host);
        return new DelegateSystem(ctx => navigation.MoveUnits(ctx.Tick, ctx.Dt));
    }

    /// <summary>
    /// Ход и пути партии — один на хост: ходовые из data/moves.json, земля, слой paving, границы карты; ими же работают
    /// инструменты 16, 17, 21–28 (EngineTools).
    /// </summary>
    private static Navigation NavigationOf(Host host)
    {
        if (host.Tools.Navigation is { } existing)
            return existing;
        var chassis = host.Content.Data("moves") is { } moves ? ChassisTable.Parse(moves) : ChassisTable.Empty;
        var bounds = host.Map is { } map ? new TileBounds(map.Left, map.Top, map.Right, map.Bottom) : (TileBounds?)null;
        return host.Tools.Navigation = new Navigation(host.World, host.Land, host.Layers, bounds, chassis);
    }

    /// <summary>followCarriers (Д2): сущности на носителях — на свои места; без носителя — уходят.</summary>
    private static ISystem FollowCarriers(Host host)
    {
        var navigation = NavigationOf(host);
        return new DelegateSystem(_ => navigation.FollowCarriers());
    }

    /// <summary>traces (Д3): колея наземных юнитов и истёкшие следы.</summary>
    private static ISystem FadeTraces(Host host) => new DelegateSystem(ctx => host.Traces?.Update(ctx.World, ctx.Tick, ctx.Dt));

    /// <summary>vision (Д4): обзор раз в <see cref="Kharos.Host.Vision.Ticks"/> тиков.</summary>
    private static ISystem UpdateVision(Host host) => new DelegateSystem(ctx =>
    {
        if (host.Vision is { } vision && ctx.Tick % Kharos.Host.Vision.Ticks == 1)
            vision.Update(ctx.World, ctx.Tick);
    });

    /// <summary>updateLandMemory (Д3): игроки узнают правки карты — свои сразу, чужие увидев (раз в 4 тика).</summary>
    private static ISystem RememberLand(Host host) => new DelegateSystem(ctx =>
    {
        if (host.Recall is { } memory && host.Vision is { } vision)
            memory.UpdateLand(new VisionRecall(vision), ctx.World, ctx.Tick);
    });

    /// <summary>updatePaveMemory (Д3): то же для готового покрытия.</summary>
    private static ISystem RememberPaving(Host host) => new DelegateSystem(ctx =>
    {
        if (host.Recall is { } memory && host.Vision is { } vision)
            memory.UpdatePaving(new VisionRecall(vision), ctx.World, ctx.Tick);
    });

    /// <summary>Обзор хоста для памяти игроков.</summary>
    private sealed class VisionRecall(Vision vision) : Kharos.Engine.Recall.IRecallVision
    {
        public IEnumerable<uint> Players => vision.Players;

        public bool Sees(uint player, double x, double y) => vision.Sees(player, x, y);
    }

    /// <summary>updateBuildMemory (Д4): игроки запоминают чужие здания, ушедшие из обзора, — на тиках пересчёта обзора.</summary>
    private static ISystem RememberBuildings(Host host) => new DelegateSystem(ctx =>
    {
        if (host.Vision is { } vision && ctx.Tick % Kharos.Host.Vision.Ticks == 1)
            host.Buildings.Update(ctx.World, vision, host.RememberComponents);
    });
}
