using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using static Kharos.Base.Tests.Mining.MiningScene;

namespace Kharos.Base.Tests.Mining;

/// <summary>
/// Слой месторождений (packages/tests/depositLayer.test.ts, часть про генератор и сохранение): месторождения — предметы
/// слоя deposits, которые генератор кладёт при начале партии. Рассылка клиенту и туман — сеть (Kharos.Host.Tests, Д4).
/// </summary>
public sealed class DepositLayerTests
{
    /// <summary>Клетка, где генератор ничего не положил (emptyCell).</summary>
    private static (int X, int Y) EmptyCell(Sim sim)
    {
        for (var cellY = -3; cellY < 3; cellY++)
            for (var cellX = -3; cellX < 3; cellX++)
                if (sim.DepositsIn(cellX, cellY).Count == 0)
                    return (cellX, cellY);
        throw new InvalidOperationException("нет пустой клетки");
    }

    [Fact(DisplayName = "кисть и правка карты месторождений не создают и не двигают")]
    public void BrushAndLandEditsNeitherCreateNorMoveDeposits()
    {
        using var sim = Small();
        // Клетка, где генератор ничего не положил: туда кисть нарисует скалу.
        var (cellX, cellY) = EmptyCell(sim);
        var (x, y) = (cellX * Sim.DepositCell, cellY * Sim.DepositCell);
        // Вся клетка — ровная скала: генератор положил бы сюда месторождение, но клетка уже взята пустой.
        var rock = new Brush { Terrain = (int)TerrainType.Rock, Tier = 1, Cliff = false };
        for (var i = 0; i < Sim.DepositCell; i += 9)
            for (var j = 0; j < Sim.DepositCell; j += 9)
                sim.Paint(x + i + 4, y + j + 4, 9, rock);
        Assert.Empty(sim.DepositsIn(cellX, cellY));

        // Правка игрой — так же: месторождение стоит там, где было, как бы ни менялась земля под ним.
        using var other = Small();
        var before = other.DepositsIn(1, 1);
        Assert.NotEmpty(before);
        foreach (var spot in before)
            other.Paint(spot.X, spot.Y, 1, new Brush { Terrain = (int)TerrainType.Sand, Biome = 0, Tier = 0, Cliff = false });
        Assert.Equal(before, other.DepositsIn(1, 1));
    }

    [Fact(DisplayName = "слой месторождений — в сохранении: и правки, и клетки, взятые до правок карты")]
    public void DepositLayerIsInTheSave()
    {
        using var sim = Small();
        var (cellX, cellY) = EmptyCell(sim);
        var (x, y) = (cellX * Sim.DepositCell + 20, cellY * Sim.DepositCell + 20);
        sim.Paint(x, y, 15, new Brush { Terrain = (int)TerrainType.Rock, Tier = 1, Cliff = false });
        sim.PutDeposit("kharite", x, y, 77);
        Assert.Equal([new DepositSpot(x, y, "kharite", 77)], sim.DepositsIn(cellX, cellY));
        using var again = sim.ThroughSave();
        Assert.Equal([new DepositSpot(x, y, "kharite", 77)], again.DepositsIn(cellX, cellY));
        // Загрузка не зовёт генератор заново: слой тот же до последнего месторождения.
        Assert.Equal(sim.Deposits(), again.Deposits());
    }

    [Fact(DisplayName = "правки месторождений из прежних сохранений переносятся в слой", Skip = "устарело: сохранений TS с правками в сущностях Deposit (kind, reserve, gone) у .NET нет — слой и сущность Deposit были такими с самого начала")]
    public void LegacyDepositEditsMoveIntoTheLayer()
    {
    }

    [Fact(DisplayName = "генератор месторождений при начале партии: все ячейки карты, по эталону sim-ts 1024²")]
    public void GeneratorMatchesTsForTheWholeMap()
    {
        using var sim = Create();
        var golden = DepositGolden.Map1024;
        var spots = sim.Deposits().OrderBy(spot => spot.Y).ThenBy(spot => spot.X).ToList();
        Assert.Equal(golden.Count, spots.Count);
        Assert.Equal(golden, spots);
    }

    [Fact(DisplayName = "богатство местности растит запас месторождений, шанс — их число")]
    public void LandRichnessAndChanceApply()
    {
        using var plain = Scene.Create(128, MiningScene.NoTree);
        var generous = Sim.Create(new SimOptions { Size = 128, Land = new { depositRichness = 2.0, depositChance = 1.0 }, Rules = MiningScene.NoTree });
        using var _ = generous;
        Assert.True(generous.Deposits().Count >= plain.Deposits().Count);
        // Одно и то же место и вид даёт запас вдвое больше.
        foreach (var spot in plain.Deposits())
        {
            var twin = generous.DepositAt(spot.X, spot.Y);
            Assert.NotNull(twin);
            Assert.Equal(spot.Kind, twin.Kind);
            Assert.InRange(twin.Reserve, spot.Reserve * 2 - 1, spot.Reserve * 2 + 1);
        }
    }
}
