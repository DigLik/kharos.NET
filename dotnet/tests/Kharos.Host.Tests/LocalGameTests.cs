using System.Diagnostics;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Одиночная игра: хост в своём потоке на 20 Гц, клиент — реплика через транспорт в памяти.</summary>
public class LocalGameTests
{
    [Fact]
    public void HostThreadServesReplicaAndStops()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        var creator = 0;
        using var game = LocalGame.Start(() =>
        {
            creator = Environment.CurrentManagedThreadId;
            return new Host(HelloGame.Content());
        });
        Assert.NotEqual(Environment.CurrentManagedThreadId, creator);
        Assert.True(game.Running);

        var replica = new Replica();
        var clock = Stopwatch.StartNew();
        while (!replica.Ready && clock.ElapsedMilliseconds < 5000)
        {
            replica.Pump(game.Client);
            Thread.Sleep(5);
        }
        Assert.True(replica.Ready);
        Assert.Equal(1u, replica.Player);
        Assert.Equal(3, HelloGame.Units(replica.World!, 1).Count);

        game.Client.Send(DeltaCodec.Order("hello.spawn", HelloGame.Spawn(20, 5, 5)));
        while (HelloGame.Units(replica.World!, 1).Count < 23 && clock.ElapsedMilliseconds < 5000)
        {
            replica.Pump(game.Client);
            Thread.Sleep(10);
        }
        Assert.Equal(23, HelloGame.Units(replica.World!, 1).Count);

        // Хост тикает сам, по часам. Сколько тиков за полсекунды — у замера PacesTwentyTicksPerSecond: на загруженной
        // машине поток хоста вытесняют, и число тиков за отрезок времени говорит о соседях; сам шаг часов проверяет Pace.
        var ticks = game.Ticks;
        while (game.Ticks < ticks + 3 && clock.ElapsedMilliseconds < 20_000)
            Thread.Sleep(10);
        Assert.True(game.Ticks >= ticks + 3, $"хост не тикает: {game.Ticks - ticks} тиков");
        TestContext.Current.TestOutputHelper?.WriteLine($"тик {game.AverageTickMs:F2} мс");

        game.Stop();
        Assert.False(game.Running);
        Assert.Null(game.Failure);
        replica.Pump(game.Client);
        Assert.True(game.Client.Closed);
        Assert.Empty(game.Host!.Errors);
    }

    [Fact]
    public void StartFailureSurfacesOnCaller()
    {
        var error = Assert.Throws<HostException>(() => LocalGame.Start(() => new Host(HelloGame.Content(), new HostOptions { Game = "nope" })));
        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public void ClientClosingStopsHost()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using var game = LocalGame.Start(() => new Host(HelloGame.Content()), step: TimeSpan.FromMilliseconds(5));
        game.Client.Dispose();
        var clock = Stopwatch.StartNew();
        while (game.Running && clock.ElapsedMilliseconds < 3000)
            Thread.Sleep(5);
        Assert.False(game.Running);
    }

    /// <summary>Игра с ручным ходом и реплика к ней: тики — только по Advance.</summary>
    private static (LocalGame Game, Replica Replica) Manual(Func<Host>? create = null)
    {
        var game = LocalGame.Start(create ?? (() => new Host(HelloGame.Content())), manual: true);
        var replica = new Replica();
        var clock = Stopwatch.StartNew();
        while (!replica.Ready && clock.ElapsedMilliseconds < 5000)
        {
            replica.Pump(game.Client);
            Thread.Sleep(2);
        }
        Assert.True(replica.Ready);
        return (game, replica);
    }

    private static List<(uint Entity, double X, double Y)> Positions(Replica replica)
    {
        var all = new List<uint>();
        replica.World!.Query([Builtin.Position], all);
        all.Sort();
        return [.. all.Select(entity => (entity, HelloGame.Position(replica.World!, entity).X, HelloGame.Position(replica.World!, entity).Y))];
    }

    [Fact(DisplayName = "делает столько тиков, сколько накопилось времени")]
    public void TicksFollowAccumulatedTime()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        // Ручной ход: накопленное время — это запрошенные тики, ни больше ни меньше.
        var (manual, _) = Manual();
        using (manual)
        {
            var start = manual.Ticks;
            manual.Advance(start + 3);
            Assert.Equal(start + 3, manual.Ticks);
            manual.Advance(start + 3);
            Assert.Equal(start + 3, manual.Ticks);
            manual.Advance(start + 5);
            Assert.Equal(start + 5, manual.Ticks);
        }

        // По часам — у замера PacesTwentyTicksPerSecond (зависит от загрузки машины) и у Pace (шаг часов без часов).
    }

    [Fact(Explicit = true, DisplayName = "замер: по часам хост делает 20 тиков в секунду, за время t — t / шаг тиков")]
    [Trait("Category", "Bench")]
    public void PacesTwentyTicksPerSecond()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using (var game = LocalGame.Start(() => new Host(HelloGame.Content())))
        {
            // 20 Гц: за ~0,5 с — около десяти тиков; бюджет тика — у TickBenchTests.
            var ticks = game.Ticks;
            Thread.Sleep(500);
            Assert.InRange(game.Ticks - ticks, 3, 16);
            Assert.True(game.AverageTickMs < 50, $"тик {game.AverageTickMs} мс");
        }
        using var timed = LocalGame.Start(() => new Host(HelloGame.Content()), step: TimeSpan.FromMilliseconds(20));
        var watch = Stopwatch.StartNew();
        var first = timed.Ticks;
        Thread.Sleep(400);
        var due = watch.Elapsed.TotalMilliseconds / 20;
        var made = timed.Ticks - first;
        Assert.InRange(made, due - 5, due + 2);
    }

    [Fact(DisplayName = "симуляция не зависит от частоты кадров")]
    public void SimulationDoesNotDependOnFrameRate()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        List<(uint Entity, double X, double Y)> Run(int[] frames)
        {
            var (game, replica) = Manual();
            using (game)
            {
                var mine = HelloGame.Units(replica.World!, 1);
                game.Client.Send(DeltaCodec.Order("hello.move", HelloGame.Move(30, -20, [.. mine])));
                var total = game.Ticks;
                foreach (var count in frames)
                {
                    total += count;
                    game.Advance(total);
                    replica.Pump(game.Client);
                }
                return Positions(replica);
            }
        }

        // Те же 16 тиков одним кадром, двумя и по одному.
        var once = Run([16]);
        Assert.NotEmpty(once);
        Assert.Equal(once, Run([8, 8]));
        Assert.Equal(once, Run([.. Enumerable.Repeat(1, 16)]));
    }

    [Fact(DisplayName = "системы идут по порядку, кадр — после тиков и с долей тика")]
    public void SystemsRunInOrderAndTicksAreCounted()
    {
        var log = new List<string>();
        SystemSpec Spec(string name) => new() { Name = name, System = new DelegateSystem(context => log.Add($"{name} {context.Tick}")) };
        var scheduler = new Scheduler(new GameWorld(new Schema()), [Spec("m/a.a"), Spec("m/b.b")]);
        scheduler.Step(0.25);
        scheduler.Step(0.25);
        // Системы — по порядку объявления в каждом тике, номер тика растёт после тика; доля тика в кадре — у клиента (FrameAlphaTests).
        Assert.Equal(["m/a.a 0", "m/b.b 0", "m/a.a 1", "m/b.b 1"], log);
        Assert.Equal(2UL, scheduler.Tick);
    }

    [Fact(DisplayName = "слишком длинный кадр не догоняется целиком")]
    public void LongStallIsNotCaughtUpInOneGo()
    {
        // 50 мс на тик: обычная задержка срок не трогает, отстав больше чем на пять тиков, отсчёт начинается заново.
        Assert.Equal(100, LocalGame.Pace(50, 60, 50));
        Assert.Equal(100, LocalGame.Pace(50, 330, 50));
        Assert.Equal(10_050, LocalGame.Pace(50, 10_000, 50));
    }

    [Fact(DisplayName = "изменения раздаются после каждого тика и после кадра")]
    public void ChangesReachReplicaAfterEveryTick()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        var (game, replica) = Manual();
        using (game)
        {
            var before = HelloGame.Units(replica.World!, 1).Count;
            var tick = replica.Tick;
            // Приказ между тиками виден только после тика.
            game.Client.Send(DeltaCodec.Order("hello.spawn", HelloGame.Spawn(5, 4, 4)));
            replica.Pump(game.Client);
            Assert.Equal(before, HelloGame.Units(replica.World!, 1).Count);
            game.Advance(game.Ticks + 1);
            replica.Pump(game.Client);
            Assert.Equal(before + 5, HelloGame.Units(replica.World!, 1).Count);
            Assert.True(replica.Tick > tick);

            // Каждый тик — свой пакет изменений: номер тика реплики растёт ровно на число тиков.
            tick = replica.Tick;
            for (var i = 0; i < 4; i++)
            {
                game.Advance(game.Ticks + 1);
                replica.Pump(game.Client);
            }
            Assert.Equal(tick + 4, replica.Tick);
        }
    }

    [Fact(DisplayName = "локальная игра присылает сохранение, и с него она продолжается")]
    public void LocalGameSavesAndResumesFromSave()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        byte[] save;
        ulong tick;
        int units;
        var (game, replica) = Manual();
        using (game)
        {
            game.Client.Send(DeltaCodec.Order("hello.spawn", HelloGame.Spawn(7, 3, 3)));
            game.Advance(game.Ticks + 120);
            // Уход: хост остановлен, мир — в байты (как LocalSession.Leave).
            game.Stop();
            Assert.Null(game.Failure);
            save = game.Host!.Save();
            tick = game.Host.Tick;
            units = HelloGame.Units(game.Host.World, 1).Count;
        }
        Assert.NotEmpty(save);
        Assert.True(tick >= 120);

        var content = HelloGame.Content();
        var (resumed, copy) = Manual(() => Host.Load(content, save, new HostOptions { Modules = new GameModuleOptions { CallTimeoutMs = 1000 } }));
        using (resumed)
        {
            Assert.True(resumed.Host!.Tick >= tick);
            Assert.Equal(units, HelloGame.Units(copy.World!, 1).Count);
            Assert.Equal(units, HelloGame.Units(resumed.Host.World, 1).Count);
        }
    }

    [Fact(DisplayName = "показательный бой идёт сам и не сохраняется")]
    public void ShowcaseBattleRunsByItselfAndIsNotSaved()
    {
        var modDir = Path.Combine(HelloGame.DotnetRoot, "mods", "base");
        var content = GameContent.Load([ModPackage.Read(modDir)]);
        var (game, replica) = Manual(() => new Host(content, new HostOptions
        {
            Game = "showcase",
            Settings = System.Text.Json.JsonDocument.Parse("{\"scene\":\"battle\"}").RootElement.Clone(),
            ModMode = ModMode.Clr,
            Seed = 1,
            Modules = new GameModuleOptions { CallTimeoutMs = 5000 },
        }));
        using (game)
        {
            replica.Pump(game.Client);
            game.Advance(game.Ticks + 40);
            replica.Pump(game.Client);
            var start = Positions(replica).ToDictionary(item => item.Entity);
            Assert.NotEmpty(start);
            var moved = false;
            for (var i = 0; i < 20 && !moved; i++)
            {
                game.Advance(game.Ticks + 20);
                replica.Pump(game.Client);
                moved = Positions(replica).Any(now => start.TryGetValue(now.Entity, out var from) && (from.X != now.X || from.Y != now.Y));
            }
            Assert.True(moved, "без приказов игрока никто не сдвинулся с места");
            Assert.Empty(game.Host!.Errors);
            // Сохраняет оболочка, и только слот (SlotLaunch в LocalSession.Leave): у фона запуск GameLaunch, ничего не пишется
            // (проверяет BackgroundTests); сама игра файлов не создаёт.
            Assert.Null(game.Failure);
        }
    }
}
