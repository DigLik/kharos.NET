using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Base.Tests.Missiles;
using Kharos.Client.Game;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.ClientAlerts;

/// <summary>
/// Уведомления (packages/tests/alerts.test.ts): клиент base на реплике хоста (Viewer) ведёт ленту слева под верхней
/// полосой — строки «alert:N» с ключами hud.alerts.*, звук сигнала и щелчок, что ведёт камеру к месту.
/// </summary>
public sealed class AlertsTests
{
    private static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    private static string T(string key, params string[] args) => Kharos.Base.Tests.Client.Hud.RequestButtonsTests.T(key, args);

    /// <summary>Лента, как её видел зритель: строки по порядку появления (id → ключ уведомления).</summary>
    private sealed class Feed(Viewer viewer)
    {
        private readonly Dictionary<string, string> _seen = [];

        public List<string> Texts => [.. _seen.Values];

        /// <summary>Сколько кадров/секунд прошло с партией; новые строки ленты записываются.</summary>
        public void Run(double seconds)
        {
            var left = seconds;
            // Шаг короче жизни строки (8 с) и сигнала: ничего не пропустить.
            while (left > 1e-9)
            {
                var step = Math.Min(0.2, left);
                viewer.Run(step);
                left -= step;
                Take();
            }
        }

        public void Take()
        {
            foreach (var id in viewer.Ui.Ids().Where(id => id.StartsWith("alert:", StringComparison.Ordinal)))
                if (!_seen.ContainsKey(id) && viewer.Ui.Text(id) is { } text)
                    _seen[id] = Key(text);
        }

        public string[] Keys(string kind) => [.. _seen.Values.Where(text => text == kind)];

        private static string Key(string text) =>
            new[] { "spotted", "baseAttacked", "unitsAttacked", "oreFound", "nukeLaunched", "nukeIncoming", "nukeImminent", "nukeIntercepted", "nukeDowned", "allyPing", "ownPing" }
                .First(key => T("hud.alerts." + key) == text);
    }

    private static Sim Arena(bool fog = false, object? land = null, int size = 256)
    {
        var sim = Sim.Create(new SimOptions { Game = "frames", Size = size, Fog = fog, Land = land ?? new { blank = true }, Players = [1, 2] });
        sim.SetCredits(1, 1000);
        return sim;
    }

    [Fact(DisplayName = "о замеченном противнике предупреждают не чаще раза в 12 секунд")]
    public void SpottedAtMostEveryTwelveSeconds()
    {
        SkipUnless();
        using var sim = Arena();
        sim.PutUnit("infantry", 1, 3.5, 3.5);
        using var viewer = sim.Viewer(1);
        viewer.Camera = new CameraState(10, 10, 16);
        var feed = new Feed(viewer);
        feed.Run(0.5);
        Assert.Empty(feed.Texts);
        sim.PutUnit("infantry", 2, 30.5, 30.5);
        feed.Run(0.3);
        Assert.Equal(["spotted"], feed.Texts);
        Assert.True(viewer.Sounds.Played("spotted"));
        // Ещё один враг через 5 секунд — тихо.
        feed.Run(5);
        sim.PutUnit("infantry", 2, 31.5, 30.5);
        feed.Run(0.3);
        Assert.Single(feed.Texts);
        // Через 12 — снова.
        feed.Run(8);
        sim.PutUnit("infantry", 2, 32.5, 30.5);
        feed.Run(0.3);
        Assert.Equal(["spotted", "spotted"], feed.Texts);
        Assert.Empty(viewer.Log);
    }

    /// <summary>Танк игрока 1 с запасом прочности и вражеский пехотинец рядом: враг стреляет, танк терпит.</summary>
    private static (uint Tank, uint Foe) Skirmish(Sim sim)
    {
        var tank = sim.PutUnit("tank", 1, 10.5, 10.5);
        sim.Set(tank, "Health.max", 100000);
        sim.Set(tank, "Health.value", 100000);
        return (tank, Foe(sim));
    }

    private static uint Foe(Sim sim)
    {
        var foe = sim.PutUnit("infantry", 2, 15.5, 10.5);
        sim.Set(foe, "Health.max", 100000);
        sim.Set(foe, "Health.value", 100000);
        return foe;
    }

    [Fact(DisplayName = "об атаке предупреждают раз, а снова — когда враги пропали из виду и напали опять")]
    public void AttackWarnsOnceAndAgainAfterFoesLeft()
    {
        SkipUnless();
        using var sim = Arena();
        var (tank, foe) = Skirmish(sim);
        using var viewer = sim.Viewer(1);
        viewer.Camera = new CameraState(12, 10, 16);
        var feed = new Feed(viewer);
        feed.Run(4);
        Assert.True(sim.HealthOf(tank) < 100000, "враг не стрелял");
        Assert.Single(feed.Keys("unitsAttacked"));
        feed.Run(3);
        Assert.Single(feed.Keys("unitsAttacked"));
        // Враг пропал из виду: флаг сброшен. Вернулся и снова бьёт — снова тревога.
        sim.Erase(foe);
        feed.Run(0.5);
        Foe(sim);
        feed.Run(4);
        Assert.Equal(2, feed.Keys("unitsAttacked").Length);
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "урон без чужого выстрела рядом — не атака: прочность падает и от нехватки энергии")]
    public void DamageWithoutShotIsNotAnAttack()
    {
        SkipUnless();
        using var sim = Arena();
        var tank = sim.PutUnit("tank", 1, 10.5, 10.5);
        sim.PutUnit("infantry", 2, 90.5, 90.5);
        using var viewer = sim.Viewer(1);
        viewer.Camera = new CameraState(12, 10, 16);
        var feed = new Feed(viewer);
        feed.Run(0.5);
        sim.Set(tank, "Health.value", sim.HealthOf(tank) - 1);
        feed.Run(0.5);
        Assert.DoesNotContain("unitsAttacked", feed.Texts);
        Assert.DoesNotContain("baseAttacked", feed.Texts);
        Assert.Empty(viewer.Log);
    }

    /// <summary>Скаут игрока 1 стоит у месторождения; карта пустая, обзор есть только вокруг него.</summary>
    private static (Sim Sim, DepositSpot[] Spots) Scouting()
    {
        var sim = Arena(fog: true, size: 256);
        var spots = new List<DepositSpot>();
        foreach (var (x, y) in new[] { (-60, -30), (-10, -30), (40, -30), (-10, 30) })
        {
            sim.Paint(x, y, 8, new Brush { Terrain = (int)Kharos.Engine.Terrain.TerrainType.Rock, Tier = 1, Cliff = false });
            sim.PutDeposit("metal", x, y, 300);
            spots.Add(new DepositSpot(x, y, "metal", 300));
        }
        Assert.Equal(4, sim.Deposits().Count);
        sim.PutUnit("buggy", 1, spots[0].X + 1.5, spots[0].Y + 1.5);
        sim.Advance(40);
        return (sim, [.. spots]);
    }

    private static void Scout(Sim sim, DepositSpot spot) => sim.PutUnit("buggy", 1, spot.X + 1.5, spot.Y + 1.5);


    [Fact(DisplayName = "о найденном месторождении сообщают, когда игрок узнал новое; знакомые при входе в мир — не новость")]
    public void FoundWhenPlayerLearnsNewDeposit()
    {
        SkipUnless();
        var (sim, spots) = Scouting();
        using var _ = sim;
        using var viewer = sim.Viewer(1);
        viewer.Camera = new CameraState(spots[0].X + 1, spots[0].Y + 1, 16);
        var feed = new Feed(viewer);
        feed.Run(1);
        // Знакомое при входе — не новость.
        Assert.Empty(feed.Texts);
        Scout(sim, spots[1]);
        feed.Run(1.5);
        Assert.Equal(["oreFound"], feed.Texts);
        Assert.True(viewer.Sounds.Played("found"));
        // Следом ещё одно — молча: армия в походе находит их пачками.
        Scout(sim, spots[2]);
        feed.Run(1);
        Assert.Single(feed.Texts);
        feed.Run(6);
        Scout(sim, spots[3]);
        feed.Run(1.5);
        Assert.Equal(["oreFound", "oreFound"], feed.Texts);
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "слой в пути не выдаёт знакомое месторождение за находку: иначе пиликает каждый перезаход")]
    public void RejoiningDoesNotReportKnownDeposits()
    {
        SkipUnless();
        var (sim, spots) = Scouting();
        using var _ = sim;
        using var first = sim.Viewer(1);
        first.Camera = new CameraState(spots[0].X + 1, spots[0].Y + 1, 16);
        var before = new Feed(first);
        before.Run(0.5);
        Scout(sim, spots[1]);
        before.Run(1.5);
        Assert.Equal(["oreFound"], before.Texts);
        // Перезаход: новый клиент получает мир вместе со слоем, где уже оба разведанных месторождения.
        using var second = sim.Viewer(1);
        second.Camera = new CameraState(spots[1].X + 1, spots[1].Y + 1, 16);
        var after = new Feed(second);
        after.Run(2);
        Assert.Empty(after.Texts);
        // А разведанное уже в этом заходе — по-прежнему находка, и именно оно.
        Scout(sim, spots[2]);
        after.Run(1.5);
        Assert.Equal(["oreFound"], after.Texts);
        Assert.Empty(first.Log);
        Assert.Empty(second.Log);
    }

    [Fact(DisplayName = "чужая ядерная ракета — сирена всем; точка удара — первый сигнал, а за WARNING секунд до удара — второй")]
    public void FoeNukeSirenAndStrikeSignals()
    {
        Scene.SkipUnlessAvailable();
        SkipUnless();
        using var sim = Sim.Create(new SimOptions { Game = "frames", Size = 512, Land = Scene.Land, Rules = Pit.Rules, Players = [1, 2, 3] });
        var camp = Pit.Base(sim, 2, "silo", -200, 0);
        sim.Set(camp.Building, "Silo.ready", 1);
        var target = Pit.Spot(sim, camp.X + 200, camp.Y);
        sim.PutBuilding("generator", target.X, target.Y, 1);
        using var viewer = sim.Viewer(1);
        viewer.Camera = new CameraState(target.X, target.Y, 16);
        var feed = new Feed(viewer);
        feed.Run(0.5);
        Assert.Empty(feed.Texts);
        sim.Send(2, new LaunchOrder { Silo = camp.Building, X = target.X + 1, Y = target.Y + 1 });
        feed.Run(Pit.Warhead.Launch + 1);
        Assert.Contains("nukeLaunched", feed.Texts);
        Assert.Contains("nukeIncoming", feed.Texts);
        Assert.DoesNotContain("nukeImminent", feed.Texts);
        Assert.True(viewer.Sounds.Played("nuke"));
        Assert.False(viewer.Sounds.Played("nukeClose"));
        // Осталось меньше WARNING — второй сигнал, один раз.
        Assert.True(viewer.Until(() => sim.Missiles().Count == 0 || sim.Marks("strike").Any(mark => (mark.Until - sim.Tick) * Sim.Dt <= MissileRules.Warning - 1), 60));
        feed.Run(1);
        Assert.Single(feed.Keys("nukeImminent"));
        Assert.Equal("nukeImminent", feed.Texts.Last());
        Assert.True(viewer.Sounds.Played("nukeClose"));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "тревоги: щелчок по строке ведёт камеру к месту")]
    public void ClickingAnAlertFliesTheCamera()
    {
        SkipUnless();
        // Игра skirmish: у неё камера мода (rts), у frames камеры нет.
        using var sim = Sim.Create(new SimOptions { Game = "skirmish", Players = [1, 2], Fog = false });
        sim.Erase([.. sim.Of(2, "Unit")]);
        using var viewer = sim.Viewer(1);
        viewer.Run(1);
        var (x, y) = sim.At(sim.Of(1, "Unit").First());
        var foe = sim.PutUnit("infantry", 2, x + 20.5, y + 20.5);
        var feed = new Feed(viewer);
        feed.Run(0.5);
        var (fx, fy) = sim.At(foe);
        Assert.Equal(["spotted"], feed.Texts);
        viewer.Click(viewer.Ui.Ids().First(item => item.StartsWith("alert:", StringComparison.Ordinal)));
        var start = viewer.Last.View;
        viewer.Run(4);
        Assert.True(Math.Abs(viewer.Last.View.X - start.X) > 1, $"камера не двинулась: старт ({start.X}, {start.Y}), цель ({fx}, {fy}), стала ({viewer.Last.View.X}, {viewer.Last.View.Y})");
        var view = viewer.Last.View;
        Assert.True(Math.Abs(view.X - fx) < 1.5, $"старт ({start.X}, {start.Y}), цель ({fx}, {fy}), стала ({view.X}, {view.Y}); юнит ({x}, {y})");
        Assert.InRange((double)view.Y, fy - 8, fy + 8);
        Assert.Empty(viewer.Log);
    }
}
