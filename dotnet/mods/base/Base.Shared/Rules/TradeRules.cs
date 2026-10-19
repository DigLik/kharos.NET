namespace Base.Shared.Rules;

/// <summary>Правила торговли, общие у хоста и клиента (trade.ts, ferry.ts): сроки рейсов, цена закупки, трюм корабля.</summary>
public static class TradeRules
{
    /// <summary>Сколько секунд проходит от погрузки последнего груза до денег: столько летит корабль (SELL_SECONDS).</summary>
    public const double SellSeconds = 20;

    /// <summary>
    /// Во сколько раз закупка с орбиты дороже продажи (BUY_MARKUP): клапан на случай, когда рядом нет нужного
    /// месторождения; своя добыча всегда выгоднее.
    /// </summary>
    public const double BuyMarkup = 3;

    /// <summary>Сколько секунд летит корабль с закупкой (BUY_SECONDS).</summary>
    public const double BuySeconds = SellSeconds;

    /// <summary>Сколько секунд летит корабль с подарком (TRANSFER_SECONDS).</summary>
    public const double TransferSeconds = SellSeconds;

    /// <summary>Наибольшее, что корабль берёт одного груза (TRANSFER_CAPACITY).</summary>
    public const double TransferCapacity = 500;

    /// <summary>Сколько стоит купить единицу ресурса с орбиты при цене price (buyPrice).</summary>
    public static double BuyPrice(double price) => price * BuyMarkup;

    /// <summary>Сколько тиков летит корабль этого космопорта: с уровнем рейс короче во столько же раз, во сколько больше трюм (flightTicks).</summary>
    public static int FlightTicks(double seconds, double level, double step) => (int)BuildRules.Round(seconds / BuildRules.LevelFactor(level) / step);
}
