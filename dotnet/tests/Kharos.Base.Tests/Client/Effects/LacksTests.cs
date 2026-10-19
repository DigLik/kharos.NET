using Base.Shared.Orders;
using Kharos.Base.Tests.Researching;
using Kharos.Client.Game;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.ClientEffects;

/// <summary>
/// Знаки нехватки (packages/tests/lacks.test.ts): клиент base на реплике хоста (Viewer) считает, чего ждут свои здания
/// (Hud/Lacks.cs), и рисует над ними значок груза (power/lack, кадр — груз), кредитов (power/broke), бледный — на исходе.
/// Значки мигают, поэтому собираются за секунду кадров.
/// </summary>
public sealed class LacksTests
{
    internal static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    /// <summary>Знак над сущностью: груз (номер в Icons.Goods) или −2 — кредиты; бледный ли.</summary>
    internal readonly record struct Sign(int Good, bool Low);

    internal const int Broke = -2;

    /// <summary>Главное здание, электростанция и здания из списка в ряд, под общей трубой (base).</summary>
    private static (Sim Sim, List<uint> Buildings) Row(params string[] types)
    {
        SkipUnless();
        var sim = Sim.Create(new SimOptions { Size = 128, Land = "default" });
        sim.SetCredits(1, 10000);
        sim.Learn(1);
        var row = Lab.Row(sim, ["command", "generator", .. types]) ?? throw new InvalidOperationException("Не нашлось места");
        return (sim, row[2..]);
    }

    /// <summary>Зритель игрока с камерой над сущностью.</summary>
    internal static Viewer Watch(Sim sim, uint player, uint at)
    {
        var viewer = sim.Viewer(player, new ViewerOptions { Width = 1280, Height = 720 });
        var (x, y) = sim.At(at);
        viewer.Camera = new CameraState(x, y, 16);
        viewer.Step(2);
        return viewer;
    }

    /// <summary>
    /// Знаки нехватки над сущностями за frames кадров (значок мигает): место значка — над серединой основания, над
    /// крышей. Сущность без основания (Footprint) — тайл.
    /// </summary>
    internal static Dictionary<uint, Sign> Signs(Viewer viewer, IEnumerable<uint> entities, int frames = 60)
    {
        var lack = viewer.Game.LookId("power/lack");
        var broke = viewer.Game.LookId("power/broke");
        Assert.True(lack >= 0 && broke >= 0, "нет обликов значков power/lack и power/broke");
        var sim = viewer.Sim;
        var bases = entities.Select(entity =>
        {
            var (x, y) = sim.At(entity);
            var footprint = sim.Has(entity, "Footprint");
            return (Entity: entity, X: x, Y: y, W: footprint ? sim.Num(entity, "Footprint.w") : 1, H: footprint ? sim.Num(entity, "Footprint.h") : 1);
        }).ToList();
        var signs = new Dictionary<uint, Sign>();
        for (var i = 0; i < frames; i++)
        {
            viewer.Frame();
            foreach (var sprite in viewer.Draws.Sprites)
            {
                if (sprite.Proto != lack && sprite.Proto != broke)
                    continue;
                var (cx, cy) = (sprite.X + sprite.Scale / 2, sprite.Y + sprite.Scale / 2);
                foreach (var b in bases)
                    if (cx >= b.X && cx <= b.X + b.W && cy >= b.Y - 2 && cy <= b.Y + 0.5)
                        signs[b.Entity] = new Sign(sprite.Proto == broke ? Broke : sprite.Frame, sprite.Alpha < 1);
            }
        }
        return signs;
    }

    /// <summary>Сколько знаков нехватки за frames кадров нарисовано вообще.</summary>
    internal static int AllSigns(Viewer viewer, int frames = 60)
    {
        var lack = viewer.Game.LookId("power/lack");
        var broke = viewer.Game.LookId("power/broke");
        var count = 0;
        for (var i = 0; i < frames; i++)
        {
            viewer.Frame();
            count += viewer.Draws.Sprites.Count(sprite => sprite.Proto == lack || sprite.Proto == broke);
        }
        return count;
    }

    private static int Good(string name) => Array.IndexOf(global::Base.Client.Icons.Goods, name);

    [Fact(DisplayName = "знак нехватки: завод с заказом без материалов ждёт первый недостающий груз; без заказа и выключенный — не ждут")]
    public void FactoryWaitsForFirstMissingMaterial()
    {
        var (sim, buildings) = Row("factory", "blockPlant");
        using var _ = sim;
        var (factory, plant) = (buildings[0], buildings[1]);
        using var viewer = Watch(sim, 1, factory);
        using var stranger = Watch(sim, 2, factory);
        viewer.Run(0.5);
        // Заказа нет — заводу ничего не нужно; цех выключен с постройки.
        Assert.Equal(0, AllSigns(viewer));

        var material = Tables.Units["buggy"].Materials.Keys.First();
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        viewer.Run(0.5);
        Assert.Equal(Good(material), Signs(viewer, [factory]).GetValueOrDefault(factory, new Sign(-1, false)).Good);
        // Чужому игроку о наших складах судить не по чему.
        stranger.Step(30);
        Assert.Equal(0, AllSigns(stranger));

        // Привезли — завод взялся за заказ и больше ничего не ждёт.
        foreach (var (good, amount) in Tables.Units["buggy"].Materials)
            sim.SetStock(factory, good, amount);
        viewer.Run(0.5);
        Assert.False(Signs(viewer, [factory]).ContainsKey(factory));

        // Включённый цех без сырья ждёт первое сырьё рецепта; выключенный — нет.
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        viewer.Run(0.5);
        Assert.Equal(1, sim.Num(plant, "Assembly.on"));
        Assert.True(Signs(viewer, [plant]).ContainsKey(plant));
        sim.Send(1, new WorkOrder { Building = plant, On = false });
        viewer.Run(0.5);
        Assert.False(Signs(viewer, [plant]).ContainsKey(plant));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "знак нехватки: своя стройка без кредитов ждёт кредитов, разбор — нет")]
    public void SiteWithoutCreditsWaitsForCredits()
    {
        SkipUnless();
        using var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        sim.SetCredits(1, 0);
        var site = sim.World.Spawn();
        sim.Add(site, "Position");
        sim.Add(site, "Site");
        sim.Add(site, "Owner");
        sim.Set(site, "Owner.player", 1);
        sim.Add(site, "Proto");
        sim.Set(site, "Proto.id", Kernel.Scene.Proto(sim, "buildings/wall"));
        sim.Add(site, "Footprint");
        sim.Set(site, "Footprint.w", 1);
        sim.Set(site, "Footprint.h", 1);
        sim.Flush();
        using var viewer = Watch(sim, 1, site);
        using var stranger = Watch(sim, 2, site);
        Assert.Equal(new Sign(Broke, false), Signs(viewer, [site]).GetValueOrDefault(site, new Sign(-1, false)));
        Assert.Equal(0, AllSigns(stranger));

        sim.SetCredits(1, 100);
        viewer.Run(0.5);
        Assert.False(Signs(viewer, [site]).ContainsKey(site));

        sim.SetCredits(1, 0);
        sim.Set(site, "Site.demolish", 1);
        sim.Flush();
        viewer.Run(0.5);
        Assert.False(Signs(viewer, [site]).ContainsKey(site));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "знак нехватки у турели и реактора: пустые — яркий, запас меньше четверти — бледный, полные — без знака")]
    public void TurretAndReactorSigns()
    {
        // Без тика: иначе грузовики и трубы успели бы подвезти. Зритель приходит после правок.
        var (sim, buildings) = Row("turret", "turret", "turret", "cannonTurret", "reactor");
        using var _ = sim;
        var (empty, low, full, cannon, reactor) = (buildings[0], buildings[1], buildings[2], buildings[3], buildings[4]);
        sim.SetStock(empty, "ammo", 0);
        sim.SetStock(low, "ammo", 20);
        sim.SetStock(full, "ammo", 120);
        // Пушке нужно 8 на выстрел: с 5 она уже молчит.
        sim.SetStock(cannon, "ammo", 5);
        sim.SetStock(reactor, "rods", 0);
        using var viewer = Watch(sim, 1, cannon);
        var signs = Signs(viewer, buildings);
        Assert.Equal(new Sign(Good("ammo"), false), signs.GetValueOrDefault(empty));
        Assert.Equal(new Sign(Good("ammo"), false), signs.GetValueOrDefault(cannon));
        Assert.Equal(new Sign(Good("rods"), false), signs.GetValueOrDefault(reactor));
        Assert.Equal(new Sign(Good("ammo"), true), signs.GetValueOrDefault(low));
        Assert.False(signs.ContainsKey(full));
        Assert.Empty(viewer.Log);
    }
}
