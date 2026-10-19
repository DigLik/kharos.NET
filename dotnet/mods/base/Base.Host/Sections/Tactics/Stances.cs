using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Tactics;

/// <summary>
/// Стойки, патруль, поход и возвращение на место (tactics.ts, без системы patrol): вне тика боя, мир со своими записями
/// (<see cref="Call"/>). Тактику (Tactics) юнит получает, когда она нужна впервые; без неё стойка — оборона.
/// </summary>
internal static class Stances
{
    public const string Aggressive = "aggressive", Defensive = "defensive", Hold = "hold", Passive = "passive";

    /// <summary>Известные стойки (STANCES).</summary>
    public static readonly string[] All = [Aggressive, Defensive, Hold, Passive];

    /// <summary>Сколько точек может быть в патруле (PATROL_LIMIT).</summary>
    public const int PatrolLimit = 8;

    /// <summary>Раз во сколько тиков вставший юнит вспоминает о патруле и о месте, куда вернуться (RESUME_TICKS).</summary>
    public const int ResumeTicks = 10;

    /// <summary>Ближе скольких тайлов к своему месту юнит считается пришедшим (HOME_REACH).</summary>
    public const double HomeReach = 1.5;

    /// <summary>Стойка юнита: без тактики — оборона (stanceOf).</summary>
    public static string StanceOf(Call call, uint entity) => call.Has(entity, S.Tactics) ? call.Text(entity, S.Tactics, T.Stance) ?? Defensive : Defensive;

    /// <summary>Точки патруля юнита (x, y подряд); нет — пусто.</summary>
    public static double[] PatrolOf(Call call, uint entity) => Unpack(call.Bytes(entity, S.Tactics, T.Patrol));

    /// <summary>Точки из байтов f64 LE.</summary>
    public static double[] Unpack(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 16)
            return [];
        var points = new double[bytes.Length / 8];
        for (var i = 0; i < points.Length; i++)
            points[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * 8));
        return points;
    }

    /// <summary>Точки патруля в байты f64 LE.</summary>
    public static byte[] Pack(IReadOnlyList<double> points)
    {
        var bytes = new byte[points.Count * 8];
        for (var i = 0; i < points.Count; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), points[i]);
        return bytes;
    }

    /// <summary>Заводит тактику, если её ещё не было (tacticsOf).</summary>
    private static void Ensure(Call call, uint entity)
    {
        if (!call.Has(entity, S.Tactics))
            call.Add(entity, S.Tactics);
    }

    /// <summary>Вооружён ли стрелок оружием (Armed и оружие вида).</summary>
    private static bool Armed(Call call, uint gunner) => call.Has(gunner, S.Armed) && Attack.WeaponOf(call, gunner) is not null;

    /// <summary>Вооружён ли юнит или хоть одна его турель.</summary>
    private static bool AnyArmed(Call call, uint entity)
    {
        if (Armed(call, entity))
            return true;
        foreach (var turret in Factory.TurretsOf(call, entity))
            if (Armed(call, turret))
                return true;
        return false;
    }

    /// <summary>Свои вооружённые юниты из списка, без повторов (fightersOf): им и их турелям раздаются стойка и патруль.</summary>
    private static List<uint> FightersOf(Call call, uint player, IEnumerable<uint> units)
    {
        var list = new List<uint>();
        foreach (var entity in units)
            if (!list.Contains(entity) && call.Has(entity, S.Unit) && call.IsOwn(player, entity) && AnyArmed(call, entity))
                list.Add(entity);
        return list;
    }

    /// <summary>Ставит своим вооружённым юнитам стойку (setStance). Неизвестная стойка — отказ.</summary>
    public static bool SetStance(Call call, uint player, IEnumerable<uint> units, string stance)
    {
        if (!All.Contains(stance))
            return false;
        var fighters = FightersOf(call, player, units);
        foreach (var entity in fighters)
        {
            Ensure(call, entity);
            call.Set(entity, S.Tactics).Text(T.Stance, stance);
            // Стрелять нельзя — бросает и самовыбранную цель; приказанную тоже: игрок сказал «не стрелять».
            if (stance != Passive)
                continue;
            Drop(call, entity);
            foreach (var turret in Factory.TurretsOf(call, entity))
                Drop(call, turret);
        }
        return fighters.Count > 0;
    }

    private static void Drop(Call call, uint gunner)
    {
        if (call.Has(gunner, S.Armed))
            call.Set(gunner, S.Armed).Number(T.ArmedTarget, 0).Flag(T.ArmedChase, false).Flag(T.ArmedOrdered, false);
    }

    /// <summary>
    /// Посылает своих вооружённых юнитов в патруль (orderPatrol): каждый ходит по кругу от тайла, где стоит, через точки (x, y
    /// подряд) и обратно. append — точки добавляются к идущему патрулю; кто не патрулировал, начинает от своего места.
    /// </summary>
    public static bool OrderPatrol(Call call, uint player, IEnumerable<uint> units, double[] points, bool append)
    {
        var stops = new List<double>();
        for (var i = 0; i + 1 < points.Length && stops.Count < PatrolLimit * 2; i += 2)
            if (IsTile(points[i]) && IsTile(points[i + 1]) && Placement.InBounds((int)points[i], (int)points[i + 1]))
            {
                stops.Add(points[i]);
                stops.Add(points[i + 1]);
            }
        if (stops.Count == 0)
            return false;
        var fighters = FightersOf(call, player, units);
        foreach (var entity in fighters)
        {
            var (x, y) = call.At(entity);
            Ensure(call, entity);
            var patrol = PatrolOf(call, entity);
            if (append && patrol.Length > 0)
            {
                var room = Math.Max(0, (PatrolLimit + 1) * 2 - patrol.Length);
                call.Set(entity, S.Tactics).Bytes(T.Patrol, Pack([.. patrol, .. stops.Take(room)]));
                continue;
            }
            call.Set(entity, S.Tactics).Bytes(T.Patrol, Pack([Math.Floor(x), Math.Floor(y), .. stops])).Number(T.Leg, 1).Flag(T.Away, false);
            Moves.Order(call, entity, (int)stops[0], (int)stops[1]);
        }
        return fighters.Count > 0;
    }

    private static bool IsTile(double value) => double.IsFinite(value) && value == Math.Floor(value) && Math.Abs(value) < 1e9;

    /// <summary>Снимает патруль и возвращение на место — оно же место назначения похода (clearTactics). Стойка остаётся.</summary>
    public static void Clear(Call call, uint entity)
    {
        if (call.Has(entity, S.Tactics))
            call.Set(entity, S.Tactics).Bytes(T.Patrol, []).Number(T.Leg, 0).Flag(T.Away, false).Flag(T.Resupply, false);
    }

    /// <summary>
    /// Поход (startMarch): свои вооружённые юниты в агрессивной стойке, посланные идти, запоминают, куда идут (конец их
    /// пути). В других стойках приказ идти — просто идти.
    /// </summary>
    public static void StartMarch(Call call, IEnumerable<uint> units)
    {
        foreach (var entity in units)
        {
            if (!call.Has(entity, S.Path) || StanceOf(call, entity) != Aggressive || !AnyGunner(call, entity))
                continue;
            Ensure(call, entity);
            call.Set(entity, S.Tactics).Flag(T.Away, true).Number(T.HomeX, call.Num(entity, S.Path, S.PathGoalX)).Number(T.HomeY, call.Num(entity, S.Path, S.PathGoalY));
        }
    }

    /// <summary>Есть ли у юнита или его турелей Armed (без проверки оружия: так в startMarch).</summary>
    private static bool AnyGunner(Call call, uint entity)
    {
        if (call.Has(entity, S.Armed))
            return true;
        foreach (var turret in Factory.TurretsOf(call, entity))
            if (call.Has(turret, S.Armed))
                return true;
        return false;
    }

    /// <summary>Юнит уходит в погоню без приказа: запоминает, откуда, чтобы вернуться (leaveHome). В патруле возвращаться некуда.</summary>
    public static void LeaveHome(Call call, uint entity)
    {
        if (call.Has(entity, S.Tactics) && (call.Flag(entity, S.Tactics, T.Away) || call.Bytes(entity, S.Tactics, T.Patrol) is { Length: > 0 }))
            return;
        var (x, y) = call.At(entity);
        Ensure(call, entity);
        call.Set(entity, S.Tactics).Flag(T.Away, true).Number(T.HomeX, Math.Floor(x)).Number(T.HomeY, Math.Floor(y));
    }
}
