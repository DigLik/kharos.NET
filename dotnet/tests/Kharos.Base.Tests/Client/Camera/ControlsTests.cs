using System.Reflection;
using System.Runtime.Loader;
using Base.Shared.Orders;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Mod.Sdk.Client;
using Kharos.Plugins.Game;

namespace Kharos.Base.Tests;

/// <summary>
/// Камера и управление base на обвязке Viewer (клиент в режиме CLR на реплике хоста сценария): старт на отряде игрока,
/// рамка, двойной щелчок, группы, полёт камеры к группе, постановка здания и покрытия, точка сбора. Режимы наведения включают
/// панели (Modes); пока их нет у зрителя, режим включает проба в общую память мода.
/// </summary>
public sealed class ControlsTests
{
    private static void SkipUnlessViewer() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    /// <summary>Общая память мода (c.Shared) у зрителя в режиме CLR: клиентский SDK живёт в своей контексте загрузки.</summary>
    private static Dictionary<string, object?> SharedOf(Viewer viewer)
    {
        const BindingFlags all = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var module = Assert.Single(viewer.Game.Mods);
        var instance = typeof(ClientModule).GetField("_instance", all)!.GetValue(module)!;
        var context = (AssemblyLoadContext)instance.GetType().GetField("_context", all)!.GetValue(instance)!;
        var sdk = context.LoadFromAssemblyName(new System.Reflection.AssemblyName("Kharos.Mod.Sdk"));
        var client = sdk.GetType("Kharos.Mod.Sdk.Runtime.ClientRuntime")!.GetField("_client", all)!.GetValue(null)!;
        return (Dictionary<string, object?>)client.GetType().GetProperty(nameof(ClientContext.Shared))!.GetValue(client)!;
    }

    /// <summary>Схватка на сценарии: хост с модом base (партия skirmish, как в TS) и зритель игрока 1.</summary>
    private static (Sim Sim, Viewer Viewer) Skirmish()
    {
        SkipUnlessViewer();
        var sim = Sim.Create(new SimOptions { Game = "skirmish", Players = [1] });
        return (sim, sim.Viewer(1));
    }

    /// <summary>Маленькое поле: главное здание игрока, фабрика, строитель и пустая земля (камера на 9, 9).</summary>
    private static (Sim Sim, Viewer Viewer, uint Builder) Field()
    {
        SkipUnlessViewer();
        var sim = Sim.Create(new SimOptions { Game = "frames", Size = 64, Fog = false, Land = new { blank = true }, Mode = Kharos.Plugins.Game.ModMode.Clr });
        sim.SetCredits(1, 5000);
        ViewerScenes.Place(sim, "command", 4, 4, 1);
        ViewerScenes.Place(sim, "factory", 10, 4, 1);
        // Земля пустого поля — песок: здания встают на фундаменте (x 6–14, y 7–13).
        for (var x = 6; x <= 14; x++)
            for (var y = 7; y <= 13; y++)
                sim.Lay("foundation", x, y);
        var builder = sim.PutUnit("builder", 1, 9.5, 10.5);
        // Зритель — тоже CLR: SharedOf достаёт c.Shared отражением из клиентской сборки (с KHAROS_MODS=wasm иначе — client.wasm).
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720, Mode = Kharos.Plugins.Game.ModMode.Clr });
        viewer.Camera = new CameraState(9, 10, 32);
        viewer.Atmosphere = new AtmosphereState(1, 1.5f, 0.4f, 0);
        viewer.Run(0.5);
        return (sim, viewer, builder);
    }

    private static List<uint> UnitsOf(Viewer viewer, string path)
    {
        var proto = viewer.Game.Content.FindProto(path)!.Id;
        var world = viewer.World;
        var list = new List<uint>();
        world.Query([world.Schema.Id("Proto"), world.Schema.Id("Owner")], list);
        list.Sort();
        return [.. list.Where(entity => (int)world.Number(entity, world.Schema.Id("Proto"), 0) == proto && world.Number(entity, Kharos.Engine.World.Builtin.Owner, 0) == viewer.Player)];
    }

    private static (double X, double Y) At(Viewer viewer, uint entity) =>
        (viewer.World.Number(entity, Kharos.Engine.World.Builtin.Position, 0), viewer.World.Number(entity, Kharos.Engine.World.Builtin.Position, 1));

    /// <summary>Сколько юнитов выделено: кольцо у каждого — один круг.</summary>
    private static int Selected(Viewer viewer) => viewer.Draws.Circles.Count;

    /// <summary>Рамка выделения вокруг точек мира (тайлы).</summary>
    private static void BoxAround(Viewer viewer, double x1, double y1, double x2, double y2)
    {
        var view = viewer.Game.Frame.View;
        viewer.Drag(view.ToScreen((float)x1, (float)y1), view.ToScreen((float)x2, (float)y2));
    }

    [Fact(DisplayName = "камера в начале партии стоит на отряде игрока, а не в углу карты")]
    public void CameraStartsOnThePlayersSquad()
    {
        var (sim, viewer) = Skirmish();
        using var _ = sim;
        viewer.Run(1);
        var mcv = Assert.Single(UnitsOf(viewer, "units/mcv"));
        var (x, y) = At(viewer, mcv);
        var view = viewer.Last.View;
        // Отряд стоит кучкой вокруг MCV; камера смотрит чуть выше его: нижняя панель закрывает низ экрана.
        Assert.True(Math.Abs(view.X - x) < 8 && Math.Abs(view.Y - y) < 8, $"камера ({view.X}, {view.Y}), MCV ({x}, {y})");
        Assert.True(viewer.Last.SpriteLayers.Sum(layer => layer.Count) > 0, "отряд не на экране");
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "рамка выделяет свои юниты, двойной щелчок — все того же вида на экране, пустой щелчок снимает выбор")]
    public void BoxDoubleClickAndEmptyClick()
    {
        var (sim, viewer) = Skirmish();
        using var _ = sim;
        viewer.Run(1);
        var buggies = UnitsOf(viewer, "units/buggy");
        Assert.Equal(2, buggies.Count);
        // Рамка вокруг всего отряда.
        var spots = UnitsOf(viewer, "units/mcv").Concat(buggies).Concat(UnitsOf(viewer, "units/infantry")).Select(entity => At(viewer, entity)).ToList();
        BoxAround(viewer, spots.Min(p => p.X) - 1, spots.Min(p => p.Y) - 1, spots.Max(p => p.X) + 1, spots.Max(p => p.Y) + 1);
        Assert.Equal(6, Selected(viewer));
        // Щелчок по пустой земле снимает выбор.
        viewer.ClickWorld(spots.Max(p => p.X) + 6, spots.Max(p => p.Y) + 6);
        viewer.Step(2);
        Assert.Equal(0, Selected(viewer));
        // Двойной щелчок по багги — обе багги.
        var (bx, by) = At(viewer, buggies[0]);
        viewer.ClickWorld(bx, by);
        viewer.Step(2);
        Assert.Equal(1, Selected(viewer));
        viewer.ClickWorld(bx, by);
        viewer.ClickWorld(bx, by);
        viewer.Step(2);
        Assert.Equal(2, Selected(viewer));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "группы: Ctrl+цифра запоминает, цифра выбирает, двойное нажатие наводит камеру")]
    public void ControlGroupsRememberSelectAndFlyTheCamera()
    {
        var (sim, viewer) = Skirmish();
        using var _ = sim;
        viewer.Run(1);
        var buggies = UnitsOf(viewer, "units/buggy");
        var (bx, by) = At(viewer, buggies[0]);
        viewer.ClickWorld(bx, by);
        viewer.ClickWorld(bx, by);
        viewer.ClickWorld(bx, by);
        viewer.Step(2);
        Assert.Equal(2, Selected(viewer));
        viewer.Frame(viewer.Input with { Ctrl = true, Hits = new[] { "1" } });
        viewer.Step(2);
        viewer.ClickWorld(bx + 8, by + 8);
        viewer.Step(2);
        Assert.Equal(0, Selected(viewer));
        // Цифра выбирает группу, и над юнитами видны плашки с цифрой.
        viewer.Frame(viewer.Input with { Hits = new[] { "1" } });
        viewer.Step(2);
        Assert.Equal(2, Selected(viewer));
        Assert.True(viewer.Draws.Rects.Count > 0, "нет плашек с цифрой группы");
        // Камеру уводим в сторону клавишей, потом двойное нажатие цифры возвращает её к группе.
        viewer.Run(1.5, viewer.Input with { Keys = new[] { "d" } });
        Assert.True(viewer.Last.View.X > bx + 5, $"камера не уехала: {viewer.Last.View.X}");
        viewer.Frame(viewer.Input with { Hits = new[] { "1" } });
        viewer.Step(5);
        viewer.Frame(viewer.Input with { Hits = new[] { "1" } });
        viewer.Run(3);
        var middle = buggies.Select(entity => At(viewer, entity)).ToList();
        Assert.True(Math.Abs(viewer.Last.View.X - middle.Average(p => p.X)) < 1 && Math.Abs(viewer.Last.View.Y - middle.Average(p => p.Y)) < 1,
            $"камера ({viewer.Last.View.X}, {viewer.Last.View.Y}), группа ({middle.Average(p => p.X)}, {middle.Average(p => p.Y)})");
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "камера: клавиши разгоняют, колесо приближает вокруг курсора")]
    public void CameraKeysAndWheelInTheGame()
    {
        var (sim, viewer) = Skirmish();
        using var _ = sim;
        viewer.Run(1);
        var start = viewer.Last.View;
        viewer.Run(0.5, viewer.Input with { Keys = new[] { "d" } });
        var moved = viewer.Last.View;
        Assert.True(moved.X > start.X + 2, $"камера не поехала: {start.X} → {moved.X}");
        viewer.Run(2);
        var stopped = viewer.Last.View;
        viewer.Run(1);
        Assert.Equal(stopped.X, viewer.Last.View.X);
        // Колесо: масштаб растёт плавно, точка под курсором остаётся на месте.
        var pointer = (X: 480f, Y: 120f);
        var before = viewer.Last.View.ToWorld(pointer.X, pointer.Y);
        viewer.Frame(viewer.Input with { Pointer = pointer, Wheel = 3 });
        Assert.True(viewer.Last.View.Zoom > moved.Zoom);
        viewer.Run(1, viewer.Input with { Pointer = pointer });
        Assert.True(viewer.Last.View.Zoom > moved.Zoom * 1.4);
        var after = viewer.Last.View.ToWorld(pointer.X, pointer.Y);
        Assert.Equal(before.X, after.X, 1);
        Assert.Equal(before.Y, after.Y, 1);
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "постановка: призрак зелёный там, где можно, красный там, где нельзя; щелчок закладывает, правая кнопка отменяет")]
    public void GhostIsGreenWhereAllowedAndRedWhereNot()
    {
        var (sim, viewer, builder) = Field();
        using var _ = sim;
        var (bx, by) = At(viewer, builder);
        viewer.ClickWorld(bx, by);
        viewer.Step(2);
        Assert.Equal(1, Selected(viewer));
        var shared = SharedOf(viewer);
        shared["mode.placing"] = "generator";
        var green = Draw.Rgba(0x59ff73, 0.85f);
        var red = Draw.Rgba(0xff4d40, 0.85f);
        // В зоне главного здания, на свободной скале.
        var view = viewer.Game.Frame.View;
        viewer.Input = viewer.Input with { Pointer = view.ToScreen(8.5f, 8.5f) };
        viewer.Step(3);
        Assert.Contains(viewer.Draws.Rects, rect => rect.Color == green);
        Assert.DoesNotContain(viewer.Draws.Rects, rect => rect.Color == red);
        Assert.Contains(viewer.Draws.Sprites, sprite => sprite.Alpha > 0.6f && sprite.Alpha < 0.8f);
        // Щелчок закладывает.
        viewer.Sent.Clear();
        viewer.ClickWorld(8.5, 8.5);
        viewer.Step(2);
        var build = Assert.Single(viewer.Sent.Of<BuildOrder>());
        Assert.Equal(("generator", 7, 7), (build.Building, build.X, build.Y));
        Assert.Equal(new[] { builder }, build.Builders);
        // Режим остаётся: можно сразу следующее. На главном здании нельзя — красный, и приказа нет.
        Assert.Equal("generator", shared["mode.placing"]);
        viewer.Sent.Clear();
        viewer.Input = viewer.Input with { Pointer = view.ToScreen(5.5f, 5.5f) };
        viewer.Step(3);
        Assert.Contains(viewer.Draws.Rects, rect => rect.Color == red);
        viewer.ClickWorld(5.5, 5.5);
        viewer.Step(2);
        Assert.Empty(viewer.Sent.Of<BuildOrder>());
        // Правая кнопка отменяет режим, и приказа точкой нет.
        viewer.ClickWorld(12.5, 12.5, Kharos.Client.Ui.PointerButtons.Right);
        viewer.Step(2);
        Assert.False(shared.ContainsKey("mode.placing"));
        Assert.Empty(viewer.Sent.Of<PointOrder>());
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "покрытие тянется от тайла к тайлу и уходит одним приказом")]
    public void PavingStrokeIsSentAsOneOrder()
    {
        var (sim, viewer, builder) = Field();
        using var _ = sim;
        var (bx, by) = At(viewer, builder);
        viewer.ClickWorld(bx, by);
        viewer.Step(2);
        SharedOf(viewer)["mode.paving"] = "road";
        viewer.Sent.Clear();
        var view = viewer.Game.Frame.View;
        viewer.Drag(view.ToScreen(7.5f, 8.5f), view.ToScreen(10.5f, 8.5f));
        viewer.Step(2);
        var pave = Assert.Single(viewer.Sent.Of<PaveOrder>());
        Assert.Equal("road", pave.Kind);
        Assert.Equal(new[] { 7, 8, 8, 8, 9, 8, 10, 8 }, pave.Tiles);
        Assert.Equal(new[] { builder }, pave.Builders);
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "выбранное производящее здание получает точку сбора правой кнопкой")]
    public void RallyPointForTheSelectedFactory()
    {
        SkipUnlessViewer();
        using var sim = Sim.Create(new SimOptions { Game = "skirmish", Players = [1], Land = new { blank = true }, Fog = false });
        var viewer = sim.Viewer(1);
        viewer.Run(1);
        var (mx, my) = At(viewer, Assert.Single(UnitsOf(viewer, "units/mcv")));
        int fx = (int)Math.Round(mx) + 7, fy = (int)Math.Round(my) - 9;
        var factory = ViewerScenes.Place(sim, "factory", fx, fy, 1);
        viewer.Run(0.5);
        // Фабрика 3×3 с левым верхним тайлом (fx, fy): щелчок в середину основания выбирает её рамкой.
        viewer.ClickWorld(fx + 1.5, fy + 1.5);
        viewer.Step(2);
        Assert.True(Selected(viewer) == 0 && viewer.Draws.Rects.Count > 0, "здание не выбрано");
        viewer.Sent.Clear();
        // Ниже окна «Первых шагов» справа сверху.
        viewer.ClickWorld(fx + 5.5, fy + 14.5, Kharos.Client.Ui.PointerButtons.Right);
        viewer.Step(2);
        var rally = Assert.Single(viewer.Sent.Of<RallyOrder>());
        Assert.Equal((factory, fx + 5, fy + 14), (rally.Building, rally.X, rally.Y));
        Assert.Empty(viewer.Sent.Of<PointOrder>());
        Assert.Empty(viewer.Log);
    }
}
