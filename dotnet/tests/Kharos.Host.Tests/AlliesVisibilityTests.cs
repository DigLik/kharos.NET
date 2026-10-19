using Kharos.Engine.World;

namespace Kharos.Host.Tests;

/// <summary>
/// Союзы в сети (Д4, allies.test.ts — то, что о видимости): союз задаётся прямо в <c>Team</c> счёта, без приказов мода
/// ally (их и остальные тесты allies.test.ts — Kharos.Base.Tests/Sim/Players/AlliesTests). Союзнику — общий обзор и всё,
/// что видит хозяин; третьему — нет.
/// </summary>
public class AlliesVisibilityTests
{
    [Fact(DisplayName = "разведка у союзников общая: после /ally каждый видит базу другого со складами, чужой — по-прежнему нет")]
    public void AlliesShareScouting()
    {
        using var arena = new Arena();
        foreach (var player in (uint[])[1, 2, 3])
            arena.Account(player);
        arena.Unit(1, -60.5, 0.5);
        var theirs = new[] { arena.Unit(2, 60.5, 0.5), arena.Unit(2, 62.5, 1.5) };
        arena.Unit(3, 0.5, -60.5);
        var first = arena.Join(1);
        var third = arena.Join(3);
        arena.Join(2);
        arena.Settle();
        Assert.All(theirs, unit => Assert.False(first.Has(unit)));

        arena.Ally(1, 2);
        arena.Settle();
        // Все юниты союзника — как на ладони, и место вокруг них открыто в тумане; третий их не видит.
        Assert.All(theirs, unit => Assert.True(first.Has(unit)));
        Assert.True(arena.Host.Vision!.Sees(1, 60.5, 0.5));
        Assert.True(first.Sees(60.5, 0.5));
        Assert.All(theirs, unit => Assert.False(third.Has(unit)));
        Assert.False(third.Sees(60.5, 0.5));
        // Союзнику сущность идёт целиком, как хозяину: чем занят строитель, видно ему, но не чужому.
        var builder = arena.Unit(2, 61.5, 0.5);
        arena.Set(builder, "Repair", "target", theirs[0]);
        arena.Step();
        Assert.True(first.Has(builder, "Repair"));
        Assert.False(arena.View(3).Shows(arena.World, builder, arena.Id("Repair")));

        // Вышел из союза — и база союзника снова в тумане.
        arena.Alone(2);
        arena.Settle();
        Assert.All(theirs, unit => Assert.False(first.Has(unit)));
        Assert.False(first.Sees(60.5, 0.5));
    }

    [Fact(DisplayName = "союзнику идут счёт игрока и потоки в его трубах, а третьему — нет")]
    public void AlliesGetAccountAndBatches()
    {
        using var arena = new Arena();
        var accounts = new[] { arena.Account(1), arena.Account(2, credits: 500), arena.Account(3) };
        var first = arena.Join(1);
        var third = arena.Join(3);
        arena.Join(2);
        arena.Step();
        // Свой счёт есть у каждого, чужой не видит никто: счёт игрока — только ему и его союзникам.
        Assert.True(first.Has(accounts[0]));
        Assert.False(first.Has(accounts[1]));
        arena.Ally(1, 2);
        arena.Step();
        Assert.Equal(500, first.Number(accounts[1], "Player", "credits"));
        // Счёт меняется на ходу: союзнику он идёт так же, как свой, — не только в миг заключения союза.
        arena.Set(accounts[1], "Player", "credits", 800);
        // Что течёт по трубам второго: союзнику видно, куда и что он возит.
        var batch = arena.World.Spawn();
        arena.Set(batch, "Batch", "amount", 4);
        arena.Set(batch, "Owner", "player", 2);
        arena.Set(batch, "Sighting", "rule", Sightings.Allies);
        arena.Step();
        Assert.Equal(800, first.Number(accounts[1], "Player", "credits"));
        Assert.False(third.Has(accounts[1]));
        Assert.Single(first.With("Batch"));
        Assert.Empty(third.With("Batch"));
    }

    [Fact(DisplayName = "путь юнита союзника уходит союзнику, а чужому — нет")]
    public void AllyPathGoesToAllyOnly()
    {
        using var arena = new Arena(fog: false);
        foreach (var player in (uint[])[1, 2, 3])
            arena.Account(player);
        var unit = arena.Unit(2, 5.5, 5.5);
        arena.Set(unit, "Path", "goalX", 9);
        arena.SetBytes(unit, "Path", "points", new byte[32]);
        var first = arena.Join(1);
        var third = arena.Join(3);
        arena.Step();
        // Путь — только своим: по нему видно, куда идёт чужой отряд.
        Assert.True(first.Has(unit));
        Assert.False(first.Has(unit, "Path"));
        arena.Ally(1, 2);
        arena.Step();
        Assert.True(first.Has(unit, "Path"));
        Assert.Equal(9, first.Number(unit, "Path", "goalX"));
        Assert.False(third.Has(unit, "Path"));
        Assert.True(arena.View(1).Shows(arena.World, unit, Builtin.Path));
        Assert.False(arena.View(3).Shows(arena.World, unit, Builtin.Path));
    }

    [Fact]
    public void ListedMarkReachesOwnerAlliesAndViewersThroughFog()
    {
        using var arena = new Arena();
        foreach (var player in (uint[])[1, 2, 3, 4])
            arena.Account(player);
        arena.Ally(1, 2);
        var peers = new[] { arena.Join(1), arena.Join(2), arena.Join(3), arena.Join(4) };
        // Метка в тумане у всех: видят хозяин и союзник где угодно и тот, кто в списке; остальные — нет.
        var mark = arena.Spawn(1, 100.5, 100.25);
        arena.Set(mark, "Mark", "player", 1);
        arena.Set(mark, "Sighting", "rule", Sightings.Listed);
        arena.SetBytes(mark, "Sighting", "viewers", Arena.Players(4));
        arena.Step();
        Assert.Equal(new[] { true, true, false, true }, peers.Select(peer => peer.Has(mark)).ToArray());
        // Обзора метка не даёт, а Sighting в сеть не уходит.
        Assert.False(arena.Host.Vision!.Sees(2, 100.5, 100.25));
        Assert.False(peers[1].Has(mark, "Sighting"));
    }
}
