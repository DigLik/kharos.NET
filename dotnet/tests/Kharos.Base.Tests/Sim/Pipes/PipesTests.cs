using Base.Shared;
using Base.Shared.Orders;
using Tables = Kharos.Base.Tests.Kernel.Data;
using static Kharos.Base.Tests.Pipes.PipesScene;

namespace Kharos.Base.Tests.Pipes;

/// <summary>Трубы: сети, пачки, протяжка труб и колодцев (packages/tests/pipes.test.ts).</summary>
public sealed class PipesTests
{
    /// <summary>Пачка — до стольких единиц (BATCH).</summary>
    private const double Batch = 5;

    [Fact(DisplayName = "здания вплотную — одна сеть, но только одинаковые; по углу и через тайл — разные, труба соединяет")]
    public void AdjacentSameBuildingsShareNetwork()
    {
        using var sim = Create();
        // Чужое здание ставится на два ряда выше: скала нужна и под ним.
        var (x, y) = Rock(sim, 16, 8);
        y += 2;
        var one = Place(sim, "generator", x, y);
        var two = Place(sim, "generator", x + 2, y);
        Assert.Equal(sim.NetworkOf(one), sim.NetworkOf(two));
        Assert.True(sim.Joined(one, two));
        // Разное здание вплотную — не одна сеть: свяжет только труба.
        var lab = Place(sim, "lab", x + 4, y);
        Assert.NotEqual(sim.NetworkOf(one), sim.NetworkOf(lab));
        // По углу не связаны и одинаковые.
        var corner = Place(sim, "generator", x + 4, y + 2);
        Assert.NotEqual(sim.NetworkOf(one), sim.NetworkOf(corner));
        // Через тайл — тоже разные, пока его не займёт труба.
        var gap = Place(sim, "radar", x + 8, y);
        Assert.NotEqual(sim.NetworkOf(lab), sim.NetworkOf(gap));
        var pipes = Lay(sim, x + 6, y, 2);
        Assert.Equal(sim.NetworkOf(lab), sim.NetworkOf(gap));
        Assert.True(sim.Joined(lab, pipes[0]));
        // Чужое здание вплотную в сеть не входит, даже такое же.
        var foreign = Place(sim, "generator", x, y - 2, 2);
        Assert.NotEqual(sim.NetworkOf(one), sim.NetworkOf(foreign));
    }

    [Fact(DisplayName = "стройка вплотную получает материалы без трубы только от здания того же вида")]
    public void AdjacentSiteGetsMaterialsFromSameKind()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 20, 8);
        var core = Place(sim, "command", x, y);
        var port = Place(sim, "spaceport", x + 6, y);
        Put(sim, port, "metal", 50);
        AddCredits(sim, 1, 5000);
        // Стройка вплотную к другому виду — не в сеть: главное здание рядом, а сети у неё нет.
        sim.OrderNow(1, new BuildOrder { Building = "spaceport", X = x, Y = y + 3, Builders = [] });
        var lone = sim.SiteAt(x, y + 3);
        Assert.NotEqual(0u, lone);
        Assert.Equal(0u, sim.NetworkOf(lone));
        // Своё здание вплотную — одна сеть с ним, и материалы доезжают без трубы.
        sim.OrderNow(1, new BuildOrder { Building = "spaceport", X = x + 6, Y = y + 3, Builders = [] });
        var site = sim.SiteAt(x + 6, y + 3);
        Assert.Equal(sim.NetworkOf(port), sim.NetworkOf(site));
        Assert.NotEqual(sim.NetworkOf(core), sim.NetworkOf(port));
        var need = Tables.Buildings["spaceport"].Materials["metal"];
        for (var i = 0; i < 400 && sim.Stock(site, "metal") < need; i++)
            sim.Advance();
        Assert.Equal(need, sim.Stock(site, "metal"));
        // Которой сети нет, той и материалов не везут.
        Assert.Equal(0, sim.Stock(lone, "metal"));
    }

    [Fact(DisplayName = "энергия идёт по зоне: рядом — и без трубы, вдали — по трубе")]
    public void PowerFollowsZone()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 24, 4);
        Place(sim, "generator", x, y);
        var near = Place(sim, "radar", x + 4, y);
        var far = Place(sim, "radar", x + 20, y);
        Assert.True(sim.PowerSupply().GetValueOrDefault(near) > 0);
        Assert.Equal(0, sim.PowerSupply().GetValueOrDefault(far));
        Lay(sim, x, y + 2, 22);
        Assert.True(sim.PowerSupply().GetValueOrDefault(far) > 0);
    }

    [Fact(DisplayName = "колодцы связываются подземным отрезком по прямой")]
    public void WellsLinkUnderground()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 20, 4);
        var one = Place(sim, "well", x, y);
        var two = Place(sim, "well", x + 8, y);
        Assert.Equal(sim.NetworkOf(one), sim.NetworkOf(two));
        var far = Place(sim, "well", x + 8 + 11, y);
        Assert.NotEqual(sim.NetworkOf(two), sim.NetworkOf(far));
    }

    /// <summary>Хранилище металла и завод стройблоков на одной трубе; в хранилище — 100 металла.</summary>
    private static (Sim Sim, uint Yard, uint Plant, List<uint> Pipes) Line()
    {
        var sim = Create();
        var (x, y) = Rock(sim, 24, 6);
        var yard = Place(sim, "metalYard", x, y);
        var plant = Place(sim, "blockPlant", x + 14, y);
        Put(sim, yard, "metal", 100);
        var pipes = Lay(sim, x, y + 2, 16);
        return (sim, yard, plant, pipes);
    }

    [Fact(DisplayName = "пачка идёт тайл за тик и приходит на склад; следующая — через тик после прихода")]
    public void BatchMovesTilePerTick()
    {
        // Без энергии завод только копит сырьё.
        var (sim, _, plant, _) = Line();
        using var _sim = sim;
        sim.Set(plant, "Assembly.on", 1);
        sim.Advance();
        var batches = sim.Batches();
        Assert.Single(batches);
        var batch = batches[0];
        Assert.Equal(Batch, batch.Amount);
        Assert.Equal(plant, batch.To);
        var length = (int)(batch.Arrive - batch.Sent);
        Assert.True(length > 10, $"{length}");
        var before = sim.Stock(plant, "metal");
        sim.Advance(length);
        Assert.True(sim.Stock(plant, "metal") >= before + Batch - 1e-9);
        // Через тик после прихода маршрут свободен и уходит следующая.
        sim.Advance();
        Assert.Contains(sim.Batches(), item => item.Sent > batch.Sent && item.To == plant);
    }

    [Fact(DisplayName = "перерезанная труба: пачка на ней пропадает")]
    public void CutPipeLosesBatch()
    {
        var (sim, yard, plant, pipes) = Line();
        using var _sim = sim;
        sim.Set(plant, "Assembly.on", 1);
        sim.Advance();
        double Metal() => sim.Stock(yard, "metal") + sim.Stock(plant, "metal");
        var total = Metal();
        sim.Erase(pipes[8]);
        sim.Advance(30);
        Assert.Equal(0, sim.Stock(plant, "metal"));
        Assert.True(Metal() <= total);
    }

    [Fact(DisplayName = "пачки в пути переживают сохранение")]
    public void BatchesSurviveSave()
    {
        var (sim, _, plant, _) = Line();
        using var _sim = sim;
        sim.Set(plant, "Assembly.on", 1);
        sim.Advance(3);
        using var loaded = sim.ThroughSave();
        Assert.Equal(1, loaded.Count(Names.Batch.Name));
        loaded.Advance(40);
        Assert.True(loaded.Stock(plant, "metal") > 0);
    }

    [Fact(DisplayName = "при постройке колодца видно, с какими колодцами он свяжется")]
    public void WellPartnersVisible()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 16, 4);
        Place(sim, "well", x, y);
        // Чужой колодец не в счёт, вплотную — не подземная связь.
        Place(sim, "well", x + 4, y + 2, 2);
        Assert.Equal([new WellSpot(x, y, true)], sim.WellPartners(1, x + 6, y));
        Assert.Empty(sim.WellPartners(1, x + 1, y));
        Assert.Empty(sim.WellPartners(1, x + 4, y + 2));
        Assert.Empty(sim.WellPartners(1, x + 11, y));
    }

    [Fact(DisplayName = "трубу тянут без зоны: цепочка закладывается целиком, строитель проходит её по очереди")]
    public void PipeStrokeOutsideZone()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 40, 4);
        Place(sim, "command", x, y);
        AddCredits(sim, 1, 1000);
        var builder = Spawn(sim, "builder", 1, x + 3, y + 3);
        // От главного здания вправо на 30 тайлов — дальше его зоны (12 от центра).
        var tiles = new List<int>();
        for (var tx = x + 3; tx < x + 33; tx++)
            tiles.AddRange([tx, y + 1]);
        Assert.All(sim.PipeStroke(1, [.. tiles]), Assert.True);
        // Своя зона не нужна и отдельному тайлу.
        Assert.Equal([true], sim.PipeStroke(1, [x + 30, y + 3]));
        sim.Send(1, new PipesOrder { Tiles = [.. tiles], Builders = [builder] });
        sim.Advance();
        Assert.Equal(30, SitesOf(sim, "pipe"));
        sim.Advance(20 * 90);
        Assert.Equal(sim.NetworkOf(sim.OccupantAt(x, y)), sim.NetworkOf(sim.OccupantAt(x + 32, y + 1)));
        Assert.NotEqual(0u, sim.NetworkOf(sim.OccupantAt(x + 32, y + 1)));
        // В чужой зоне класть нельзя.
        Place(sim, "command", x + 36, y, 2);
        Assert.Equal([false], sim.PipeStroke(1, [x + 34, y + 3]));
    }

    [Fact(DisplayName = "«Снять» убирает и трубы: заложенную — сразу с возвратом, готовую разбирают строители")]
    public void UnpaveRemovesPipes()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 16, 4);
        Place(sim, "command", x, y);
        AddCredits(sim, 1, 1000);
        var builder = Spawn(sim, "builder", 1, x + 3, y + 3);
        var built = Place(sim, "pipe", x + 3, y + 1);
        sim.Send(1, new PipesOrder { Tiles = [x + 10, y + 1], Builders = [] });
        sim.Advance();
        var credits = sim.FreeCredits(1);
        sim.Send(1, new UnpaveOrder { Tiles = [x + 3, y + 1, x + 10, y + 1], Builders = [builder] });
        sim.Advance();
        // Заложенная отменилась: она ничего не стоила, обещанное ей вернулось в свободные; готовую разбирают.
        Assert.Equal(0u, sim.SiteAt(x + 10, y + 1));
        Assert.True(sim.FreeCredits(1) - credits >= 5, $"{sim.FreeCredits(1) - credits}");
        Assert.True(sim.Has(built, Names.Site.Name) && sim.Num(built, "Site.demolish") != 0);
        sim.Advance(20 * 10);
        Assert.False(sim.Alive(built));
    }

    [Fact(DisplayName = "колодцы кладут парой (по прямой, от 2 до 10 тайлов, оба или ни одного) или по одному")]
    public void WellsInPairsOrSingle()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 16, 4);
        AddCredits(sim, 1, 1000);
        // Не по прямой, слишком близко и слишком далеко — не кладут.
        foreach (var tiles in new[] { new[] { x, y, x + 4, y + 1 }, [x, y, x + 1, y], [x, y, x + 11, y] })
            sim.Send(1, new WellsOrder { Tiles = tiles, Builders = [] });
        sim.Advance();
        Assert.Equal(0, SitesOf(sim, "well"));
        var credits = sim.FreeCredits(1);
        sim.Send(1, new WellsOrder { Tiles = [x, y, x + 8, y], Builders = [] });
        sim.Advance();
        Assert.Equal(2, SitesOf(sim, "well"));
        Assert.Equal(80, credits - sim.FreeCredits(1));
        // И по одному: щелчок без протяжки.
        sim.Send(1, new WellsOrder { Tiles = [x + 12, y], Builders = [] });
        sim.Advance();
        Assert.Equal(3, SitesOf(sim, "well"));
        Assert.Equal(120, credits - sim.FreeCredits(1));
    }

    [Fact(DisplayName = "«Разобрать» рамкой разбирает здания, только целиком попавшие в неё")]
    public void DemolishFrameTakesWholeBuildings()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 16, 4);
        AddCredits(sim, 1, 1000);
        var builder = Spawn(sim, "builder", 1, x + 6, y + 3);
        var inside = Place(sim, "generator", x, y);
        var half = Place(sim, "generator", x + 3, y);
        // Рамка 4×2 от (x, y): первая станция целиком, вторая — наполовину.
        var tiles = new List<int>();
        for (var ty = y; ty < y + 2; ty++)
            for (var tx = x; tx < x + 4; tx++)
                tiles.AddRange([tx, ty]);
        sim.Send(1, new UnpaveOrder { Tiles = [.. tiles], Builders = [builder] });
        sim.Advance();
        Assert.True(sim.Has(inside, Names.Site.Name) && sim.Num(inside, "Site.demolish") != 0);
        Assert.False(sim.Has(half, Names.Site.Name));
    }

    [Fact(DisplayName = "протяжка через стоящую трубу пропускает её тайл и тянется дальше, и без кредитов — тоже")]
    public void StrokeSkipsStandingPipe()
    {
        int Stroke(double credits)
        {
            using var sim = Create();
            var (x, y) = Rock(sim, 20, 2);
            Place(sim, "pipe", x + 4, y);
            AddCredits(sim, 1, credits);
            var tiles = new List<int>();
            for (var tx = x; tx < x + 15; tx++)
                tiles.AddRange([tx, y]);
            Assert.Equal(14, sim.PipeStroke(1, [.. tiles]).Count(ok => ok));
            sim.Send(1, new PipesOrder { Tiles = [.. tiles], Builders = [] });
            sim.Advance();
            return SitesOf(sim, "pipe");
        }
        Assert.Equal(14, Stroke(1000));
        // Платят по ходу стройки: протяжка ложится целиком и без кредитов.
        Assert.Equal(14, Stroke(Cost("pipe") * 6));
    }

    [Fact(DisplayName = "колодец кладут вместо своей трубы и трубу вместо колодца: старое убирается с возвратом, новое закладывается")]
    public void WellReplacesPipeAndBack()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 16, 4);
        AddCredits(sim, 1, 1000);
        Lay(sim, x, y, 3);
        Place(sim, "well", x + 8, y);
        var credits = sim.FreeCredits(1);
        sim.Send(1, new WellsOrder { Tiles = [x + 1, y], Builders = [] });
        sim.Send(2, new WellsOrder { Tiles = [x, y], Builders = [] });
        sim.Advance();
        Assert.Equal(["pipe", "well?", "pipe"], new[] { KindAt(sim, x, y), KindAt(sim, x + 1, y), KindAt(sim, x + 2, y) });
        Assert.Equal(Cost("well") - Refund("pipe"), credits - sim.FreeCredits(1), 9);
        // Обратно: труба поверх заложенного колодца (возврат целиком) и поверх готового (как за разбор).
        credits = sim.FreeCredits(1);
        sim.Send(1, new PipesOrder { Tiles = [x + 1, y, x + 8, y], Builders = [] });
        sim.Advance();
        Assert.Equal(["pipe?", "pipe?"], new[] { KindAt(sim, x + 1, y), KindAt(sim, x + 8, y) });
        Assert.Equal(2 * Cost("pipe") - Cost("well") - Refund("well"), credits - sim.FreeCredits(1), 9);
        // Колодец на своём колодце и труба на своей трубе — не кладутся.
        Place(sim, "well", x + 12, y);
        credits = sim.FreeCredits(1);
        sim.Send(1, new WellsOrder { Tiles = [x + 12, y], Builders = [] });
        sim.Send(1, new PipesOrder { Tiles = [x, y], Builders = [] });
        sim.Advance();
        Assert.Equal(credits, sim.FreeCredits(1), 9);
        // Чужую трубу не заменить.
        Place(sim, "pipe", x + 5, y + 3, 2);
        sim.Advance();
        Assert.Equal(0u, sim.PipeSwap(1, "well", x + 5, y + 3));
        Assert.Equal([false], sim.PipeStroke(1, [x + 5, y + 3], "well"));
    }

    [Fact(DisplayName = "протяжка колодца от своего колодца закладывает только второй, а от своей трубы — заменяет её и кладёт оба")]
    public void WellStrokeFromOwnWell()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 16, 4);
        AddCredits(sim, 1, 1000);
        var well = Place(sim, "well", x, y);
        Lay(sim, x, y + 2, 2);
        var credits = sim.FreeCredits(1);
        // От стоящего колодца дальше 10 тайлов и не по прямой — никак; по прямой — один новый.
        sim.Send(1, new WellsOrder { Tiles = [x, y, x + 11, y], Builders = [] });
        sim.Send(1, new WellsOrder { Tiles = [x, y, x + 6, y + 1], Builders = [] });
        sim.Advance();
        Assert.Equal(credits, sim.FreeCredits(1), 9);
        sim.Send(1, new WellsOrder { Tiles = [x, y, x + 6, y], Builders = [] });
        sim.Advance();
        Assert.True(sim.Alive(well));
        Assert.Equal(["well", "well?"], new[] { KindAt(sim, x, y), KindAt(sim, x + 6, y) });
        Assert.Equal(Cost("well"), credits - sim.FreeCredits(1), 9);
        Assert.Equal([new WellSpot(x, y, true)], sim.WellPartners(1, x + 6, y));
        // Оба конца уже колодцы — класть нечего.
        credits = sim.FreeCredits(1);
        sim.Send(1, new WellsOrder { Tiles = [x, y, x + 6, y], Builders = [] });
        sim.Advance();
        Assert.Equal(credits, sim.FreeCredits(1), 9);
        // От трубы: труба уходит, колодцев два.
        sim.Send(1, new WellsOrder { Tiles = [x + 1, y + 2, x + 9, y + 2], Builders = [] });
        sim.Advance();
        Assert.Equal(["pipe", "well?", "well?"], new[] { KindAt(sim, x, y + 2), KindAt(sim, x + 1, y + 2), KindAt(sim, x + 9, y + 2) });
        Assert.Equal(2 * Cost("well") - Refund("pipe"), credits - sim.FreeCredits(1), 9);
    }

    [Fact(DisplayName = "укладка труб не стирает очередь строителя и не срывает его с работы; с Shift встаёт в очередь всем")]
    public void PipesKeepBuilderQueue()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 20, 8);
        AddCredits(sim, 1, 5000);
        var busy = Spawn(sim, "builder", 1, x + 1, y + 6);
        var free = Spawn(sim, "builder", 1, x + 3, y + 6);
        // Занятый строит колодец по приказу, и в очереди у него ещё один.
        sim.Send(1, new WellsOrder { Tiles = [x + 14, y + 6], Builders = [busy] });
        sim.Advance();
        var first = sim.SiteAt(x + 14, y + 6);
        sim.Send(1, new WellsOrder { Tiles = [x + 17, y + 6], Builders = [busy], Queue = true });
        sim.Advance();
        var second = sim.SiteAt(x + 17, y + 6);
        Assert.Equal((first, 1.0), ((uint)sim.Num(busy, "Builds.site"), sim.Num(busy, "Builds.ordered")));
        Assert.Equal([second], Queued(sim, busy));

        sim.Send(1, new PipesOrder { Tiles = [x, y, x + 1, y, x + 2, y], Builders = [busy, free] });
        sim.Advance();
        var pipe = sim.SiteAt(x, y);
        // Занятый доделывает своё, труба встала ему в конец очереди; свободный поехал сразу.
        Assert.Equal(first, (uint)sim.Num(busy, "Builds.site"));
        Assert.Equal([second, pipe], Queued(sim, busy));
        Assert.Equal(pipe, (uint)sim.Num(free, "Builds.site"));
        // С Shift — в очередь и свободному; покрытие и снятие — так же.
        sim.Send(1, new PaveOrder { Kind = "road", Tiles = [x + 5, y + 3], Builders = [busy, free], Queue = true });
        sim.Send(1, new UnpaveOrder { Tiles = [x + 2, y], Builders = [busy], Queue = true });
        sim.Advance();
        Assert.Equal(pipe, (uint)sim.Num(free, "Builds.site"));
        Assert.Single(Queued(sim, free));
        Assert.Equal([second, pipe], Queued(sim, busy).Take(2));
        Assert.Equal(3, Queued(sim, busy).Count);
        Assert.Equal(0u, sim.SiteAt(x + 2, y));
    }

    /// <summary>Хранилище, завод стройблоков и космопорт на одной трубе (резерв и продажа в pipes.test.ts).</summary>
    private static (Sim Sim, uint Yard, uint Plant, uint Port) Market()
    {
        var sim = Create();
        var (x, y) = Rock(sim, 30, 10);
        y += 6;
        var yard = Place(sim, "metalYard", x, y);
        var plant = Place(sim, "blockPlant", x + 14, y);
        var port = Place(sim, "spaceport", x + 20, y + 2 - (int)Tables.Buildings["spaceport"].Height);
        Put(sim, yard, "metal", 100);
        Lay(sim, x, y + 2, 24);
        return (sim, yard, plant, port);
    }

    [Fact(DisplayName = "резерв хранилища: заводу — только сверх резерва, продаже — всё; резерв переживает сохранение")]
    public void StoreReserve()
    {
        var (sim, yard, plant, port) = Market();
        using var _sim = sim;
        Assert.Equal(sim.NetworkOf(yard), sim.NetworkOf(plant));
        Assert.Equal(sim.NetworkOf(yard), sim.NetworkOf(port));

        // Не хранилище резерва не получает; больше, чем вмещает хранилище, не поставить.
        sim.OrderNow(1, new ReserveOrder { Buildings = [plant], Amount = 10 });
        Assert.Equal(0, sim.Num(plant, "Stock.reserve"));
        sim.OrderNow(2, new ReserveOrder { Buildings = [yard], Amount = 10 });
        Assert.Equal(0, sim.Num(yard, "Stock.reserve"));
        sim.OrderNow(1, new ReserveOrder { Buildings = [yard], Amount = 1e9 });
        Assert.Equal(sim.Num(yard, "Stock.capacity"), sim.Num(yard, "Stock.reserve"));
        sim.OrderNow(1, new ReserveOrder { Buildings = [yard], Amount = 97 });
        using (var loaded = sim.ThroughSave())
            Assert.Equal(97, loaded.Num(yard, "Stock.reserve"));

        // Завод просит 8 металла, а получает три — что лежит сверх резерва.
        sim.Set(plant, "Assembly.on", 1);
        sim.Advance(100);
        Assert.Equal(97, sim.Stock(yard, "metal"), 6);
        Assert.Equal(3, sim.Stock(plant, "metal"), 6);

        // Продажа — воля игрока: она забирает и резерв.
        sim.OrderNow(1, new SellOrder { Port = port, Resource = "metal", Amount = 50 });
        Assert.True(sim.Has(port, "Trade"));
        sim.Advance(400);
        Assert.Equal(47, sim.Stock(yard, "metal"), 6);
        Assert.Equal(3, sim.Stock(plant, "metal"), 6);

        // Резерв снят — завод добирает своё.
        sim.OrderNow(1, new ReserveOrder { Buildings = [yard], Amount = 0 });
        sim.Advance(100);
        Assert.Equal(8, sim.Stock(plant, "metal"), 6);
    }

    [Fact(DisplayName = "продажа добирает заявку до конца, когда в хранилищах лежат дробные остатки")]
    public void SaleTakesFractionalRemainders()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 30, 10);
        y += 6;
        // Ближнее к космопорту хранилище почти пусто: оно отдаёт дробную пачку, и заявке остаётся меньше половины единицы.
        var far = Place(sim, "metalYard", x, y);
        var near = Place(sim, "metalYard", x + 10, y);
        var port = Place(sim, "spaceport", x + 20, y + 2 - (int)Tables.Buildings["spaceport"].Height);
        Put(sim, far, "metal", 100);
        Put(sim, near, "metal", 4.6);
        Lay(sim, x, y + 2, 24);
        sim.OrderNow(1, new SellOrder { Port = port, Resource = "metal", Amount = 10 });
        Assert.True(sim.Has(port, "Trade"));
        sim.Advance(400);
        Assert.True(sim.Num(port, "Trade.total") > 0);
    }

    [Fact(DisplayName = "металл на продажу грузовик везёт и из другой сети: космопорт без трубы к складу продаёт весь запас")]
    public void TruckSellsFromOtherNetwork()
    {
        using var sim = Create();
        var (x, y) = Rock(sim, 30, 10);
        var yard = Place(sim, "metalYard", x, y + 6);
        var port = Place(sim, "spaceport", x + 15, y + 5);
        Put(sim, yard, "metal", 30);
        Spawn(sim, "truck", 1, x + 10, y + 9);
        Assert.NotEqual(sim.NetworkOf(port), sim.NetworkOf(yard));
        sim.OrderNow(1, new SellOrder { Port = port, Resource = "metal", Amount = 30 });
        Assert.True(sim.Has(port, "Trade"));
        sim.Advance(1200);
        Assert.Equal(0, sim.Stock(yard, "metal"), 3);
        Assert.True(!sim.Has(port, "Trade") || sim.Num(port, "Trade.total") > 0);
    }
}
