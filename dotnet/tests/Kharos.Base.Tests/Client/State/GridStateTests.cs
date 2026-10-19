using Base.Shared;
using Base.Shared.Rules;
using Kharos.Client.Game;

namespace Kharos.Base.Tests.ClientState;

/// <summary>
/// Энергия для клиента (пакет Э): хост пишет сети игрока в Grids на сущности счёта (только хозяину) и Grid у зданий сети
/// (номер сети скрыт от чужих, биты состояния — всем). Клиент рисует молнии и знаки нехватки, звучит перегрузом и
/// показывает энергию в HUD по сетям, а не одной сетью на игрока.
/// </summary>
public sealed class GridStateTests
{
    private static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    private static string T(string key, params string[] args) => Kharos.Base.Tests.Client.Hud.RequestButtonsTests.T(key, args);

    /// <summary>
    /// Три сети игрока 1 на ровной земле: A — главное здание, электростанция и плавильня (энергии хватает); B — главное
    /// здание, электростанция и три завода (полная нагрузка 15 при выработке 10 — перегруз); C — главное здание и
    /// плавильня без станции (нехватка). Одной сетью на игрока (20 на 1 + 6 + 1, полная 18) бед не было бы вовсе.
    /// </summary>
    private sealed record Scene(Sim Sim, uint PlantA, uint SmelterA, uint PlantB, uint SmelterC) : IDisposable
    {
        public void Dispose() => Sim.Dispose();
    }

    private static Scene Make(uint[] players)
    {
        SkipUnless();
        var sim = Sim.Create(new SimOptions { Game = "frames", Size = 96, Fog = false, Land = new { blank = true }, Players = players });
        sim.SetCredits(1, 5000);
        sim.Learn(1);
        ViewerScenes.Place(sim, "command", 0, 0, 1);
        var plantA = ViewerScenes.Place(sim, "generator", 4, 0, 1);
        var smelterA = ViewerScenes.Place(sim, "smelter", 0, 4, 1);
        ViewerScenes.Place(sim, "command", 50, 0, 1);
        var plantB = ViewerScenes.Place(sim, "generator", 54, 0, 1);
        ViewerScenes.Place(sim, "factory", 50, 4, 1);
        ViewerScenes.Place(sim, "factory", 54, 4, 1);
        ViewerScenes.Place(sim, "factory", 58, 0, 1);
        ViewerScenes.Place(sim, "command", 0, 50, 1);
        var smelterC = ViewerScenes.Place(sim, "smelter", 4, 50, 1);
        sim.Advance(2);
        return new Scene(sim, plantA, smelterA, plantB, smelterC);
    }

    private static int State(Sim sim, uint entity) => (int)sim.Num(entity, Names.Grid.Name, Names.Grid.State);

    [Fact(DisplayName = "хост пишет сети игрока: Grid у зданий (номер сети и беда) и Grids на счёте — как хозяйства сетей")]
    public void HostPublishesGrids()
    {
        using var scene = Make([1]);
        var sim = scene.Sim;
        var zones = sim.ZoneEconomies(1);
        Assert.Equal(3, zones.Length);
        var nets = GridWire.Unpack(sim.Bytes(sim.Wallet(1), "Grids.nets"));
        Assert.Equal(zones.Length, nets.Count);
        for (var i = 0; i < nets.Count; i++)
        {
            Assert.Equal(zones[i].Produced, nets[i].Produced, 3);
            Assert.Equal(zones[i].Demand, nets[i].Demand, 3);
            Assert.Equal(zones[i].Peak, nets[i].Peak, 3);
            Assert.Equal(zones[i].Income, nets[i].Income, 3);
            Assert.Equal(PowerRules.OverloadOf(zones[i].Produced, zones[i].Peak, zones[i].Spare) > 0, nets[i].Overload);
            Assert.True(nets[i].Core);
        }
        // Номера сетей у зданий — разные, беды — по своей сети, как powerStates хоста.
        var (a, b, c) = ((int)sim.Num(scene.PlantA, "Grid.net"), (int)sim.Num(scene.PlantB, "Grid.net"), (int)sim.Num(scene.SmelterC, "Grid.net"));
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
        Assert.Equal(a, (int)sim.Num(scene.SmelterA, "Grid.net"));
        Assert.True(nets[b - 1].Overload);
        Assert.Equal(GridWire.Overload, State(sim, scene.PlantB) & GridWire.Overload);
        Assert.Equal(0, State(sim, scene.PlantA) & (GridWire.Overload | GridWire.Starved));
        Assert.Equal(GridWire.Starved, State(sim, scene.SmelterC) & GridWire.Starved);
        Assert.Equal(0, State(sim, scene.SmelterA) & GridWire.Starved);
        var states = sim.PowerStates();
        Assert.Equal("overload", states[scene.PlantB]);
        Assert.Equal("starved", states[scene.SmelterC]);
        Assert.False(states.ContainsKey(scene.PlantA));
        // Станция под нагрузкой — бит Loaded, как loadedPlants.
        foreach (var plant in sim.LoadedPlants())
            Assert.Equal(GridWire.Loaded, State(sim, plant) & GridWire.Loaded);
    }

    [Fact(DisplayName = "энергия сетей уходит хозяину: чужой не получает Grids и номера сетей, но видит беды зданий")]
    public void GridsReachOnlyOwner()
    {
        using var scene = Make([1, 2]);
        var sim = scene.Sim;
        using var own = sim.Viewer(1);
        using var stranger = sim.Viewer(2);
        own.Step(3);
        stranger.Step(3);
        int Id(Viewer viewer, string name) => viewer.World.Schema.Id(name);
        uint Account(Viewer viewer, uint player)
        {
            var world = viewer.World;
            var list = new List<uint>();
            world.Query([Id(viewer, Names.Player.Name)], list);
            return list.FirstOrDefault(entity => world.Number(entity, Id(viewer, Names.Player.Name), world.Schema[Id(viewer, Names.Player.Name)].FieldIndex(Names.Player.Id)) == player);
        }
        double Net(Viewer viewer, uint entity) =>
            viewer.World.Number(entity, Id(viewer, Names.Grid.Name), viewer.World.Schema[Id(viewer, Names.Grid.Name)].FieldIndex(Names.Grid.Net));
        double Bits(Viewer viewer, uint entity) =>
            viewer.World.Number(entity, Id(viewer, Names.Grid.Name), viewer.World.Schema[Id(viewer, Names.Grid.Name)].FieldIndex(Names.Grid.State));

        // Хозяин: свой счёт с сетями, номера сетей у своих зданий.
        var mine = Account(own, 1);
        Assert.NotEqual(0u, mine);
        Assert.True(own.World.Has(mine, Id(own, Names.Grids.Name)));
        var nets = GridWire.Unpack(own.World.GetBytes(mine, Id(own, Names.Grids.Name), 0));
        Assert.Equal(3, nets.Count);
        Assert.Equal(sim.Num(scene.PlantB, "Grid.net"), Net(own, scene.PlantB));

        // Чужой: счёта игрока 1 с сетями нет, номера сетей — по умолчанию, а беда станции видна (молнии видит каждый).
        var theirs = Account(stranger, 1);
        Assert.True(theirs == 0 || !stranger.World.Has(theirs, Id(stranger, Names.Grids.Name)));
        Assert.True(stranger.World.Has(scene.PlantB, Id(stranger, Names.Grid.Name)));
        Assert.Equal(0, Net(stranger, scene.PlantB));
        Assert.Equal(GridWire.Overload, (int)Bits(stranger, scene.PlantB) & GridWire.Overload);
        Assert.Empty(own.Log);
        Assert.Empty(stranger.Log);
    }

    [Fact(DisplayName = "клиент рисует и показывает энергию по сетям: молнии у перегруженной станции, знак нехватки в сети без станции, полоса — сеть выбранного")]
    public void ClientUsesNetworks()
    {
        using var scene = Make([1]);
        var sim = scene.Sim;
        using var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
        var bolt = viewer.Game.LookId("power/bolt");
        var icon = viewer.Game.LookId("power/icon");
        Assert.True(bolt >= 0 && icon >= 0);

        bool Near(SceneSprite sprite, uint entity, double w)
        {
            var (x, y) = sim.At(entity);
            var cx = sprite.X + sprite.Scale / 2;
            return cx >= x - 0.5 && cx <= x + w + 0.5 && sprite.Y >= y - 3 && sprite.Y <= y + 1;
        }
        (bool Bolt, bool Icon) Seen(uint entity, double w)
        {
            var (x, y) = sim.At(entity);
            viewer.Camera = new CameraState(x + 1, y + 1, 32);
            var (bolts, icons) = (false, false);
            for (var i = 0; i < 60; i++)
            {
                viewer.Frame();
                bolts |= viewer.Draws.Sprites.Any(sprite => sprite.Proto == bolt && Near(sprite, entity, w));
                icons |= viewer.Draws.Sprites.Any(sprite => sprite.Proto == icon && Near(sprite, entity, w));
            }
            return (bolts, icons);
        }

        viewer.Step(3);
        Assert.True(Seen(scene.PlantB, 2).Bolt, "нет молний у перегруженной станции сети B");
        Assert.False(Seen(scene.PlantA, 2).Bolt, "молнии у станции сети A, где энергии хватает");
        Assert.True(Seen(scene.SmelterC, 3).Icon, "нет знака нехватки у плавильни сети без станции");
        Assert.False(Seen(scene.SmelterA, 3).Icon, "знак нехватки у плавильни сети со станцией");
        // Петля перегруза звучит только при звуке; здесь её нет, но и ошибок нет.
        Assert.Empty(viewer.Log);

        // Полоса: выбрана станция сети B — энергия сети B, выбрана плавильня сети C — сети C (одной сетью было бы 8 / 20).
        string? Bar(uint entity)
        {
            var (x, y) = sim.At(entity);
            viewer.Camera = new CameraState(x + 1, y + 1, 32);
            viewer.Step(2);
            viewer.ClickWorld(x + 1, y + 1);
            viewer.Step(2);
            return viewer.Ui.Text("grid");
        }
        var zones = sim.ZoneEconomies(1);
        string Expect(int net)
        {
            var zone = zones[net - 1];
            return "⚡ " + global::Base.Client.Commands.N(Math.Ceiling(zone.Demand * 10) / 10) + " / " + global::Base.Client.Commands.N(Math.Floor(zone.Produced * 10) / 10);
        }
        var b = (int)sim.Num(scene.PlantB, "Grid.net");
        var c = (int)sim.Num(scene.SmelterC, "Grid.net");
        Assert.Equal(Expect(b), Bar(scene.PlantB));
        Assert.Equal(Expect(c), Bar(scene.SmelterC));
        // Нехватка у выбранной плавильни сети C — подсказка панели.
        Assert.Contains(viewer.Ui.Texts(), text => text.Contains(T("hud.starved"), StringComparison.Ordinal));
        Assert.Empty(viewer.Log);
    }
}
