using System.Runtime.CompilerServices;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>Чей шаг тика: приказы (хост до систем), система мода или система движка.</summary>
internal enum StepKind
{
    Orders,
    Mod,
    Engine,
}

/// <summary>Шаг тика: ключ системы, раздел-владелец, имя системы в sim.ts.</summary>
internal sealed record Step(string Key, string Section, StepKind Kind, string Ts);

/// <summary>
/// Порядок тика (docs/dotnet-n5.md, Н5.2) — 39 систем sim.ts по порядку; что считает движок, — ключами engine.*.
/// Раздел объявляет систему через <see cref="System"/>: её место берётся отсюда. После всех разделов
/// <see cref="Apply"/> ставит каждую систему мода после ближайшей предыдущей объявленной, так что недописанные
/// системы (шаги без объявления) планировщику не нужны.
/// </summary>
internal static class Order
{
    /// <summary>
    /// Ссылаться ли на системы движка (engine.*) в After и Before. Включено: хост ставит их всегда (Kharos.Host,
    /// EngineSystems.cs; пока не сделанные — пустые места), After на неизвестную систему было бы ошибкой расписания.
    /// </summary>
    public static readonly bool EngineKeys = true;

    private const string Kernel = "kernel", Energy = "energy", Mining = "mining", Production = "production",
        Research = "research", Missiles = "missiles", Construction = "construction", Engine = "engine", Pipes = "pipes",
        Logistics = "logistics", Trade = "trade", Combat = "combat", Tactics = "tactics", Defense = "defense";

    /// <summary>Шаги тика по порядку sim.ts.</summary>
    public static readonly Step[] Tick =
    [
        // Приказы игроков — хост до систем (Kernel/Commands); Shift — очередь Orders.
        new("commands", Kernel, StepKind.Orders, "commands"),
        // До превращений: доехавший MCV начинает разворачиваться в этот же тик.
        new("deployOrders", Kernel, StepKind.Mod, "deployOrders"),
        new("convert", Kernel, StepKind.Mod, "convert"),
        // До энергии зон: горящий стержень уже даёт энергию в этот тик.
        new("burnFuel", Energy, StepKind.Mod, "burnFuel"),
        new("markBusy", Energy, StepKind.Mod, "markBusy"),
        new("storeEnergy", Energy, StepKind.Mod, "storeEnergy"),
        new("openBooks", Energy, StepKind.Mod, "openBooks"),
        new("produceUnits", Kernel, StepKind.Mod, "produceUnits"),
        new("refine", Mining, StepKind.Mod, "refine"),
        new("assemble", Production, StepKind.Mod, "assemble"),
        new("research", Research, StepKind.Mod, "research"),
        new("triggers", Research, StepKind.Mod, "triggers"),
        new("scan", Missiles, StepKind.Mod, "scan"),
        // До движения: закрывшиеся ворота уже не пропустят.
        new("gates", Construction, StepKind.Mod, "gates"),
        new("engine.paths", Engine, StepKind.Engine, "planPaths"),
        new("engine.move", Engine, StepKind.Engine, "moveUnits"),
        new("engine.mounts", Engine, StepKind.Engine, "followCarriers"),
        new("construct", Construction, StepKind.Mod, "construct"),
        // До грузовиков: что идёт по трубам, грузовики уже не везут.
        new("pipeFlow", Pipes, StepKind.Mod, "pipeFlow"),
        new("haul", Logistics, StepKind.Mod, "haul"),
        new("fetchMaterials", Logistics, StepKind.Mod, "fetchMaterials"),
        new("trade", Trade, StepKind.Mod, "trade"),
        new("ferry", Trade, StepKind.Mod, "ferry"),
        // До боя: взрыв долетевшей ракеты бьёт в этот же бой.
        new("launchMissiles", Missiles, StepKind.Mod, "launchMissiles"),
        new("fight", Combat, StepKind.Mod, "fight"),
        new("patrol", Tactics, StepKind.Mod, "patrol"),
        new("supply", Defense, StepKind.Mod, "supply"),
        // После всех дел тика: освободившиеся берут следующий приказ из очереди.
        new("followOrders", Tactics, StepKind.Mod, "followOrders"),
        new("restTurrets", Defense, StepKind.Mod, "restTurrets"),
        new("recover", Combat, StepKind.Mod, "recover"),
        new("chargeShields", Defense, StepKind.Mod, "chargeShields"),
        new("earn", Energy, StepKind.Mod, "earn"),
        new("engine.traces", Engine, StepKind.Engine, "traces"),
        new("engine.vision", Engine, StepKind.Engine, "vision"),
        // После обзора: игроки узнают правки карты, которые теперь видят.
        new("engine.recallLand", Engine, StepKind.Engine, "updateLandMemory"),
        new("renewDeposits", Mining, StepKind.Mod, "renewDeposits"),
        new("engine.recallPaving", Engine, StepKind.Engine, "updatePaveMemory"),
        new("engine.memory", Engine, StepKind.Engine, "updateBuildMemory"),
        new("closeBooks", Energy, StepKind.Mod, "closeBooks"),
    ];

    private static readonly ConditionalWeakTable<ModBuilder, List<(int At, SystemDeclaration Declaration)>> Declared = new();

    /// <summary>Номер шага по ключу; нет — -1.</summary>
    public static int IndexOf(string key) => Array.FindIndex(Tick, step => step.Key == key);

    /// <summary>
    /// Система мода на шаге key (ключ из <see cref="Tick"/>, раздел-владелец — его): имя «раздел/ключ», ключ — для
    /// чужих After и Before. Запрос и запись — дальше цепочкой.
    /// </summary>
    public static SystemDeclaration System(ModBuilder mod, string key, ISystem system)
    {
        var at = IndexOf(key);
        if (at < 0 || Tick[at].Kind != StepKind.Mod)
            throw new ArgumentException($"шага {key} нет среди систем мода в Order.Tick");
        var declaration = mod.System($"{Tick[at].Section}/{key}", system).Key(key);
        Declared.GetOrCreateValue(mod).Add((at, declaration));
        return declaration;
    }

    /// <summary>Порядок объявленных систем мода: каждая — после ближайшей предыдущей объявленной (и между шагами движка).</summary>
    public static void Apply(ModBuilder mod)
    {
        if (!Declared.TryGetValue(mod, out var declared))
            return;
        declared.Sort((a, b) => a.At.CompareTo(b.At));
        for (var i = 0; i < declared.Count; i++)
        {
            var (at, declaration) = declared[i];
            var previous = i > 0 ? declared[i - 1].At : -1;
            if (previous >= 0)
                declaration.After(Tick[previous].Key);
            if (!EngineKeys)
                continue;
            // Системы движка — по обе стороны: ближайшая до шага и ближайшая после.
            for (var step = at - 1; step >= 0; step--)
                if (Tick[step].Kind == StepKind.Engine)
                {
                    declaration.After(Tick[step].Key);
                    break;
                }
            for (var step = at + 1; step < Tick.Length; step++)
                if (Tick[step].Kind == StepKind.Engine)
                {
                    declaration.Before(Tick[step].Key);
                    break;
                }
        }
        Declared.Remove(mod);
    }
}
