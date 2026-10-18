namespace Kharos.Base.Tests.Kernel;

/// <summary>
/// Игровой баланс (packages/tests/gameplay.test.ts), часть ядра: стартовые кредиты и награды, окупаемость шахты по
/// данным. Боевой треугольник — в разделе Production (С4), когда будет бой.
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

    [Fact(DisplayName = "боевой треугольник: у каждого юнита есть контр-юнит", Skip = "ждёт С5а: бой (spawnBattle, driveBattle)")]
    public void EveryUnitHasACounter()
    {
    }
}
