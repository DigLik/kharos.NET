namespace Kharos.Host.Tests;

/// <summary>
/// Призраки чужих зданий (Д4, net.test.ts; buildMemory.ts): у подключённого — замершие сущности, у хоста — память
/// engine.memory по игрокам: её получает вернувшийся игрок, она переживает сохранение.
/// </summary>
public class BuildMemoryTests
{
    /// <summary>Глаза игрока 1 — два юнита рядом с (x, 0.5); здание игрока 2 — склад 3 × 3 с углом (2, 0).</summary>
    private sealed class Scene : IDisposable
    {
        public Scene(Arena? arena = null)
        {
            Arena = arena ?? new Arena();
            Eyes = [Arena.Unit(1, 0.5, 0.5), Arena.Unit(1, 0.5, 1.5)];
            Yard = Arena.Building(2, 2, 0, 3, 3);
            Arena.Set(Yard, "Stock", "metal", 30);
        }

        public Arena Arena { get; }
        public uint[] Eyes { get; }
        public uint Yard { get; }

        /// <summary>Все глаза — на x.</summary>
        public void MoveEyes(double x)
        {
            foreach (var eye in Eyes)
                Arena.Set(eye, "Position", "x", x);
        }

        public bool Remembers() => Arena.Host.Buildings.Remembers(1, Yard);

        public void Dispose() => Arena.Dispose();
    }

    [Fact(DisplayName = "чужое здание, ушедшее в туман, остаётся призраком, пока место не увидят снова")]
    public void ForeignBuildingStaysAsGhost()
    {
        using var scene = new Scene();
        var arena = scene.Arena;
        var first = arena.Join(1);
        arena.Step();
        Assert.True(first.Has(scene.Yard, "Building"));
        Assert.True(arena.View(1).Entity(scene.Yard));

        // Юниты ушли — хост здание больше не шлёт, а клиент его помнит.
        scene.MoveEyes(-60.5);
        arena.Settle();
        Assert.False(arena.View(1).Entity(scene.Yard));
        Assert.True(first.Has(scene.Yard, "Building"));

        // Здание снесли в тумане: клиент узнаёт об этом, только вернувшись.
        arena.World.Destroy(scene.Yard);
        arena.Step();
        Assert.True(first.Has(scene.Yard));
        scene.MoveEyes(0.5);
        arena.Settle();
        Assert.False(first.Has(scene.Yard));
    }

    [Fact]
    public void DeadGhostGoesWhenAnyFootprintTileIsSeen()
    {
        using var scene = new Scene();
        var arena = scene.Arena;
        var first = arena.Join(1);
        arena.Step();
        scene.MoveEyes(-60.5);
        arena.Settle();
        arena.World.Destroy(scene.Yard);
        // Видна только правая колонка основания (x = 4), угол (2, 0) — нет.
        scene.MoveEyes(11.5);
        arena.Settle();
        Assert.False(arena.Host.Vision!.Sees(1, 2.5, 0.5));
        Assert.True(arena.Host.Vision!.Sees(1, 4.5, 0.5));
        Assert.False(first.Has(scene.Yard));
        Assert.False(scene.Remembers());
    }

    [Fact(DisplayName = "хост забывает призрак, когда место снова на виду, а здание стоит")]
    public void HostForgetsGhostSeenAgain()
    {
        using var scene = new Scene();
        var arena = scene.Arena;
        arena.Host.Join(1);
        arena.Step(8);
        Assert.False(scene.Remembers());
        scene.MoveEyes(-60.5);
        arena.Step(8);
        Assert.True(scene.Remembers());
        scene.MoveEyes(0.5);
        arena.Step(8);
        Assert.False(scene.Remembers());
    }

    [Fact(DisplayName = "вернувшийся игрок видит призраками чужие здания, разведанные раньше, — и после перезапуска сервера")]
    public void ReturningPlayerGetsGhostsAlsoAfterRestart()
    {
        using var scene = new Scene();
        var arena = scene.Arena;
        arena.Set(scene.Yard, "Building", "level", 2);
        var first = arena.Join(1);
        arena.Step(8);
        scene.MoveEyes(-60.5);
        arena.Step(8);
        arena.Leave(first);

        // Здание в тумане: новое подключение его не видит, но хост помнит, каким игрок видел его последний раз.
        var again = arena.Join(1);
        Assert.False(arena.View(1).Entity(scene.Yard));
        Assert.True(again.Has(scene.Yard, "Building"));
        Assert.Equal(2, again.Number(scene.Yard, "Building", "level"));
        Assert.Equal(2, again.Number(scene.Yard, "Position", "x"));
        Assert.Equal(0, again.Number(scene.Yard, "Position", "y"));
        // Чужой склад — тайна хозяина и в памяти.
        Assert.True(arena.World.Has(scene.Yard, arena.Id("Stock")));
        Assert.False(again.Has(scene.Yard, "Stock"));
        arena.Leave(again);

        using (var restarted = arena.Reload())
        {
            Assert.True(restarted.Host.Buildings.Remembers(1, scene.Yard));
            var peer = restarted.Join(1);
            Assert.True(peer.Has(scene.Yard, "Building"));
            Assert.False(peer.Has(scene.Yard, "Stock"));
        }

        // Здание снесли, и юниты игрока увидели пустое место, пока его самого не было: призрака больше нет.
        arena.World.Destroy(scene.Yard);
        arena.Step(8);
        var absent = arena.Join(1);
        Assert.True(absent.Has(scene.Yard));
        arena.Leave(absent);
        scene.MoveEyes(0.5);
        arena.Step(8);
        scene.MoveEyes(-60.5);
        arena.Step(8);
        var back = arena.Join(1);
        Assert.False(back.Has(scene.Yard));
    }

    [Fact]
    public void RememberedGhostSeenAgainArrivesAsItIsNow()
    {
        using var scene = new Scene();
        var arena = scene.Arena;
        var first = arena.Join(1);
        arena.Step(8);
        scene.MoveEyes(-60.5);
        arena.Step(8);
        arena.Leave(first);
        // В тумане здание выросло: призрак — прежний, а увиденное снова — каким стало.
        arena.Set(scene.Yard, "Building", "level", 3);
        var again = arena.Join(1);
        Assert.Equal(1, again.Number(scene.Yard, "Building", "level"));
        arena.Step();
        Assert.Equal(1, again.Number(scene.Yard, "Building", "level"));
        scene.MoveEyes(0.5);
        arena.Settle();
        Assert.Equal(3, again.Number(scene.Yard, "Building", "level"));
        Assert.False(again.Has(scene.Yard, "Stock"));
        // Подарен игроку — уходит ему целиком.
        arena.Set(scene.Yard, "Owner", "player", 1);
        arena.Step();
        Assert.Equal(30, again.Number(scene.Yard, "Stock", "metal"));
    }
}
