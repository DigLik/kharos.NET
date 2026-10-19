using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Kharos.Host;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Players;

/// <summary>
/// Союзы (packages/tests/allies.test.ts): предложение, согласие, группа во главе с главой, исключение и выход, бой и ворота
/// между союзниками, сигналы союзникам. Команды чата /ally и /unally хоста TS — приказы ally, breakAlly, exitAlly; строки
/// чата — вести ally (ключи те же). Сетевые тесты (общая разведка, счёт и потоки союзника, путь юнита союзника) — в
/// Kharos.Host.Tests/AlliesVisibilityTests.
/// </summary>
public sealed class AlliesTests
{
    private static Sim Players(params uint[] players)
    {
        Scene.SkipUnlessAvailable();
        var sim = Sim.Create(new SimOptions { Size = 128, Land = Scene.Land, Players = players });
        foreach (var player in players)
            sim.SetCredits(player, 100);
        return sim;
    }

    [Fact(DisplayName = "союз — предложение и согласие; главой становится предложивший, и он один распоряжается союзом")]
    public void OfferAndAccept()
    {
        using var sim = Players(1, 2, 3, 4);
        sim.Advance();
        Assert.Equal("refused", sim.Offer(1, 1));
        Assert.Equal("offered", sim.Offer(1, 2));
        Assert.Equal("pending", sim.Offer(1, 2));
        Assert.Equal([1u], sim.TeamOf(2).Asked);
        Assert.False(sim.Allied(1, 2));
        // Встречное предложение — согласие, и главой становится тот, кто предложил первым.
        Assert.Equal("allied", sim.Offer(2, 1));
        Assert.True(sim.Allied(1, 2) && sim.Allied(2, 1));
        var first = sim.TeamOf(1);
        Assert.Equal([2u], first.Allies);
        Assert.Equal(1, first.Head);
        Assert.Empty(first.Offered);
        Assert.Empty(first.Asked);
        Assert.Equal(1, sim.HeadOf(1));
        Assert.Equal(1, sim.HeadOf(2));
        Assert.Equal("already", sim.Offer(1, 2));
        // Союз — группа: принявший союз у главы становится союзником всех, а главой остаётся глава группы.
        Assert.Equal("offered", sim.Offer(1, 3));
        Assert.Equal("allied", sim.Offer(3, 1));
        Assert.True(sim.Allied(3, 2) && sim.Allied(2, 3));
        Assert.Equal([2u, 3u], sim.AlliesOf(1));
        Assert.Equal(1, sim.HeadOf(1));
        Assert.Equal([1u, 2u], sim.AlliesOf(3));
        Assert.Equal(1, sim.HeadOf(3));
        // Рядовой союзом не распоряжается: ни приглашать, ни отвечать чужому рядовому, ни исключать.
        Assert.Equal("fromMember", sim.Offer(2, 4));
        Assert.Equal("toMember", sim.Offer(4, 2));
        Assert.Equal("notHead", sim.BreakAlly(2, 1));
        // Глава исключает рядового — тот покидает союз целиком, а связи оставшихся целы.
        Assert.Equal("kicked", sim.BreakAlly(1, 3));
        Assert.Empty(sim.AlliesOf(3));
        Assert.Equal(-1, sim.HeadOf(3));
        Assert.Equal([2u], sim.AlliesOf(1));
        Assert.Equal([1u], sim.AlliesOf(2));
        // Рядовой выходит сам — и тоже целиком: с ним рвутся все его связи, и он снова одиночка.
        Assert.Equal("left", sim.ExitAlly(2));
        Assert.Empty(sim.AlliesOf(2));
        Assert.Equal(-1, sim.HeadOf(2));
        Assert.Empty(sim.AlliesOf(1));
        Assert.Equal(-1, sim.HeadOf(1));
        Assert.Equal("nothing", sim.ExitAlly(2));
        // Глава уходит — союз распускается: без главы он не живёт.
        sim.Offer(1, 3);
        Assert.Equal("allied", sim.Offer(3, 1));
        sim.Offer(1, 4);
        Assert.Equal("allied", sim.Offer(4, 1));
        Assert.Equal([1u, 4u], sim.AlliesOf(3));
        Assert.Equal("dissolved", sim.ExitAlly(1));
        foreach (var player in new uint[] { 1, 3, 4 })
        {
            Assert.Empty(sim.AlliesOf(player));
            Assert.Equal(-1, sim.HeadOf(player));
        }
        // Отозвать своё предложение и отклонить чужое может всякий: союза тут ещё нет.
        sim.Offer(4, 1);
        Assert.Equal("declined", sim.BreakAlly(1, 4));
        Assert.Empty(sim.TeamOf(1).Asked);
        Assert.Empty(sim.TeamOf(4).Offered);
        Assert.Equal("nothing", sim.BreakAlly(1, 4));
        // Начавший заново — новый игрок для всех: союзы и предложения пропадают у обеих сторон (wipePlayer).
        sim.Offer(3, 2);
        sim.Respawn(2, forced: true);
        Assert.False(sim.Allied(2, 3));
        Assert.Empty(sim.TeamOf(3).Offered);
        Assert.Empty(sim.AlliesOf(2));
    }

    [Fact(DisplayName = "вступление в группу: принявший союз у главы — союзник и всем остальным, и предложения ждать больше нечего")]
    public void JoinGroup()
    {
        using var sim = Players(1, 2, 3, 4);
        sim.Offer(1, 2);
        sim.Offer(2, 1);
        // Четвёртый и третий предложили главе: предложение, ставшее союзом, из списков уходит.
        sim.Offer(4, 1);
        sim.Offer(3, 1);
        Assert.Equal([1u], sim.TeamOf(4).Offered);
        Assert.Empty(sim.TeamOf(4).Asked);
        Assert.Equal([4u, 3u], sim.TeamOf(1).Asked);
        Assert.Equal("allied", sim.Offer(1, 3));
        Assert.Equal([1u, 3u], sim.AlliesOf(2));
        Assert.Equal([1u, 2u], sim.AlliesOf(3));
        Assert.Empty(sim.TeamOf(2).Offered);
        Assert.Empty(sim.TeamOf(2).Asked);
        // Предложение четвёртого — предложение всей группе: принимает глава, и четвёртый входит в неё целиком.
        Assert.Equal([4u], sim.TeamOf(1).Asked);
        Assert.Equal("allied", sim.Offer(1, 4));
        Assert.Equal([1u, 2u, 3u], sim.AlliesOf(4));
        Assert.Equal([1u, 2u, 4u], sim.AlliesOf(3));
        Assert.Equal(1, sim.HeadOf(4));
    }

    [Fact(DisplayName = "сохранение прежних версий с попарными союзами достраивается до групп, а главой становится младший",
        Skip = "устарело: сохранений TS с попарными союзами у .NET нет — союз с первой версии один Team.head на группу (materializeAlliances не нужен)")]
    public void PairsBecomeGroups()
    {
    }

    [Fact(DisplayName = "союзников не атакуют — ни сами, ни по приказу, — и ворота перед ними не закрываются")]
    public void AlliesAreNotAttacked()
    {
        var (sim, x, y) = Field(rules: new { protection = 0, techTree = false });
        using var _sim = sim;
        var own = Spawn(sim, "infantry", 1, x, y);
        var other = Spawn(sim, "infantry", 2, x + 3, y);
        var gate = Place(sim, "gate", x + 1, y + 3, 1);
        sim.SetCredits(1, 100);
        sim.SetCredits(2, 100);
        sim.Offer(1, 2);
        sim.Offer(2, 1);
        sim.Advance(100);
        Assert.False(sim.CanAttack(1, other));
        Assert.Equal(0u, sim.TargetOf(own));
        Assert.True(IsOpen(sim, gate));
        Assert.Equal(2, sim.With(Names.Unit.Name).Count(unit => sim.OwnerOf(unit) is 1 or 2));
        // Союзник вышел из союза — снова враги.
        Assert.Equal("left", sim.ExitAlly(2));
        sim.Advance(10);
        Assert.True(sim.CanAttack(1, other));
        Assert.Equal(other, sim.TargetOf(own));
        Assert.False(IsOpen(sim, gate));
    }

    /// <summary>Открыты ли ворота (isOpenGate): Gate.open и не стройка.</summary>
    private static bool IsOpen(Sim sim, uint gate) => sim.Num(gate, "Gate.open") != 0 && !sim.Has(gate, "Site");

    [Fact(DisplayName = "союзом распоряжается глава: рядовой не зовёт и не исключает, выходит сам, а уходит глава — союз распущен")]
    public void HeadRulesAlliance()
    {
        using var sim = Players(1, 2, 3);
        sim.Advance(10);
        Assert.Contains((1u, "allyOffered", 2u), sim.Tells(1, new AllyOrder { To = 2 }));
        Assert.Contains((1u, "allyFormed", 2u), sim.Tells(2, new AllyOrder { To = 1 }));
        // Рядовой зовёт третьего — отказ: приглашать может только глава. И исключать — тоже.
        Assert.Contains((2u, "allyNotHead", 0u), sim.Tells(2, new AllyOrder { To = 3 }));
        Assert.Contains((2u, "allyKickNotHead", 0u), sim.Tells(2, new BreakAllyOrder { To = 1 }));
        Assert.Equal([2u], sim.AlliesOf(1));
        // Глава исключает рядового — тот покидает союз целиком.
        var kicked = sim.Tells(1, new BreakAllyOrder { To = 2 });
        Assert.Contains((1u, "allyKicked", 2u), kicked);
        Assert.Contains((2u, "allyKickedBy", 1u), kicked);
        Assert.Empty(sim.AlliesOf(2));
        Assert.Equal(-1, sim.HeadOf(2));
        // Одиночке выходить неоткуда — и он слышит об этом.
        Assert.Contains((3u, "allyNoAlliance", 0u), sim.Tells(3, new ExitAllyOrder()));
        // Глава предложил, третий принял — и вышел сам: главе весть, сам он снова один.
        sim.Tells(1, new AllyOrder { To = 3 });
        Assert.Contains((3u, "allyFormed", 1u), sim.Tells(3, new AllyOrder { To = 1 }));
        var exited = sim.Tells(3, new ExitAllyOrder());
        Assert.Contains((3u, "allyExited", 0u), exited);
        Assert.Contains((1u, "allyLeftBy", 3u), exited);
        Assert.Empty(sim.AlliesOf(1));
        // Глава уходит сам — союз распускается целиком.
        sim.Tells(1, new AllyOrder { To = 3 });
        sim.Tells(3, new AllyOrder { To = 1 });
        var dissolved = sim.Tells(1, new ExitAllyOrder());
        Assert.Contains((1u, "allyDissolved", 0u), dissolved);
        Assert.Contains((3u, "allyDissolvedBy", 1u), dissolved);
        Assert.Empty(sim.AlliesOf(3));
    }

    [Fact(DisplayName = "стена союзника стрельбе не мешает, а чужая — по-прежнему укрытие")]
    public void AlliedWallIsNoCover()
    {
        var (sim, x, y) = Field(rules: new { protection = 0, techTree = false });
        using var _sim = sim;
        // Счета — сразу: правка, ставящая здание игроку без счёта, заводит его первым, и номер здания был бы не тот.
        foreach (var player in new uint[] { 1, 2, 3 })
            sim.SetCredits(player, 100);
        var victim = Spawn(sim, "infantry", 2, x + 3, y);
        Spawn(sim, "infantry", 1, x, y);
        var wall = Place(sim, "wall", x + 1, y, 3);
        // Стена третьего — чужая для стрелка: пули достаются ей, пехотинец за ней цел.
        sim.Advance(60);
        Assert.Equal(1, Health(sim, victim));
        Assert.True(Health(sim, wall) < 1);
        // Союз — и та же стена не мешает: своя стена — бруствер, через неё стреляют.
        sim.Offer(1, 3);
        Assert.Equal("allied", sim.Offer(3, 1));
        sim.Advance(50);
        Assert.True(Health(sim, victim) < 0.9);
    }

    /// <summary>Метки сигналов в мире: место и чья (pings).</summary>
    private static List<(double X, double Y, uint Player)> Pings(Sim sim, uint? viewer = null)
    {
        var view = viewer is { } who ? new VisionView(sim.Host, who) : null;
        return [.. sim.With(Names.Mark.Name, Names.Position.Name)
            .Where(entity => sim.Text(entity, "Mark.kind") == "ping" && (view is null || view.Entity(entity)))
            .Select(entity => (sim.At(entity).X, sim.At(entity).Y, (uint)sim.Num(entity, "Mark.player")))];
    }

    private const int PingLimit = 3;
    private const double PingWindow = 5, PingSeconds = 8;

    [Fact(DisplayName = "сигнал союзникам: метку видят сам игрок и союзник где угодно, чужой — нет; сигналами не засыпать, и они гаснут")]
    public void PingLimits()
    {
        using var sim = Players(1, 2, 3);
        sim.Seconds(0.5);
        sim.Offer(1, 2);
        sim.Offer(2, 1);
        sim.Seconds(0.5);
        // Точка в тумане у всех троих: сигнал — метка, а не обзор.
        var vision = sim.Host.Vision!;
        (double X, double Y) spot = (0, 0);
        for (var y = -60; y < 60 && spot == (0, 0); y += 7)
            for (var x = -60; x < 60; x += 7)
                if (!new uint[] { 1, 2, 3 }.Any(player => vision.Sees(player, x, y)))
                {
                    spot = (x + 0.5, y + 0.25);
                    break;
                }
        Assert.False(vision.Sees(2, spot.X, spot.Y));
        sim.Send(1, new PingOrder { X = spot.X, Y = spot.Y });
        sim.Seconds(0.3);
        Assert.Equal([(spot.X, spot.Y, 1u)], Pings(sim));
        Assert.Equal([(spot.X, spot.Y, 1u)], Pings(sim, 1));
        Assert.Equal([(spot.X, spot.Y, 1u)], Pings(sim, 2));
        Assert.Empty(Pings(sim, 3));
        // Обзора метка не даёт.
        Assert.False(vision.Sees(2, spot.X, spot.Y));
        // За карту и чаще положенного — нельзя.
        sim.Send(1, new PingOrder { X = 1e6, Y = 0 });
        for (var i = 0; i < PingLimit + 2; i++)
            sim.Send(1, new PingOrder { X = i, Y = i });
        sim.Seconds(0.3);
        Assert.Equal(PingLimit, Pings(sim).Count);
        // Прошло окно — можно снова; чужой игрок сигналит своим, и первым двум его метка не идёт.
        sim.Seconds(PingWindow);
        sim.Send(1, new PingOrder { X = 5, Y = 5 });
        sim.Send(3, new PingOrder { X = 7, Y = 7 });
        sim.Seconds(0.3);
        Assert.Equal(PingLimit + 2, Pings(sim).Count);
        Assert.DoesNotContain(Pings(sim, 2), ping => ping.Player == 3);
        Assert.Equal([(7.0, 7.0, 3u)], Pings(sim, 3));
        // Срок вышел — меток нет ни у кого.
        sim.Seconds(PingSeconds + 0.5);
        Assert.Empty(Pings(sim));
    }
}
