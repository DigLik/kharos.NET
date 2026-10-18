using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// mechanics/crowd: теснота — юниты не наезжают друг на друга. После хода (<c>after drive</c>) корпуса
/// <c>Unit.radius</c> расталкивает пакетный инструмент движка (<see cref="Tools.Separate"/>, ctx:separate пилота):
/// стоящий (<c>Drive.moving == 0</c>) уступает идущему втрое охотнее. Сила — data/mechanics.json, crowd.strength.
/// </summary>
internal sealed class Crowd(BaseMod mod) : ISystem
{
    private Column _x, _y, _radius, _moving;

    public void Bind(SystemBinding binding)
    {
        _x = binding.Column("Position", "x");
        _y = binding.Column("Position", "y");
        _radius = binding.Column("Unit", "radius");
        _moving = binding.Column("Drive", "moving");
    }

    public void Run(SystemContext ctx)
    {
        // Одному расталкивать не с кем: вызов инструмента не нужен.
        if (ctx.Count > 1)
            Tools.Separate(ctx.Column(_x), ctx.Column(_y), ctx.Column(_radius), ctx.Column(_moving), mod.D.CrowdStrength);
    }
}
