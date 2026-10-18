using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// moves/wheels: ходовая «колёса» — поворот с ограниченной скоростью и ход туда, куда смотрит; чем сильнее цель в
/// стороне, тем медленнее, а цель за спиной — разворот на месте. Система <c>moves/wheels.drive</c> работает над
/// столбцами всех юнитов с <c>Drive.move == "wheels"</c> разом; едут только те, у кого <c>Drive.moving</c>.
/// </summary>
internal sealed class Wheels : ISystem
{
    private Column _x, _y, _facing, _speed, _turn, _tx, _ty, _moving;

    public void Bind(SystemBinding binding)
    {
        _x = binding.Column("Position", "x");
        _y = binding.Column("Position", "y");
        _facing = binding.Column("Facing", "angle");
        _speed = binding.Column("Drive", "speed");
        _turn = binding.Column("Drive", "turn");
        _tx = binding.Column("Drive", "tx");
        _ty = binding.Column("Drive", "ty");
        _moving = binding.Column("Drive", "moving");
    }

    public void Run(SystemContext ctx)
    {
        var x = ctx.Column(_x);
        var y = ctx.Column(_y);
        var facing = ctx.Column(_facing);
        var speed = ctx.Column(_speed);
        var turn = ctx.Column(_turn);
        var tx = ctx.Column(_tx);
        var ty = ctx.Column(_ty);
        var moving = ctx.Column(_moving);
        var dt = ctx.Dt;
        for (var i = 0; i < ctx.Count; i++)
        {
            if (moving[i] == 0)
                continue;
            double px = x[i], py = y[i];
            double dx = tx[i] - px, dy = ty[i] - py;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var step = speed[i] * dt;
            var face = facing[i];
            if (distance <= step)
            {
                // Доехал: встаёт в точку цели и перестаёт ехать.
                x[i] = px + dx;
                y[i] = py + dy;
                moving[i] = 0;
                continue;
            }
            var limit = turn[i] * dt;
            var off = Geom.Wrap(Math.Atan2(dy, dx) - face);
            if (Math.Abs(off) <= limit)
                face += off;
            else
                face += off > 0 ? limit : -limit;
            facing[i] = Geom.Wrap(face);
            var ahead = Math.Cos(off);
            if (ahead > 0)
            {
                x[i] = px + Math.Cos(face) * step * ahead;
                y[i] = py + Math.Sin(face) * step * ahead;
            }
        }
    }
}
