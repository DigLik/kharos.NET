using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Protocol;
using KHost = Kharos.Host.Host;

namespace Kharos.Base.Tests;

/// <summary>Сеть партии base: что реплика игрока получает по обзору (VisionView) — счета, призраки зданий. Ждут старта партии (Б1).</summary>
public class NetTests
{
    /// <summary>Клиент на конце транспорта: реплика, которую питает сессия.</summary>
    private sealed class Client
    {
        private readonly MemoryTransport _end;

        public Client(HostSession session, uint player)
        {
            (var hostEnd, _end) = MemoryTransport.Pair();
            session.Connect(hostEnd, player);
            Pump();
        }

        public Replica Replica { get; } = new();
        public GameWorld World => Replica.World!;

        public void Pump()
        {
            Replica.Pump(_end);
            Replica.Inbox.Clear();
        }

        /// <summary>Сущности реплики с компонентами, по возрастанию номеров.</summary>
        public List<uint> With(params string[] components)
        {
            var list = new List<uint>();
            World.Query([.. components.Select(World.Schema.Id)], list);
            list.Sort();
            return list;
        }

        public bool Has(uint entity) => World.Alive(entity);
    }

    private sealed class Match : IDisposable
    {
        public KHost Host { get; } = BaseGame.Started();
        public HostSession Session { get; }
        public Client One { get; }
        public Client Two { get; }

        public Match()
        {
            Session = new HostSession(Host, new HostSessionOptions { SendRate = 0 });
            One = new Client(Session, 1);
            Two = new Client(Session, 2);
        }

        public void Step()
        {
            Session.Step();
            One.Pump();
            Two.Pump();
        }

        /// <summary>Тики, пока условие не выполнится (не больше предела); выполнилось ли.</summary>
        public bool Until(Func<bool> done, int limit = 2000)
        {
            for (var tick = 0; tick < limit && !done(); tick++)
                Step();
            return done();
        }

        public void Dispose() => Host.Dispose();
    }

    [Fact(Skip = "ждёт Б1")]
    public void PlayerAccountReachesOnlyItsOwner()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        using var match = new Match();
        match.Step();
        foreach (var (client, player) in new[] { (match.One, 1u), (match.Two, 2u) })
        {
            var accounts = client.With("Player");
            var account = Assert.Single(accounts);
            Assert.Equal(player, (uint)client.World.Number(account, client.World.Schema.Id("Player"), client.World.Schema[client.World.Schema.Id("Player")].FieldIndex("id")));
            Assert.Equal(player, (uint)client.World.Number(account, Builtin.Owner, 0));
        }
        // Доход растёт на хосте — у хозяина счёт обновляется, а чужой так и не приходит.
        var before = BaseGame.Credits(match.Host, 1);
        Assert.True(match.Until(() => BaseGame.Credits(match.Host, 1) > before, 100));
        Assert.Single(match.Two.With("Player"));
        Assert.True(match.Two.Has(BaseGame.Wallet(match.Host, 2)));
        Assert.False(match.One.Has(BaseGame.Wallet(match.Host, 2)));
        Assert.False(match.Two.Has(BaseGame.Wallet(match.Host, 1)));
        Assert.Empty(match.Host.Errors);
    }

    [Fact(Skip = "ждёт Б1")]
    public void ForeignFactorySeenOnceStaysAsGhost()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        using var match = new Match();
        var host = match.Host;
        var factory = BaseGame.Factory(host, 1);
        var (fx, fy) = BaseGame.At(host, factory);
        var (hx, hy) = BaseGame.At(host, BaseGame.Factory(host, 2));
        match.Step();
        Assert.True(match.One.Has(factory));
        Assert.False(match.Two.Has(factory), "чужой завод виден издалека");

        // Игрок 2 подъехал к заводу игрока 1 — завод показался (и юниты хозяина рядом с ним).
        var units = BaseGame.Units(host, 2);
        BaseGame.Move(host, 2, fx, fy + 6, [.. units]);
        Assert.True(match.Until(() => match.Two.Has(factory)), "завод не показался подъехавшим юнитам");

        // Уехал далеко: завод вне обзора, но остался призраком; обычные чужие юниты — исчезли.
        BaseGame.Move(host, 2, hx, hy, [.. units]);
        var view = new VisionView(host, 2);
        Assert.True(match.Until(() => !view.Sees(fx, fy)), "завод не вышел из обзора");
        for (var tick = 0; tick < 40; tick++)
            match.Step();
        Assert.True(match.Two.Has(factory), "завод пропал вне обзора");
        Assert.Equal(fx, match.Two.World.Number(factory, Builtin.Position, 0), 0.01);
        Assert.Equal(fy, match.Two.World.Number(factory, Builtin.Position, 1), 0.01);
        Assert.All(BaseGame.Units(host, 1), unit => Assert.False(match.Two.Has(unit)));
        Assert.Empty(host.Errors);
    }
}
