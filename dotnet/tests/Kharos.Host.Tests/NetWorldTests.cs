using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Base.Scenario;
using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>
/// Сеть на настоящей партии base (packages/tests/net.test.ts): клиент — реплика за HostSession, приказы идут по сети
/// с правами «клиент по сети». Хост — мод base со стартовым набором (skirmish), туман и память — как в игре.
/// </summary>
public sealed class NetWorldTests
{
    [Fact(DisplayName = "клиент получает мир сразу после подключения")]
    public void ClientGetsWorldAtOnce()
    {
        using var net = BaseNet.Create();
        var peer = net.Join();
        Assert.Equal(1u, peer.Player);
        Assert.True(peer.Replica.Ready);
        var own = peer.Units(1);
        Assert.Equal(net.HostUnits(1), own);
        Assert.NotEmpty(own);
    }

    [Fact(DisplayName = "приказ клиента выполняет сервер, а клиент видит результат")]
    public void ServerExecutesClientOrder()
    {
        using var net = BaseNet.Create(rules: new { techTree = false });
        var peer = net.Join();
        // Багги: техника, обрыв ей закрыт, но узкий проход у края ей не тесен — не то что MCV.
        var unit = peer.Units(1).First(entity => net.Kind(entity) == "buggy");
        var from = net.Sim.At(unit);
        // Куда-нибудь в три тайла, куда технике можно: рядом может быть обрыв.
        var target = new[] { (3, 0), (-3, 0), (0, 3), (0, -3) }
            .Select(d => ((int)Math.Floor(from.X) + d.Item1, (int)Math.Floor(from.Y) + d.Item2))
            .First(tile => net.Sim.Walkable(tile.Item1, tile.Item2));
        peer.Order(MoveOrder.Name, MemoryPackSerializer.Serialize(new MoveOrder { Units = [unit], X = target.Item1, Y = target.Item2 }));
        // Сама копия мир не считает: без сервера юнит стоит.
        peer.Pump();
        Assert.Equal(from.X, peer.Number(unit, "Position", "x"), 2);
        Assert.Equal(from.Y, peer.Number(unit, "Position", "y"), 2);

        net.Step(40);
        Assert.Equal(net.Sim.Host.Tick, peer.Replica.Tick);
        // Место приходит округлённым (NetFormat): допуск — половина шага.
        var there = net.Sim.At(unit);
        Assert.Equal(there.X, peer.Number(unit, "Position", "x"), 2);
        Assert.Equal(there.Y, peer.Number(unit, "Position", "y"), 2);
        Assert.True(Math.Sqrt((there.X - from.X) * (there.X - from.X) + (there.Y - from.Y) * (there.Y - from.Y)) > 1);
        Assert.Empty(net.Sim.Log);
    }

    [Fact(DisplayName = "чужими юнитами клиент не командует")]
    public void ClientDoesNotCommandForeignUnits()
    {
        using var net = BaseNet.Create(rules: new { techTree = false });
        var first = net.Join();
        var second = net.Join();
        Assert.Equal(2u, second.Player);
        // Юнитов первого игрока второй не видит — они далеко, — но номер юнита можно и угадать.
        Assert.Empty(second.Units(1));
        var unit = first.Units(1).First(entity => net.Kind(entity) == "buggy");
        var from = net.Sim.At(unit);
        second.Order(MoveOrder.Name, MemoryPackSerializer.Serialize(new MoveOrder { Units = [unit], X = (int)Math.Floor(from.X) + 3, Y = (int)Math.Floor(from.Y) }));
        second.Order(PointOrder.Name, MemoryPackSerializer.Serialize(new PointOrder { Units = [unit], X = from.X + 3, Y = from.Y }));
        net.Step(20);
        var after = net.Sim.At(unit);
        Assert.Equal(from.X, after.X, 6);
        Assert.Equal(from.Y, after.Y, 6);
        Assert.Equal(from.X, first.Number(unit, "Position", "x"), 2);
    }

    [Fact(DisplayName = "вернувшийся игрок получает прежние юниты, а мусор из сети сервер не роняет")]
    public void ReturningPlayerKeepsUnitsAndGarbageDoesNotCrashServer()
    {
        using var net = BaseNet.Create();
        var first = net.Join();
        net.Step();
        // Мусор вместо сообщений: пустое, неизвестный тип, обрывки приказа, лишние байты, приказ без смысла.
        first.End.Send([]);
        first.End.Send([0xFF]);
        first.End.Send([(byte)MessageType.Order]);
        first.End.Send([(byte)MessageType.Order, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);
        first.End.Send([(byte)MessageType.Respawn, 1, 2, 3]);
        first.End.Send([(byte)MessageType.View, 0, 0]);
        first.End.Send(DeltaCodec.Order(MoveOrder.Name, [1, 2, 3]));
        first.End.Send(DeltaCodec.Order(MoveOrder.Name, MemoryPackSerializer.Serialize(new MoveOrder { Units = [uint.MaxValue, 0, 7], X = int.MinValue, Y = int.MaxValue })));
        first.End.Send(DeltaCodec.Order("нет такого приказа", []));
        net.Step(2);
        Assert.Single(net.Session.Connections);
        var entities = new List<uint>();
        net.Sim.World.Entities(entities);
        var size = entities.Count;
        var token = first.Token;
        var units = first.Units(1);

        net.Leave(first);
        Assert.DoesNotContain(1u, net.Sim.Host.OnlinePlayers);
        var again = net.Join(token);
        Assert.Equal(1u, again.Player);
        Assert.Equal(token, again.Token);
        // Счёт приходит и вернувшемуся: кредиты на полосе — из копии мира, а не с хоста.
        Assert.Equal(net.Sim.Credits(1), again.Credits(1));
        Assert.True(again.Credits(1) > 0);
        Assert.Equal(units, again.Units(1));
        entities.Clear();
        net.Sim.World.Entities(entities);
        Assert.Equal(size, entities.Count);
        // Чужой токен хост не знает: это новый игрок со своим токеном.
        var stranger = net.Join("выдуманный");
        Assert.Equal(2u, stranger.Player);
        Assert.NotEqual("выдуманный", stranger.Token);
    }

    [Fact(DisplayName = "сдавшийся на сервере начинает заново: новый стартовый набор, а до поражения — нельзя")]
    public void SurrenderedPlayerStartsOverButNotBeforeDefeat()
    {
        using var net = BaseNet.Create(rules: new { techTree = false });
        var peer = net.Join();
        net.Step();
        var before = net.HostUnits(1).Count;
        var home = net.Sim.At(net.HostUnits(1)[0]);
        // Не проигравший заново не начинает.
        peer.End.Send(Messages.Respawn());
        net.Step();
        Assert.Equal(before, net.HostUnits(1).Count);
        peer.Order(SurrenderOrder.Name, MemoryPackSerializer.Serialize(new SurrenderOrder()));
        net.Step(160);
        Assert.Empty(net.HostUnits(1));
        Assert.Empty(peer.Units(1));
        peer.End.Send(Messages.Respawn());
        net.Step();
        Assert.Equal(before, net.HostUnits(1).Count);
        Assert.Equal(1u, peer.Player);
        net.Step();
        // Клиент получил новый набор по дельтам: юниты нового набора у него есть, а туман — как в новом мире.
        Assert.Equal(net.HostUnits(1), peer.Units(1));
        Assert.Equal(net.Sim.Credits(1), peer.Credits(1));
        Assert.True(peer.Credits(1) > 0);
        var fresh = net.Sim.At(net.HostUnits(1)[0]);
        if (Math.Sqrt((fresh.X - home.X) * (fresh.X - home.X) + (fresh.Y - home.Y) * (fresh.Y - home.Y)) > 60)
        {
            Assert.False(net.Sim.Host.Vision!.Explored(1, home.X, home.Y));
            Assert.False(peer.Replica.Explored((int)Math.Floor(home.X), (int)Math.Floor(home.Y)));
        }
        Assert.True(peer.Replica.Explored((int)Math.Floor(fresh.X), (int)Math.Floor(fresh.Y)));
    }

    [Fact(DisplayName = "цель приказа выбирает сервер по точке: атаку по номеру сущности не берёт, а точка в тумане — движение")]
    public void ServerPicksOrderTargetByPoint()
    {
        // Без защиты новичка: под ней врага не атаковать.
        using var net = BaseNet.Create(rules: new { protection = 0 });
        var first = net.Join();
        net.Join();
        var sim = net.Sim;
        var own = net.HostUnits(1)[0];
        var far = net.HostUnits(2)[0];
        var (ox, oy) = sim.At(own);
        var gunner = sim.PutUnit("infantry", 1, ox, oy);
        net.Step();
        var (fx, fy) = sim.At(far);
        Assert.False(sim.Host.Vision!.Sees(1, fx, fy));
        // По номеру врага, которого игрок не видит, приказать нельзя.
        first.Order(AttackOrder.Name, MemoryPackSerializer.Serialize(new AttackOrder { Units = [gunner], Target = far }));
        net.Step(2);
        Assert.Equal(0, sim.Num(gunner, "Armed.ordered"));
        Assert.False(sim.Has(gunner, "Path"));
        // Точка, где он стоит, в тумане: туда просто идут.
        first.Order(PointOrder.Name, MemoryPackSerializer.Serialize(new PointOrder { Units = [gunner], X = fx, Y = fy }));
        net.Step(2);
        Assert.Equal(0, sim.Num(gunner, "Armed.ordered"));
        Assert.True(sim.Has(gunner, "Path"));
        // Враг на виду: тот же приказ точкой — атака.
        var (gx, gy) = sim.At(gunner);
        var near = sim.PutUnit("truck", 2, gx + 3, gy);
        net.Step();
        var (nx, ny) = sim.At(near);
        first.Order(PointOrder.Name, MemoryPackSerializer.Serialize(new PointOrder { Units = [gunner], X = nx, Y = ny }));
        net.Step(2);
        Assert.Equal(near, sim.TargetOf(gunner));
        Assert.Equal(1, sim.Num(gunner, "Armed.ordered"));
    }

    [Fact(DisplayName = "выстрел идёт в кадре двоичной записью, а клиенту приходит как set — раньше остального о той же сущности")]
    public void ShotArrivesWholeInTheFrameThatBornIt()
    {
        // В .NET записи «выстрел» нет: Shot — обычный компонент (схема combat из base), и сущность уходит в кадре рождения
        // целиком, значения — двоичными полями NetFormat. «Раньше остального» значит: в том же сообщении, что и место.
        var schema = new Dictionary<string, byte[]>
        {
            ["combat"] = System.Text.Encoding.UTF8.GetBytes("""
                { "Shot": { "player": 0, "source": 0, "target": 0, "fromX": 0, "fromY": 0, "toX": 0, "toY": 0,
                            "age": 0, "life": 0, "blocked": false, "air": false } }
                """),
        };
        var manifest = System.Text.Encoding.UTF8.GetBytes("""{ "name": "shots", "game": "duel" }""");
        var games = System.Text.Encoding.UTF8.GetBytes("""{ "duel": { "size": 128, "fog": false } }""");
        var content = Kharos.Content.GameContent.Load([new Kharos.Content.ModPackage("shots", manifest, new Dictionary<string, byte[]> { ["games"] = games }, schema: schema)]);
        using var host = new Host(content, new HostOptions { Seed = 1 });
        host.Start();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0, Local = true });
        var peer = NetPeer.Connect(session, 1);
        session.Step();
        peer.Pump();
        var world = host.World;
        var shotId = world.Schema.Id("Shot");
        uint Fire(uint target, bool blocked, bool air)
        {
            var shot = world.Spawn();
            world.Add(shot, Builtin.Position);
            world.Add(shot, Builtin.Owner);
            world.SetNumber(shot, Builtin.Owner, 0, 3);
            world.Add(shot, shotId);
            void Set(string field, double value) => world.SetNumber(shot, shotId, world.Schema.Get("Shot").FieldIndex(field), value);
            Set("player", 3);
            Set("source", 70000);
            Set("target", target);
            Set("fromX", -12.345);
            Set("fromY", 300.5);
            Set("toX", 0.001);
            Set("toY", -511.999);
            Set("age", 4);
            Set("life", 130);
            Set("blocked", blocked ? 1 : 0);
            Set("air", air ? 1 : 0);
            return shot;
        }
        var first = Fire(0, true, false);
        var second = Fire(5, false, true);
        session.Step();
        peer.Pump();
        foreach (var (shot, target, blocked, air) in new[] { (first, 0u, true, false), (second, 5u, false, true) })
        {
            Assert.True(peer.Has(shot, "Shot"));
            Assert.True(peer.Has(shot, "Position"));
            Assert.Equal(3, peer.Number(shot, "Shot", "player"));
            Assert.Equal(70000, peer.Number(shot, "Shot", "source"));
            Assert.Equal(target, peer.Number(shot, "Shot", "target"));
            Assert.Equal(-12.345, peer.Number(shot, "Shot", "fromX"), 2);
            Assert.Equal(300.5, peer.Number(shot, "Shot", "fromY"), 2);
            Assert.Equal(0.001, peer.Number(shot, "Shot", "toX"), 2);
            Assert.Equal(-511.999, peer.Number(shot, "Shot", "toY"), 2);
            Assert.Equal(130, peer.Number(shot, "Shot", "life"));
            Assert.Equal(blocked ? 1 : 0, peer.Number(shot, "Shot", "blocked"));
            Assert.Equal(air ? 1 : 0, peer.Number(shot, "Shot", "air"));
        }
        // Двоичная запись: пара выстрелов — в сотню-другую байт, а не в полтора килобайта JSON.
        Assert.True(peer.Connection.LastDeltaBytes < 300, $"дельта с двумя выстрелами: {peer.Connection.LastDeltaBytes} байт");
        NetAssert.Same(host, peer.Replica, peer.Connection.Filter);
    }

    [Fact(DisplayName = "притихшее сверяется реже, но изменение доходит за секунду, а тронувшийся с места юнит не плывёт к новому месту")]
    public void QuietThingsCostNothingAndChangesArriveWithinASecond()
    {
        // В .NET хост шлёт точные изменения, а не периодическую сверку: тихое не стоит ничего, а изменение приходит
        // в следующем же тике — «за секунду» выполняется с запасом.
        using var net = BaseNet.Create(rules: new { techTree = false });
        var first = net.Join();
        var second = net.Join();
        var sim = net.Sim;
        var near = sim.At(net.HostUnits(2)[0]);
        var building = sim.PutBuilding("metalYard", (int)Math.Floor(near.X) + 3, (int)Math.Floor(near.Y) + 3, 2);
        var moves = new List<PoseMove>();
        first.Applied = (replica, type) => moves.AddRange(replica.Moves);
        // Две секунды без дела: здание и стартовый набор притихли.
        net.Step(40);
        var quiet = new List<int>();
        for (var tick = 0; tick < 5; tick++)
        {
            net.Step();
            quiet.Add(second.Connection.LastDeltaBytes);
        }
        var max = sim.Num(building, "Health.max");
        // Прочность меняется без попадания: здание не «в бою».
        sim.Set(building, "Health.value", max / 2);
        var ticks = 0;
        while (Math.Abs(second.Number(building, "Health", "value") - max / 2) > 0.01 * max && ticks < 40)
        {
            net.Step();
            ticks++;
        }
        Assert.Equal(max / 2, second.Number(building, "Health", "value"), 1);
        Assert.InRange(ticks, 1, 20);
        Assert.All(quiet, bytes => Assert.True(bytes < 64, $"тихий тик стоит {bytes} байт"));

        // Юнит первого клиента стоял, теперь сдвинулся на тайл: он тронулся только что, срок движения — один тик.
        var unit = net.HostUnits(1)[0];
        moves.Clear();
        sim.Set(unit, "Position.x", sim.At(unit).X + 1);
        net.Step();
        var move = Assert.Single(moves, m => m.Entity == unit);
        Assert.Equal(1, move.Span);
    }

    [Fact(DisplayName = "готовое покрытие: своё клиент знает сразу, чужое — увидев, и помнит его в тумане, пока не увидит снова")]
    public void ReadyPavingIsKnownByOwnerAtOnceAndByOthersOnSight()
    {
        using var net = BaseNet.Create(size: 512);
        var first = net.Join();
        var second = net.Join();
        net.Step(2);
        var sim = net.Sim;
        int Laid(NetPeer peer, int x, int y) => (int)peer.Replica.Layers!.Get(peer.Replica.Layers.Id("paving"), LayerOwner.World, x, y, 0);
        void Move(IEnumerable<uint> units, double x, double y)
        {
            foreach (var unit in units)
            {
                sim.Set(unit, "Position.x", x);
                sim.Set(unit, "Position.y", y);
            }
        }
        // Дорога второго игрока в стороне от всех; войско первого — ещё дальше.
        var home = sim.At(net.HostUnits(2)[0]);
        var shift = home.X + 60 < 240 ? 60 : -60;
        var road = ((int)Math.Floor(home.X) + shift, (int)Math.Floor(home.Y));
        var away = (X: road.Item1 - 2 * shift, Y: (double)road.Item2);
        var code = PaveRules.Code("road", 2);
        Move(net.HostUnits(1), away.X, away.Y);
        sim.Lay("road", road.Item1, road.Item2, 2);
        net.Step(2);
        Assert.Equal(code, Laid(second, road.Item1, road.Item2));
        Assert.Equal(PaveRules.None, Laid(first, road.Item1, road.Item2));

        // Подошёл — увидел.
        Move(net.HostUnits(1), road.Item1 + 2, road.Item2);
        net.Step(10);
        Assert.Equal(code, Laid(first, road.Item1, road.Item2));
        // Ушёл — дорогу разобрали, а он помнит её такой, какой видел.
        Move(net.HostUnits(1), away.X, away.Y);
        net.Step(10);
        sim.Host.Layers.Set(sim.Host.Layers.Id("paving"), LayerOwner.World, road.Item1, road.Item2, 0, PaveRules.None);
        net.Step(10);
        Assert.Equal(code, Laid(first, road.Item1, road.Item2));
        Assert.Equal(PaveRules.None, Laid(second, road.Item1, road.Item2));
        // Вернулся — узнал.
        Move(net.HostUnits(1), road.Item1 + 2, road.Item2);
        net.Step(10);
        Assert.Equal(PaveRules.None, Laid(first, road.Item1, road.Item2));
    }

    [Fact(DisplayName = "новый игрок появляется там, где MCV сразу развернётся")]
    public void NewPlayerAppearsWhereMcvDeploysAtOnce()
    {
        Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is { } reason, Sim.Unavailable(Sim.DefaultMode) ?? "");
        using var sim = Sim.Create(new SimOptions { Game = "skirmish", Size = 1024, Land = "default", Players = [] });
        for (uint player = 1; player <= 30; player++)
            Assert.True(sim.Join(player));
        var mcvs = 0;
        foreach (var entity in sim.With("Unit", "Owner"))
        {
            if (sim.Host.Content.Protos[(int)sim.Num(entity, "Proto.id")].Name != "mcv")
                continue;
            mcvs++;
            var player = (uint)sim.Num(entity, "Owner.player");
            Assert.True(sim.Query<bool>(KernelQueries.CanDeploy, new DeployArgs { Player = player, Unit = entity }), $"MCV игрока {player} не развернуть");
        }
        Assert.Equal(30, mcvs);
    }
}
