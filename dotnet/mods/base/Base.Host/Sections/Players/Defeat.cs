using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Kernel;
using Base.Host.Sections.Mining;
using Base.Host.Sections.Trade;
using Base.Shared.Signals;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Поражение (defeat.ts): проиграл игрок, у которого не осталось ни одного готового здания и нет MCV. Сдача (surrender) —
/// волна взрывов от главного здания, уничтожение своих юнитов (scuttle) — гибель на месте. Начать заново (событие
/// движка player_respawn, restart в host.ts) можно проигравшему, а по велению администратора — всякому: остатки базы
/// исчезают, союзы забываются, и игрок получает новое стартовое.
/// </summary>
internal static class Defeat
{
    /// <summary>За сколько секунд волна взрывов при сдаче доходит от главного здания на тайл дальше (BLAST_WAVE).</summary>
    private const double BlastWave = 0.12;

    /// <summary>Первый взрыв — не сразу (BLAST_DELAY).</summary>
    private const double BlastDelay = 0.6;

    /// <summary>Разброс взрывов одной волны, секунды (BLAST_JITTER).</summary>
    private const double BlastJitter = 0.5;

    /// <summary>MCV: пока он есть, игрок не проиграл.</summary>
    public const string Mcv = "mcv";

    /// <summary>
    /// Проиграл ли игрок (isDefeated): ни одного здания, кроме недостроенного, и нет MCV. Игрока, которого в мире нет
    /// (нет счёта), проигравшим не считают.
    /// </summary>
    public static bool IsDefeated(Call call, uint player)
    {
        if (Accounts.Find(call, player) == 0)
            return false;
        foreach (var entity in Core.All(S.Unit))
            if (call.OwnerOf(entity) == player && call.UnitType(entity) == Mcv)
                return false;
        foreach (var entity in Core.All(S.Building))
            if (call.OwnerOf(entity) == player && !call.Has(entity, S.Site))
                return false;
        return true;
    }

    /// <summary>Всё игрока в мире, кроме счёта и покрытия (им пользуются и другие), по номеру.</summary>
    private static List<uint> Owned(Call call, uint player)
    {
        var list = new List<uint>();
        foreach (var entity in Core.All(S.Owner))
            if (call.OwnerOf(entity) == player && !call.Has(entity, S.Player) && !call.Has(entity, S.Pave))
                list.Add(entity);
        list.Sort();
        return list;
    }

    /// <summary>Обнуляет кредиты игрока (и накопленные доли).</summary>
    private static void Bankrupt(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        if (account != 0)
            call.Set(account, S.Player).Number(S.PlayerCredits, 0).Number(S.PlayerEarned, 0);
    }

    /// <summary>
    /// Убирает игрока из мира (wipePlayer): юниты и здания исчезают, груз с их складов падает дропами, кредиты — в ноль,
    /// союзы и предложения забываются. Покрытие остаётся. Турели уходят вместе с носителем.
    /// </summary>
    public static void Wipe(Call call, uint player)
    {
        var gone = Owned(call, player).Where(entity => call.Num(entity, S.Mount, S.MountParent) == 0).ToList();
        foreach (var entity in gone)
            Drops.DropCargo(call, entity);
        foreach (var entity in gone)
            if (call.Alive(entity))
                call.Destroy(entity);
        Bankrupt(call, player);
        Alliances.Forget(call, player);
    }

    /// <summary>
    /// Игрок сдаётся (surrender): все его юниты и здания взрываются волной от главного здания (или от середины войска)
    /// наружу, каждое — обычной гибелью в бою (Doomed). Недостроенные площадки исчезают сразу, кредиты — в ноль.
    /// </summary>
    public static bool Surrender(Call call, uint player)
    {
        var doomed = new List<(uint Entity, double X, double Y)>();
        var sites = new List<uint>();
        double sumX = 0, sumY = 0;
        (double X, double Y)? center = null;
        foreach (var entity in Owned(call, player))
        {
            if (!call.Has(entity, S.Position) || call.Has(entity, S.Mount) || call.Has(entity, P.Doomed))
                continue;
            if (!call.Has(entity, S.Unit) && !call.Has(entity, S.Building))
            {
                if (call.Has(entity, S.Site))
                    sites.Add(entity);
                continue;
            }
            var (x, y) = call.At(entity);
            doomed.Add((entity, x, y));
            sumX += x;
            sumY += y;
            if (center is null && call.Has(entity, S.Building) && call.BuildingType(entity) == Conversion.Command)
                center = (x + 1.5, y + 1.5);
        }
        foreach (var site in sites)
        {
            Drops.DropCargo(call, site);
            call.Destroy(site);
        }
        Bankrupt(call, player);
        if (doomed.Count == 0)
            return true;
        var (cx, cy) = center ?? (sumX / doomed.Count, sumY / doomed.Count);
        foreach (var (entity, x, y) in doomed)
        {
            // Как в TS: номер × 2654435761 в числах double, остаток от тысячи.
            var jitter = (entity * 2654435761.0 % 1000) / 1000;
            var seconds = BlastDelay + Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) * BlastWave + jitter * BlastJitter;
            call.Add(entity, P.Doomed).Number(P.DoomedLeft, Math.Max(1, Math.Floor(seconds / Core.Step + 0.5)));
        }
        return true;
    }

    /// <summary>
    /// Игрок уничтожает своих юнитов (scuttle): в ближайшем бою они гибнут обычной гибелью, без урона вокруг. Турель на
    /// носителе отдельно не уничтожить. false — уничтожать нечего.
    /// </summary>
    public static bool Scuttle(Call call, uint player, IEnumerable<uint> units)
    {
        var taken = Gift.Giftable(call, player, units);
        foreach (var entity in taken)
            call.Add(entity, P.Doomed).Number(P.DoomedLeft, 1).Flag(P.DoomedScuttled, true);
        return taken.Count > 0;
    }

    /// <summary>
    /// Событие player_respawn (u32 игрок, u8 — велел администратор): проигравший, а по велению администратора — всякий,
    /// начинает заново (restart в host.ts): остатки базы исчезают, и в партии со стартом он получает новое стартовое.
    /// </summary>
    public static void OnRespawn(EventContext ctx)
    {
        if (ctx.Payload.Length < 4)
            return;
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var forced = ctx.Payload.Length > 4 && ctx.Payload[4] != 0;
        var call = Call.Of(ctx);
        if (player == 0 || Accounts.Find(call, player) == 0 || (!forced && !IsDefeated(call, player)))
            return;
        Wipe(call, player);
        Watch.Forget(call, player);
        if (Core.Game.Start)
            Spawning.Place(call, player);
        // Хост закроет туман и забудет память о зданиях (Host.RespawnedSignal).
        call.SignalTo(RespawnedSignal.Name, new RespawnedSignal { Player = player }, player);
    }
}
