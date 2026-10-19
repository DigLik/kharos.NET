using Base.Shared;

namespace Kharos.Base.Tests.Kernel;

/// <summary>
/// Игровой баланс (packages/tests/gameplay.test.ts), часть ядра: стартовые кредиты и награды, окупаемость шахты по
/// данным. Боевой треугольник — бой (С5а).
/// </summary>
public sealed class GameplayTests
{
    /// <summary>Стартовые кредиты (STARTING_CREDITS) — партия skirmish.</summary>
    private static double StartingCredits => Data.All.Games["skirmish"].Credits;

    [Fact(DisplayName = "стартовых кредитов хватает на первый набор: электростанцию, генератор материи, шахту и грузовик")]
    public void StartingCreditsBuyTheFirstKit()
    {
        var buildings = Data.Buildings;
        var kit = buildings["generator"].Cost + buildings["matter"].Cost + buildings["mine"].Cost + Data.Units["truck"].Cost;
        Assert.True(kit <= StartingCredits);
        // Развернув MCV, игрок получает награду — её хватает ещё и на первый космопорт.
        Assert.True(StartingCredits + Data.All.Rewards["deploy"].Credits >= kit + buildings["spaceport"].Cost);
    }

    [Fact(DisplayName = "продажа сырья — основной доход: шахта с грузовиком окупается за минуты")]
    public void SellingOrePaysOffInMinutes()
    {
        var perSecond = Data.All.Deposits["metal"].Rate * Data.All.Resources["metal"].Price;
        Assert.True(perSecond > 0);
        // Стоимость шахты и грузовика отбивается потоком сырья быстрее двух минут.
        Assert.True((Data.Buildings["mine"].Cost + Data.Units["truck"].Cost) / perSecond < 120);
        // Грузовик увозит заметную партию, а шахта успевает её накопить.
        var truck = Data.Units["truck"].Inventory ?? 0;
        Assert.True(truck >= 20);
        Assert.True(Data.Buildings["mine"].Inventory > truck);
    }

    [Fact(DisplayName = "боевой треугольник: у каждого юнита есть контр-юнит")]
    public void EveryUnitHasACounter()
    {
        // Пехоту — дешёвый рой — бьют миниганы, осколки и разряд.
        Assert.True(Beats("buggy", "infantry"));
        Assert.True(Beats("tesla", "infantry"));
        Assert.True(Beats("tank", "infantry"));
        // Пехота сама бьёт ракетчиков (по ней ракета слаба) и авиацию.
        Assert.True(Beats("infantry", "rocketeer"));
        Assert.True(Beats("infantry", "drone"));
        // Ракетчик — против лёгкой техники и авиации.
        Assert.True(Beats("rocketeer", "buggy"));
        Assert.True(Beats("rocketeer", "lancer"));
        Assert.True(Beats("rocketeer", "gunship"));
        // Танк переживает ракеты и давит осколками; против танка — лазер и авиация.
        Assert.True(Beats("tank", "rocketeer"));
        Assert.True(Beats("lancer", "tank"));
        Assert.True(Beats("drone", "tank"));
        Assert.True(Beats("gunship", "tank"));
    }

    /// <summary>Цена отряда в бою: у каждой стороны столько юнитов, сколько в неё влезает.</summary>
    private const double DuelBudget = 2400;

    /// <summary>Дистанции между передними рядами в боях, по которым судит Beats.</summary>
    private static readonly int[] Gaps = [5, 7, 9];

    /// <summary>Побеждает ли первый отряд второй равной цены в большинстве боёв на ровном поле с разной дистанцией.</summary>
    private static bool Beats(string own, string foe) => Gaps.Count(gap => Wins(own, foe, gap)) > Gaps.Length / 2.0;

    /// <summary>Левый верхний тайл ровной проходимой площадки width×height (arena).</summary>
    private static (int X, int Y) Arena(Sim sim, int width, int height)
    {
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
            {
                var flat = true;
                for (var dy = 0; dy < height && flat; dy++)
                    for (var dx = 0; dx < width && flat; dx++)
                        flat = sim.Walkable(x + dx, y + dy);
                if (flat)
                    return (x, y);
            }
        throw new InvalidOperationException($"Не нашлось площадки {width}×{height}");
    }

    /// <summary>Бой двух отрядов равной цены на ровном поле: true — победил первый. gap — дистанция между передними рядами.</summary>
    private static bool Wins(string ownType, string foeType, int gap)
    {
        using var sim = Scene.Create();
        var ownCount = Math.Max(1, (int)Math.Floor(DuelBudget / Data.Units[ownType].Cost));
        var foeCount = Math.Max(1, (int)Math.Floor(DuelBudget / Data.Units[foeType].Cost));
        var spot = Arena(sim, 20 + (int)Math.Ceiling(Math.Max(ownCount, foeCount) / 3.0) * 2, 16);
        sim.SpawnBattle(1, spot.X + 14, spot.Y + 8, Enumerable.Repeat(ownType, ownCount).ToList(), Enumerable.Repeat(foeType, foeCount).ToList(), gap);
        for (double spent = 0; spent < 240; spent += 0.25)
        {
            if (!sim.DriveBattle(1))
                break;
            sim.Seconds(0.25);
        }
        var owners = sim.With(Names.Unit.Name, Names.Owner.Name).Select(sim.OwnerOf).ToList();
        return owners.Any(owner => owner == 1) && !owners.Any(owner => owner != 1 && owner != 0);
    }
}
