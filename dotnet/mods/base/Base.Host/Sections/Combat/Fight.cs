using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Система fight (combat.ts): бой. Вооружённые бьют врагов — чужих юнитов и здания; свободный стреляет в тех, до кого
/// достаёт с места, получив приказ или попав под огонь — гонится за целью; стреляет стоя и повернувшись к цели. Снаряды
/// летят и бьют, долетев; прочность у нуля — гибель; аварии (Burst) бьют всех вокруг; едкое болото жжёт.
/// <para>
/// Доступ к чужому (Н5-3): кадр — стрелки (Armed, Position), всё о целях — побочными таблицами: метки (Health, Position,
/// Owner), юниты, здания, турели (Mount), выстрелы, взрывы, аварии, обречённые, пути, превращения, выключенные, тактика,
/// счета игроков, магазины юнитов. Урон, повороты, цели копятся в памяти тика (<see cref="Battle"/>) и уходят в мир в
/// конце: прочность и повороты — командами, цели — столбцами кадра, выстрелы и взрывы — столбцами пишущих таблиц, траты
/// магазинов — командами.
/// </para>
/// </summary>
internal sealed class Fight : ISystem
{
    public const int MarksT = 0, UnitsT = 1, BuildingsT = 2, MountsT = 3, ShotsT = 4, BlastsT = 5, BurstsT = 6, DoomedT = 7, PathsT = 8,
        ConvertingT = 9, OffT = 10, TacticsT = 11, PlayersT = 12, MagazinesT = 13;

    private readonly Battle _battle = new();

    /// <summary>Объявление системы fight: кадр и таблицы по порядку номеров выше.</summary>
    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Armed.Name, Names.Position.Name).Write(Names.Armed.Name)
        .Table(Names.Health.Name, Names.Position.Name, Names.Owner.Name)
        .Table(Names.Unit.Name, Names.Proto.Name, Names.Facing.Name)
        .Table(Names.Building.Name, Names.Proto.Name, Names.Footprint.Name)
        .Table(Names.Mount.Name, Names.Proto.Name)
        .WriteTable(Names.Shot.Name, Names.Position.Name, Names.Proto.Name)
        .WriteTable(Names.Blast.Name)
        .Table(Names.Burst.Name, Names.Position.Name)
        .WriteTable(Names.Doomed.Name)
        .Table(Names.Path.Name)
        .Table(Names.Converting.Name)
        .Table(Names.Off.Name)
        .Table(Names.Tactics.Name)
        .Table(Names.Player.Name, Names.Team.Name)
        .Table(Names.Magazine.Name);

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        Traces.Layer = binding.Layer("traces");
        _battle.Bind(binding);
    }

    public void Run(SystemContext ctx) => _battle.Run(ctx);
}
