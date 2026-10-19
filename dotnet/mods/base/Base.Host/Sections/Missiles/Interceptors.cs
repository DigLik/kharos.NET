using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Research;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Перехватчики (intercept в silos.ts): включённый, у которого ракет меньше ёмкости, начинает сборку следующей, как только
/// хватает кредитов и готового в сети, — цена списывается сразу; сборка идёт долей энергии. С ракетами он стреляет по
/// чужой ядерной, которая вошла в его круг и метит в него же, — по ракете на каждую; из нескольких — ближайший к ней.
/// Решено сразу, а ракета-перехватчик (Chaser) летит к точке встречи и успевает раньше удара всегда.
/// </summary>
internal static class Interceptors
{
    /// <summary>Перехватчик этого вида (interceptOf); у других зданий — null.</summary>
    public static InterceptSpec? SpecOf(Call call, uint building) => call.BuildingType(building) is { } type ? Factory.Building(type)?.Intercept : null;

    /// <summary>Что мешает перехватчику начать сборку следующей ракеты (interceptorLack): «off», «credits», «materials»; null — ничего, он уже собирает или полон.</summary>
    public static string? Lack(Call call, uint entity)
    {
        var player = call.OwnerOf(entity);
        if (SpecOf(call, entity) is not { } spec || !call.Has(entity, S.Interceptor) || call.Num(entity, S.Interceptor, Refs.R.InterceptorTicks) > 0
            || call.Num(entity, S.Interceptor, Refs.R.InterceptorMissiles) >= spec.Capacity)
            return null;
        if (call.Has(entity, Refs.R.Off))
            return "off";
        if (Accounts.Credits(call, player) < spec.Cost)
            return "credits";
        return new Networks(call).Has(entity, Silos.Items(spec.Materials)) ? null : "materials";
    }

    /// <summary>Перехватчик, готовый стрелять: хозяин, центр, круг, скорость ракеты.</summary>
    private sealed record Guard(uint Entity, uint Player, double X, double Y, double Radius, double Speed);

    /// <summary>
    /// Через сколько тиков ракета-перехватчик со скоростью speed из (x, y) встретит ядерную ракету, которая сейчас в
    /// (missileX, missileY) и летит в (toX, toY) (meetTicks): не успевает по скорости — встреча в последний тик до удара:
    /// перехват уже решён, скорость только для картинки. Ноль — сбить сразу.
    /// </summary>
    private static int MeetTicks(double x, double y, double missileX, double missileY, double toX, double toY, double speed)
    {
        var flight = Silos.Warhead.Speed * Core.Step;
        var reach = DetMath.Hypot(toX - missileX, toY - missileY);
        var last = (int)Math.Ceiling(reach / flight) - 1;
        for (var k = 1; k < last; k++)
        {
            var flown = Math.Min(reach, flight * k) / (reach == 0 ? 1 : reach);
            var meetX = missileX + (toX - missileX) * flown;
            var meetY = missileY + (toY - missileY) * flown;
            if (DetMath.Hypot(meetX - x, meetY - y) <= speed * Core.Step * k)
                return k;
        }
        return Math.Max(0, last);
    }

    /// <summary>Собирает ракеты перехватчиков и сбивает ядерные, что вошли в круг (intercept): interceptors — сущности Interceptor, missiles — Missile.</summary>
    public static void Run(Call call, IReadOnlyList<uint> interceptors, IReadOnlyList<uint> missiles)
    {
        var r = Refs.R;
        var tick = (double)call.Tick;
        var armed = new List<Guard>();
        foreach (var entity in interceptors)
        {
            if (SpecOf(call, entity) is not { } spec || call.Has(entity, S.Site) || call.Has(entity, r.Off))
                continue;
            var player = call.OwnerOf(entity);
            var ticks = call.Num(entity, S.Interceptor, r.InterceptorTicks);
            if (ticks > 0)
            {
                var progress = call.Num(entity, S.Interceptor, r.InterceptorProgress) + Books.ShareOf(call, entity);
                if (progress >= ticks)
                    call.Set(entity, S.Interceptor).Number(r.InterceptorProgress, 0).Number(r.InterceptorTicks, 0)
                        .Number(r.InterceptorMissiles, call.Num(entity, S.Interceptor, r.InterceptorMissiles) + 1);
                else
                    call.Set(entity, S.Interceptor).Number(r.InterceptorProgress, progress);
            }
            else if (Lack(call, entity) is null && call.Num(entity, S.Interceptor, r.InterceptorMissiles) < spec.Capacity && Accounts.Pay(call, player, spec.Cost))
            {
                new Networks(call).Take(entity, Silos.Items(spec.Materials));
                call.Set(entity, S.Interceptor).Number(r.InterceptorProgress, 0).Number(r.InterceptorTicks, Core.Ticks(spec.Seconds));
            }
            if (call.Num(entity, S.Interceptor, r.InterceptorMissiles) > 0)
            {
                var (x, y) = call.At(entity);
                var size = Factory.Building(call.BuildingType(entity)!)!;
                armed.Add(new Guard(entity, player, x + size.Width / 2, y + size.Height / 2, spec.Radius, spec.Speed));
            }
        }
        if (armed.Count == 0)
            return;
        var warhead = Silos.Warhead;
        foreach (var missile in missiles)
        {
            if (!call.Alive(missile) || call.Num(missile, r.Missile, r.MissileHunted, -1) >= 0)
                continue;
            var player = (uint)call.Num(missile, r.Missile, r.MissilePlayer);
            double x = call.Num(missile, r.Missile, r.MissileX), y = call.Num(missile, r.Missile, r.MissileY);
            double toX = call.Num(missile, r.Missile, r.MissileToX), toY = call.Num(missile, r.Missile, r.MissileToY);
            Guard? best = null;
            var bestReach = double.PositiveInfinity;
            foreach (var guard in armed)
            {
                if (guard.Player == player || Attack.Allied(call, guard.Player, player))
                    continue;
                if (call.Num(guard.Entity, S.Interceptor, r.InterceptorMissiles) <= 0)
                    continue;
                var reach = DetMath.Hypot(x - guard.X, y - guard.Y);
                if (reach > guard.Radius || reach >= bestReach || DetMath.Hypot(toX - guard.X, toY - guard.Y) > guard.Radius)
                    continue;
                best = guard;
                bestReach = reach;
            }
            if (best is null)
                continue;
            call.Set(best.Entity, S.Interceptor).Number(r.InterceptorMissiles, call.Num(best.Entity, S.Interceptor, r.InterceptorMissiles) - 1);
            var k = MeetTicks(best.X, best.Y, x, y, toX, toY, best.Speed);
            call.Set(missile, r.Missile).Number(r.MissileHunted, tick + k);
            if (k == 0)
                continue;
            var left = DetMath.Hypot(toX - x, toY - y);
            var distance = left == 0 ? 1 : left;
            var ahead = Math.Min(distance, warhead.Speed * Core.Step * k);
            var meetX = x + (toX - x) * ahead / distance;
            var meetY = y + (toY - y) * ahead / distance;
            var born = call.Num(missile, r.Missile, r.MissileBorn);
            var chaser = call.Spawn();
            call.Add(chaser, r.Chaser).Number(r.ChaserFromX, best.X).Number(r.ChaserFromY, best.Y).Number(r.ChaserToX, meetX).Number(r.ChaserToY, meetY)
                .Number(r.ChaserLift, MissileRules.Lift((tick + k - born) * Core.Step, distance - ahead, warhead.Speed)).Number(r.ChaserBorn, tick)
                .Number(r.ChaserArrive, tick + k).Number(r.ChaserPlayer, best.Player).Number(r.ChaserMissile, missile);
            call.Add(chaser, S.Owner).Number(S.OwnerPlayer, best.Player);
            call.Add(chaser, r.Sighting).Number(r.SightingRule, Refs.Points).Number(r.SightingAx, best.X).Number(r.SightingAy, best.Y)
                .Number(r.SightingBx, meetX).Number(r.SightingBy, meetY).Number(r.SightingCx, (best.X + meetX) / 2).Number(r.SightingCy, (best.Y + meetY) / 2);
        }
    }
}
