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

    /// <summary>planPaths (Д1).</summary>
    private static ISystem PlanPaths(Host host) => Placeholder;

    /// <summary>moveUnits (Д1).</summary>
    private static ISystem MoveUnits(Host host) => Placeholder;

    /// <summary>followCarriers (Д2).</summary>
    private static ISystem FollowCarriers(Host host) => Placeholder;

    /// <summary>traces (Д3).</summary>
    private static ISystem FadeTraces(Host host) => Placeholder;

    /// <summary>vision (Д4): обзор раз в <see cref="Kharos.Host.Vision.Ticks"/> тиков.</summary>
    private static ISystem UpdateVision(Host host) => new DelegateSystem(ctx =>
    {
        if (host.Vision is { } vision && ctx.Tick % Kharos.Host.Vision.Ticks == 1)
            vision.Update(ctx.World);
    });

    /// <summary>updateLandMemory (Д3).</summary>
    private static ISystem RememberLand(Host host) => Placeholder;

    /// <summary>updatePaveMemory (Д3).</summary>
    private static ISystem RememberPaving(Host host) => Placeholder;

    /// <summary>updateBuildMemory (Д4).</summary>
    private static ISystem RememberBuildings(Host host) => Placeholder;
}
