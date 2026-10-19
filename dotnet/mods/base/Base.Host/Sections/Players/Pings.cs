using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Сигнал союзникам (pings.ts): метка в точке карты, которую <see cref="Seconds"/> секунд видят сам игрок и его союзники —
/// где бы ни была их камера и что бы ни скрывал туман (метка, а не обзор: Mark с правилом Sighting.listed без списка).
/// Не больше <see cref="Limit"/> сигналов за <see cref="Window"/> секунд. Гасит метки система <see cref="Expire"/>.
/// </summary>
internal static class Pings
{
    /// <summary>Сколько секунд сигнал виден на карте (PING_SECONDS).</summary>
    public const double Seconds = 8;

    /// <summary>Не больше стольких сигналов от игрока за <see cref="Window"/> секунд (PING_LIMIT, PING_WINDOW).</summary>
    public const int Limit = 3;

    public const double Window = 5;

    /// <summary>Вид метки сигнала (Mark.kind).</summary>
    public const string Kind = "ping";

    /// <summary>Ставит метку игрока в точке (x, y), тайлы (ping). Возвращает, поставлена ли.</summary>
    public static bool Ping(Call call, uint player, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue
            || !Placement.InBounds((int)Math.Floor(x), (int)Math.Floor(y)))
            return false;
        var life = Core.Ticks(Seconds);
        var recent = 0;
        foreach (var mark in Core.All(P.Mark))
        {
            if (call.Text(mark, P.Mark, P.MarkKind) != Kind || (uint)call.Num(mark, P.Mark, P.MarkPlayer) != player)
                continue;
            var born = call.Num(mark, P.Mark, P.MarkUntil) - life;
            if ((call.Tick - born) * Core.Step < Window)
                recent++;
        }
        if (recent >= Limit)
            return false;
        var entity = call.Spawn();
        call.Add(entity, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
        call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(entity, P.Mark).Text(P.MarkKind, Kind).Number(P.MarkUntil, call.Tick + (ulong)life).Number(P.MarkPlayer, player);
        call.Add(entity, P.Sighting).Number(P.SightingRule, P.Listed);
        return true;
    }

    /// <summary>Система players/pings: срок вышел — метки сигнала нет (тот же обход, что гасит метки ракет в silos.ts).</summary>
    public sealed class Expire : ISystem
    {
        private Column _until;

        public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
            .Query(Names.Mark.Name).Where(Names.Mark.Name, Names.Mark.Kind, Kind).Write(Names.Mark.Name);

        public void Bind(SystemBinding binding)
        {
            P.Load(binding.Component);
            _until = binding.Column(Names.Mark.Name, Names.Mark.Until);
        }

        public void Run(SystemContext ctx)
        {
            var until = ctx.Column(_until);
            for (var row = 0; row < ctx.Count; row++)
                if (ctx.Tick >= until[row])
                    ctx.Commands.Destroy(ctx.Entities[row]);
        }
    }
}
