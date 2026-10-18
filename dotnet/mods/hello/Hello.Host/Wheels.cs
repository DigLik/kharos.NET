namespace Hello;

/// <summary>Куда система сообщает о доехавших: в моде — событием, в сверке на хосте — счётом.</summary>
public interface IArrivals
{
    void Arrived(int row, uint entity, double x, double y);
}

/// <summary>
/// Ходовая «колёса» (как mods/base/moves/wheels.luau и move_rust из crates/content-bench): поворот с ограниченной
/// скоростью и ход туда, куда смотрит; чем сильнее цель в стороне, тем медленнее, цель за спиной — разворот на месте.
/// Доехавший получает новую цель. Чистый код над столбцами без SDK: тот же файл собирается в мод (wasm) и в
/// тесты и замер на хосте — для сверки.
/// </summary>
public static class Wheels
{
    public const double FullTurn = Math.PI * 2;

    /// <summary>Угол в (−π, π].</summary>
    public static double Wrap(double angle) => angle - FullTurn * Math.Floor(angle / FullTurn + 0.5);

    /// <summary>Новая цель: как retarget у crates/content-bench, от номера сущности и тика.</summary>
    public static (double X, double Y) Retarget(uint entity, ulong tick)
    {
        var seed = (entity * 7919UL + tick * 104729UL) % 1000003UL;
        return (seed % 500 + 0.5, seed / 500 % 500 + 0.5);
    }

    /// <summary>Тик ходовой над n сущностями: x, y, facing, tx, ty меняются на месте.</summary>
    public static void Drive<TArrivals>(
        Span<double> x, Span<double> y, Span<double> facing,
        ReadOnlySpan<double> speed, ReadOnlySpan<double> turn, Span<double> tx, Span<double> ty,
        ReadOnlySpan<uint> entities, ulong tick, double dt, double arrival, ref TArrivals arrivals)
        where TArrivals : struct, IArrivals
    {
        var n = entities.Length;
        x = x[..n];
        y = y[..n];
        facing = facing[..n];
        speed = speed[..n];
        turn = turn[..n];
        tx = tx[..n];
        ty = ty[..n];
        var near = arrival * arrival;
        for (var i = 0; i < n; i++)
        {
            double px = x[i], py = y[i];
            double dx = tx[i] - px, dy = ty[i] - py;
            if (dx * dx + dy * dy < near)
            {
                arrivals.Arrived(i, entities[i], px, py);
                var (gx, gy) = Retarget(entities[i], tick);
                tx[i] = gx;
                ty[i] = gy;
                dx = gx - px;
                dy = gy - py;
            }
            var step = speed[i] * dt;
            var limit = turn[i] * dt;
            var face = facing[i];
            var off = Wrap(Math.Atan2(dy, dx) - face);
            face += Math.Abs(off) <= limit ? off : Math.CopySign(limit, off);
            var ahead = Math.Cos(off);
            if (ahead > 0)
            {
                x[i] = px + Math.Cos(face) * step * ahead;
                y[i] = py + Math.Sin(face) * step * ahead;
            }
            facing[i] = Wrap(face);
        }
    }
}
