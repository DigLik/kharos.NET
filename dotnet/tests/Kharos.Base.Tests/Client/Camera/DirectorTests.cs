using Base.Shared;
using Kharos.Base.Scenario;

namespace Kharos.Base.Tests;

/// <summary>Камера-режиссёр витрины меню (Camera/Director.cs) на обвязке Viewer: партия showcase, клиент в режиме меню.</summary>
public sealed class DirectorTests
{
    private static (double X, double Y) Middle(Sim sim)
    {
        double x = 0, y = 0;
        var list = sim.With(Names.Unit.Name).Concat(sim.With(Names.Building.Name)).ToList();
        foreach (var entity in list)
        {
            var at = sim.At(entity);
            x += at.X;
            y += at.Y;
        }
        return (x / list.Count, y / list.Count);
    }

    [Fact(DisplayName = "режиссёр витрины: камера встаёт на действие правее меню и идёт за ним, когда оно уходит")]
    public void DirectorFollowsTheAction()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        using var sim = Sim.Create(new SimOptions { Game = "showcase", Land = new { seed = 3 }, Settings = new { scene = "battle" }, Players = [1] });
        using var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
        viewer.Input = viewer.Input with { Shell = 1 /* ClientAbi.ShellMenu */ };
        viewer.Run(1);
        var camera = viewer.Game.Scene.Camera;
        var middle = Middle(sim);
        // Первый кадр — сразу на месте: середина видимой части (правее меню) в пределах круга режиссёра.
        const double inset = 400 / 2 / 28.0;
        Assert.Equal(28, camera.Zoom);
        Assert.InRange(camera.X + inset, middle.X - 12, middle.X + 12);
        Assert.InRange(camera.Y, middle.Y - 12, middle.Y + 12);

        // Действие уходит: подкрепление слева, за своими (в зоне, что хост шлёт по камере) — камера плавно идёт к новой середине.
        for (var i = 0; i < 60; i++)
            sim.PutUnit("infantry", 1, -18.5 - i % 6, -10.5 + i / 6 * 2);
        var shifted = Middle(sim);
        Assert.True(shifted.X < middle.X - 3, $"середина действия не сдвинулась: {middle.X:0.0} → {shifted.X:0.0}");
        viewer.Run(20);
        var now = Middle(sim);
        Assert.InRange(viewer.Game.Scene.Camera.X + inset, now.X - 12, now.X + 12);
        Assert.InRange(viewer.Game.Scene.Camera.Y, now.Y - 12, now.Y + 12);
    }
}
