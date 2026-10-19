using Base.Host.Kernel;
using Base.Host.Sections.Research;
using Base.Shared.Data;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Ракетная шахта (silos.ts): боеголовку она собирает, как техцентр тему, — цена в кредитах и готовом из сети труб
/// списывается сразу, сборка идёт долей энергии (<see cref="Flight"/>), боеголовка одна за раз. Готовую игрок наводит на
/// любую точку карты: через launch секунд после приказа ракета стартует, до этого пуск можно отменить.
/// </summary>
internal static class Silos
{
    /// <summary>Почему сборку не начать: «busy» — шахта собирает, держит боеголовку или не своя, «credits», «materials».</summary>
    public const string Busy = "busy", Credits = "credits", Materials = "materials";

    /// <summary>Боеголовка шахты этого вида (warheadOf в silos.ts): у других зданий — null.</summary>
    public static WarheadSpec? WarheadOf(Call call, uint building) => call.BuildingType(building) is { } type ? Factory.Building(type)?.Warhead : null;

    /// <summary>Боеголовка вида «silo»: ракета несёт её, вид шахты один (WARHEAD).</summary>
    public static WarheadSpec Warhead => Core.Tables.Buildings["silo"].Warhead!;

    public static IEnumerable<(string Good, double Amount)> Items(Dictionary<string, double> materials) => materials.Select(pair => (pair.Key, pair.Value));

    /// <summary>Своя готовая шахта игрока (ownSilo): её боеголовка; нет — null.</summary>
    private static WarheadSpec? Own(Call call, uint player, uint silo) =>
        WarheadOf(call, silo) is { } warhead && call.Has(silo, S.Silo) && call.Has(silo, S.Building) && !call.Has(silo, S.Site) && call.IsOwn(player, silo) ? warhead : null;

    /// <summary>Что мешает начать сборку боеголовки в своей шахте (warheadLack); null — ничего.</summary>
    public static string? Lack(Call call, uint player, uint silo)
    {
        if (Own(call, player, silo) is not { } warhead)
            return Busy;
        if (call.Num(silo, S.Silo, Refs.R.SiloTicks) > 0 || call.Flag(silo, S.Silo, Refs.R.SiloReady))
            return Busy;
        if (Accounts.Credits(call, player) < warhead.Cost)
            return Credits;
        return new Networks(call).Has(silo, Items(warhead.Materials)) ? null : Materials;
    }

    /// <summary>Начинает сборку боеголовки в своей пустой шахте: кредиты и готовое списываются сразу (startWarhead).</summary>
    public static bool Start(Call call, uint player, uint silo)
    {
        if (Own(call, player, silo) is not { } warhead || Lack(call, player, silo) is not null || !Accounts.Pay(call, player, warhead.Cost))
            return false;
        new Networks(call).Take(silo, Items(warhead.Materials));
        call.Set(silo, S.Silo).Number(Refs.R.SiloProgress, 0).Number(Refs.R.SiloTicks, Core.Ticks(warhead.Seconds)).Number(Refs.R.SiloCost, warhead.Cost);
        return true;
    }

    /// <summary>Бросает сборку: цена возвращается целиком, вложенное время пропадает; готовую боеголовку так не разобрать (cancelWarhead).</summary>
    public static bool Cancel(Call call, uint player, uint silo)
    {
        if (Own(call, player, silo) is not { } warhead || call.Num(silo, S.Silo, Refs.R.SiloTicks) <= 0 || call.Flag(silo, S.Silo, Refs.R.SiloReady))
            return false;
        Accounts.Add(call, player, call.Num(silo, S.Silo, Refs.R.SiloCost));
        new Networks(call).Return(silo, Items(warhead.Materials));
        call.Set(silo, S.Silo).Number(Refs.R.SiloProgress, 0).Number(Refs.R.SiloTicks, 0).Number(Refs.R.SiloCost, 0);
        return true;
    }

    /// <summary>
    /// Приказ о пуске готовой боеголовки в тайл (x, y): ракета стартует через warhead.launch секунд. Наведённую можно навести
    /// заново, пока она не стартовала. Пуск — нападение: он снимает с игрока защиту новичка (orderLaunch).
    /// </summary>
    public static bool Launch(Call call, uint player, uint silo, int x, int y)
    {
        if (Own(call, player, silo) is null || !call.Flag(silo, S.Silo, Refs.R.SiloReady) || !Placement.InBounds(x, y))
            return false;
        if (call.Num(silo, S.Silo, Refs.R.SiloStart, -1) < 0)
            call.Set(silo, S.Silo).Number(Refs.R.SiloStart, call.Tick);
        call.Set(silo, S.Silo).Number(Refs.R.SiloToX, x + 0.5).Number(Refs.R.SiloToY, y + 0.5);
        Unprotect(call, player);
        return true;
    }

    /// <summary>Отменяет пуск, пока ракета не стартовала: боеголовка остаётся в шахте (abortLaunch).</summary>
    public static bool Abort(Call call, uint player, uint silo)
    {
        if (Own(call, player, silo) is null || call.Num(silo, S.Silo, Refs.R.SiloStart, -1) < 0)
            return false;
        call.Set(silo, S.Silo).Number(Refs.R.SiloStart, -1);
        return true;
    }

    /// <summary>Сколько тиков до старта ракеты у шахты в пуске (launchLeft); −1 — пуск не идёт.</summary>
    public static int Left(Call call, uint silo)
    {
        if (!call.Has(silo, S.Silo) || WarheadOf(call, silo) is not { } warhead)
            return -1;
        var start = call.Num(silo, S.Silo, Refs.R.SiloStart, -1);
        return start < 0 ? -1 : (int)Math.Max(0, start + Core.Ticks(warhead.Launch) - call.Tick);
    }

    /// <summary>Снимает защиту новичка, если она есть (unprotect).</summary>
    private static void Unprotect(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        if (account != 0 && call.Num(account, S.Player, S.PlayerSafe) > call.Tick)
            call.Set(account, S.Player).Number(S.PlayerSafe, 0);
    }
}
