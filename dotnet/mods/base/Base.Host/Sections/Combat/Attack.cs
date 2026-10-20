using Base.Host.Kernel;
using Base.Shared.Orders;
using static Base.Host.Sections.Combat.Refs;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Приказ атаковать (orderAttack, stopAttack, canAttack в combat.ts; canFight в turrets.ts; правило атаки в target.ts).
/// Вне тика боя: читается мир со своими записями (<see cref="Call"/>).
/// </summary>
internal static class Attack
{
    /// <summary>Оружие юнита или турели (weaponOf); безоружен — null.</summary>
    public static Weapon? WeaponOf(Call call, uint entity) =>
        call.Has(entity, S.Unit) || call.Has(entity, S.Mount) ? Arms.Gun((int)call.Num(entity, S.Proto, S.ProtoId, -1)) : null;

    /// <summary>Может ли носитель воевать (canFight): вооружён сам или несёт вооружённые турели.</summary>
    public static bool CanFight(Call call, uint entity)
    {
        if (call.Has(entity, S.Armed))
            return true;
        foreach (var turret in Factory.TurretsOf(call, entity))
            if (call.Has(turret, S.Armed))
                return true;
        return false;
    }

    /// <summary>Сколько тиков защиты новичка осталось у игрока (protectionLeft); ноль — нет.</summary>
    public static double ProtectionLeft(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        return account == 0 ? 0 : Math.Max(0, call.Num(account, S.Player, S.PlayerSafe) - call.Tick);
    }

    /// <summary>Союзники ли игроки (одинаковый Team.head ≥ 0).</summary>
    public static bool Allied(Call call, uint a, uint b)
    {
        if (a == b)
            return false;
        var (first, second) = (Accounts.Find(call, a), Accounts.Find(call, b));
        if (first == 0 || second == 0)
            return false;
        var head = call.Num(first, S.Team, S.TeamHead, -1);
        return head >= 0 && head == call.Num(second, S.Team, S.TeamHead, -1);
    }

    /// <summary>Может ли игрок приказать атаковать это (canAttack): чужой юнит или здание не союзника, не под защитой.</summary>
    public static bool CanAttack(Call call, uint player, uint target)
    {
        if (!call.Has(target, S.Position) || (!call.Has(target, S.Unit) && !call.Has(target, S.Building)))
            return false;
        var owner = call.OwnerOf(target);
        return Battle.Hostile(player, owner) && !Allied(call, player, owner) && ProtectionLeft(call, owner) == 0;
    }

    /// <summary>Снимает защиту новичка с игрока, который сам приказал атаковать (unprotect).</summary>
    private static void Unprotect(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        if (account != 0 && call.Num(account, S.Player, S.PlayerSafe) > call.Tick)
            call.Set(account, S.Player).Number(S.PlayerSafe, 0);
    }

    /// <summary>
    /// Юнитам игрока — атаковать цель (orderAttack): они подходят на выстрел и бьют, пока она жива; турели носителя берут
    /// цель вместе с ним, а он подъезжает на выстрел турелей. Чужие, безоружные и не достающие (по летающим) — мимо.
    /// </summary>
    public static bool Order(Call call, uint player, IEnumerable<uint> units, uint target)
    {
        if (!CanAttack(call, player, target))
            return false;
        Unprotect(call, player);
        var air = call.UnitType(target) is { } type && Factory.Unit(type)?.Kind == "air";
        var ordered = false;
        var list = units.Distinct().ToList();
        // Что о юнитах спросят ниже — одним чтением на всех: приказ армии — тысячи вопросов сцене.
        if (call.Writes)
            call.Stage.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), S.Owner, S.Armed, S.Unit, S.Mount, S.Proto, S.Tactics);
        foreach (var entity in list)
        {
            if (!call.IsOwn(player, entity))
                continue;
            // Приказ атаки снимает патруль и возвращение на место.
            ClearTactics(call, entity);
            // Отступление от TS: тот же приказ повторно (игрок снова щёлкнул по той же цели) оставляет путь погони к ней.
            // В TS путь снимается всегда, а снятый прокладывается заново лишь в очередь юнита раз в SCAN_TICKS тиков, и
            // юнит, чья очередь выпадает на тик перед повтором, при частых повторах не трогался бы с места вовсе.
            var again = Chasing(call, entity, target);
            if (Aim(call, entity, target, air, again))
            {
                // Прежний путь больше не нужен: к цели юнит тронется сам в ближайший тик.
                if (!again)
                    call.Remove(entity, S.Path);
                ordered = true;
                continue;
            }
            // Безоружный носитель подвозит турели на выстрел; повтор — если все взявшие цель турели уже гнались за ней.
            var any = false;
            again = true;
            foreach (var turret in Factory.TurretsOf(call, entity))
            {
                var chasing = Chasing(call, turret, target);
                if (!Aim(call, turret, target, air, chasing))
                    continue;
                any = true;
                again &= chasing;
            }
            if (!any)
                continue;
            if (!again)
                call.Remove(entity, S.Path);
            ordered = true;
        }
        return ordered;
    }

    /// <summary>Гонится ли стрелок уже за этой целью по приказу игрока.</summary>
    private static bool Chasing(Call call, uint gunner, uint target) =>
        call.Has(gunner, S.Armed) && call.Num(gunner, S.Armed, R.ArmedTarget) == target
        && call.Flag(gunner, S.Armed, R.ArmedChase) && call.Flag(gunner, S.Armed, R.ArmedOrdered);

    /// <summary>Берёт ли цель этот стрелок; взял — Armed с погоней по приказу. Повтор (again) погоню не трогает.</summary>
    private static bool Aim(Call call, uint entity, uint target, bool air, bool again)
    {
        if (!call.Has(entity, S.Armed) || WeaponOf(call, entity) is not { } weapon || (air ? !weapon.Air : weapon.OnlyAir))
            return false;
        if (!again)
            call.Set(entity, S.Armed).Number(R.ArmedTarget, target).Flag(R.ArmedChase, true).Flag(R.ArmedOrdered, true).Number(R.ArmedStuck, 0);
        return true;
    }

    /// <summary>Снимает с юнита и его турелей цель (stopAttack): они снова бьют только тех, до кого достают с места.</summary>
    public static void Stop(Call call, uint entity)
    {
        Clear(call, entity);
        foreach (var turret in Factory.TurretsOf(call, entity))
            Clear(call, turret);
    }

    private static void Clear(Call call, uint gunner)
    {
        if (!call.Has(gunner, S.Armed))
            return;
        if (call.Num(gunner, S.Armed, R.ArmedTarget) == 0 && !call.Flag(gunner, S.Armed, R.ArmedChase) && !call.Flag(gunner, S.Armed, R.ArmedOrdered))
            return;
        call.Set(gunner, S.Armed).Number(R.ArmedTarget, 0).Flag(R.ArmedChase, false).Flag(R.ArmedOrdered, false);
    }

    /// <summary>Снимает патруль и поход (clearTactics); раздел Tactics может поставить своё.</summary>
    public static void ClearTactics(Call call, uint entity)
    {
        if (Hooks.ClearTactics is { } hook)
        {
            hook(call, entity);
            return;
        }
        if (!call.Has(entity, S.Tactics))
            return;
        call.Set(entity, S.Tactics).Bytes(R.TacticsPatrol, []).Number(R.TacticsLeg, 0).Flag(R.TacticsAway, false).Flag(R.TacticsResupply, false);
    }

    /// <summary>Правило приказа точкой (resolveOrder): вооружённые по врагу в точке — атакуют.</summary>
    public static IReadOnlyList<object>? Resolve(Call call, uint player, uint[] units, in Commands.Point point)
    {
        if (!point.Seen)
            return null;
        var enemy = point.Enemy != 0 ? point.Enemy : point.Occupant;
        if (enemy == 0 || !units.Any(unit => CanFight(call, unit)) || !CanAttack(call, player, enemy))
            return null;
        return [new AttackOrder { Units = units, Target = enemy }];
    }
}
