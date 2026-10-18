using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using KHost = Kharos.Host.Host;
using KHostEngine = Kharos.Host.EngineTools;

namespace Kharos.Base.Tests;

/// <summary>Состав мода, начало партии skirmish, вход игроков, месторождения (games/skirmish, mechanics/deposits, players).</summary>
public class SetupTests
{
    [Fact]
    public void ContentHasPilotKindsAndIgnoresStageTables()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        var content = BaseGame.Content();
        foreach (var path in new[]
        {
            "units/buggy", "turrets/gunner", "buildings/factory", "games/skirmish", "lands/desert",
            "deposits/fuel", "deposits/kharite", "deposits/lithium", "deposits/metal", "deposits/silicon", "deposits/uranium",
            "mechanics/crowd", "mechanics/orders", "mechanics/players", "mechanics/production", "moves/wheels",
        })
            Assert.NotNull(content.FindProto(path));
        // Таблицы TS лежат в data/tables/ и движком не читаются (до Н5): видов из них нет.
        Assert.Null(content.FindProto("units/mcv"));
        Assert.Null(content.FindProto("research/solar"));
        Assert.Equal("skirmish", content.DefaultGame);
        var game = content.Game();
        Assert.Equal((-128, -128, 128, 128), game.Bounds);
        Assert.True(game.Fog);
        Assert.Equal("desert", game.LandName);
        Assert.Equal(1337, game.Land!.Seed);
    }

    [Fact]
    public void SchemaDeclaresPilotComponentsAndSystemsKeepPilotNames()
    {
        using var host = BaseGame.Create();
        var schema = host.World.Schema;
        string Fields(string component) => string.Join(",", schema.Get(component).Fields.Select(f => $"{f.Name}:{f.Kind}"));
        Assert.Equal("move:String,speed:Number,turn:Number,tx:Number,ty:Number,moving:Flag", Fields("Drive"));
        Assert.Equal("unit:String,progress:Number,queue:Bytes,rally:Flag,rallyX:Number,rallyY:Number", Fields("Producer"));
        Assert.Equal("parent:Number,along:Number,across:Number", Fields("Attached"));
        Assert.Equal("angle:Number", Fields("Turret"));
        Assert.Equal("id:Number,credits:Number", Fields("Player"));
        Assert.Equal("radius:Number", Fields("Unit"));
        Assert.Equal("width:Number,height:Number", Fields("Building"));
        Assert.Equal("reserve:Number,total:Number", Fields("Deposit"));
        // Порядок как у пилота: вид по имени раздела, after/before сдвигают ходовую вперёд своих последователей.
        Assert.Equal(
            ["mechanics/players.income", "mechanics/production.produce", "moves/wheels.drive", "mechanics/crowd.separate", "mechanics/mounts.follow", KHost.VisionSystem],
            host.Scheduler.Names);
    }

    [Fact]
    public void JoinSpawnsFactoryAndThreeBuggiesWithGunnersAndWallet()
    {
        using var host = BaseGame.Started(1);
        var world = host.World;
        var buggy = host.Content.FindProto("units/buggy")!.Id;
        var gunner = host.Content.FindProto("turrets/gunner")!.Id;
        var factoryProto = host.Content.FindProto("buildings/factory")!.Id;

        var factory = BaseGame.Factory(host, 1);
        Assert.Equal((0.0, 0.0), BaseGame.At(host, factory));
        Assert.Equal(factoryProto, BaseGame.Num(host, factory, "Proto", "id"));
        Assert.Equal(5.5, BaseGame.Num(host, factory, "Sight", "radius"));
        Assert.Equal((3.0, 3.0), (BaseGame.Num(host, factory, "Building", "width"), BaseGame.Num(host, factory, "Building", "height")));
        Assert.Equal("", BaseGame.Str(host, factory, "Producer", "unit"));
        Assert.Empty(BaseGame.Bytes(host, factory, "Producer", "queue"));

        var units = BaseGame.Units(host, 1);
        Assert.Equal(3, units.Count);
        for (var k = 0; k < 3; k++)
        {
            var unit = units[k];
            // x − 2 + номер × 1,4 по порядку, на 4 тайла выше дома; смотрят вверх (π/2).
            var (x, y) = BaseGame.At(host, unit);
            Assert.Equal(-2 + (k + 1) * 1.4, x);
            Assert.Equal(4.0, y);
            Assert.Equal(Math.PI / 2, BaseGame.Angle(host, unit));
            Assert.Equal(buggy, BaseGame.Num(host, unit, "Proto", "id"));
            Assert.Equal("wheels", BaseGame.Str(host, unit, "Drive", "move"));
            Assert.Equal((6.0, 5.0, 0.0), (BaseGame.Num(host, unit, "Drive", "speed"), BaseGame.Num(host, unit, "Drive", "turn"), BaseGame.Num(host, unit, "Drive", "moving")));
            Assert.Equal(0.45, BaseGame.Num(host, unit, "Unit", "radius"));
            Assert.Equal(11, BaseGame.Num(host, unit, "Sight", "radius"));
            Assert.Equal(1, BaseGame.Num(host, unit, "Owner", "player"));
        }

        // У каждой багги — турель-стрелок: Attached на носителе, место и угол носителя.
        var turrets = BaseGame.Turrets(host);
        Assert.Equal(3, turrets.Count);
        foreach (var unit in units)
        {
            var turret = Assert.Single(turrets, t => BaseGame.Num(host, t, "Attached", "parent") == unit);
            Assert.Equal(gunner, BaseGame.Num(host, turret, "Proto", "id"));
            Assert.Equal(-0.16, BaseGame.Num(host, turret, "Attached", "along"));
            Assert.Equal(0, BaseGame.Num(host, turret, "Attached", "across"));
            Assert.Equal(1, BaseGame.Num(host, turret, "Owner", "player"));
            Assert.Equal(BaseGame.At(host, unit), BaseGame.At(host, turret));
            Assert.Equal(BaseGame.Angle(host, unit), BaseGame.Angle(host, turret));
        }

        Assert.Equal(1000, BaseGame.Credits(host, 1));
        Assert.Single(BaseGame.With(host, "Producer"));
        Assert.Empty(host.Errors);
        Assert.True(world.Alive(factory));
    }

    [Fact]
    public void PlayersGetOwnPlacesOnTheCircleAndOwnWallets()
    {
        using var host = BaseGame.Started(1, 2, 3);
        Assert.False(host.Join(2));
        var radius = 24.0;
        var second = (Math.Cos((2 - 2) * Math.PI * 2 / 6) * radius, Math.Sin((2 - 2) * Math.PI * 2 / 6) * radius);
        var third = (Math.Cos((3 - 2) * Math.PI * 2 / 6) * radius, Math.Sin((3 - 2) * Math.PI * 2 / 6) * radius);
        Assert.Equal((0.0, 0.0), BaseGame.At(host, BaseGame.Factory(host, 1)));
        Assert.Equal(second, BaseGame.At(host, BaseGame.Factory(host, 2)));
        Assert.Equal(third, BaseGame.At(host, BaseGame.Factory(host, 3)));
        foreach (var player in new uint[] { 1, 2, 3 })
        {
            Assert.Equal(3, BaseGame.Units(host, player).Count);
            Assert.Equal(1000, BaseGame.Credits(host, player));
        }
        // Первый юнит второго — x − 2 + 1,4 от его дома, на 4 тайла вперёд.
        var first = BaseGame.Units(host, 2)[0];
        Assert.Equal((second.Item1 - 2 + 1.4, second.Item2 + 4), BaseGame.At(host, first));
        Assert.Equal(3, BaseGame.With(host, "Player").Count);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void IncomeAddsFiveCreditsEverySecond()
    {
        using var host = BaseGame.Started(1);
        BaseGame.Run(host, 19);
        Assert.Equal(1000, BaseGame.Credits(host, 1));
        BaseGame.Run(host, 1);
        Assert.Equal(1005, BaseGame.Credits(host, 1), 9);
        BaseGame.Run(host, 40);
        Assert.Equal(1015, BaseGame.Credits(host, 1), 9);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void DepositsAreLaidOutAsTheGeneratorPlacesThem()
    {
        using var host = BaseGame.Create();
        host.Start();
        var deposits = BaseGame.With(host, "Deposit");
        Assert.Equal(6, host.Content.Protos.Count(p => p.Section == "deposits"));
        Assert.InRange(deposits.Count, 5, 64);
        Assert.Empty(host.Errors);

        // Эталон — раскладка из lib/deposits.luau, написанная заново на Hash и Land движка.
        var land = host.Land!;
        var expected = Oracle(host, land);
        var actual = deposits
            .Select(e =>
            {
                var proto = host.Content.Protos[(int)BaseGame.Num(host, e, "Proto", "id")];
                Assert.Equal("deposits", proto.Section);
                var (x, y) = BaseGame.At(host, e);
                Assert.Equal(BaseGame.Num(host, e, "Deposit", "reserve"), BaseGame.Num(host, e, "Deposit", "total"));
                return (proto.Name, x, y, BaseGame.Num(host, e, "Deposit", "reserve"));
            })
            .OrderBy(d => d.y).ThenBy(d => d.x).ToList();
        Assert.Equal(expected.OrderBy(d => d.Y).ThenBy(d => d.X).Select(d => (d.Kind, d.X + 1.0, d.Y + 1.0, d.Reserve)), actual);

        // Свойства раскладки: целые тайлы 2 × 2 на скале не у обрыва, внутри поля, запас в пределах вида.
        var ranges = new Dictionary<string, (double Min, double Max)>
        {
            ["fuel"] = (800, 1600), ["kharite"] = (200, 400), ["lithium"] = (400, 800),
            ["metal"] = (1200, 2500), ["silicon"] = (1200, 2500), ["uranium"] = (300, 600),
        };
        foreach (var (kind, cx, cy, reserve) in actual)
        {
            Assert.InRange(cx - 1, -128, 126);
            Assert.InRange(cy - 1, -128, 126);
            Assert.Equal(Math.Floor(cx), cx);
            for (var ty = (int)cy - 1; ty <= (int)cy; ty++)
                for (var tx = (int)cx - 1; tx <= (int)cx; tx++)
                {
                    var (terrain, _, foot) = KHostEngine.Terrain(land, tx, ty);
                    Assert.Equal(2, terrain);
                    Assert.False(foot);
                }
            Assert.InRange(reserve, ranges[kind].Min, ranges[kind].Max);
            Assert.Equal(Math.Round(reserve), reserve);
        }
        // Не больше одного в клетке 40 × 40.
        Assert.Equal(actual.Count, actual.Select(d => (Math.Floor((d.x - 1) / 40), Math.Floor((d.y - 1) / 40))).Distinct().Count());
        Assert.True(actual.Select(d => d.Name).Distinct().Count() >= 2, "ожидались разные виды");
    }

    /// <summary>Раскладка месторождений заново: hash движка, местность Land, виды и запасы по данным.</summary>
    private static List<(string Kind, int X, int Y, double Reserve)> Oracle(KHost host, Land land)
    {
        var data = host.Content.Data("deposits")!.Value;
        var order = new[] { "metal", "silicon", "fuel", "kharite", "lithium", "uranium" };
        var biomes = new[] { "erg", "saltFlats", "redWastes", "marsh" };
        var seed = 1337 + 17011;
        var result = new List<(string, int, int, double)>();
        for (var cy = -4; cy <= 3; cy++)
            for (var cx = -4; cx <= 3; cx++)
            {
                double Random(int k) => TerrainGenerator.Hash(cx, cy, seed + k);
                if (Random(0) >= 0.95)
                    continue;
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    var x = cx * 40 + (int)Math.Floor(Random(1 + attempt * 2) * 38);
                    var y = cy * 40 + (int)Math.Floor(Random(2 + attempt * 2) * 38);
                    if (x < -128 || y < -128 || x + 2 > 128 || y + 2 > 128)
                        continue;
                    var fits = true;
                    for (var ty = y; ty < y + 2; ty++)
                        for (var tx = x; tx < x + 2; tx++)
                        {
                            var (terrain, _, foot) = KHostEngine.Terrain(land, tx, ty);
                            fits &= terrain == 2 && !foot;
                        }
                    if (!fits)
                        continue;
                    var biome = KHostEngine.Terrain(land, x, y).Biome;
                    var pick = Random(53);
                    var kind = "metal";
                    foreach (var candidate in order)
                    {
                        pick -= data.GetProperty(candidate).GetProperty("biomes").GetProperty(biomes[biome]).GetDouble();
                        if (pick < 0)
                        {
                            kind = candidate;
                            break;
                        }
                    }
                    var spec = data.GetProperty(kind);
                    var min = spec.GetProperty("min").GetDouble();
                    var max = spec.GetProperty("max").GetDouble();
                    result.Add((kind, x, y, Math.Round(min + Random(97) * (max - min), MidpointRounding.AwayFromZero)));
                    break;
                }
            }
        return result;
    }
}
