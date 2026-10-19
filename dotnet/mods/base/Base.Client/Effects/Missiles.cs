using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>
/// Ядерные ракеты и перехватчики (missiles.ts, combatPass.ts). Места ракет хост не шлёт: они летят по прямой с известной
/// скоростью, и клиент ведёт их сам. Своя (и союзная) ракета останавливается в точке удара; у чужой точка удара скрыта
/// (Missile.toX, toY — @private: приходят NaN или нулями, docs/dotnet-n6.md, Н6-8) — она слепая: летит по курсу
/// (courseX, courseY), пока хост её не уберёт. Рисуются тень на земле, корпус на высоте полёта, пламя и густой след;
/// у шахты в пуске — пар из ствола.
/// </summary>
internal sealed class Missiles(Fx fx, Smoke smoke)
{
    /// <summary>Длина корпуса ракеты и перехватчика в тайлах, шаг клубов следа (MISSILE_BODY, CHASER_BODY, MISSILE_SMOKE_STEP).</summary>
    private const double MissileBody = 0.9, ChaserBody = 0.5, SmokeStep = 0.35;

    /// <summary>Пар из ствола шахты в пуске: с какой доли срока и сколько клубов в секунду в конце (STEAM_FROM, STEAM_RATE).</summary>
    private const double SteamFrom = 0.45, SteamRate = 40;

    private static readonly Rgb Flame = new(1, 0.6f, 0.2f), Shell = new(0.12f, 0.12f, 0.14f), Rocket = new(0.85f, 0.87f, 0.9f);
    private static readonly Rgb Ember = new(1, 0.35f, 0.08f), Wreck = new(1, 0.22f, 0.1f);

    /// <summary>Ракета, какой её знает клиент: откуда, куда (у слепой — точка на курсе), когда вылетела.</summary>
    public readonly record struct Flight(double FromX, double FromY, double ToX, double ToY, double Born, bool Blind);

    /// <summary>Где ракета в момент clock (тик с долей), step — длина тика, speed — скорость боеголовки (missileAt).</summary>
    public static (double X, double Y, double Lift, double HeadingX, double HeadingY) At(Flight missile, double clock, double step, double speed)
    {
        var reach = Fx.Hypot(missile.ToX - missile.FromX, missile.ToY - missile.FromY);
        if (reach == 0)
            reach = 1;
        var headingX = (missile.ToX - missile.FromX) / reach;
        var headingY = (missile.ToY - missile.FromY) / reach;
        var age = Math.Max(0, (clock - missile.Born) * step);
        var flown = missile.Blind ? speed * age : Math.Min(reach, speed * age);
        return (missile.FromX + headingX * flown, missile.FromY + headingY * flown,
            MissileRules.Lift(age, missile.Blind ? double.PositiveInfinity : reach - flown, speed), headingX, headingY);
    }

    /// <summary>Ракета-перехватчик в момент clock: со станка круто вверх, к встрече — на высоте ракеты (chaserAt).</summary>
    public static (double X, double Y, double Lift) ChaserAt(double fromX, double fromY, double toX, double toY, double lift, double born, double arrive, double clock)
    {
        var share = Math.Min(1, Math.Max(0, (clock - born) / Math.Max(1, arrive - born)));
        return (fromX + (toX - fromX) * share, fromY + (toY - fromY) * share, lift * (1 - (1 - share) * (1 - share)));
    }

    /// <summary>Скорость боеголовки шахты (warhead.speed).</summary>
    private double Speed => fx.Tables.Buildings.GetValueOrDefault("silo")?.Warhead?.Speed ?? 1;

    /// <summary>
    /// Ракета этого зрителя: слепая — если точки удара он не знает (чужая; NaN или нули вместо неё); тогда курс — из
    /// courseX, courseY.
    /// </summary>
    public Flight FlightOf(ComponentValues missile)
    {
        var m = fx.Missile;
        double fromX = missile.Number(m["fromX"]), fromY = missile.Number(m["fromY"]);
        double toX = missile.Number(m["toX"]), toY = missile.Number(m["toY"]);
        var player = (uint)missile.Number(m["player"]);
        var hidden = double.IsNaN(toX) || double.IsNaN(toY) || (!fx.Friend(player) && toX == 0 && toY == 0);
        var blind = hidden || missile.Flag(m["blind"]);
        if (hidden)
            (toX, toY) = (fromX + missile.Number(m["courseX"]), fromY + missile.Number(m["courseY"]));
        return new Flight(fromX, fromY, toX, toY, missile.Number(m["born"]), blind);
    }

    /// <summary>Огни, след и облако на старте; пар шахт в пуске (emitSmoke).</summary>
    public void Emit(Dictionary<uint, (double X, double Y)> trails, HashSet<uint> alive)
    {
        var c = fx.C;
        var speed = Speed;
        foreach (var entity in c.Each(fx.Missile))
        {
            if (c.Get(entity, fx.Missile) is not { } values)
                continue;
            alive.Add(entity);
            var flight = FlightOf(values);
            var (x, y, lift, _, _) = At(flight, fx.Clock, fx.Step, speed);
            c.Draw.Light(x, y - lift, 18, 8, 0.9f);
            if (!trails.TryGetValue(entity, out var last))
            {
                // На старте — облако над шахтой.
                trails[entity] = (x, y);
                for (var i = 0; i < 24; i++)
                    smoke.Add(flight.FromX + fx.Spread(2.5), flight.FromY + fx.Spread(2.5), 1 + fx.Random(), 3 + fx.Random() * 2, 0.9, 0.7, fx.Spread(2), fx.Spread(2));
                continue;
            }
            // В полёте — густой след на высоте полёта.
            var distance = Fx.Hypot(x - last.X, y - last.Y);
            var count = distance > 0 ? Math.Min(24, (int)Math.Floor(distance / SmokeStep)) : 0;
            for (var i = 1; i <= count; i++)
            {
                var share = i * SmokeStep / distance;
                smoke.Add(last.X + (x - last.X) * share + fx.Spread(0.1), last.Y + (y - last.Y) * share - lift + fx.Spread(0.1), 0.5, 2.5, 0.85, 0.7, fx.Spread(0.3), fx.Spread(0.3));
            }
            if (count > 0)
            {
                var share = count * SmokeStep / distance;
                trails[entity] = (last.X + (x - last.X) * share, last.Y + (y - last.Y) * share);
            }
        }
        foreach (var entity in c.Each(fx.Chaser))
        {
            if (c.Get(entity, fx.Chaser) is not { } chaser)
                continue;
            alive.Add(entity);
            var (fromX, fromY) = (chaser.Number(fx.Chaser["fromX"]), chaser.Number(fx.Chaser["fromY"]));
            var (x, y, lift) = Chase(chaser, fx.Clock);
            c.Draw.Light(x, y - lift, 12, 5, 0.8f);
            if (!trails.TryGetValue(entity, out var last))
            {
                trails[entity] = (x, y - lift);
                for (var i = 0; i < 8; i++)
                    smoke.Add(fromX + fx.Spread(1), fromY + fx.Spread(1), 0.6 + fx.Random() * 0.4, 1.5, 0.9, 0.7, fx.Spread(1.5), fx.Spread(1.5));
                continue;
            }
            // Тонкий белый след от станка до встречи.
            var distance = Fx.Hypot(x - last.X, y - lift - last.Y);
            var count = distance > 0 ? Math.Min(24, (int)Math.Floor(distance / SmokeStep)) : 0;
            for (var i = 1; i <= count; i++)
            {
                var share = i * SmokeStep / distance;
                smoke.Add(last.X + (x - last.X) * share + fx.Spread(0.05), last.Y + (y - lift - last.Y) * share + fx.Spread(0.05), 0.3, 1.5, 0.95, 0.6, fx.Spread(0.2), fx.Spread(0.2));
            }
            if (count > 0)
            {
                var share = count * SmokeStep / distance;
                trails[entity] = (last.X + (x - last.X) * share, last.Y + (y - lift - last.Y) * share);
            }
        }
        Steam();
    }

    private (double X, double Y, double Lift) Chase(ComponentValues chaser, double clock)
    {
        var h = fx.Chaser;
        return ChaserAt(chaser.Number(h["fromX"]), chaser.Number(h["fromY"]), chaser.Number(h["toX"]), chaser.Number(h["toY"]), chaser.Number(h["lift"]),
            chaser.Number(h["born"]), chaser.Number(h["arrive"]), clock);
    }

    /// <summary>Шахта в пуске парит из открытого ствола, и чем ближе старт, тем гуще.</summary>
    private void Steam()
    {
        var c = fx.C;
        if (fx.Tables.Buildings.GetValueOrDefault("silo")?.Warhead is not { } warhead)
            return;
        var launchTicks = Math.Max(1, Math.Round(warhead.Launch / fx.Step));
        foreach (var entity in c.Each(fx.Silo, fx.Building, fx.Position))
        {
            var start = fx.Num(entity, fx.Silo, "start", -1);
            if (start < 0 || fx.BuildingOf(entity) is not { } kind || c.Pose(entity) is not { } at)
                continue;
            var share = (fx.Clock - start) / launchTicks;
            if (share < SteamFrom)
                continue;
            var ramp = (share - SteamFrom) / (1 - SteamFrom);
            var expected = SteamRate * fx.Delta * ramp * ramp;
            var count = (int)Math.Floor(expected) + (fx.Random() < expected % 1 ? 1 : 0);
            var cx = at.X + kind.Spec.Width / 2 + 0.2;
            var cy = at.Y + kind.Spec.Height / 2 - 0.1;
            for (var i = 0; i < count; i++)
                smoke.Add(cx + fx.Spread(0.9), cy + fx.Spread(0.9), 0.5 + fx.Random() * 0.4, 1.6, 0.95, 0.55, fx.Spread(1.2), fx.Spread(1.2) - 0.4);
        }
    }

    /// <summary>Ракеты: тень на земле, корпус на высоте полёта, пламя за ним; перехватчики — тонкие, белые, с пламенем.</summary>
    public void Draw()
    {
        var c = fx.C;
        var speed = Speed;
        foreach (var entity in c.Each(fx.Missile))
        {
            if (c.Get(entity, fx.Missile) is not { } values)
                continue;
            var (x, y, lift, headingX, headingY) = At(FlightOf(values), fx.Clock, fx.Step, speed);
            if (!fx.Visible(x, y))
                continue;
            var tailX = x - headingX * MissileBody;
            var tailY = y - headingY * MissileBody;
            fx.Line(tailX, tailY, x, y, 5, Shell, 0.3);
            fx.Line(tailX - headingX * 0.5, tailY - headingY * 0.5 - lift, tailX, tailY - lift, 6, Flame);
            fx.Dot(tailX, tailY - lift, 9, Ember, 0.8);
            fx.Line(tailX, tailY - lift, x, y - lift, 6, Shell);
            fx.Line(tailX, tailY - lift, x, y - lift, 4, Rocket);
            fx.Dot(x, y - lift, 3, Wreck);
        }
        foreach (var entity in c.Each(fx.Chaser))
        {
            if (c.Get(entity, fx.Chaser) is not { } chaser)
                continue;
            var (x, y, lift) = Chase(chaser, fx.Clock);
            if (!fx.Visible(x, y) && !fx.Visible(x, y - lift))
                continue;
            // Корпус смотрит туда, куда она летит на экране: в начале — круто вверх. Направление — по точке чуть раньше.
            var back = Chase(chaser, fx.Clock - 0.5);
            var screenX = x - back.X;
            var screenY = y - lift - (back.Y - back.Lift);
            var reach = Fx.Hypot(screenX, screenY);
            if (reach == 0)
                reach = 1;
            var dx = screenX / reach * ChaserBody;
            var dy = screenY / reach * ChaserBody;
            var tailX = x - dx;
            var tailY = y - lift - dy;
            fx.Dot(x, y, 3, Shell, 0.25);
            fx.Line(tailX - dx * 0.4, tailY - dy * 0.4, tailX, tailY, 4, Flame);
            fx.Dot(tailX, tailY, 6, Ember, 0.8);
            fx.Line(tailX, tailY, x, y - lift, 3, Rocket);
        }
    }
}
