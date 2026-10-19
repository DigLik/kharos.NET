using System.Buffers.Binary;
using Base.Shared;
using Base.Shared.Queries;
using Kharos.Host;

namespace Kharos.Base.Scenario;

/// <summary>Метка (Mark) глазами теста: вид («launch», «strike»), тик конца, хозяин ракеты и игроки из списка зрителей.</summary>
public sealed record MarkState(uint Entity, string Kind, double Until, uint Player, uint[] Viewers);

// Ракеты (раздел Missiles, С7): шахты, ракеты в полёте, метки, перехватчики, видимость — запросы missiles.* и чтение мира.
public sealed partial class Sim
{
    /// <summary>Что мешает начать сборку боеголовки в шахте (warheadLack): «busy», «credits», «materials»; null — ничего.</summary>
    public string? WarheadLack(uint player, uint silo) =>
        Query<string>(MissilesQueries.WarheadLack, new TargetArgs { Player = player, Target = silo }) is { Length: > 0 } lack ? lack : null;

    /// <summary>Что мешает перехватчику начать сборку ракеты (interceptorLack): «off», «credits», «materials»; null — ничего.</summary>
    public string? InterceptorLack(uint building) =>
        Query<string>(MissilesQueries.InterceptorLack, new EntityArgs { Entity = building }) is { Length: > 0 } lack ? lack : null;

    /// <summary>Сколько тиков до старта ракеты у шахты в пуске (launchLeft); null — пуск не идёт.</summary>
    public int? LaunchLeft(uint silo) => Query<int>(MissilesQueries.LaunchLeft, new EntityArgs { Entity = silo }) is var left and >= 0 ? left : null;

    /// <summary>Ядерные ракеты в полёте по возрастанию номеров.</summary>
    public List<uint> Missiles() => With(Names.Missile.Name);

    /// <summary>Ракеты-перехватчики в полёте.</summary>
    public List<uint> Chasers() => With(Names.Chaser.Name);

    /// <summary>Метка по номеру.</summary>
    public MarkState MarkOf(uint entity)
    {
        var bytes = Has(entity, Names.Sighting.Name) ? Bytes(entity, Names.Sighting.Name + "." + Names.Sighting.Viewers) : [];
        var viewers = new uint[bytes.Length / 4];
        for (var i = 0; i < viewers.Length; i++)
            viewers[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        return new MarkState(entity, Text(entity, "Mark.kind"), Num(entity, "Mark.until"), (uint)Num(entity, "Mark.player"), viewers);
    }

    /// <summary>Метки вида kind («launch», «strike») по возрастанию номеров.</summary>
    public List<MarkState> Marks(string kind) => [.. With(Names.Mark.Name).Select(MarkOf).Where(mark => mark.Kind == kind)];

    /// <summary>Узнаёт ли игрок о сущности (shownTo): так хост решает, что слать клиенту.</summary>
    public bool ShownTo(uint player, uint entity) => new VisionView(Host, player).Entity(entity);

    /// <summary>Видит ли игрок точку сейчас (vision.sees).</summary>
    public bool Sees(uint player, double x, double y) => Host.Vision!.Sees(player, x, y);

    /// <summary>Разведана ли точка игроку (vision.explored).</summary>
    public bool Explored(uint player, double x, double y) => Host.Vision!.Explored(player, x, y);
}
