using Base.Host.Sections.Combat;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Defense;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Kernel;
using Base.Host.Sections.Logistics;
using Base.Host.Sections.Mining;
using Base.Host.Sections.Missiles;
using Base.Host.Sections.Pipes;
using Base.Host.Sections.Players;
using Base.Host.Sections.Production;
using Base.Host.Sections.Research;
using Base.Host.Sections.Showcase;
using Base.Host.Sections.Tactics;
using Base.Host.Sections.Trade;
using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// Мод base — хост-часть игры Kharos (docs/dotnet-n5.md): механики sim-ts по разделам на модели компонентов TS
/// (schema/*.json), данные — data/*.json (<see cref="BaseTables"/>), приказы — Base.Shared/Orders. Разделы объявляют
/// системы, события и приказы; порядок тика — <see cref="Order"/>.
/// </summary>
public sealed class BaseMod : Mod
{
    /// <summary>Таблицы data/*.json после патчей модов.</summary>
    internal BaseTables Tables { get; private set; } = new();

    /// <summary>Идущая партия (запись games.json); у чужой партии — null.</summary>
    internal GameSpec? Game { get; private set; }

    public override void Setup(ModBuilder mod)
    {
        KernelSection.Setup(mod);
        ConstructionSection.Setup(mod);
        MiningSection.Setup(mod);
        LogisticsSection.Setup(mod);
        PipesSection.Setup(mod);
        TradeSection.Setup(mod);
        EnergySection.Setup(mod);
        ProductionSection.Setup(mod);
        CombatSection.Setup(mod);
        DefenseSection.Setup(mod);
        ResearchSection.Setup(mod);
        MissilesSection.Setup(mod);
        TacticsSection.Setup(mod);
        PlayersSection.Setup(mod);
        ShowcaseSection.Setup(mod);
        Order.Apply(mod);
    }

    public override void Load(LoadContext ctx)
    {
        Tables = BaseTables.Load(name => ctx.TryData(name, out var bytes) ? bytes.ToArray() : null);
        Game = Tables.Games.GetValueOrDefault(ctx.Game);
        // Общее ядра для разделов: таблицы, правила и партия с настройками, схема (Kernel/Base.cs).
        Kernel.Core.Load(Tables, ctx);
        MiningSection.Load(ctx);
    }
}
