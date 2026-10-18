using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Host.Tests;

/// <summary>
/// Обзор движка (Д4, vision.test.ts): глаза Sight, здания по середине основания, вспышки Reveal, общая разведка
/// союзников по Team, сохранение разведанного, партия без тумана, инструмент 15 Sees.
/// </summary>
public class VisionRulesTests
{
    [Fact(DisplayName = "обзор: разведчик видит дальше пехоты, стрелок — дальше своего оружия, радар — дальше всех зданий", Skip = "ждёт Б1")]
    public void SightNumbersComeFromData()
    {
        // Радиус обзора — данные мода (unitSight, buildingSight): base ставит Sight.radius, движок его только читает.
    }

    [Fact(DisplayName = "игрок видит вокруг своих юнитов, а ушедший юнит оставляет за собой разведанное, но не видимое")]
    public void UnitLeavesExploredBehind()
    {
        using var arena = new Arena();
        const double sight = 7;
        var scout = arena.Unit(1, 0.5, 0.5, sight);
        arena.Step();
        var vision = arena.Host.Vision!;
        Assert.True(vision.Sees(1, 0, 0));
        Assert.True(vision.Sees(1, sight - 1, 0));
        Assert.False(vision.Sees(1, sight + 2, 0));
        Assert.False(vision.Explored(1, 30, 30));
        // Другой игрок здесь ничего не видит.
        Assert.False(vision.Sees(2, 0, 0));

        arena.Set(scout, "Position", "x", 40.5);
        // Обзор пересчитывается раз в несколько тиков, системой engine.vision.
        arena.Step(5);
        Assert.False(vision.Sees(1, 0, 0));
        Assert.True(vision.Explored(1, 0, 0));
        Assert.True(vision.Sees(1, 40, 0));
    }

    [Fact(DisplayName = "чужое видно, только пока оно в обзоре: здание — хотя бы краем основания")]
    public void ForeignShownWhileInSightBuildingByEdge()
    {
        using var arena = new Arena();
        const int sight = 7;
        arena.Unit(1, 0.5, 0.5, sight);
        var near = arena.Unit(2, 3.5, 0.5);
        var far = arena.Unit(2, 0.5, 30.5);
        // Край склада — в обзоре, его середина — нет.
        var edge = arena.Building(2, sight - 1, -1, 3, 3);
        arena.Step();
        Assert.False(arena.Host.Vision!.Sees(1, sight + 1.5, 0.5));
        var one = arena.View(1);
        Assert.True(one.Entity(near));
        Assert.False(one.Entity(far));
        Assert.True(one.Entity(edge));
        // Своё видно всегда.
        Assert.True(arena.View(2).Entity(far));
    }

    [Fact(DisplayName = "разведанное переживает сохранение, а видимое — нет: после загрузки видно только то, что видят юниты")]
    public void ExploredSurvivesSave()
    {
        using var arena = new Arena();
        arena.Host.Join(1);
        var scout = arena.Unit(1, 0.5, 0.5);
        arena.Step();
        arena.Set(scout, "Position", "x", 60.5);
        arena.Step(4);
        Assert.True(arena.Host.Vision!.Explored(1, 0, 0));

        using var loaded = arena.Reload();
        var vision = loaded.Host.Vision!;
        Assert.True(vision.Explored(1, 0, 0));
        Assert.False(vision.Sees(1, 0, 0));
        Assert.True(vision.Sees(1, 60, 0));
        Assert.False(vision.Explored(1, -60, 0));
    }

    [Fact(DisplayName = "без тумана войны видно всё, и параметр переживает сохранение")]
    public void NoFogShowsEverything()
    {
        using var arena = new Arena(fog: false);
        arena.Unit(1, 0.5, 0.5);
        var far = arena.Unit(2, 0.5, 80.5);
        arena.Step();
        Assert.Null(arena.Host.Vision);
        Assert.True(arena.View(1).Sees(100, -100));
        Assert.True(arena.View(1).Entity(far));
        // У игрока, у которого на карте ничего нет, — тоже.
        Assert.True(arena.View(3).Sees(0, 0));
        Assert.Equal(new byte[] { 2, 2 }, Sees(arena.Host, 3, (0, 0), (100, -100)));
        using var loaded = arena.Reload();
        Assert.Null(loaded.Host.Vision);
        Assert.True(loaded.View(1).Sees(100, -100));
    }

    [Fact]
    public void BuildingSeesFromFootprintCentreAndMountedEyeIsBlind()
    {
        using var arena = new Arena();
        // Основание 4 × 4 с углом (10, 10): круг радиуса 2 — от (12, 12).
        arena.Building(1, 10, 10, 4, 4, sight: 2);
        var carrier = arena.Unit(1, -40.5, 0.5, sight: 0);
        var turret = arena.Unit(1, -40.5, 0.5, sight: 9);
        arena.Set(turret, "Mount", "parent", carrier);
        arena.Step();
        var vision = arena.Host.Vision!;
        Assert.True(vision.Sees(1, 13.5, 12.5));
        Assert.False(vision.Sees(1, 9.5, 12.5));
        // Глаз на носителе не видит: за него смотрит носитель (TS: Attached).
        Assert.False(vision.Sees(1, -40, 0));
    }

    [Fact]
    public void RevealFlashesEveryoneOrItsOwnerUntilItEnds()
    {
        using var arena = new Arena();
        arena.Account(1);
        arena.Account(2);
        arena.Account(3);
        arena.Ally(1, 2);
        // Вспышка радара хозяину (и союзнику), ядерная — всем; гаснут на тике until.
        var radar = arena.Spawn(1, 50.5, 50.5);
        arena.Set(radar, "Reveal", "radius", 6);
        arena.Set(radar, "Reveal", "until", 20);
        var glare = arena.Spawn(0, -50.5, -50.5);
        arena.Set(glare, "Reveal", "radius", 4);
        arena.Set(glare, "Reveal", "until", 20);
        arena.Set(glare, "Reveal", "all", 1);
        arena.Step();
        var vision = arena.Host.Vision!;
        Assert.True(vision.Sees(1, 53, 50));
        Assert.True(vision.Sees(2, 53, 50));
        Assert.False(vision.Sees(3, 53, 50));
        foreach (var player in (uint[])[1, 2, 3])
            Assert.True(vision.Sees(player, -50, -50));
        arena.Step(20);
        Assert.False(vision.Sees(1, 53, 50));
        Assert.False(vision.Sees(3, -50, -50));
        Assert.True(vision.Explored(3, -50, -50));
    }

    [Fact]
    public void AlliesShareSightUntilAllianceEnds()
    {
        using var arena = new Arena();
        arena.Account(1);
        arena.Account(2);
        arena.Account(3);
        arena.Unit(2, 60.5, 0.5);
        arena.Step();
        var vision = arena.Host.Vision!;
        Assert.False(vision.Sees(1, 60, 0));
        arena.Ally(1, 2);
        arena.Settle();
        Assert.True(vision.Sees(1, 60, 0));
        Assert.False(vision.Sees(3, 60, 0));
        // Союз с одиночкой без head (−1) — не союз: одинаковый head только ≥ 0.
        arena.Alone(1);
        arena.Alone(2);
        arena.Settle();
        Assert.False(vision.Sees(1, 60, 0));
        Assert.True(vision.Explored(1, 60, 0));
        Assert.True(vision.Teams.Head(1) < 0);
    }

    [Fact]
    public void SeesToolAnswersByVision()
    {
        using var arena = new Arena();
        arena.Unit(1, 0.5, 0.5);
        arena.Step();
        var scout = arena.Unit(1, 30.5, 0.5);
        arena.Step(4);
        arena.World.Destroy(scout);
        arena.Step(4);
        // 2 — видит, 1 — разведано, 0 — нет.
        Assert.Equal(new byte[] { 2, 1, 0 }, Sees(arena.Host, 1, (0, 0), (30, 0), (-60, 0)));
    }

    /// <summary>Инструмент 15 Sees.</summary>
    private static byte[] Sees(Host host, uint player, params (double X, double Y)[] points)
    {
        var input = new byte[8 + points.Length * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(input, player);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), (uint)points.Length);
        for (var i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(input.AsSpan(8 + i * 16), points[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(input.AsSpan(16 + i * 16), points[i].Y);
        }
        var output = new byte[points.Length];
        Assert.Equal(points.Length, host.Tools.Call(null, ModAbi.ToolSees, input, output));
        return output;
    }
}
