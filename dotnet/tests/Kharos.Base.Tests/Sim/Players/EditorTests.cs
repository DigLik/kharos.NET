using System.Text.Json.Nodes;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;

namespace Kharos.Base.Tests.Players;

/// <summary>
/// Редактор карты (packages/tests/editor.test.ts): правки edit.* с правом редактора — земля, здания, юниты, месторождения,
/// деньги, камера. Что в TS — выбор мышью в редакторе (entityAt, entitiesIn, depositUnder), — клиент редактора (Н6):
/// здесь то же проверяется по миру; призрак юнита — Client/Editor/EditorPanelTests.
/// </summary>
public sealed class EditorTests
{
    private static readonly Brush RockBrush = new() { Terrain = (int)TerrainType.Rock, Biome = 0, Tier = 1, Cliff = false, Shape = "square" };

    /// <summary>Мир 256² без игроков: скала с 1-м ярусом на −30..29 по обеим осям (world).</summary>
    private static Sim World(object? land = null, int size = 128)
    {
        Scene.SkipUnlessAvailable();
        var sim = Sim.Create(new SimOptions { Size = size, Land = land ?? Scene.Land, Players = [] });
        sim.Paint(0, 0, 60, RockBrush);
        return sim;
    }

    /// <summary>Новый игрок редактора (addPlayer): его номер.</summary>
    private static uint AddPlayer(Sim sim)
    {
        sim.AddPlayer();
        return Players(sim).Last();
    }

    private static uint[] Players(Sim sim) => sim.Query<PlayerList>(PlayersQueries.All).Items;

    /// <summary>Правка вышла: мод не ответил отказом.</summary>
    private static bool Done(Sim sim, Action edit)
    {
        var before = sim.Signals.Count;
        edit();
        return !sim.Signals.Skip(before).Any(signal => signal.Name == RefusedSignal.Name);
    }

    /// <summary>
    /// Месторождение ячейки (0, 0) (depositIn(sim, 0, 0)). В TS месторождения ячейки появляются при первом обращении — уже
    /// на скале, накрашенной world(); у .NET генератор кладёт их в начале партии, и на этом зерне ячейка может быть пуста:
    /// тогда месторождение металла кладёт правка.
    /// </summary>
    private static DepositSpot Origin(Sim sim)
    {
        if (sim.DepositIn(0, 0) is { } spot)
            return spot;
        Assert.True(PutDeposit(sim, 20, 20, "metal", 1200));
        return sim.DepositIn(0, 0)!;
    }

    private static bool PutDeposit(Sim sim, int x, int y, string kind, double reserve) => Done(sim, () => sim.PutDeposit(kind, x, y, reserve));

    private static uint? TryBuilding(Sim sim, string type, int x, int y, uint player)
    {
        try
        {
            return sim.PutBuilding(type, x, y, player);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static uint? TryUnit(Sim sim, string type, int x, int y, uint player)
    {
        try
        {
            return sim.PutUnit(type, player, x + 0.5, y + 0.5);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [Fact(DisplayName = "кисть красит карту, и правка — в сохранении для всех, без памяти тумана")]
    public void BrushPaintsAndSaves()
    {
        using var sim = World();
        sim.Paint(0, 0, 3, new Brush { Terrain = (int)TerrainType.Swamp });
        Assert.Equal(TerrainType.Swamp, sim.Land!.TerrainAt(-1, -1));
        Assert.Equal(TerrainType.Swamp, sim.Land.TerrainAt(1, 1));
        Assert.Equal(TerrainType.Rock, sim.Land.TerrainAt(2, 2));
        using var loaded = sim.ThroughSave();
        Assert.Equal(TerrainType.Swamp, loaded.Land!.TerrainAt(0, 0));
    }

    [Fact(DisplayName = "здание и юнит ставятся по правилам места, без денег и строителей")]
    public void PutByPlacementRules()
    {
        using var sim = World();
        var player = AddPlayer(sim);
        Assert.Equal([player], Players(sim));
        var generator = TryBuilding(sim, "generator", 0, 0, player);
        Assert.NotNull(generator);
        Assert.Null(TryBuilding(sim, "generator", 0, 0, player));
        Assert.Null(TryUnit(sim, "tank", 0, 0, player));
        var tank = TryUnit(sim, "tank", 10, 10, player)!.Value;
        Assert.Equal(generator, Scene.Occupant(sim, 0, 0));
        Assert.Equal((10.5, 10.5), sim.At(tank));
        Assert.Equal(0, sim.Credits(player));
        sim.Move(2, 2, tank);
        Assert.Equal((12.5, 12.5), sim.At(tank));
    }

    [Fact(DisplayName = "деньги, прочность, склад и владелец правятся и переживают сохранение")]
    public void EditsSurviveSave()
    {
        using var sim = World();
        var player = AddPlayer(sim);
        var other = AddPlayer(sim);
        sim.SetCredits(player, 5000);
        var store = sim.PutBuilding("metalYard", 0, 0, player);
        sim.SetStock(store, "metal", 77);
        sim.SetHealth(0.4, store);
        var carrier = sim.PutUnit("carrier", player, 10.5, 10.5);
        sim.SetOwner(other, store);
        using (var loaded = sim.ThroughSave())
        {
            Assert.Equal(5000, loaded.Credits(player));
            Assert.Equal(77, loaded.Stock(store, "metal"));
            Assert.Equal(0.4, loaded.Num(store, "Health.value") / loaded.Num(store, "Health.max"), 6);
            Assert.Equal(other, loaded.OwnerOf(store));
        }
        sim.Erase(carrier);
        Assert.Equal(0, sim.Count(Names.Mount.Name));
        Assert.Equal(0, sim.Count(Names.Unit.Name));
    }

    [Fact(DisplayName = "месторождения: запас и вид правятся, новые ложатся сколько угодно рядом с прежними, убранного нет — всё в сохранении")]
    public void DepositEdits()
    {
        using var sim = World();
        var spot = Origin(sim);
        sim.SetDeposit([spot.X, spot.Y], "kharite", 50);
        Assert.Equal("kharite", sim.DepositAt(spot.X, spot.Y)!.Kind);
        Assert.Equal(50, sim.ReserveLeft(spot.X, spot.Y));
        using (var loaded = sim.ThroughSave())
            Assert.Equal(50, loaded.ReserveLeft(spot.X, spot.Y));

        // Много новых в одной клетке, прежнее — на месте. Внахлёст — нельзя.
        var free = new List<(int X, int Y)>();
        for (var y = 0; y < 38 && free.Count < 6; y += 3)
            for (var x = 0; x < 38 && free.Count < 6; x += 3)
                if (Math.Abs(x - spot.X) >= 3 || Math.Abs(y - spot.Y) >= 3)
                    free.Add((x, y));
        foreach (var (x, y) in free)
            Assert.True(PutDeposit(sim, x, y, "fuel", 900), $"месторождение в ({x}, {y})");
        Assert.False(PutDeposit(sim, free[0].X + 1, free[0].Y, "fuel", 900));
        Assert.Equal(free.Count + 1, sim.DepositsIn(0, 0).Count);
        Assert.Equal("kharite", sim.DepositAt(spot.X, spot.Y)!.Kind);
        using (var loaded = sim.ThroughSave())
            Assert.Equal(free.Count + 1, loaded.DepositsIn(0, 0).Count);
        // Добытое из нового — свой счёт.
        Assert.Equal((free[2].X, free[2].Y), (sim.DepositNear(free[2].X + 1, free[2].Y + 1, 0.5)!.X, sim.DepositNear(free[2].X + 1, free[2].Y + 1, 0.5)!.Y));

        sim.RemoveDeposit(free[0].X, free[0].Y);
        sim.RemoveDeposit(spot.X, spot.Y);
        Assert.Null(sim.DepositAt(spot.X, spot.Y));
        using (var loaded = sim.ThroughSave())
            Assert.Equal(free.Count - 1, loaded.DepositsIn(0, 0).Count);
        // На песок не кладётся.
        sim.Paint(30, 30, 3, new Brush { Terrain = (int)TerrainType.Sand });
        Assert.False(PutDeposit(sim, 29, 29, "metal", 100));
    }

    [Fact(DisplayName = "юниты и здания тащатся вместе: всё или ничего, друг другу не мешают")]
    public void GroupMovesTogether()
    {
        using var sim = World();
        var player = AddPlayer(sim);
        var generator = sim.PutBuilding("generator", 0, 0, player);
        var tank = sim.PutUnit("tank", player, 4.5, 0.5);
        // Здание заезжает на место, откуда ушло само.
        Assert.True(Done(sim, () => sim.Move(1, 0, generator, tank)));
        Assert.Equal((1.0, 0.0), sim.At(generator));
        Assert.Equal(generator, Scene.Occupant(sim, 1, 0));
        Assert.Equal(0u, Scene.Occupant(sim, 0, 0));
        Assert.Equal((5.5, 0.5), sim.At(tank));
        // Танку некуда — не едет никто.
        sim.Paint(15, 0, 1, new Brush { Terrain = (int)TerrainType.Rock, Tier = 3, Cliff = true });
        sim.PutBuilding("generator", 10, 0, player);
        Assert.True(Done(sim, () => sim.Move(0, 0, generator, tank)));
        Assert.False(Done(sim, () => sim.Move(5, 0, generator, tank)));
        Assert.Equal((1.0, 0.0), sim.At(generator));
        Assert.Equal(generator, Scene.Occupant(sim, 1, 0));
    }

    /// <summary>Сдвинуть месторождение с левым верхним тайлом (x, y) в (toX, toY) (moveDeposit): вышло ли.</summary>
    private static bool MoveDeposit(Sim sim, int x, int y, int toX, int toY) =>
        Done(sim, () => sim.Edit(new EditMoveOrder { Entities = [], Dx = toX - x, Dy = toY - y, Deposits = [x, y] }));

    [Fact(DisplayName = "месторождение тащится с видом и остатком, но не внахлёст с другим")]
    public void DepositDrags()
    {
        using var sim = World();
        var spot = Origin(sim);
        sim.SetDeposit([spot.X, spot.Y], "fuel", 300);
        var x = spot.X < 10 ? 20 : 2;
        Assert.True(PutDeposit(sim, x + 6, 2, "metal", 10));
        Assert.True(MoveDeposit(sim, spot.X, spot.Y, x, 2));
        Assert.Equal("fuel", sim.DepositAt(x, 2)!.Kind);
        Assert.Equal(300, sim.ReserveLeft(x, 2));
        Assert.Null(sim.DepositAt(spot.X, spot.Y));
        // Сдвиг на тайл — внахлёст с самим собой — можно.
        Assert.True(MoveDeposit(sim, x, 2, x + 1, 2));
        Assert.NotNull(sim.DepositAt(x + 1, 2));
        Assert.False(MoveDeposit(sim, x + 1, 2, x + 5, 2));
    }

    /// <summary>Куда смотрят турели сущности, по миру (turretFacings): корпус и поворот турели.</summary>
    private static double[] TurretFacings(Sim sim, uint entity)
    {
        var hull = sim.Has(entity, Names.Unit.Name) ? sim.Num(entity, "Facing.angle") : 0;
        return [.. sim.TurretsOf(entity).Select(turret => Wrap(hull + sim.Num(turret, "Mount.angle")))];
    }

    private static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor(angle / (Math.PI * 2) + 0.5);

    [Fact(DisplayName = "месторождения тащатся вместе с юнитами; поворот корпуса и турелей; задания снимаются и выдаются сразу")]
    public void DepositsDragWithUnitsTurretsAndOrders()
    {
        using var sim = World();
        var player = AddPlayer(sim);
        var tank = sim.PutUnit("tank", player, 10.5, 10.5);
        var spot = Origin(sim);
        Assert.True(Done(sim, () => sim.Edit(new EditMoveOrder { Entities = [tank], Dx = 3, Dy = 0, Deposits = [spot.X, spot.Y] })));
        Assert.Equal(spot.Kind, sim.DepositAt(spot.X + 3, spot.Y)!.Kind);
        Assert.Equal(13.5, sim.At(tank).X);

        sim.SetFacing(Math.PI, tank);
        Assert.Equal(Math.PI, Math.Abs(sim.Num(tank, "Facing.angle")), 6);
        sim.SetTurret(0.5, null, tank);
        Assert.Equal(0.5, TurretFacings(sim, tank)[0], 6);
        // У носителя турели поворачиваются по одной.
        var carrier = sim.PutUnit("carrier", player, 20.5, 20.5);
        var before = TurretFacings(sim, carrier);
        Assert.True(before.Length > 1);
        sim.SetTurret(1, 1, carrier);
        var after = TurretFacings(sim, carrier);
        Assert.Equal(1, after[1], 6);
        Assert.Equal(before[0], after[0], 6);
        // У MCV — пушка на крыше: поворачивается, но не стреляет.
        var mcv = sim.PutUnit("mcv", player, 25.5, 25.5);
        Assert.Single(TurretFacings(sim, mcv));
        Assert.False(sim.CanFight(mcv));

        Assert.True(sim.Now(player, new MoveOrder { Units = [tank], X = 20, Y = 10 }));
        Assert.True(sim.Has(tank, Names.Path.Name));
        sim.ClearTasks(tank);
        Assert.False(sim.Has(tank, Names.Path.Name));
    }

    [Fact(DisplayName = "кисть ставит гору одной вершиной и стирает задетую гору целиком; барханы — только на песке и в сохранении")]
    public void MountainsAndDunes()
    {
        using var sim = World();
        var land = sim.Land!;
        sim.Paint(0, 0, 5, new Brush { Terrain = (int)TerrainType.Mountain });
        Assert.Equal((0.5, 0.5), (land.PeakOf(0, 0)!.Value.X, land.PeakOf(0, 0)!.Value.Y));
        Assert.Equal(TerrainType.Mountain, land.TerrainAt(2, 0));
        Assert.Equal(TerrainType.Rock, land.TerrainAt(4, 0));
        // Задели краем — стёрта вся.
        sim.Paint(2, 0, 1, new Brush { Terrain = (int)TerrainType.Sand });
        Assert.Equal(TerrainType.Sand, land.TerrainAt(2, 0));
        for (var y = -3; y <= 3; y++)
            for (var x = -3; x <= 3; x++)
                Assert.NotEqual(TerrainType.Mountain, land.TerrainAt(x, y));
        // Гора к горе — гряда: тайлы на стыке у ближней вершины, обе остаются.
        sim.Paint(10, 10, 5, new Brush { Terrain = (int)TerrainType.Mountain });
        sim.Paint(13, 10, 5, new Brush { Terrain = (int)TerrainType.Mountain });
        Assert.Equal((10.5, 10.5), (land.PeakOf(10, 10)!.Value.X, land.PeakOf(10, 10)!.Value.Y));
        Assert.Equal((13.5, 10.5), (land.PeakOf(13, 10)!.Value.X, land.PeakOf(13, 10)!.Value.Y));
        Assert.Equal((13.5, 10.5), (land.PeakOf(12, 10)!.Value.X, land.PeakOf(12, 10)!.Value.Y));

        sim.Paint(20, 20, 3, new Brush { Terrain = (int)TerrainType.Sand, Dunes = 2 });
        sim.Paint(25, 20, 3, new Brush { Terrain = (int)TerrainType.Rock, Dunes = 2 });
        static int Dunes(Land land, int x, int y) => (land.Stored(x, y).Relief >> TileFormat.DuneShift) & 3;
        Assert.Equal(2, Dunes(land, 20, 20));
        Assert.Equal(0, Dunes(land, 25, 20));
        using var loaded = sim.ThroughSave();
        Assert.Equal(2, Dunes(loaded.Land!, 20, 20));
        Assert.Equal((13.5, 10.5), (loaded.Land!.PeakOf(12, 10)!.Value.X, loaded.Land.PeakOf(12, 10)!.Value.Y));
    }

    /// <summary>Сколько тайлов квадрата 9×9 вокруг (x, y) кисть сделала песком.</summary>
    private static int Sand(Sim sim, int x, int y)
    {
        var count = 0;
        for (var ty = y - 4; ty <= y + 4; ty++)
            for (var tx = x - 4; tx <= x + 4; tx++)
                if (sim.Land!.TerrainAt(tx, ty) == TerrainType.Sand)
                    count++;
        return count;
    }

    [Fact(DisplayName = "формы кисти: круг меньше квадрата, разброс — часть круга; пакетная правка считает подножие обрыва")]
    public void BrushShapes()
    {
        using var sim = World();
        // brushTiles — внутри правки edit.paint: формы видно по закрашенному.
        sim.Paint(-15, -15, 7, new Brush { Terrain = (int)TerrainType.Sand, Shape = "square" });
        sim.Paint(0, -15, 7, new Brush { Terrain = (int)TerrainType.Sand, Shape = "circle" });
        sim.Paint(15, -15, 7, new Brush { Terrain = (int)TerrainType.Sand, Shape = "spray" });
        Assert.Equal(49, Sand(sim, -15, -15));
        var circle = Sand(sim, 0, -15);
        Assert.True(circle < 49);
        Assert.True(Sand(sim, 15, -15) < circle);
        sim.Paint(5, 5, 5, new Brush { Terrain = (int)TerrainType.Rock, Tier = 2, Cliff = true });
        Assert.True(sim.Land!.IsCliffFoot(5.5, 8.5));
        Assert.False(sim.Land.IsCliffFoot(5.5, 5.5));
    }

    /// <summary>Местность default из lands.json с правкой полей (как <c>{ ...DEFAULT_CONFIG, … }</c>).</summary>
    private static JsonObject Land(Action<JsonObject> change)
    {
        var lands = JsonNode.Parse(Sim.Content().Data("lands")!.Value.GetRawText())!.AsObject();
        var land = lands[Scene.Land]!.DeepClone().AsObject();
        change(land);
        return land;
    }

    [Fact(DisplayName = "прямоугольная карта, чистая пустыня, богатство месторождений и кисть биома")]
    public void BlankRichAndBiome()
    {
        // Прямоугольной карты у .NET нет (games.json: size — полусторона квадрата); остальное — как в TS.
        Scene.SkipUnlessAvailable();
        using var blank = Sim.Create(new SimOptions { Size = 64, Land = Land(land => land["blank"] = true), Players = [] });
        foreach (var (x, y) in new[] { (0, 0), (-60, 40), (50, -50) })
            Assert.Equal(TerrainType.Sand, blank.Land!.TerrainAt(x, y));
        Assert.Empty(blank.DepositsIn(0, 0));

        using (var poor = Sim.Create(new SimOptions { Size = 128, Land = Land(land => land["depositChance"] = 0), Players = [] }))
            for (var cy = -3; cy < 3; cy++)
                for (var cx = -3; cx < 3; cx++)
                    Assert.Empty(poor.DepositsIn(cx, cy));
        using (var rich = Sim.Create(new SimOptions { Size = 128, Land = Land(land => land["depositRichness"] = 3), Players = [] }))
        using (var normal = Sim.Create(new SimOptions { Size = 128, Land = Scene.Land, Players = [] }))
            for (var cy = -2; cy < 2; cy++)
                for (var cx = -2; cx < 2; cx++)
                    if (normal.DepositIn(cx, cy) is { } spot)
                        Assert.True(Math.Abs(rich.DepositIn(cx, cy)!.Reserve - spot.Reserve * 3) < 5, $"({cx}, {cy}): {rich.DepositIn(cx, cy)!.Reserve} против {spot.Reserve} × 3");

        blank.Paint(0, 0, 3, new Brush { Biome = (int)Biome.RedWastes });
        Assert.Equal(Biome.RedWastes, blank.Land!.BiomeAt(0.5, 0.5));
        using var loaded = blank.ThroughSave();
        Assert.Equal(Biome.RedWastes, loaded.Land!.BiomeAt(1.5, 1.5));
    }

    [Fact(DisplayName = "камера игрока задаётся, переживает сохранение и убирается")]
    public void PlayerCamera()
    {
        using var sim = World();
        var player = AddPlayer(sim);
        static double[]? Camera(Sim sim, uint player) =>
            sim.Bytes(sim.Wallet(player), "Player.camera") is { Length: >= 24 } bytes
                ? [BitConverter.ToDouble(bytes, 0), BitConverter.ToDouble(bytes, 8), BitConverter.ToDouble(bytes, 16)]
                : null;
        Assert.Null(Camera(sim, player));
        sim.Edit(new EditCameraOrder { Player = player, X = 12, Y = -7, Zoom = 24 });
        using (var loaded = sim.ThroughSave())
            Assert.Equal([12.0, -7.0, 24.0], Camera(loaded, player)!);
        sim.Edit(new EditCameraOrder { Player = player, Clear = true });
        Assert.Null(Camera(sim, player));
    }

    [Fact(DisplayName = "сети пересчитываются после смены владельца и переноса здания в редакторе")]
    public void NetworksFollowOwnerAndMove()
    {
        using var sim = World();
        var first = AddPlayer(sim);
        var second = AddPlayer(sim);
        var generator = sim.PutBuilding("generator", 0, 0, first);
        Assert.Contains(sim.Zones(first), zone => zone.Buildings.Contains(generator));
        sim.SetOwner(second, generator);
        Assert.DoesNotContain(sim.Zones(first), zone => zone.Buildings.Contains(generator));
        Assert.Contains(sim.Zones(second), zone => zone.Buildings.Contains(generator));
        double Circle() => sim.Zones(second).First(zone => zone.Buildings.Contains(generator)).Circles[0];
        var before = Circle();
        Assert.True(Done(sim, () => sim.Move(5, 0, generator)));
        Assert.Equal(before + 5, Circle());
    }
}
