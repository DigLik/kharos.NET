using Kharos.Client.Game;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Синхронизация клиента с хостом (packages/tests/net.test.ts): время между тиками и обрыв, игроки и ники, сохранённый
/// сервер, сверка копии с хостом, срок движения и придержанные юниты. Хост — мод hello (CLR) или арена без кода.
/// </summary>
public sealed class NetSyncTests
{
    private static readonly GameModuleOptions Modules = new() { CallTimeoutMs = 1000 };

    private static Host Hello() => HelloGame.Create(ModMode.Clr);

    private static HostSession Open(Host host, int sendRate = 0)
    {
        host.Start();
        return new HostSession(host, new HostSessionOptions { SendRate = sendRate });
    }

    private static void Step(HostSession session, params NetPeer[] peers)
    {
        session.Step();
        foreach (var peer in peers)
            peer.Pump();
    }

    [Fact(DisplayName = "между тиками копия ведёт alpha, а пропавшее соединение останавливает игру")]
    public void ReplicaKeepsAlphaBetweenTicksAndLostConnectionStopsTheGame()
    {
        using var arena = new Arena(fog: false);
        arena.Unit(1, 0.5, 0.5);
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        var connection = arena.Session.Connect(hostEnd, 1);
        using var game = new ClientGame(arena.GameContent, clientEnd, new ClientGameOptions { ModuleOptions = Modules });
        var dt = game.Replica.Welcome!.Dt;
        ClientInput At(double time) => new() { Width = 640, Height = 360, Time = time };

        // Тик пришёл — alpha с нуля; растёт с временем кадра до единицы и дальше не уходит.
        arena.Session.Step();
        game.Step(At(10));
        Assert.Equal(0, game.Poses.Alpha);
        game.Step(At(10 + dt / 2));
        Assert.Equal(0.5, game.Poses.Alpha, 6);
        game.Step(At(10 + dt));
        Assert.Equal(1, game.Poses.Alpha, 6);
        game.Step(At(11));
        Assert.Equal(1, game.Poses.Alpha);
        var tick = game.Replica.Tick;
        Assert.Equal(arena.Host.Tick, tick);

        // Соединение пропало: копия видит закрытый транспорт (оболочка тут останавливает игру), а мир не тикает.
        arena.Session.Disconnect(connection);
        game.Step(At(12));
        Assert.True(clientEnd.Closed);
        arena.Step(3);
        game.Step(At(13));
        Assert.Equal(tick, game.Replica.Tick);
        Assert.Equal(1, game.Poses.Alpha);
    }

    [Fact(DisplayName = "вернувшийся игрок получает карту, разведанную раньше")]
    public void ReturningPlayerGetsMapExploredBefore()
    {
        using var arena = new Arena();
        var unit = arena.Unit(1, 0.5, 0.5);
        var first = arena.Join(1);
        arena.Step();
        first.Pump();
        var (x, y) = (arena.Host.World.Number(unit, Builtin.Position, 0), arena.Host.World.Number(unit, Builtin.Position, 1));
        arena.Leave(first);

        var again = arena.Join(1);
        // Ещё ни одного тика от хоста, а разведанное уже есть.
        Assert.True(again.Replica.Explored((int)Math.Floor(x), (int)Math.Floor(y)));
        Assert.False(again.Replica.Explored((int)Math.Floor(x) + 40, (int)Math.Floor(y)));
    }

    [Fact(DisplayName = "игроки видят ники друг друга и кто из них в сети")]
    public void PlayersSeeEachOthersNamesAndWhoIsOnline()
    {
        using var host = Hello();
        var session = Open(host);
        var first = NetPeer.Join(session, null, "  Вася\u0007 ");
        var second = NetPeer.Join(session);
        first.Pump();
        Assert.Equal([new PlayerInfo(1, "Вася", true), new PlayerInfo(2, "Игрок 2", true)], first.Players);
        session.Disconnect(second.Connection);
        first.Pump();
        Assert.False(first.Players[1].Online);
        // Вернулся с ником — ник новый.
        var back = NetPeer.Join(session, second.Token, "Петя");
        Assert.Equal(new PlayerInfo(2, "Петя", true), back.Players[1]);
        // Вернулся без ника — ник прежний.
        Assert.Equal("Вася", NetPeer.Join(session, first.Token).Players[0].Name);
        Assert.Equal("Петя", NetPeer.Join(session, back.Token).Players[1].Name);
    }

    [Fact(DisplayName = "сохранённый сервер после перезапуска узнаёт игроков по id, помнит ники и не путает номера новых")]
    public void RestartedServerKnowsPlayersByTokenRemembersNamesAndKeepsNumbers()
    {
        using var host = Hello();
        var session = Open(host);
        var first = NetPeer.Join(session, null, "Вася");
        session.Disconnect(NetPeer.Join(session, null, "Петя").Connection);
        for (var tick = 0; tick < 4; tick++)
            Step(session, first);
        var units = first.Units(1).Count;
        var token = first.Token;
        var save = host.Save();

        using var restarted = Host.Load(HelloGame.Content(), save, new HostOptions { Modules = Modules, ModMode = ModMode.Clr });
        var resumed = new HostSession(restarted, new HostSessionOptions { SendRate = 0 });
        var back = NetPeer.Join(resumed, token);
        Assert.Equal(1u, back.Player);
        Assert.Equal(units, HelloGame.Units(restarted.World, 1).Count);
        Assert.Equal(units, back.With("Drive").Count(entity => back.Number(entity, "Owner", "player") == 1));
        Assert.Equal([new PlayerInfo(1, "Вася", true), new PlayerInfo(2, "Петя", false)], back.Players);
        Assert.Equal(3u, NetPeer.Join(resumed).Player);
    }

    [Fact(DisplayName = "мир клиента, собранный из изменений, совпадает с тем, что игрок видит на хосте")]
    public void ReplicaBuiltFromChangesEqualsWhatPlayerSeesOnHost()
    {
        using var host = Hello();
        var session = Open(host);
        var one = NetPeer.Connect(session, 1);
        var two = NetPeer.Connect(session, 2);
        var ones = HelloGame.Units(host.World, 1);
        var twos = HelloGame.Units(host.World, 2);
        // Юниты второго едут навстречу первым, появляются в обзоре, двигаются, уходят и возвращаются.
        for (var tick = 1; tick <= 600; tick++)
        {
            var x = tick <= 200 || tick > 400 ? 3 : 80;
            host.Send(1, "hello.move", HelloGame.Move(0, 4, [.. ones]));
            host.Send(2, "hello.move", HelloGame.Move(x, 4, [.. twos]));
            session.Step();
            // Первый клиент применяет каждое изменение, второй — пачками по несколько.
            one.Pump();
            if (tick % 7 == 0)
                two.Pump();
            if (tick % 50 == 0)
                NetAssert.Same(host, one.Replica, one.Connection.Filter);
        }
        two.Pump();
        NetAssert.Same(host, two.Replica, two.Connection.Filter);
        Assert.NotEmpty(one.Units(2));
        Assert.Empty(host.Errors);
    }

    [Fact(DisplayName = "движение в кадре несёт срок: за сколько тиков оно набежало, не больше SPAN_MOST")]
    public void MotionInFrameCarriesSpanAtMostSpanMost()
    {
        // Кодек: срок пишется как есть, но не короче одного тика и не длиннее SpanMost.
        using var arena = new Arena(fog: false);
        var unit = arena.Unit(1, 0.5, 0.5);
        var peer = arena.Join(1);
        arena.Step();
        var world = arena.World;
        ulong tick = arena.Host.Tick;
        PoseMove Moved(int span)
        {
            var w = new DeltaWriter();
            w.Begin(MessageType.Delta, ++tick, arena.Host.Format);
            w.Strings(world.Strings, world.Strings.Count);
            w.Fields(world, unit, Builtin.Position, 0b11);
            w.Span(unit, span);
            peer.Replica.Apply(w.Finish());
            return Assert.Single(peer.Replica.Moves);
        }
        Assert.Equal(0.5, Moved(7).X);
        Assert.Equal(7, peer.Replica.Moves[0].Span);
        Assert.Equal(1, Moved(0).Span);
        Assert.Equal(1, Moved(-5).Span);
        Assert.Equal(DeltaWriter.SpanMost, Moved(500).Span);
        Assert.Equal(DeltaWriter.SpanMost, Moved(DeltaWriter.SpanMost).Span);
        Assert.Equal(1, Moved(1).Span);
    }

    [Fact(DisplayName = "тесный бюджет: срок движения придержанных не больше MaxSpan хоста и равен тикам ожидания")]
    public void HeldBackUnitsGetSpanEqualToTheirWait()
    {
        // Придержанная сущность приходит со сроком «столько тиков, сколько ждала» (не больше MaxSpan = 40 хоста);
        // ждавшая дольше встаёт на место сразу — срок 1.
        using var host = Hello();
        var session = Open(host, sendRate: 2048);
        var client = NetPeer.Connect(session, 1);
        host.Send(1, "hello.spawn", HelloGame.Spawn(150, -40, 0, spread: 12));
        host.Send(1, "hello.spawn", HelloGame.Spawn(150, 40, 0, spread: 12));
        Step(session, client);
        var last = new Dictionary<uint, ulong>();
        foreach (var entity in client.Units(1))
            last[entity] = client.Replica.Tick;
        int held = 0, snapped = 0, checks = 0;
        client.Applied = (replica, type) =>
        {
            if (type != MessageType.Delta)
                return;
            foreach (var move in replica.Moves)
            {
                var wait = (long)(replica.Tick - last.GetValueOrDefault(move.Entity, replica.Tick - 1));
                last[move.Entity] = replica.Tick;
                checks++;
                Assert.InRange(move.Span, 1, 40);
                Assert.True(move.Span <= Math.Max(1, wait), $"{move.Entity}: срок {move.Span} при ожидании {wait}");
                if (wait is > 1 and <= 40)
                {
                    Assert.Equal(wait, move.Span);
                    held++;
                }
                else if (wait > 40)
                {
                    Assert.Equal(1, move.Span);
                    snapped++;
                }
            }
        };
        for (var tick = 0; tick < 400; tick++)
            Step(session, client);
        Assert.True(checks > 0);
        Assert.True(held > 0, "никого не придержали");
        Assert.True(session.Deferred > 0);
        Assert.Empty(host.Errors);
        Assert.True(snapped >= 0);
    }

    [Fact(DisplayName = "юнит, которого хост придержал на несколько тиков, едет к новому месту столько же тиков, а не прыгает")]
    public void HeldBackUnitGlidesForAsManyTicksAsItWasHeld()
    {
        using var arena = new Arena(fog: false);
        var unit = arena.Unit(1, 0.5, 0.5);
        var peer = arena.Join(1);
        arena.Step();
        var world = peer.World;
        var poses = new Poses();
        const double dt = 0.05;
        double X(double time)
        {
            poses.Time = time;
            Assert.True(poses.TryGet(world, unit, out var x, out _, out _));
            return x;
        }
        void Receive(double x, int span, double time)
        {
            var before = world.Number(unit, Builtin.Position, 0);
            world.SetNumber(unit, Builtin.Position, 0, x);
            poses.Moved([new PoseMove(unit, before, 0.5, 0, span)], time, dt);
        }

        // Четыре тика о нём не слышно, потом место в четырёх тайлах и срок — четыре тика.
        Receive(4.5, 4, 1.0);
        Assert.Equal(0.5, X(1.0), 6);
        Assert.Equal(1.5, X(1.0 + dt), 6);
        Assert.Equal(2.5, X(1.0 + 2 * dt), 6);
        Assert.Equal(3.5, X(1.0 + 3 * dt), 6);
        Assert.Equal(4.5, X(1.0 + 4 * dt), 6);
        // Доехал — стоит.
        Assert.Equal(4.5, X(1.0 + 9 * dt), 6);
        // Новое место посреди пути — едет к нему оттуда, где оказался; срок в один тик — сразу на место.
        Receive(8.5, 2, 2.0);
        Assert.Equal(6.5, X(2.0 + dt), 6);
        Receive(0.5, 1, 2.0 + dt);
        Assert.Equal(6.5, X(2.0 + dt), 6);
        Assert.Equal(0.5, X(2.0 + 2 * dt), 6);
    }
}
