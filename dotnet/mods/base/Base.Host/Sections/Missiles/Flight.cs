using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Система launchMissiles (silos.ts): ядерный удар. Раз в тик, до боя: шахты собирают боеголовки и пускают ракеты,
/// перехватчики (<see cref="Interceptors"/>) собирают свои и стреляют, ракеты летят и рвутся — их взрыв (<c>Burst</c>) бьёт в
/// этот же бой, — или гибнут от перехватчика; метки, вспышки и ракеты-перехватчики, чей срок вышел, пропадают.
/// <para>
/// Ракета летит по прямой speed тайлов в секунду; её весь полёт видят все (Sighting everyone), а обычное ПВО её не бьёт.
/// Со старта место пуска на <see cref="MissileRules.LaunchMark"/> секунд отмечено у всех, у кого есть готовый радар. Точку
/// удара весь полёт видят те, чьи здания или юниты в радиусе взрыва: список сверяется раз в <see cref="MissileRules.Watch"/>
/// секунд. Чужим ракета уходит без точки удара, только с курсом (Missile.courseX/Y; toX, toY, hunted, mark — @private).
/// Вспышка взрыва — <c>Reveal</c> для всех на <see cref="MissileRules.Glare"/> секунд. Кадр — шахты; всё остальное —
/// побочные таблицы (перехватчики, ракеты, метки, вспышки, ракеты-перехватчики), значения читаются и пишутся через вызов.
/// </para>
/// </summary>
internal sealed class Flight : ISystem
{
    private const int InterceptorsT = 0, MissilesT = 1, MarksT = 2, RevealsT = 3, ChasersT = 4;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Silo.Name, Names.Building.Name)
        .Table(Names.Interceptor.Name, Names.Building.Name)
        .Table(Names.Missile.Name)
        .Table(Names.Mark.Name)
        .Table(Names.Reveal.Name)
        .Table(Names.Chaser.Name);

    public void Bind(SystemBinding binding) => Refs.Load(binding.Component);

    public void Run(SystemContext ctx)
    {
        var interceptors = ctx.Table(InterceptorsT).Entities.ToArray();
        var missiles = ctx.Table(MissilesT).Entities.ToArray();
        var marks = ctx.Table(MarksT).Entities.ToArray();
        var reveals = ctx.Table(RevealsT).Entities.ToArray();
        var chasers = ctx.Table(ChasersT).Entities.ToArray();
        if (ctx.Count == 0 && interceptors.Length + missiles.Length + marks.Length + reveals.Length + chasers.Length == 0)
            return;
        var call = Call.Of(ctx);
        var r = Refs.R;
        var tick = (double)ctx.Tick;
        var fired = new List<Launch>();
        foreach (var silo in ctx.Entities)
        {
            var ticks = call.Num(silo, S.Silo, r.SiloTicks);
            if (ticks > 0 && !call.Flag(silo, S.Silo, r.SiloReady) && !call.Has(silo, r.Off))
            {
                // Энергии не хватает — сборка идёт медленнее.
                var progress = call.Num(silo, S.Silo, r.SiloProgress) + Books.ShareOf(call, silo);
                if (progress >= ticks)
                    call.Set(silo, S.Silo).Number(r.SiloProgress, 0).Number(r.SiloTicks, 0).Number(r.SiloCost, 0).Flag(r.SiloReady, true);
                else
                    call.Set(silo, S.Silo).Number(r.SiloProgress, progress);
            }
            var start = call.Num(silo, S.Silo, r.SiloStart, -1);
            if (Silos.WarheadOf(call, silo) is not { } warhead || start < 0 || tick - start < Core.Ticks(warhead.Launch))
                continue;
            var size = Factory.Building(call.BuildingType(silo)!)!;
            var (x, y) = call.At(silo);
            fired.Add(new Launch(call.OwnerOf(silo), x + size.Width / 2, y + size.Height / 2, call.Num(silo, S.Silo, r.SiloToX), call.Num(silo, S.Silo, r.SiloToY), silo));
        }
        Interceptors.Run(call, interceptors, missiles);

        // Ракета несёт боеголовку шахты: вид шахты один.
        var warheadSpec = Silos.Warhead;
        var (speed, blast) = (warheadSpec.Speed, warheadSpec.Blast);
        var arrived = new List<uint>();
        var downed = new List<uint>();
        var watch = Core.Ticks(MissileRules.Watch);
        // Игроки, чьё сейчас под ударом ракеты, кроме её хозяина: он точку удара знает и так.
        List<uint> Under(double toX, double toY, uint player) => PlayersNear(call, toX, toY, blast.Radius, player);
        foreach (var missile in missiles)
        {
            if (!call.Alive(missile))
                continue;
            var hunted = call.Num(missile, r.Missile, r.MissileHunted, -1);
            if (hunted >= 0 && tick >= hunted)
            {
                downed.Add(missile);
                continue;
            }
            double x = call.Num(missile, r.Missile, r.MissileX), y = call.Num(missile, r.Missile, r.MissileY);
            double toX = call.Num(missile, r.Missile, r.MissileToX), toY = call.Num(missile, r.Missile, r.MissileToY);
            var reach = DetMath.Hypot(toX - x, toY - y);
            var step = speed * Core.Step;
            if (reach <= step)
            {
                call.Set(missile, r.Missile).Number(r.MissileX, toX).Number(r.MissileY, toY);
                arrived.Add(missile);
                continue;
            }
            call.Set(missile, r.Missile).Number(r.MissileX, x + (toX - x) / reach * step).Number(r.MissileY, y + (toY - y) / reach * step);
            // Под удар за время полёта входят и из-под него уходят.
            if ((tick - call.Num(missile, r.Missile, r.MissileBorn)) % watch != 0)
                continue;
            var mark = (uint)call.Num(missile, r.Missile, r.MissileMark);
            if (call.Alive(mark) && call.Has(mark, r.Mark))
                SetViewers(call, mark, Under(toX, toY, (uint)call.Num(missile, r.Missile, r.MissilePlayer)));
        }
        // Сбитая гибнет в воздухе: вспышка без урона, метка точки удара пропадает.
        foreach (var missile in downed)
        {
            DestroyMark(call, missile);
            double x = call.Num(missile, r.Missile, r.MissileX), y = call.Num(missile, r.Missile, r.MissileY);
            var lift = MissileRules.Lift((tick - call.Num(missile, r.Missile, r.MissileBorn)) * Core.Step,
                DetMath.Hypot(call.Num(missile, r.Missile, r.MissileToX) - x, call.Num(missile, r.Missile, r.MissileToY) - y), speed);
            var spark = call.Spawn();
            call.Add(spark, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
            call.Add(spark, r.Blast).Number(r.BlastSize, MissileRules.Airburst).Number(r.BlastLift, lift)
                .Number(r.BlastLife, Core.Ticks(0.25 + MissileRules.Airburst * 0.2));
            call.Destroy(missile);
        }
        foreach (var missile in arrived)
        {
            DestroyMark(call, missile);
            double toX = call.Num(missile, r.Missile, r.MissileToX), toY = call.Num(missile, r.Missile, r.MissileToY);
            var burst = call.Spawn();
            call.Add(burst, S.Position).Number(S.PositionX, toX).Number(S.PositionY, toY);
            call.Add(burst, r.Burst).Number(r.BurstRadius, blast.Radius).Number(r.BurstDamage, blast.Damage).Number(r.BurstCore, blast.Core);
            // Вспышка взрыва открывает его круг всем игрокам.
            var glare = call.Spawn();
            call.Add(glare, S.Position).Number(S.PositionX, toX).Number(S.PositionY, toY);
            call.Add(glare, r.Reveal).Number(r.RevealRadius, blast.Radius).Number(r.RevealUntil, tick + Core.Ticks(MissileRules.Glare)).Flag(r.RevealAll, true);
            call.Add(glare, r.Sighting).Number(r.SightingRule, Refs.Everyone);
            call.Destroy(missile);
        }

        // Стартовавшая ракета трогается со следующего тика: в тик born она над шахтой, так её ведёт и клиент.
        List<uint>? radars = null;
        foreach (var launch in fired)
        {
            call.Set(launch.Silo, S.Silo).Flag(r.SiloReady, false).Number(r.SiloStart, -1);
            var reach = DetMath.Hypot(launch.ToX - launch.X, launch.ToY - launch.Y);
            // Точка удара отмечена весь полёт; метку убирает сама ракета, срок — с запасом в тик.
            var left = Math.Ceiling(reach / (speed * Core.Step));
            var strike = call.Spawn();
            call.Add(strike, S.Position).Number(S.PositionX, launch.ToX).Number(S.PositionY, launch.ToY);
            call.Add(strike, r.Mark).Text(r.MarkKind, "strike").Number(r.MarkUntil, tick + left + 1).Number(r.MarkPlayer, launch.Player);
            call.Add(strike, S.Owner).Number(S.OwnerPlayer, launch.Player);
            call.Add(strike, r.Sighting).Number(r.SightingRule, Refs.Listed).Bytes(r.SightingViewers, Pack(Under(launch.ToX, launch.ToY, launch.Player)));
            var missile = call.Spawn();
            call.Add(missile, r.Missile).Number(r.MissileFromX, launch.X).Number(r.MissileFromY, launch.Y).Number(r.MissileToX, launch.ToX).Number(r.MissileToY, launch.ToY)
                .Number(r.MissileBorn, tick).Number(r.MissileX, launch.X).Number(r.MissileY, launch.Y).Number(r.MissilePlayer, launch.Player)
                .Number(r.MissileMark, strike).Number(r.MissileHunted, -1)
                .Number(r.MissileCourseX, reach == 0 ? 0 : (launch.ToX - launch.X) / reach).Number(r.MissileCourseY, reach == 0 ? 0 : (launch.ToY - launch.Y) / reach);
            call.Add(missile, S.Owner).Number(S.OwnerPlayer, launch.Player);
            call.Add(missile, r.Sighting).Number(r.SightingRule, Refs.Everyone);
            // Место пуска видно тем, у кого есть готовый радар.
            radars ??= RadarOwners(call);
            var place = call.Spawn();
            call.Add(place, S.Position).Number(S.PositionX, launch.X).Number(S.PositionY, launch.Y);
            call.Add(place, r.Mark).Text(r.MarkKind, "launch").Number(r.MarkUntil, tick + Core.Ticks(MissileRules.LaunchMark)).Number(r.MarkPlayer, launch.Player);
            call.Add(place, S.Owner).Number(S.OwnerPlayer, launch.Player);
            call.Add(place, r.Sighting).Number(r.SightingRule, Refs.Listed).Bytes(r.SightingViewers, Pack(radars.Where(viewer => viewer != launch.Player)));
        }

        foreach (var mark in marks)
            if (call.Alive(mark) && tick >= call.Num(mark, r.Mark, r.MarkUntil))
                call.Destroy(mark);
        foreach (var flash in reveals)
            if (call.Alive(flash) && tick >= call.Num(flash, r.Reveal, r.RevealUntil))
                call.Destroy(flash);
        foreach (var chaser in chasers)
            if (call.Alive(chaser) && tick >= call.Num(chaser, r.Chaser, r.ChaserArrive))
                call.Destroy(chaser);
    }

    private sealed record Launch(uint Player, double X, double Y, double ToX, double ToY, uint Silo);

    /// <summary>Метка точки удара ракеты пропадает вместе с ней.</summary>
    private static void DestroyMark(Call call, uint missile)
    {
        var mark = (uint)call.Num(missile, Refs.R.Missile, Refs.R.MissileMark);
        if (call.Alive(mark) && call.Has(mark, Refs.R.Mark))
            call.Destroy(mark);
    }

    private static byte[] Pack(IEnumerable<uint> players)
    {
        var list = players.ToList();
        var bytes = new byte[list.Count * 4];
        for (var i = 0; i < list.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), list[i]);
        return bytes;
    }

    /// <summary>Пишет список зрителей метки, если он изменился.</summary>
    private static void SetViewers(Call call, uint mark, List<uint> viewers)
    {
        var bytes = Pack(viewers);
        if (call.Bytes(mark, Refs.R.Sighting, Refs.R.SightingViewers) is { } now && now.AsSpan().SequenceEqual(bytes))
            return;
        call.Set(mark, Refs.R.Sighting).Bytes(Refs.R.SightingViewers, bytes);
    }

    /// <summary>
    /// Игроки, чьи здания или юниты не дальше radius от точки (x, y) (playersNear): им видна точка удара. except — хозяин
    /// ракеты. Метки и вспышки с хозяином — не в счёт; по возрастанию номеров.
    /// </summary>
    private static List<uint> PlayersNear(Call call, double x, double y, double radius, uint except)
    {
        // Здание считается по прямоугольнику, а Position — его левый верхний угол: ищем с запасом на самое большое.
        var margin = Core.Tables.Buildings.Values.Max(spec => Math.Max(spec.Width, spec.Height));
        var found = Tools.Near(x, y, radius + margin, S.Owner, S.Position);
        var players = new SortedSet<uint>();
        if (found.Length == 0)
            return [];
        var values = new double[found.Length * 5];
        var has = new bool[found.Length * 5];
        Tools.Columns(found, [(S.Owner, S.OwnerPlayer), (S.Position, S.PositionX), (S.Position, S.PositionY), (S.Footprint, S.FootprintW), (S.Footprint, S.FootprintH)], values, has);
        var marked = new bool[found.Length];
        var flashes = new bool[found.Length];
        Tools.Has(found, Refs.R.Mark, marked);
        Tools.Has(found, Refs.R.Reveal, flashes);
        var n = found.Length;
        for (var i = 0; i < n; i++)
        {
            var player = (uint)values[i];
            if (player == 0 || player == except || marked[i] || flashes[i] || players.Contains(player))
                continue;
            var (width, height) = has[3 * n + i] ? (values[3 * n + i], values[4 * n + i]) : (0, 0);
            if (BuildRules.RectDistance(values[n + i], values[2 * n + i], width, height, x, y) <= radius)
                players.Add(player);
        }
        return [.. players];
    }

    /// <summary>Игроки, у которых есть готовый радар (radarOwners): им видно место пуска. Нейтральных нет.</summary>
    private static List<uint> RadarOwners(Call call)
    {
        var buildings = Core.All(S.Building, S.Owner);
        var players = new SortedSet<uint>();
        if (buildings.Length == 0)
            return [];
        var n = buildings.Length;
        var values = new double[n * 2];
        Tools.Columns(buildings, [(S.Owner, S.OwnerPlayer), (S.Proto, S.ProtoId)], values);
        var sites = new bool[n];
        Tools.Has(buildings, S.Site, sites);
        for (var i = 0; i < n; i++)
        {
            var player = (uint)values[i];
            if (player != 0 && !sites[i] && !players.Contains(player) && Core.ProtoName((int)values[n + i], "buildings") is { } type && Factory.Building(type)?.Scan > 0)
                players.Add(player);
        }
        return [.. players];
    }
}
