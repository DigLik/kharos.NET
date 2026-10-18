using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Компоненты и поля схемы мира для команд и чтения (берутся по именам Base.Shared.Names при загрузке). Раздел, которому
/// нужно поле, которого здесь нет, берёт его так же: <c>S.Health[Names.Health.Regen]</c> — или дописывает сюда.
/// </summary>
internal static class S
{
    public static ComponentRef Position = null!, Facing = null!, Owner = null!, Proto = null!, Sight = null!, Footprint = null!,
        Body = null!, Mover = null!, Path = null!, Mount = null!, Team = null!;

    public static ComponentRef Unit = null!, Building = null!, Health = null!, Producer = null!, Converting = null!, Deploys = null!,
        Orders = null!, Player = null!, Diplomacy = null!, Science = null!, Site = null!, Repair = null!, Armed = null!,
        Magazine = null!, Hauler = null!, Stock = null!, Limits = null!, Beam = null!, Shield = null!, Accumulator = null!,
        Wanted = null!, Reactor = null!, Silo = null!, Interceptor = null!, Assembly = null!, Drop = null!, Pave = null!,
        Deposit = null!, Builds = null!, Fetch = null!, Tactics = null!, Gate = null!;

    /// <summary>Память мода (Storage движка); нет — null.</summary>
    public static ComponentRef? Storage;

    /// <summary>Слои base (data/layers.json): готовое покрытие и месторождения; у партии без них — null.</summary>
    public static LayerRef? Paving, Deposits, Land;

    public static FieldRef PositionX, PositionY, FacingAngle, OwnerPlayer, ProtoId, SightRadius;
    public static FieldRef FootprintW, FootprintH, FootprintSolid, FootprintOpen;
    public static FieldRef BodyRadius, BodyMass, BodyRooted, MoverChassis, MoverSpeed, MoverTurn;
    public static FieldRef PathGoalX, PathGoalY, PathWait, PathTries, PathNear, PathRoads, PathDirect, PathStuck, PathBest, PathSlot, PathPoints;
    public static FieldRef MountParent, MountAlong, MountAcross, MountAngle, TeamPlayer, TeamHead;
    public static FieldRef BuildingPhase, BuildingLevel;
    public static FieldRef HealthValue, HealthMax, HealthRepairable, HealthRegen;
    public static FieldRef ProducerProgress, ProducerCurrent, ProducerRally, ProducerRallyX, ProducerRallyY, ProducerQueue;
    public static FieldRef ConvertingLeft, ConvertingTotal, DeploysX, DeploysY, DeploysMisses, OrdersList;
    public static FieldRef PlayerId, PlayerCredits, PlayerEarned, PlayerSafe, PlayerRewards, PlayerCamera;
    public static FieldRef ScienceDone, RepairRadius, MagazineAmmo, StockCapacity, BeamRadius, BeamRate, BeamGive, BeamTake;
    public static FieldRef PaveKind, PaveRemove, DepositMined, GateOpen;

    public static void Load(LoadContext ctx)
    {
        ComponentRef C(string name) => ctx.Component(name);
        Position = C(Names.Position.Name);
        Facing = C(Names.Facing.Name);
        Owner = C(Names.Owner.Name);
        Proto = C(Names.Proto.Name);
        Sight = C(Names.Sight.Name);
        Footprint = C(Names.Footprint.Name);
        Body = C(Names.Body.Name);
        Mover = C(Names.Mover.Name);
        Path = C(Names.Path.Name);
        Mount = C(Names.Mount.Name);
        Team = C(Names.Team.Name);
        Unit = C(Names.Unit.Name);
        Building = C(Names.Building.Name);
        Health = C(Names.Health.Name);
        Producer = C(Names.Producer.Name);
        Converting = C(Names.Converting.Name);
        Deploys = C(Names.Deploys.Name);
        Orders = C(Names.Orders.Name);
        Player = C(Names.Player.Name);
        Diplomacy = C(Names.Diplomacy.Name);
        Science = C(Names.Science.Name);
        Site = C(Names.Site.Name);
        Repair = C(Names.Repair.Name);
        Armed = C(Names.Armed.Name);
        Magazine = C(Names.Magazine.Name);
        Hauler = C(Names.Hauler.Name);
        Stock = C(Names.Stock.Name);
        Limits = C(Names.Limits.Name);
        Beam = C(Names.Beam.Name);
        Shield = C(Names.Shield.Name);
        Accumulator = C(Names.Accumulator.Name);
        Wanted = C(Names.Wanted.Name);
        Reactor = C(Names.Reactor.Name);
        Silo = C(Names.Silo.Name);
        Interceptor = C(Names.Interceptor.Name);
        Assembly = C(Names.Assembly.Name);
        Drop = C(Names.Drop.Name);
        Pave = C(Names.Pave.Name);
        Deposit = C(Names.Deposit.Name);
        Builds = C(Names.Builds.Name);
        Fetch = C(Names.Fetch.Name);
        Tactics = C(Names.Tactics.Name);
        Gate = C(Names.Gate.Name);
        Storage = ctx.FindComponent("Storage");
        Paving = ctx.Layer("paving");
        Deposits = ctx.Layer("deposits");
        Land = ctx.Layer("land");

        PositionX = Position[Names.Position.X];
        PositionY = Position[Names.Position.Y];
        FacingAngle = Facing[Names.Facing.Angle];
        OwnerPlayer = Owner[Names.Owner.Player];
        ProtoId = Proto[Names.Proto.Id];
        SightRadius = Sight[Names.Sight.Radius];
        FootprintW = Footprint[Names.Footprint.W];
        FootprintH = Footprint[Names.Footprint.H];
        FootprintSolid = Footprint[Names.Footprint.Solid];
        FootprintOpen = Footprint[Names.Footprint.Open];
        BodyRadius = Body[Names.Body.Radius];
        BodyMass = Body[Names.Body.Mass];
        BodyRooted = Body[Names.Body.Rooted];
        MoverChassis = Mover[Names.Mover.Chassis];
        MoverSpeed = Mover[Names.Mover.Speed];
        MoverTurn = Mover[Names.Mover.Turn];
        PathGoalX = Path[Names.Path.GoalX];
        PathGoalY = Path[Names.Path.GoalY];
        PathWait = Path[Names.Path.Wait];
        PathTries = Path[Names.Path.Tries];
        PathNear = Path[Names.Path.Near];
        PathRoads = Path[Names.Path.Roads];
        PathDirect = Path[Names.Path.Direct];
        PathStuck = Path[Names.Path.Stuck];
        PathBest = Path[Names.Path.Best];
        PathSlot = Path[Names.Path.Slot];
        PathPoints = Path[Names.Path.Points];
        MountParent = Mount[Names.Mount.Parent];
        MountAlong = Mount[Names.Mount.Along];
        MountAcross = Mount[Names.Mount.Across];
        MountAngle = Mount[Names.Mount.Angle];
        TeamPlayer = Team[Names.Team.Player];
        TeamHead = Team[Names.Team.Head];
        BuildingPhase = Building[Names.Building.Phase];
        BuildingLevel = Building[Names.Building.Level];
        HealthValue = Health[Names.Health.Value];
        HealthMax = Health[Names.Health.Max];
        HealthRepairable = Health[Names.Health.Repairable];
        HealthRegen = Health[Names.Health.Regen];
        ProducerProgress = Producer[Names.Producer.Progress];
        ProducerCurrent = Producer[Names.Producer.Current];
        ProducerRally = Producer[Names.Producer.Rally];
        ProducerRallyX = Producer[Names.Producer.RallyX];
        ProducerRallyY = Producer[Names.Producer.RallyY];
        ProducerQueue = Producer[Names.Producer.Queue];
        ConvertingLeft = Converting[Names.Converting.Left];
        ConvertingTotal = Converting[Names.Converting.Total];
        DeploysX = Deploys[Names.Deploys.X];
        DeploysY = Deploys[Names.Deploys.Y];
        DeploysMisses = Deploys[Names.Deploys.Misses];
        OrdersList = Orders[Names.Orders.List];
        PlayerId = Player[Names.Player.Id];
        PlayerCredits = Player[Names.Player.Credits];
        PlayerEarned = Player[Names.Player.Earned];
        PlayerSafe = Player[Names.Player.Safe];
        PlayerRewards = Player[Names.Player.Rewards];
        PlayerCamera = Player[Names.Player.Camera];
        ScienceDone = Science[Names.Science.Done];
        RepairRadius = Repair[Names.Repair.Radius];
        MagazineAmmo = Magazine[Names.Magazine.Ammo];
        StockCapacity = Stock[Names.Stock.Capacity];
        BeamRadius = Beam[Names.Beam.Radius];
        BeamRate = Beam[Names.Beam.Rate];
        BeamGive = Beam[Names.Beam.Give];
        BeamTake = Beam[Names.Beam.Take];
        PaveKind = Pave[Names.Pave.Kind];
        PaveRemove = Pave[Names.Pave.Remove];
        DepositMined = Deposit[Names.Deposit.Mined];
        GateOpen = Gate[Names.Gate.Open];
    }
}
