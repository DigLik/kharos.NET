using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Камера-режиссёр витрины меню — перенос direct из game/game.ts: плавно идёт к середине всех юнитов и зданий, медленно
/// кружа вокруг неё, — так в кадре бой или работающая база, даже когда всё стоит. Управления нет: ни клавиш, ни колеса.
/// На широком экране меню стоит слева, и действие держится правее него. Первый кадр — сразу на месте, дальше — вдогонку.
/// </summary>
internal sealed class Director(BaseState state)
{
    /// <summary>Масштаб, пикселей на тайл; как быстро камера догоняет действие (в секунду) и как медленно кружит (оборотов в секунду); радиус круга.</summary>
    public const double Zoom = 28, Follow = 0.4, Orbit = 0.05, Radius = 6;

    /// <summary>С какой ширины экрана меню стоит слева от действия и сколько пикселей оно занимает (не больше 40 % экрана).</summary>
    public const double Wide = 760, MenuWidth = 400;

    private double _angle = Random.Shared.NextDouble() * Math.PI * 2;
    private (double X, double Y)? _focus;

    /// <summary>Середина действия (сумма позиций юнитов и зданий); нет ничего — null.</summary>
    private (double X, double Y)? Middle(ClientContext c)
    {
        double x = 0, y = 0;
        var count = 0;
        foreach (var entity in c.Each(state.P.Unit))
            if (c.Pose(entity) is { } at)
            {
                x += at.X;
                y += at.Y;
                count++;
            }
        foreach (var entity in c.Each(state.P.Building))
            if (c.Pose(entity) is { } at)
            {
                x += at.X;
                y += at.Y;
                count++;
            }
        return count > 0 ? (x / count, y / count) : null;
    }

    public void Update(ClientContext c)
    {
        if (Middle(c) is not { } middle)
            return;
        var camera = c.Camera;
        var inset = c.ScreenWidth >= Wide ? Math.Min(c.ScreenWidth * 0.4, MenuWidth) : 0;
        _angle += c.Dt * Orbit * Math.PI * 2;
        var target = (X: middle.X + Math.Cos(_angle) * Radius, Y: middle.Y + Math.Sin(_angle) * Radius);
        var focus = _focus ?? target;
        var follow = _focus is null ? 1 : 1 - Math.Exp(-c.Dt * Follow);
        focus = (focus.X + (target.X - focus.X) * follow, focus.Y + (target.Y - focus.Y) * follow);
        _focus = focus;
        camera.Zoom = Zoom;
        // Видимая часть экрана — правее меню: её середина на inset / 2 правее середины экрана.
        camera.X = focus.X - inset / 2 / Zoom;
        camera.Y = focus.Y;
    }
}
