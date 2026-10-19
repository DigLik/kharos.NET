using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using MemoryPack;
using static Kharos.Base.Tests.Construction.Yard;

namespace Kharos.Base.Tests.Construction;

/// <summary>Стройка (packages/tests/construction.test.ts): площадки, строители, кредиты по ходу, отмена, зона, материалы.</summary>
public sealed class ConstructionTests
{
    private static double Cost(string type) => Tables.Buildings[type].Cost;

    private static bool Inside(Sim sim, uint entity, (int X, int Y) site, int w = 2, int h = 2)
    {
        var (x, y) = sim.At(entity);
        return x >= site.X && x < site.X + w && y >= site.Y && y < site.Y + h;
    }

    [Fact(DisplayName = "строитель возводит здание: кредиты списаны по ходу, площадка проходима, готовое здание занимает тайлы")]
    public void BuilderRaisesBuilding()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        var credits = sim.Credits(1);
        var earned = sim.Num(sim.Wallet(1), "Player.earned");
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [builders[0]] });
        sim.Advance();
        // Закладка ничего не списывает, но обещает цену стройке: свободных кредитов меньше.
        Assert.Equal(credits, sim.Credits(1));
        Assert.Equal(credits - Cost("generator"), sim.FreeCredits(1));
        var entity = sim.SiteAt(site.X + 1, site.Y + 1);
        Assert.NotEqual(0u, entity);
        Assert.Equal("generator", TypeOf(sim, entity));
        Assert.Equal((0.0, 0.0, 0.0), (sim.Num(entity, "Site.progress"), sim.Num(entity, "Site.demolish"), sim.Num(entity, "Site.undo")));
        // Пока строитель не доехал, площадка никому не мешает, но второе здание на неё не поставить.
        Assert.True(sim.Walkable(site.X, site.Y));
        Assert.False(sim.CanBuild(1, "metalYard", site.X, site.Y));
        Assert.Equal(entity, (uint)sim.Num(builders[0], "Builds.site"));

        sim.Seconds(8);
        Assert.True(sim.Has(entity, Names.Building.Name));
        Assert.False(sim.Walkable(site.X, site.Y));
        Assert.True(sim.Has(entity, Names.Site.Name));

        sim.TakeSignals(BuiltSignal.Name);
        sim.Seconds(Cost("generator") / 20);
        Assert.False(sim.Has(entity, Names.Site.Name));
        // Готовое здание — событие, ровно одно.
        var built = sim.TakeSignals(BuiltSignal.Name).Select(signal => MemoryPackSerializer.Deserialize<BuiltSignal>(signal.Payload)!).ToList();
        var (px, py) = sim.At(entity);
        Assert.Single(built);
        Assert.Equal((entity, 1u, "generator", px, py), (built[0].Entity, built[0].Player, built[0].Building, built[0].X, built[0].Y));
        // Списано по ходу — цена целиком; назад пришла награда за электростанцию. Дохода главного здания (С3) пока нет:
        // доли кредита, оставшиеся в earned, — в счёт.
        var spent = credits + earned + Tables.All.Rewards["generator"].Credits - sim.Credits(1) - sim.Num(sim.Wallet(1), "Player.earned");
        Assert.True(spent <= Cost("generator") + 1e-9, $"{spent}");
        Assert.True(spent > Cost("generator") - 40, $"{spent}");
        Assert.Equal(sim.Num(entity, "Health.max"), sim.Num(entity, "Health.value"));
        Assert.Equal("generator", TypeOf(sim, entity));
        // Первый генератор приносит награду.
        Assert.Equal(["deploy", "generator"], Scene.Rewards(sim, 1));
        Assert.False(sim.Has(builders[0], Names.Builds.Name));
        Assert.Equal(0u, sim.SiteAt(site.X, site.Y));
    }

    [Fact(DisplayName = "два строителя строят вдвое быстрее одного")]
    public void TwoBuildersBuildTwiceAsFast()
    {
        double Progress(int count)
        {
            using var yard = Start();
            var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
            // Лишнего строителя убираем: свободный сам пришёл бы помогать.
            sim.Erase([.. builders.Skip(count)]);
            sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders.Take(count)] });
            sim.Seconds(6);
            var entity = sim.OccupantAt(site.X, site.Y);
            var before = Yard.Progress(sim, entity);
            sim.Seconds(2);
            return Yard.Progress(sim, entity) - before;
        }
        Assert.Equal(40, Progress(1), 6);
        Assert.Equal(80, Progress(2), 6);
    }

    [Fact(DisplayName = "свои юниты уходят с площадки, и только потом начинается стройка")]
    public void OwnUnitsLeaveSiteFirst()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        var soldier = Scene.UnitsOf(sim, "infantry")[0];
        sim.Send(1, new MoveOrder { Units = [soldier], X = site.X, Y = site.Y });
        sim.Send(1, new MoveOrder { Units = [builders[1]], X = site.X + 1, Y = site.Y + 1 });
        sim.Seconds(10);
        Assert.True(Inside(sim, soldier, site));
        Assert.True(Inside(sim, builders[1], site));
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [builders[0]] });
        sim.Seconds(10);
        // Ушёл и пехотинец, и строитель, стоявший на площадке без дела.
        Assert.False(Inside(sim, soldier, site));
        Assert.False(Inside(sim, builders[1], site));
        Assert.NotEqual(0u, sim.OccupantAt(site.X, site.Y));
    }

    [Fact(DisplayName = "чужой юнит на площадке держит стройку, пока не уйдёт")]
    public void ForeignUnitHoldsSite()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        var stranger = Scene.Spawn(sim, "infantry", 0, site.X, site.Y);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Seconds(10);
        var entity = sim.SiteAt(site.X, site.Y);
        // Чужого не выгнали, стройка не началась, тайлы свободны.
        Assert.Equal(site.X, (int)Math.Floor(sim.At(stranger).X));
        Assert.False(sim.Has(entity, Names.Building.Name));
        Assert.Equal(0, Yard.Progress(sim, entity));
        Assert.Equal(0u, sim.OccupantAt(site.X, site.Y));

        sim.Send(0, new MoveOrder { Units = [stranger], X = site.X - 3, Y = site.Y });
        sim.Seconds(5);
        Assert.True(sim.Has(entity, Names.Building.Name));
        Assert.True(Yard.Progress(sim, entity) > 0);
    }

    [Fact(DisplayName = "строить можно только в радиусе контроля и только строителями, а закладывать — и без кредитов")]
    public void BuildOnlyInZoneWithBuilders()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        // Кредитов — ровно на четыре электростанции с небольшим остатком.
        Wallet(sim, 1, 4 * Cost("generator") + 100);
        var credits = sim.Credits(1);
        Assert.False(sim.CanBuild(1, "generator", site.X + 60, site.Y));
        Assert.False(sim.CanBuild(2, "generator", site.X, site.Y));
        // Главное здание строители не возводят: его привозит MCV.
        Assert.False(sim.CanBuild(1, "command", site.X, site.Y));
        Assert.False(sim.CanBuild(1, "command", site.X + 60, site.Y));
        sim.Send(2, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Send(1, new BuildOrder { Building = "windtrap", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Advance();
        Assert.Equal(0u, sim.SiteAt(site.X, site.Y));
        Assert.Equal(credits, sim.Credits(1));

        // Пехотинец и чужой строитель на стройку не идут.
        var stranger = Scene.Spawn(sim, "builder", 2, site.X - 1, site.Y);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. Scene.UnitsOf(sim, "infantry"), stranger] });
        sim.Advance();
        Assert.NotEqual(0u, sim.SiteAt(site.X, site.Y));
        Assert.Equal(0, sim.Count(Names.Builds.Name));

        // Площадки можно закладывать и без строителей: каждая обещает свою цену из свободных кредитов.
        foreach (var y in (int[])[site.Y - 3, site.Y + 3, site.Y + 6])
        {
            Assert.True(sim.CanBuild(1, "generator", site.X, y));
            sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = y });
            sim.Advance();
        }
        Assert.Equal(4, sim.Count(Names.Site.Name));
        Assert.Equal(credits, sim.Credits(1));
        Assert.Equal(credits - 4 * Cost("generator"), sim.FreeCredits(1));

        // Пятая закладывается, хотя на неё не хватит: платят по ходу, и она подождёт кредитов.
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X + 3, Y = site.Y });
        sim.Advance();
        Assert.Equal(5, sim.Count(Names.Site.Name));
        Assert.Equal(credits, sim.Credits(1));
    }

    [Fact(DisplayName = "отмена разбирает недостроенное и возвращает кредиты по ходу, а приказ идти снимает строителя со стройки")]
    public void CancelDismantlesAndMoveReleases()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        var credits = sim.Credits(1);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Seconds(7);
        var entity = sim.OccupantAt(site.X, site.Y);
        sim.Send(1, new MoveOrder { Units = [builders[0]], X = site.X - 3, Y = site.Y });
        sim.Send(2, new CancelBuildOrder { Site = entity });
        sim.Advance();
        Assert.False(sim.Has(builders[0], Names.Builds.Name));
        Assert.True(sim.Has(builders[1], Names.Builds.Name));
        Assert.True(sim.Alive(entity));

        // Строителя можно вернуть на стройку.
        sim.Send(1, new AssistOrder { Units = [builders[0]], Site = entity });
        sim.Advance();
        Assert.Equal(entity, (uint)sim.Num(builders[0], "Builds.site"));

        // Отмена не убирает здание сразу: строители его разбирают, и уплаченное возвращается по ходу.
        var paid = credits - sim.Credits(1);
        Assert.True(paid > 0);
        sim.Send(1, new CancelBuildOrder { Site = entity });
        sim.Seconds(0.2);
        Assert.True(sim.Alive(entity));
        Assert.Equal((1.0, 1.0), (sim.Num(entity, "Site.demolish"), sim.Num(entity, "Site.undo")));
        Assert.True(credits - sim.Credits(1) < paid);
        sim.Seconds(8);
        Assert.False(sim.Alive(entity));
        // Вернулось всё; сверх того — кредит-другой дохода.
        Assert.True(sim.Credits(1) - credits >= -1, $"{sim.Credits(1) - credits}");
        Assert.True(sim.Credits(1) - credits < 5, $"{sim.Credits(1) - credits}");
        Assert.True(sim.Walkable(site.X, site.Y));
        Assert.Equal(0, sim.Count(Names.Builds.Name));
    }

    [Fact(DisplayName = "разбор отменённой стройки не отменить; площадку без начатой работы убирают сразу")]
    public void UndoCannotBeUndone()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Seconds(8);
        var entity = sim.OccupantAt(site.X, site.Y);
        sim.Send(1, new CancelBuildOrder { Site = entity });
        sim.Seconds(0.5);
        var left = Yard.Progress(sim, entity);
        sim.Send(1, new CancelBuildOrder { Site = entity });
        sim.Seconds(0.5);
        Assert.Equal((1.0, 1.0), (sim.Num(entity, "Site.demolish"), sim.Num(entity, "Site.undo")));
        Assert.True(Yard.Progress(sim, entity) < left);

        // Площадка без начатой работы ничего не стоила — она исчезает сразу.
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y + 4 });
        sim.Advance();
        var plot = sim.SiteAt(site.X, site.Y + 4);
        Assert.NotEqual(0u, plot);
        sim.Send(1, new CancelBuildOrder { Site = plot });
        sim.Advance();
        Assert.False(sim.Alive(plot));
    }

    [Fact(DisplayName = "без главного здания стройка стоит, готовые здания остаются")]
    public void SiteStallsWithoutCore()
    {
        using var yard = Start();
        var (sim, core, builders, site) = (yard.Sim, yard.Core, yard.Builders, yard.Site);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [builders[0]] });
        sim.Send(1, new PackOrder { Building = core });
        sim.Seconds(10.5);
        var entity = sim.OccupantAt(site.X, site.Y);
        var frozen = Yard.Progress(sim, entity);
        Assert.True(frozen > 0);
        sim.Seconds(5);
        Assert.Equal(frozen, Yard.Progress(sim, entity));
        Assert.False(sim.CanBuild(1, "metalYard", site.X, site.Y + 3));

        // Развернулся на прежнем месте — стройка продолжается.
        sim.Send(1, new DeployOrder { Unit = Scene.UnitsOf(sim, "mcv")[0] });
        sim.Seconds(20);
        Assert.False(sim.Has(entity, Names.Site.Name));
        Assert.True(sim.Has(entity, Names.Building.Name));
    }

    [Fact(DisplayName = "сохранение посреди стройки продолжается так же")]
    public void SaveMidConstruction()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Seconds(7);
        using var copy = sim.ThroughSave();
        sim.Seconds(12);
        copy.Seconds(12);
        Assert.True(copy.SameWorld(sim));
        Assert.Equal(0, copy.Count(Names.Site.Name));
    }

    [Fact(DisplayName = "свободный строитель сам берётся за стройку рядом, а за далёкую и за стройку вне зоны — нет")]
    public void IdleBuilderVolunteers()
    {
        using var yard = Start();
        var (sim, core, site) = (yard.Sim, yard.Core, yard.Site);
        // Остаётся ближний к площадке строитель (местность .NET другая: дальний может стоять за WORK_RADIUS).
        var builders = yard.Builders.OrderBy(builder => Sim.Distance(sim.At(builder), (site.X + 1, site.Y + 1))).ToList();
        sim.Erase(builders[1]);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y });
        sim.Seconds(1.1);
        var entity = sim.SiteAt(site.X, site.Y);
        Assert.Equal(entity, (uint)sim.Num(builders[0], "Builds.site"));
        sim.Seconds(6);
        Assert.True(Yard.Progress(sim, entity) > 0);

        // Дальше WORK_RADIUS свободный строитель за стройку не берётся.
        var position = sim.At(builders[0]);
        var spot = Builders.FreeTilesNear(sim, site.X + 10 + 6, site.Y, 1)[0];
        var far = Scene.Spawn(sim, "builder", 1, spot.X, spot.Y);
        sim.Seconds(2);
        Assert.False(sim.Has(far, Names.Builds.Name));

        // Главное здание свёрнуто — стройка стоит, и браться за неё незачем.
        sim.Send(1, new PackOrder { Building = core });
        sim.Seconds(10.1);
        sim.Send(1, new MoveOrder { Units = [builders[0]], X = (int)Math.Floor(position.X) - 1, Y = (int)Math.Floor(position.Y) });
        sim.Seconds(3);
        Assert.False(sim.Has(builders[0], Names.Builds.Name));
    }

    [Fact(DisplayName = "дерево технологий: после главного здания — электростанция, после неё шахта, лаборатория и радар, переработка и военное — ещё и после исследований")]
    public void TechTree()
    {
        using var yard = Start(rules: new { techTree = true });
        var (sim, site) = (yard.Sim, yard.Site);
        Assert.True(sim.Unlocked(1, "generator"));
        // Стену и радар открывают ещё и исследования.
        Assert.False(sim.Unlocked(1, "wall"));
        // Открытие prospecting (шахта) даёт увиденное месторождение — система triggers раздела Research (С6); здесь — сразу.
        Research(sim, 1, "walls", "radar", "prospecting");
        Assert.True(sim.Unlocked(1, "wall"));
        foreach (var type in (string[])["matter", "mine", "radar", "smelter", "factory", "turret", "rocketTurret"])
            Assert.False(sim.Unlocked(1, type), type);
        Assert.False(sim.CanBuild(1, "radar", site.X, site.Y));

        // Недостроенная электростанция ничего не открывает, готовая — открывает.
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. Scene.UnitsOf(sim, "builder")] });
        sim.Advance();
        Assert.False(sim.Unlocked(1, "radar"));
        var generator = Scene.Place(sim, "generator", site.X - 3, site.Y + 4, 1);
        Assert.True(sim.Unlocked(1, "radar"));
        Assert.True(sim.Unlocked(1, "mine"));
        Assert.True(sim.Unlocked(1, "lab"));
        // Переработку открывает электростанция и исследование своей руды.
        foreach (var type in (string[])["smelter", "siliconWorks", "distillery", "enricher"])
            Assert.False(sim.Unlocked(1, type), type);
        Research(sim, 1, [.. Tables.All.Research.Keys]);
        foreach (var type in (string[])["smelter", "siliconWorks", "distillery", "enricher"])
            Assert.True(sim.Unlocked(1, type), type);
        Assert.False(sim.Unlocked(1, "factory"));
        // Чужое здание не в счёт.
        Assert.False(sim.Unlocked(2, "radar"));

        Scene.Place(sim, "radar", site.X + 3, site.Y + 4, 1);
        foreach (var type in (string[])["barracks", "factory", "airfield", "turret", "rocketTurret"])
            Assert.True(sim.Unlocked(1, type), type);
        Assert.False(sim.Unlocked(1, "repairStation"));
        // Снесли электростанцию — новый радар не заложить, но военное держится на стоящем радаре.
        sim.Erase(generator);
        Assert.False(sim.Unlocked(1, "radar"));
        Assert.True(sim.Unlocked(1, "barracks"));
        // Главное здание строителям недоступно никогда: его привозит MCV.
        Assert.False(sim.Unlocked(1, "command"));
    }

    [Fact(DisplayName = "стройка, которая ждёт материалов, строителя не держит: он уходит на другую работу рядом")]
    public void WaitingSiteReleasesBuilder()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        // Грузовиков нет — металл космопорту не привезут.
        var trucks = Scene.UnitsOf(sim, "truck");
        if (trucks.Count > 0)
            sim.Erase([.. trucks]);
        var builder = builders[0];
        sim.Erase([.. builders.Skip(1)]);
        sim.Send(1, new BuildOrder { Building = "spaceport", X = site.X, Y = site.Y, Builders = [builder] });
        sim.Seconds(1);
        var waiting = sim.SiteAt(site.X, site.Y);
        Assert.Equal(waiting, (uint)sim.Num(builder, "Builds.site"));
        // Рядом — электростанция, ей материалы не нужны; заложена без строителей.
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X + 4, Y = site.Y });
        sim.Seconds(1);
        var ready = sim.SiteAt(site.X + 4, site.Y);
        Assert.NotEqual(0u, ready);
        sim.Seconds(15);
        // Электростанцию строит он — или уже построил.
        Assert.True(!sim.Has(ready, Names.Site.Name) || (uint)sim.Num(builder, "Builds.site") == ready);
        Assert.True(sim.Has(ready, Names.Building.Name));
    }

    [Fact(DisplayName = "стройка, которой не хватает лишь погрешности материалов, достраивается")]
    public void SiteShortByEpsilonCompletes()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        sim.Send(1, new BuildOrder { Building = "spaceport", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Seconds(1);
        var building = sim.SiteAt(site.X, site.Y);
        // Луч переносит груз долями: на площадке может оказаться на крошку меньше нужного.
        sim.Set(building, "Stock.metal", Tables.Buildings["spaceport"].Materials["metal"] - 5e-7);
        sim.Seconds(120);
        Assert.False(sim.Has(building, Names.Site.Name));
    }

    [Fact(DisplayName = "стройка, которой до конца осталась лишь крошка счёта, не ждёт материалов и достраивается")]
    public void SiteShortByRoundingCompletes()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [builders[0]] });
        sim.Seconds(8);
        var entity = sim.SiteAt(site.X + 1, site.Y + 1);
        Assert.True(sim.Has(entity, Names.Building.Name) && sim.Has(entity, Names.Site.Name));
        // Доли работы сложились в 399,99999999999966 из 400, а строители как раз ушли.
        sim.Set(entity, "Site.progress", SiteTicks("generator") - 3e-13);
        sim.Set(entity, "Health.value", sim.Num(entity, "Health.max"));
        foreach (var builder in builders)
            sim.Remove(builder, Names.Builds.Name);
        Assert.False(sim.AwaitsMaterials(entity));
        sim.Seconds(10);
        Assert.False(sim.Has(entity, Names.Site.Name));
    }

    [Fact(DisplayName = "стена протяжкой: закладывается по тайлам, занятое пропускается, без кредитов — тоже")]
    public void WallStrokeLays()
    {
        (int Sites, double Left) Stroke(double credits)
        {
            using var sim = Create();
            var at = WallLine(sim, 10);
            Scene.Place(sim, "wall", at.X + 4, at.Y, 1);
            sim.SetCredits(1, credits);
            var tiles = new List<int>();
            for (var tx = at.X; tx < at.X + 10; tx++)
                tiles.AddRange([tx, at.Y]);
            // Повтор тайла в протяжке второй раз не кладётся.
            tiles.AddRange([at.X, at.Y]);
            Assert.Equal(9, sim.WallStroke(1, [.. tiles]).Count(ok => ok));
            sim.Send(1, new WallsOrder { Tiles = [.. tiles] });
            sim.Advance();
            return (SitesOf(sim, "wall").Count, sim.FreeCredits(1));
        }
        Assert.Equal((9, Cost("wall") * 11), Stroke(Cost("wall") * 20));
        // Платят по ходу стройки: протяжка ложится целиком, недостающее стройки подождут.
        Assert.Equal((9, -Cost("wall") * 5), Stroke(Cost("wall") * 4));
    }

    /// <summary>Ряд из length тайлов, куда встают стены, — ближайший к началу мира с (−200, −200).</summary>
    internal static (int X, int Y) WallLine(Sim sim, int length)
    {
        var land = sim.Land!;
        bool Ground(int x, int y) => land.TerrainAt(x, y) is Kharos.Engine.Terrain.TerrainType.Rock or Kharos.Engine.Terrain.TerrainType.Sand && !land.IsCliffFoot(x + 0.5, y + 0.5);
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
            {
                var ok = true;
                for (var tx = x; tx < x + length && ok; tx++)
                    ok = Ground(tx, y);
                if (!ok)
                    continue;
                for (var tx = x; tx < x + length && ok; tx++)
                    ok = Scene.CanPlace(sim, "wall", tx, y);
                if (ok)
                    return (x, y);
            }
        throw new InvalidOperationException("Не нашлось места под стену");
    }

    [Fact(DisplayName = "недостроенное здание держит удар по построенному: урон откатывает стройку, отмена разбором возвращает цену того, что стоит")]
    public void DamageRollsBackSite()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        var credits = sim.Credits(1);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        var total = SiteTicks("generator");
        uint entity = 0;
        for (var i = 0; i < 600; i++)
        {
            sim.Advance();
            entity = sim.OccupantAt(site.X, site.Y);
            if (entity != 0 && Yard.Progress(sim, entity) >= total / 2.0)
                break;
        }
        double Share() => Yard.Progress(sim, entity) / total;
        double Health() => sim.Num(entity, "Health.value") / sim.Num(entity, "Health.max");
        // Прочность — доля построенного, списано — та же доля цены.
        Assert.Equal(Share(), Health(), 2);
        Assert.True(credits - sim.Credits(1) > Cost("generator") * Share() - 20);
        Assert.True(credits - sim.Credits(1) <= Cost("generator") * Share() + 1);

        // Удар сносит половину построенного: стройка откатывается, строить и платить снова.
        sim.Set(entity, "Health.value", sim.Num(entity, "Health.max") * Share() / 2);
        sim.Advance();
        Assert.True(Share() < 0.3, $"{Share()}");
        var standing = Share();
        var before = sim.Credits(1);
        sim.Send(1, new CancelBuildOrder { Site = entity });
        sim.Seconds(10);
        Assert.False(sim.Alive(entity));
        var back = sim.Credits(1) - before;
        Assert.True(back > Cost("generator") * standing - 20, $"{back}");
        Assert.True(back < Cost("generator") * 0.3, $"{back}");
    }

    [Fact(DisplayName = "без кредитов стройка стоит, но новую закладывать можно")]
    public void NoCreditsStallsButLaysNew()
    {
        using var yard = Start();
        var (sim, builders, site) = (yard.Sim, yard.Builders, yard.Site);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. builders] });
        sim.Seconds(8);
        var entity = sim.OccupantAt(site.X, site.Y);
        // Деньги ушли на другое: стройка встаёт там, где кончились.
        Wallet(sim, 1, 0);
        var stuck = Yard.Progress(sim, entity);
        sim.Seconds(1);
        Assert.True(Yard.Progress(sim, entity) < stuck + 2);
        Assert.True(sim.FreeCredits(1) < 0);
        Assert.True(sim.CanBuild(1, "generator", site.X, site.Y + 4));
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y + 4 });
        sim.Advance();
        Assert.Equal(2, sim.Count(Names.Site.Name));
    }

    [Fact(DisplayName = "строитель, которого обступили свои, сходит с площадки трубы у здания, и труба строится")]
    public void BuilderLeavesPipeSite()
    {
        using var yard = Start();
        var (sim, core, builders) = (yard.Sim, yard.Core, yard.Builders);
        Research(sim, 1, "pipes");
        var (cx, cy) = sim.At(core);
        // Тайл над правым краем главного здания: снизу — здание, справа — своя труба, слева — багги, сверху — второй строитель.
        var (x, y) = ((int)cx + 2, (int)cy - 1);
        sim.PutBuilding("pipe", x + 1, y, 1);
        Scene.Spawn(sim, "buggy", 1, x - 1, y);
        sim.Erase([.. builders]);
        var (inside, above) = (Scene.Spawn(sim, "builder", 1, x, y), Scene.Spawn(sim, "builder", 1, x, y - 1));
        sim.Send(1, new PipesOrder { Tiles = [x, y], Builders = [above, inside] });
        sim.Seconds(15);
        var pipe = sim.OccupantAt(x, y);
        Assert.NotEqual(0u, pipe);
        Assert.Equal("pipe", TypeOf(sim, pipe));
        Assert.False(sim.Has(pipe, Names.Site.Name));
    }
}
