using Base.Shared.Orders;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Protocol;
using KHost = Kharos.Host.Host;

namespace Kharos.Base.Tests;

/// <summary>Сеть партии base: что реплика игрока получает по обзору (VisionView) — счета, призраки зданий.</summary>
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

    [Fact]
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
        // Кредиты первого меняются на хосте — у хозяина счёт обновляется, а чужой так и не приходит.
        Assert.Equal(1500, BaseGame.Credits(match.Host, 1));
        BaseGame.Edit(match.Host, EditCreditsOrder.Name, new EditCreditsOrder { Player = 1, Amount = 2345 });
        var player1 = match.One.With("Player").Single();
        var credits = match.One.World.Schema.Id("Player");
        double Credits() => match.One.World.Number(player1, credits, match.One.World.Schema[credits].FieldIndex("credits"));
        Assert.True(match.Until(() => Credits() == 2345, 100), "счёт хозяина не обновился");
        Assert.Single(match.Two.With("Player"));
        Assert.True(match.Two.Has(BaseGame.Wallet(match.Host, 2)));
        Assert.False(match.One.Has(BaseGame.Wallet(match.Host, 2)));
        Assert.False(match.Two.Has(BaseGame.Wallet(match.Host, 1)));
        Assert.Empty(match.Host.Errors);
    }

    /// <summary>Здание игрока player рядом с точкой (x, y): правка редактора пробует места по кругу, пока не встанет.</summary>
    private static uint PutBuilding(Match match, uint player, string type, double x, double y)
    {
        var host = match.Host;
        var before = BaseGame.With(host, "Building").ToHashSet();
        foreach (var (dx, dy) in new (int, int)[] { (0, 0), (5, 0), (-5, 0), (0, 5), (0, -5), (7, 7), (-7, 7), (7, -7), (-7, -7), (10, 0), (-10, 0) })
        {
            BaseGame.Edit(host, EditBuildingOrder.Name, new EditBuildingOrder { Type = type, X = (int)x + dx, Y = (int)y + dy, Player = player });
            match.Step();
            if (BaseGame.With(host, "Building").FirstOrDefault(e => !before.Contains(e)) is var found and not 0)
                return found;
        }
        throw new InvalidOperationException($"здание {type} не встало у ({x}, {y})");
    }

    [Fact]
    public void ForeignBuildingSeenOnceStaysAsGhost()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        using var match = new Match();
        var host = match.Host;
        match.Step();
        // Игрок 1 ставит главное здание у своего MCV; игрок 2 живёт далеко и его не видит.
        var (mx, my) = BaseGame.At(host, BaseGame.Factory(host, 1));
        var building = PutBuilding(match, 1, "command", mx - 1, my - 1);
        var (bx, by) = BaseGame.At(host, building);
        for (var tick = 0; tick < 10; tick++)
            match.Step();
        Assert.True(match.One.Has(building));
        Assert.False(match.Two.Has(building), "чужое здание видно издалека");

        // Разведчик игрока 2 вырос рядом со зданием — здание показалось.
        BaseGame.Edit(host, EditUnitOrder.Name, new EditUnitOrder { Type = "buggy", X = bx + 6.5, Y = by + 1.5, Player = 2 });
        Assert.True(match.Until(() => match.Two.Has(building), 100), "здание не показалось разведчику");
        var scout = BaseGame.Units(host, 2).Single(e => Math.Abs(BaseGame.At(host, e).X - (bx + 6.5)) < 1);

        // Разведчика больше нет: здание вне обзора, но осталось призраком; чужие юниты, которых не видели, — не пришли.
        BaseGame.Edit(host, EditEraseOrder.Name, new EditEraseOrder { Entities = [scout] });
        var view = new VisionView(host, 2);
        Assert.True(match.Until(() => !view.Sees(bx, by)), "здание не вышло из обзора");
        for (var tick = 0; tick < 40; tick++)
            match.Step();
        Assert.True(match.Two.Has(building), "здание пропало вне обзора");
        Assert.Equal(bx, match.Two.World.Number(building, Builtin.Position, 0), 0.01);
        Assert.Equal(by, match.Two.World.Number(building, Builtin.Position, 1), 0.01);
        Assert.All(BaseGame.Units(host, 1), unit => Assert.False(match.Two.Has(unit)));
        Assert.Empty(host.Errors);
    }
}
