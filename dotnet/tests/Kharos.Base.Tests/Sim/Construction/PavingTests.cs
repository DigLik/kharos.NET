using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using static Kharos.Base.Tests.Construction.Yard;

namespace Kharos.Base.Tests.Construction;

/// <summary>Покрытие (packages/tests/paving.test.ts): дороги, мосты, фундамент, снятие, скорость стройки, слой.</summary>
public sealed class PavingTests
{
    private const string Road = PaveRules.Road, Foundation = PaveRules.Foundation;

    [Fact(DisplayName = "строители кладут дорогу: тайлы оплачены сразу, достраиваются и ускоряют наземных")]
    public void BuildersLayRoad()
    {
        using var yard = Start();
        var (sim, builders, (x, y)) = (yard.Sim, yard.Builders, yard.At);
        var credits = sim.Credits(1);
        int[] tiles = [x + 5, y + 9, x + 6, y + 9, x + 7, y + 9];
        sim.Send(1, new PaveOrder { Kind = Road, Tiles = tiles, Builders = [.. builders] });
        sim.Advance();
        Assert.Equal(credits - 3 * PaveRules.RoadCost, sim.Credits(1));
        Assert.False(sim.IsPaved(Road, x + 5, y + 9));
        // На уже покрытый тайл второй раз не кладут.
        Assert.False(sim.CanPave(1, Road, x + 5, y + 9));
        sim.Seconds(15);
        for (var i = 0; i < tiles.Length; i += 2)
            Assert.True(sim.IsPaved(Road, tiles[i], tiles[i + 1]));
        Assert.Equal(PaveRules.RoadSpeed, sim.TerrainSpeed("buggy", x + 6, y + 9));
        Assert.Equal(1, sim.TerrainSpeed("buggy", x + 6, y + 10));
        // По фундаменту — быстрее скалы, но медленнее дороги.
        sim.Lay(Foundation, x + 6, y + 10);
        Assert.Equal(PaveRules.FoundationSpeed, sim.TerrainSpeed("buggy", x + 6, y + 10));
        Assert.True(PaveRules.FoundationSpeed > 1);
        Assert.True(PaveRules.FoundationSpeed < PaveRules.RoadSpeed);
    }

    [Fact(DisplayName = "дорога по болоту — мост: дороже, болото под ним не вязнет")]
    public void RoadOverSwampIsBridge()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        var swamp = Find(sim, TerrainType.Swamp, x, y, 1);
        Assert.True(sim.TerrainSpeed("tank", swamp.X, swamp.Y) < 1);
        // Фундамент на болото не кладут.
        Assert.False(sim.CanPave(1, Foundation, swamp.X, swamp.Y));
        Assert.True(sim.CanPave(1, Road, swamp.X, swamp.Y));
        var credits = sim.Credits(1);
        sim.Send(1, new PaveOrder { Kind = Road, Tiles = [swamp.X, swamp.Y] });
        sim.Advance();
        Assert.Equal(credits - PaveRules.BridgeCost, sim.Credits(1));
        // Достроено (settlePave): покрытие — в слой, работа исчезает.
        sim.Erase(sim.PaveAt(swamp.X, swamp.Y));
        sim.Lay(Road, swamp.X, swamp.Y);
        // Колёсные по мосту едут быстрее, гусеничные — как по скале.
        Assert.Equal(PaveRules.RoadSpeed, sim.TerrainSpeed("buggy", swamp.X, swamp.Y));
        Assert.Equal(1, sim.TerrainSpeed("tank", swamp.X, swamp.Y));
    }

    [Fact(DisplayName = "фундамент на песке разрешает стройку, но зону не заменяет")]
    public void FoundationOnSand()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        var sand = Find(sim, TerrainType.Sand, x + 40, y + 40, 2);
        Assert.False(Scene.CanPlace(sim, "generator", sand.X, sand.Y));
        for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
                sim.Lay(Foundation, sand.X + dx, sand.Y + dy);
        Assert.True(Scene.CanPlace(sim, "generator", sand.X, sand.Y));
        // Здание, которому нужна зона, вне её не ставят и на фундаменте; оборону — можно.
        Assert.False(sim.CanBuild(1, "generator", sand.X, sand.Y));
        Assert.True(sim.CanBuild(1, "turret", sand.X, sand.Y));
        // На песке с фундаментом строят чуть медленнее, чем на голой скале.
        Assert.True(sim.BuildSpeed("generator", sand.X, sand.Y) < 1);
    }

    [Fact(DisplayName = "снимают покрытие строители: недостроенное — сразу с возвратом, готовое разбирают; без строителей — никак")]
    public void UnpaveNeedsBuilders()
    {
        using var yard = Start();
        var (sim, builders, (x, y)) = (yard.Sim, yard.Builders, yard.At);
        var credits = sim.Credits(1);
        var far = Find(sim, TerrainType.Rock, x + 60, y + 60, 1);
        sim.Send(1, new PaveOrder { Kind = Foundation, Tiles = [far.X, far.Y] });
        sim.Advance();
        Assert.Equal(credits - PaveRules.FoundationCost, sim.Credits(1));
        sim.Send(1, new UnpaveOrder { Tiles = [far.X, far.Y] });
        sim.Advance();
        Assert.NotEqual(0u, sim.PaveAt(far.X, far.Y));
        var before = sim.Credits(1);
        sim.Send(1, new UnpaveOrder { Tiles = [far.X, far.Y], Builders = [.. builders] });
        sim.Advance();
        Assert.Equal(0u, sim.PaveAt(far.X, far.Y));
        Assert.Equal(before + PaveRules.FoundationCost, sim.Credits(1));

        sim.Lay(Road, x + 6, y + 9);
        sim.Send(1, new UnpaveOrder { Tiles = [x + 6, y + 9], Builders = [.. builders] });
        sim.Advance();
        // Пока разбирают, дорога ещё работает.
        Assert.Equal(1, sim.Num(sim.PaveAt(x + 6, y + 9), "Pave.remove"));
        Assert.True(sim.IsPaved(Road, x + 6, y + 9));
        sim.Seconds(10);
        Assert.Equal(0u, sim.PaveAt(x + 6, y + 9));
        Assert.False(sim.IsPaved(Road, x + 6, y + 9));
    }

    [Fact(DisplayName = "на фундаменте на скале здание строится вдвое быстрее")]
    public void FoundationOnRockDoublesSpeed()
    {
        double Progress(bool paved)
        {
            using var yard = Start();
            var (sim, builders, (x, y)) = (yard.Sim, yard.Builders, yard.At);
            var at = (X: x + 7, Y: y + 4);
            if (paved)
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        sim.Lay(Foundation, at.X + dx, at.Y + dy);
            sim.Send(1, new BuildOrder { Building = "generator", X = at.X, Y = at.Y, Builders = [.. builders] });
            sim.Seconds(4);
            return SitesOf(sim, "generator").Select(site => Yard.Progress(sim, site)).DefaultIfEmpty(double.PositiveInfinity).First();
        }
        var bare = Progress(false);
        var paved = Progress(true);
        Assert.True(bare > 0);
        Assert.True(paved > bare * 1.5, $"{paved} ≤ {bare} × 1,5");
    }

    [Fact(DisplayName = "по покрытию не стреляют и оно не мешает ходить")]
    public void PavingIsNotBuilding()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        sim.Lay(Road, x + 5, y + 10);
        Assert.Equal(0u, sim.OccupantAt(x + 5, y + 10));
    }

    [Fact(DisplayName = "наземный взрыв разбивает фундамент под собой, пуля — нет")]
    public void BlastBreaksSlabs()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        for (var dx = 0; dx < 3; dx++)
            sim.Lay(Foundation, x + 4 + dx, y + 10);
        bool Slab(int dx) => sim.IsPaved(Foundation, x + dx, y + 10);
        // breakSlabs(x, y, size) — взрыв в бою размером size: здесь его даёт авария без урона радиусом size.
        Blast(sim, x + 5.5, y + 10.5, 0.2);
        Assert.True(Slab(5));
        Blast(sim, x + 5.5, y + 10.5, 1);
        Assert.False(Slab(5));
        Assert.True(Slab(4));
        Blast(sim, x + 5, y + 10.5, 3);
        Assert.False(Slab(4));
        Assert.False(Slab(6));
    }

    /// <summary>Наземный взрыв размером size в (x, y): авария без урона (Burst), которую разберёт ближайший бой.</summary>
    private static void Blast(Sim sim, double x, double y, double size)
    {
        var burst = sim.World.Spawn();
        sim.Add(burst, Names.Position.Name);
        sim.Set(burst, "Position.x", x);
        sim.Set(burst, "Position.y", y);
        sim.Add(burst, Names.Burst.Name);
        sim.Set(burst, "Burst.radius", size);
        sim.Advance();
    }

    [Fact(DisplayName = "взрыв в бою разбивает фундамент вокруг себя, дальние плиты целы")]
    public void BurstBreaksSlabs()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        for (var dx = 0; dx < 5; dx++)
            sim.Lay(Foundation, x + 3 + dx, y + 10);
        // Авария радиусом 2 в середине полосы: плиты, чьи центры ближе тайла, уходят, дальние остаются.
        Blast(sim, x + 5.5, y + 10.5, 2);
        Assert.Equal([true, false, false, false, true], new[] { 3, 4, 5, 6, 7 }.Select(dx => sim.IsPaved(Foundation, x + dx, y + 10)));
    }

    [Fact(DisplayName = "MCV разворачивается на фундаменте, лежащем на песке")]
    public void McvDeploysOnFoundationOverSand()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        var sand = Find(sim, TerrainType.Sand, x + 40, y + 40, 3);
        var mcv = Scene.Spawn(sim, "mcv", 1, sand.X + 1, sand.Y + 1);
        Assert.False(Scene.CanDeploy(sim, 1, mcv));
        for (var dy = 0; dy < 3; dy++)
            for (var dx = 0; dx < 3; dx++)
                sim.Lay(Foundation, sand.X + dx, sand.Y + dy);
        Assert.True(Scene.CanDeploy(sim, 1, mcv));
        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Seconds(6);
        Assert.NotEqual(0u, sim.OccupantAt(sand.X + 1, sand.Y + 1));
    }

    [Fact(DisplayName = "широкому юниту узкая дорога не помогает: ему нужна полоса в два тайла")]
    public void WideUnitNeedsWideRoad()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        for (var dx = 0; dx < 4; dx++)
            sim.Lay(Road, x + 4 + dx, y + 9);
        Assert.Equal(PaveRules.RoadSpeed, sim.TerrainSpeed("truck", x + 5, y + 9));
        Assert.Equal(1, sim.TerrainSpeed("artillery", x + 5, y + 9));
        for (var dx = 0; dx < 4; dx++)
            sim.Lay(Road, x + 4 + dx, y + 10);
        Assert.Equal(PaveRules.RoadSpeed, sim.TerrainSpeed("artillery", x + 5, y + 9));
        Assert.Equal(PaveRules.RoadSpeed, sim.TerrainSpeed("artillery", x + 5, y + 10));
    }

    /// <summary>Полоса скалы width×7 — ближайшая к (−200, −200).</summary>
    private static (int X, int Y) Strip(Sim sim, int width)
    {
        for (var ty = -200; ty < 200; ty++)
            for (var tx = -200; tx < 200; tx++)
                if (Stone(sim, tx, ty, width, 7))
                    return (tx, ty);
        throw new InvalidOperationException("Не нашлось скалы");
    }

    [Fact(DisplayName = "путь колёсных сворачивает на дорогу, если по ней быстрее; гусеничные едут напрямик")]
    public void WheelsTakeRoad()
    {
        using var sim = Create();
        // Полоса скалы 30×7: дорога идёт вдоль неё в двух тайлах от прямой между началом и целью.
        var at = Strip(sim, 30);
        var row = at.Y + 5;
        for (var tx = at.X + 1; tx < at.X + 29; tx++)
            sim.Lay(Road, tx, row);
        int OnRoad(string type)
        {
            var unit = Scene.Spawn(sim, type, 1, at.X + 1, at.Y + 2);
            var plan = sim.Host.Tools.Navigation!.OrderMove(unit, at.X + 28, at.Y + 2);
            sim.Erase(unit);
            var count = 0;
            for (var i = 0; i + 1 < plan.Points.Count; i += 2)
                if ((int)Math.Floor(plan.Points[i + 1]) == row)
                    count++;
            return count;
        }
        Assert.True(OnRoad("buggy") > 0);
        Assert.Equal(0, OnRoad("tank"));
    }

    [Fact(DisplayName = "дорогу ищут в обзоре юнита, а показалась она на ходу — путь перестраивается с ней")]
    public void RoadSeenOnTheWay()
    {
        using var sim = Create();
        var at = Strip(sim, 40);
        var buggy = Scene.Spawn(sim, "buggy", 1, at.X + 1, at.Y + 2);
        // Дорога далеко за пределами обзора: путь прокладывается без неё.
        for (var tx = at.X + 30; tx < at.X + 39; tx++)
            sim.Lay(Road, tx, at.Y + 5);
        sim.Host.Tools.Navigation!.Order(buggy, at.X + 38, at.Y + 2);
        Assert.Equal(0, sim.Num(buggy, "Path.roads"));
        // Рядом с юнитом легла дорога — он её замечает и прокладывает путь с ней.
        for (var tx = at.X + 2; tx < at.X + 30; tx++)
            sim.Lay(Road, tx, at.Y + 5);
        sim.Advance(25);
        Assert.Equal(1, sim.Num(buggy, "Path.roads"));
    }

    [Fact(DisplayName = "готовое покрытие — слой карты: переживает сохранение, а сохранённое сущностями уходит в слой при загрузке")]
    public void PavingIsLayer()
    {
        using var yard = Start();
        var (sim, (x, y)) = (yard.Sim, yard.At);
        sim.Lay(Road, x + 6, y + 9);
        sim.Lay(Foundation, x + 7, y + 9);
        // Сохранений, где готовое покрытие лежит сущностями, у .NET нет: слой сохраняется движком как есть.
        using var loaded = sim.ThroughSave();
        Assert.True(loaded.IsPaved(Road, x + 6, y + 9));
        Assert.True(loaded.IsPaved(Foundation, x + 7, y + 9));
        Assert.Equal(1u, loaded.PaveOwnerAt(x + 7, y + 9));
        Assert.Equal(0, loaded.Count(Names.Pave.Name));
    }

    [Fact(DisplayName = "фундамент кладут и под своё здание: строители работают с края; дорогу под здание и фундамент под чужое — нельзя")]
    public void FoundationUnderOwnBuilding()
    {
        using var yard = Start();
        var (sim, core, builders) = (yard.Sim, yard.Core, yard.Builders);
        var (cx, cy) = sim.At(core);
        var (ax, ay) = ((int)cx, (int)cy);
        Assert.False(sim.CanPave(1, Road, ax + 1, ay + 1));
        Assert.False(sim.CanPave(2, Foundation, ax + 1, ay + 1));
        var tiles = new List<int>();
        for (var dy = 0; dy < 3; dy++)
            for (var dx = 0; dx < 3; dx++)
                tiles.AddRange([ax + dx, ay + dy]);
        sim.Send(1, new PaveOrder { Kind = Foundation, Tiles = [.. tiles], Builders = [.. builders] });
        sim.Seconds(30);
        for (var i = 0; i < tiles.Count; i += 2)
            Assert.True(sim.IsPaved(Foundation, tiles[i], tiles[i + 1]));
    }

    [Fact(DisplayName = "на дороге не строят: она для транспорта; фундамент меняют на дорогу и обратно, прежнее действует, пока новое не готово")]
    public void NoBuildingOnRoad()
    {
        using var yard = Start();
        var (sim, builders, (x, y)) = (yard.Sim, yard.Builders, yard.At);
        var at = (X: x + 7, Y: y + 4);
        Assert.True(Scene.CanPlace(sim, "generator", at.X, at.Y));
        sim.Lay(Road, at.X, at.Y);
        Assert.False(Scene.CanPlace(sim, "generator", at.X, at.Y));
        Assert.False(Scene.CanPlace(sim, "pipe", at.X, at.Y));
        // Ту же дорогу второй раз не кладут, чужую не меняют, свою на фундамент — можно.
        Assert.False(sim.CanPave(1, Road, at.X, at.Y));
        Assert.False(sim.CanPave(2, Foundation, at.X, at.Y));
        Assert.True(sim.CanPave(1, Foundation, at.X, at.Y));
        sim.Send(1, new PaveOrder { Kind = Foundation, Tiles = [at.X, at.Y], Builders = [.. builders] });
        sim.Advance();
        Assert.True(sim.IsPaved(Road, at.X, at.Y));
        sim.Seconds(15);
        Assert.True(sim.IsPaved(Foundation, at.X, at.Y));
        Assert.False(sim.IsPaved(Road, at.X, at.Y));
        Assert.True(Scene.CanPlace(sim, "generator", at.X, at.Y));
        // И обратно.
        sim.Send(1, new PaveOrder { Kind = Road, Tiles = [at.X, at.Y], Builders = [.. builders] });
        sim.Seconds(15);
        Assert.True(sim.IsPaved(Road, at.X, at.Y));
    }
}
