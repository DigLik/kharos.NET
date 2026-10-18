using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Сущности по видам (spawnUnit в units.ts, placeBuilding и equip в buildings.ts, mountTurrets в turrets.ts): юнит,
/// готовое здание и их турели — со всем, что положено виду по данным. Что раздел добавляет сверх данных, он
/// вешает на <see cref="OnUnit"/> и <see cref="OnBuilding"/> (зовутся после снаряжения по данным).
/// </summary>
internal static class Factory
{
    /// <summary>Снаряжение сверх данных: сущность, вид, хозяин.</summary>
    public delegate void Equip(Call call, uint entity, string type, uint player);

    private static readonly List<Equip> Units = [];
    private static readonly List<Equip> Buildings = [];

    /// <summary>Сколько склада у производителя без своего (PRODUCER_HOLD): под материалы заказов.</summary>
    public const double ProducerHold = 100;

    /// <summary>Пехота сама поправляется на такую долю прочности в секунду (INFANTRY_REGEN).</summary>
    public const double InfantryRegen = 0.02;

    /// <summary>Куда смотрит новый юнит (Unit.facing по умолчанию в TS).</summary>
    public const double SpawnFacing = Math.PI / 2;

    /// <summary>Снаряжение юнитов раздела (в его Setup).</summary>
    public static void OnUnit(Equip equip) => Units.Add(equip);

    /// <summary>Снаряжение готовых зданий раздела (в его Setup).</summary>
    public static void OnBuilding(Equip equip) => Buildings.Add(equip);

    /// <summary>Вид юнита; нет — null.</summary>
    public static UnitSpec? Unit(string? type) => type is null ? null : Core.Tables.Units.GetValueOrDefault(type);

    /// <summary>Вид здания; нет — null.</summary>
    public static BuildingSpec? Building(string? type) => type is null ? null : Core.Tables.Buildings.GetValueOrDefault(type);

    /// <summary>
    /// Юнит вида type игрока player с центром в (x, y) — обычно центр тайла, x + 0,5 (spawnUnit): тело, ход, обзор,
    /// прочность, снаряжение по данным и турели.
    /// </summary>
    public static uint SpawnUnit(Call call, string type, uint player, double x, double y)
    {
        var spec = Unit(type) ?? throw new ArgumentException($"нет юнита {type}");
        var entity = call.Spawn();
        call.Add(entity, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
        call.Add(entity, S.Facing).Number(S.FacingAngle, SpawnFacing);
        call.Add(entity, S.Unit);
        call.Add(entity, S.Proto).Number(S.ProtoId, Core.UnitProto(type));
        call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(entity, S.Body).Number(S.BodyRadius, spec.Radius);
        call.Add(entity, S.Mover).Text(S.MoverChassis, spec.Chassis).Number(S.MoverSpeed, spec.Speed).Number(S.MoverTurn, spec.Turn);
        call.Add(entity, S.Sight).Number(S.SightRadius, UnitSight(type));
        // Пехоту не чинят: она поправляется сама.
        if (spec.Kind == "infantry")
            call.Add(entity, S.Health).Flag(S.HealthRepairable, false).Number(S.HealthRegen, InfantryRegen);
        else
            call.Add(entity, S.Health);
        if (type == "mcv")
            AddProducer(call, entity);
        if (spec.Hauls)
            call.Add(entity, S.Hauler);
        if (spec.Weapon is not null)
            call.Add(entity, S.Armed);
        if (spec.Ammo is { } ammo)
            call.Add(entity, S.Magazine).Number(S.MagazineAmmo, ammo);
        if (spec.Repair is { } repair)
            call.Add(entity, S.Repair).Number(S.RepairRadius, repair);
        Storage(call, entity, spec.Inventory, [], spec.Beam);
        Mounts(call, entity, spec.Mounts, x, y, SpawnFacing, player);
        foreach (var equip in Units)
            equip(call, entity, type, player);
        return entity;
    }

    /// <summary>
    /// Готовое здание вида type левым верхним тайлом в (x, y), без проверок (placeBuilding): основание, прочность —
    /// durability, производство (у производящего с хозяином), снаряжение по данным и турели.
    /// </summary>
    public static uint PlaceBuilding(Call call, string type, int x, int y, uint player, double durability = 1)
    {
        var spec = Building(type) ?? throw new ArgumentException($"нет здания {type}");
        var entity = call.Spawn();
        call.Add(entity, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
        // Сдвиг анимации у каждого свой: по счёту зданий в мире (newBuilding).
        call.Add(entity, S.Building).Number(S.BuildingPhase, Tools.Count(S.Building) * 5);
        call.Add(entity, S.Proto).Number(S.ProtoId, Core.BuildingProto(type));
        call.Add(entity, S.Footprint).Number(S.FootprintW, spec.Width).Number(S.FootprintH, spec.Height).Flag(S.FootprintSolid, true);
        call.Add(entity, S.Health).Number(S.HealthValue, durability).Number(S.HealthMax, durability);
        call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(entity, S.Sight).Number(S.SightRadius, BuildingSight(type));
        if (spec.Produces.Length > 0 && player != 0)
            AddProducer(call, entity);
        EquipBuilding(call, entity, type, player);
        return entity;
    }

    /// <summary>Снаряжение готового здания по данным (equip): ремонт, щит, хранилища, турели… и разделов.</summary>
    public static void EquipBuilding(Call call, uint entity, string type, uint player)
    {
        var spec = Building(type)!;
        if (spec.Repair is { } repair)
            call.Add(entity, S.Repair).Number(S.RepairRadius, repair);
        if (spec.Shield is not null)
            call.Add(entity, S.Shield);
        if (spec.Battery is not null)
            call.Add(entity, S.Accumulator);
        if (spec.Requester)
            call.Add(entity, S.Wanted);
        if (spec.Burns is not null)
            call.Add(entity, S.Reactor);
        if (spec.Warhead is not null)
            call.Add(entity, S.Silo);
        if (spec.Intercept is not null)
            call.Add(entity, S.Interceptor);
        var hold = spec.Inventory ?? (spec.Produces.Length > 0 ? ProducerHold : null);
        Storage(call, entity, hold, spec.Limits, spec.Beam);
        // Завод изделий стоит выключенным, пока игрок его не включит.
        if (spec.Assembles is not null)
            call.Add(entity, S.Assembly);
        var (x, y) = call.At(entity);
        Mounts(call, entity, spec.Mounts, x + spec.Width / 2, y + spec.Height / 2, 0, player);
        foreach (var equip in Buildings)
            equip(call, entity, type, player);
    }

    /// <summary>Производство: пустая очередь (у MCV и производящих зданий).</summary>
    private static void AddProducer(Call call, uint entity) => call.Add(entity, S.Producer).Number(S.ProducerCurrent, -1);

    /// <summary>Склад (equipStorage): ёмкость, пределы по грузам, луч.</summary>
    private static void Storage(Call call, uint entity, double? capacity, Dictionary<string, double> limits, BeamSpec? beam)
    {
        if (capacity is { } room and > 0)
        {
            call.Add(entity, S.Stock).Number(S.StockCapacity, room);
            if (limits.Count > 0)
            {
                var writer = call.Add(entity, S.Limits);
                foreach (var (good, limit) in limits)
                    if (S.Limits.Find(good) is { } field)
                        writer.Number(field, limit);
            }
        }
        if (beam is not null)
            call.Add(entity, S.Beam).Number(S.BeamRadius, beam.Radius).Number(S.BeamRate, beam.Rate).Flag(S.BeamGive, beam.Give).Flag(S.BeamTake, beam.Take);
    }

    /// <summary>Турели носителя (mountTurrets): своя сущность на каждом креплении, Mount держит её на месте.</summary>
    private static void Mounts(Call call, uint carrier, MountSpec[] mounts, double x, double y, double facing, uint player)
    {
        if (mounts.Length == 0)
            return;
        var (cos, sin) = (DetMath.Cos(facing), DetMath.Sin(facing));
        foreach (var mount in mounts)
        {
            var turret = Core.Tables.Turrets.GetValueOrDefault(mount.Turret);
            var entity = call.Spawn();
            call.Add(entity, S.Position).Number(S.PositionX, x + mount.Along * cos - mount.Across * sin).Number(S.PositionY, y + mount.Along * sin + mount.Across * cos);
            call.Add(entity, S.Facing).Number(S.FacingAngle, facing);
            call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
            call.Add(entity, S.Proto).Number(S.ProtoId, Core.Proto("turrets", mount.Turret));
            call.Add(entity, S.Mount).Number(S.MountParent, carrier).Number(S.MountAlong, mount.Along).Number(S.MountAcross, mount.Across);
            if (turret?.Weapon is not null)
                call.Add(entity, S.Armed);
            if (turret?.Repair is { } repair)
                call.Add(entity, S.Repair).Number(S.RepairRadius, repair);
        }
    }

    /// <summary>Турели носителя: сущности с Mount.parent = carrier, по возрастанию номеров (и созданные в этом вызове).</summary>
    public static List<uint> TurretsOf(Call call, uint carrier)
    {
        var list = new List<uint>(Tools.Children(carrier));
        if (call.Writes)
            foreach (var born in call.Stage.Spawned)
                if (!list.Contains(born) && call.Num(born, S.Mount, S.MountParent) == carrier)
                    list.Add(born);
        list.RemoveAll(entity => !call.Alive(entity));
        return list;
    }

    // ===== Обзор (vision.ts) =====

    /// <summary>Обзор юнита по классу брони (CLASS_SIGHT).</summary>
    private static double ClassSight(string kind) => kind switch
    {
        "infantry" => 7,
        "air" => 10,
        _ => 8,
    };

    /// <summary>Насколько обзор дальше самого дальнобойного оружия (SIGHT_OVER_RANGE).</summary>
    private const double SightOverRange = 1.5;

    /// <summary>Сколько тайлов от края видит здание без своего числа и стройка (BUILDING_SIGHT, SITE_SIGHT).</summary>
    private const double BuildingSightMargin = 4, SiteSightMargin = 2;

    /// <summary>Дальность самого дальнобойного оружия вида — своего или на турелях.</summary>
    private static double LongestRange(string? weapon, MountSpec[] mounts)
    {
        var range = weapon is not null && Core.Tables.Weapons.TryGetValue(weapon, out var own) ? own.Range : 0;
        foreach (var mount in mounts)
            if (Core.Tables.Turrets.GetValueOrDefault(mount.Turret)?.Weapon is { } gun && Core.Tables.Weapons.TryGetValue(gun, out var spec))
                range = Math.Max(range, spec.Range);
        return range;
    }

    /// <summary>Сколько тайлов видит юнит вида type (unitSight).</summary>
    public static double UnitSight(string type)
    {
        var spec = Unit(type)!;
        var range = LongestRange(spec.Weapon, spec.Mounts);
        return Math.Max(spec.Sight ?? ClassSight(spec.Kind), range > 0 ? range + SightOverRange : 0);
    }

    /// <summary>Сколько тайлов от центра видит здание вида type (buildingSight); site — стройка, off — без тока.</summary>
    public static double BuildingSight(string type, bool site = false, bool off = false)
    {
        var spec = Building(type)!;
        var half = Math.Max(spec.Width, spec.Height) / 2;
        if (site)
            return half + SiteSightMargin;
        if (off)
            return half + BuildingSightMargin;
        var range = LongestRange(null, spec.Mounts);
        return Math.Max(spec.Sight ?? half + BuildingSightMargin, range > 0 ? range + SightOverRange : 0);
    }
}
