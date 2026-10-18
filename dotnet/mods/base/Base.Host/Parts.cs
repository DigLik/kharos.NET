using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>Компоненты схемы мира, которыми пользуется мод: движка (Position…) и объявленные в mod.json.</summary>
internal sealed class Parts
{
    public Parts(LoadContext ctx)
    {
        Position = ctx.Component("Position");
        Facing = ctx.Component("Facing");
        Owner = ctx.Component("Owner");
        Proto = ctx.Component("Proto");
        Sight = ctx.Component("Sight");
        Drive = ctx.Component("Drive");
        Attached = ctx.Component("Attached");
        Turret = ctx.Component("Turret");
        Player = ctx.Component("Player");
        Producer = ctx.Component("Producer");
        Unit = ctx.Component("Unit");
        Building = ctx.Component("Building");
        Deposit = ctx.Component("Deposit");
    }

    public ComponentRef Position { get; }
    public ComponentRef Facing { get; }
    public ComponentRef Owner { get; }
    public ComponentRef Proto { get; }
    public ComponentRef Sight { get; }
    public ComponentRef Drive { get; }
    public ComponentRef Attached { get; }
    public ComponentRef Turret { get; }
    public ComponentRef Player { get; }
    public ComponentRef Producer { get; }
    public ComponentRef Unit { get; }
    public ComponentRef Building { get; }
    public ComponentRef Deposit { get; }
}
