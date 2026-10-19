// Совпадает со schema/*.json и компонентами движка (Schema.cs): сверяет тест SchemaNamesTests.
namespace Base.Shared;

/// <summary>
/// Имена компонентов и полей схемы: движка (Builtin) и base (schema/*.json по разделам). Компонент — класс с
/// <c>Name</c>, поле — константа с его именем: опечатка не соберётся.
/// </summary>
public static class Names
{
    /// <summary>Position (движок).</summary>
    public static class Position
    {
        public const string Name = "Position";
        public const string X = "x";
        public const string Y = "y";
    }

    /// <summary>Facing (движок).</summary>
    public static class Facing
    {
        public const string Name = "Facing";
        public const string Angle = "angle";
    }

    /// <summary>Owner (движок).</summary>
    public static class Owner
    {
        public const string Name = "Owner";
        public const string Player = "player";
    }

    /// <summary>Proto (движок).</summary>
    public static class Proto
    {
        public const string Name = "Proto";
        public const string Id = "id";
    }

    /// <summary>Sight (движок).</summary>
    public static class Sight
    {
        public const string Name = "Sight";
        public const string Radius = "radius";
    }

    /// <summary>Footprint (движок).</summary>
    public static class Footprint
    {
        public const string Name = "Footprint";
        public const string W = "w";
        public const string H = "h";
        public const string Solid = "solid";
        public const string Open = "open";
    }

    /// <summary>Body (движок).</summary>
    public static class Body
    {
        public const string Name = "Body";
        public const string Radius = "radius";
        public const string Mass = "mass";
        public const string Rooted = "rooted";
    }

    /// <summary>Mover (движок).</summary>
    public static class Mover
    {
        public const string Name = "Mover";
        public const string Chassis = "chassis";
        public const string Speed = "speed";
        public const string Turn = "turn";
    }

    /// <summary>Path (движок).</summary>
    public static class Path
    {
        public const string Name = "Path";
        public const string GoalX = "goalX";
        public const string GoalY = "goalY";
        public const string Wait = "wait";
        public const string Tries = "tries";
        public const string Near = "near";
        public const string Roads = "roads";
        public const string Direct = "direct";
        public const string Stuck = "stuck";
        public const string Best = "best";
        public const string Slot = "slot";
        public const string Points = "points";
    }

    /// <summary>Mount (движок).</summary>
    public static class Mount
    {
        public const string Name = "Mount";
        public const string Parent = "parent";
        public const string Along = "along";
        public const string Across = "across";
        public const string Angle = "angle";
    }

    /// <summary>Team (движок).</summary>
    public static class Team
    {
        public const string Name = "Team";
        public const string Player = "player";
        public const string Head = "head";
    }

    /// <summary>Sighting (движок).</summary>
    public static class Sighting
    {
        public const string Name = "Sighting";
        public const string Rule = "rule";
        public const string Ax = "ax";
        public const string Ay = "ay";
        public const string Bx = "bx";
        public const string By = "by";
        public const string Cx = "cx";
        public const string Cy = "cy";
        public const string Viewers = "viewers";
    }

    /// <summary>Reveal (движок).</summary>
    public static class Reveal
    {
        public const string Name = "Reveal";
        public const string Radius = "radius";
        public const string Until = "until";
        public const string All = "all";
    }

    /// <summary>Unit (schema/kernel.json).</summary>
    public static class Unit
    {
        public const string Name = "Unit";
    }

    /// <summary>Building (schema/kernel.json).</summary>
    public static class Building
    {
        public const string Name = "Building";
        public const string Phase = "phase";
        public const string Level = "level";
    }

    /// <summary>Health (schema/kernel.json).</summary>
    public static class Health
    {
        public const string Name = "Health";
        public const string Value = "value";
        public const string Max = "max";
        public const string Repairable = "repairable";
        public const string Regen = "regen";
        public const string Hit = "hit";
    }

    /// <summary>Producer (schema/kernel.json).</summary>
    public static class Producer
    {
        public const string Name = "Producer";
        public const string Progress = "progress";
        public const string Current = "current";
        public const string Rally = "rally";
        public const string RallyX = "rallyX";
        public const string RallyY = "rallyY";
        public const string Queue = "queue";
    }

    /// <summary>Converting (schema/kernel.json).</summary>
    public static class Converting
    {
        public const string Name = "Converting";
        public const string Left = "left";
        public const string Total = "total";
    }

    /// <summary>Deploys (schema/kernel.json).</summary>
    public static class Deploys
    {
        public const string Name = "Deploys";
        public const string X = "x";
        public const string Y = "y";
        public const string Misses = "misses";
    }

    /// <summary>Orders (schema/kernel.json).</summary>
    public static class Orders
    {
        public const string Name = "Orders";
        public const string List = "list";
    }

    /// <summary>Site (schema/construction.json).</summary>
    public static class Site
    {
        public const string Name = "Site";
        public const string Progress = "progress";
        public const string Demolish = "demolish";
        public const string Undo = "undo";
    }

    /// <summary>Builds (schema/construction.json).</summary>
    public static class Builds
    {
        public const string Name = "Builds";
        public const string Site = "site";
        public const string Ordered = "ordered";
        public const string Misses = "misses";
    }

    /// <summary>Repair (schema/construction.json).</summary>
    public static class Repair
    {
        public const string Name = "Repair";
        public const string Radius = "radius";
        public const string Rate = "rate";
        public const string Target = "target";
    }

    /// <summary>Pave (schema/construction.json).</summary>
    public static class Pave
    {
        public const string Name = "Pave";
        public const string Kind = "kind";
        public const string Work = "work";
        public const string Done = "done";
        public const string Remove = "remove";
    }

    /// <summary>Dig (schema/construction.json).</summary>
    public static class Dig
    {
        public const string Name = "Dig";
        public const string Kind = "kind";
        public const string Cost = "cost";
        public const string Work = "work";
        public const string PeakX = "peakX";
        public const string PeakY = "peakY";
    }

    /// <summary>Gate (schema/construction.json).</summary>
    public static class Gate
    {
        public const string Name = "Gate";
        public const string Open = "open";
    }

    /// <summary>Deposit (schema/mining.json).</summary>
    public static class Deposit
    {
        public const string Name = "Deposit";
        public const string Mined = "mined";
        public const string Spent = "spent";
    }

    /// <summary>Drop (schema/mining.json).</summary>
    public static class Drop
    {
        public const string Name = "Drop";
    }

    /// <summary>Hauler (schema/logistics.json).</summary>
    public static class Hauler
    {
        public const string Name = "Hauler";
        public const string Mine = "mine";
        public const string From = "from";
        public const string To = "to";
        public const string Resource = "resource";
        public const string Amount = "amount";
        public const string Full = "full";
        public const string Loading = "loading";
        public const string Waiting = "waiting";
        public const string Misses = "misses";
        public const string Stop = "stop";
        public const string Filter = "filter";
        public const string Pickup = "pickup";
        public const string Supply = "supply";
        public const string Trapped = "trapped";
        public const string Route = "route";
        public const string Serve = "serve";
    }

    /// <summary>Carry (schema/logistics.json).</summary>
    public static class Carry
    {
        public const string Name = "Carry";
        public const string From = "from";
        public const string To = "to";
        public const string Resource = "resource";
        public const string Left = "left";
        public const string Full = "full";
    }

    /// <summary>Stock (schema/logistics.json).</summary>
    public static class Stock
    {
        public const string Name = "Stock";
        public const string Capacity = "capacity";
        public const string Reserve = "reserve";
        public const string Metal = "metal";
        public const string Silicon = "silicon";
        public const string Fuel = "fuel";
        public const string Kharite = "kharite";
        public const string Lithium = "lithium";
        public const string Uranium = "uranium";
        public const string Blocks = "blocks";
        public const string Ammo = "ammo";
        public const string Parts = "parts";
        public const string Rods = "rods";
        public const string MetalOre = "metalOre";
        public const string SiliconOre = "siliconOre";
        public const string FuelOre = "fuelOre";
        public const string KhariteOre = "khariteOre";
        public const string LithiumOre = "lithiumOre";
        public const string UraniumOre = "uraniumOre";
    }

    /// <summary>Limits (schema/logistics.json).</summary>
    public static class Limits
    {
        public const string Name = "Limits";
        public const string Metal = "metal";
        public const string Silicon = "silicon";
        public const string Fuel = "fuel";
        public const string Kharite = "kharite";
        public const string Lithium = "lithium";
        public const string Uranium = "uranium";
        public const string Blocks = "blocks";
        public const string Ammo = "ammo";
        public const string Parts = "parts";
        public const string Rods = "rods";
        public const string MetalOre = "metalOre";
        public const string SiliconOre = "siliconOre";
        public const string FuelOre = "fuelOre";
        public const string KhariteOre = "khariteOre";
        public const string LithiumOre = "lithiumOre";
        public const string UraniumOre = "uraniumOre";
    }

    /// <summary>Wanted (schema/logistics.json).</summary>
    public static class Wanted
    {
        public const string Name = "Wanted";
        public const string Metal = "metal";
        public const string Silicon = "silicon";
        public const string Fuel = "fuel";
        public const string Kharite = "kharite";
        public const string Lithium = "lithium";
        public const string Uranium = "uranium";
        public const string Blocks = "blocks";
        public const string Ammo = "ammo";
        public const string Parts = "parts";
        public const string Rods = "rods";
        public const string MetalOre = "metalOre";
        public const string SiliconOre = "siliconOre";
        public const string FuelOre = "fuelOre";
        public const string KhariteOre = "khariteOre";
        public const string LithiumOre = "lithiumOre";
        public const string UraniumOre = "uraniumOre";
    }

    /// <summary>Fetch (schema/logistics.json).</summary>
    public static class Fetch
    {
        public const string Name = "Fetch";
        public const string From = "from";
        public const string To = "to";
        public const string Resource = "resource";
        public const string Amount = "amount";
        public const string Full = "full";
        public const string Misses = "misses";
    }

    /// <summary>Unreachable (schema/logistics.json).</summary>
    public static class Unreachable
    {
        public const string Name = "Unreachable";
        public const string Until = "until";
    }

    /// <summary>Beam (schema/logistics.json).</summary>
    public static class Beam
    {
        public const string Name = "Beam";
        public const string Radius = "radius";
        public const string Rate = "rate";
        public const string Give = "give";
        public const string Take = "take";
        public const string Links = "links";
    }

    /// <summary>Batch (schema/pipes.json).</summary>
    public static class Batch
    {
        public const string Name = "Batch";
        public const string From = "from";
        public const string To = "to";
        public const string Resource = "resource";
        public const string Amount = "amount";
        public const string Sent = "sent";
        public const string Arrive = "arrive";
        public const string Push = "push";
        public const string Path = "path";
    }

    /// <summary>Trade (schema/trade.json).</summary>
    public static class Trade
    {
        public const string Name = "Trade";
        public const string Resource = "resource";
        public const string Wanted = "wanted";
        public const string Left = "left";
        public const string Total = "total";
        public const string Buy = "buy";
    }

    /// <summary>Transfer (schema/trade.json).</summary>
    public static class Transfer
    {
        public const string Name = "Transfer";
        public const string To = "to";
        public const string X = "x";
        public const string Y = "y";
        public const string Credits = "credits";
        public const string Left = "left";
        public const string Total = "total";
        public const string Done = "done";
    }

    /// <summary>Off (schema/energy.json).</summary>
    public static class Off
    {
        public const string Name = "Off";
    }

    /// <summary>Busy (schema/energy.json).</summary>
    public static class Busy
    {
        public const string Name = "Busy";
    }

    /// <summary>Accumulator (schema/energy.json).</summary>
    public static class Accumulator
    {
        public const string Name = "Accumulator";
        public const string Charge = "charge";
        public const string Flow = "flow";
    }

    /// <summary>Reactor (schema/energy.json).</summary>
    public static class Reactor
    {
        public const string Name = "Reactor";
        public const string Left = "left";
    }

    /// <summary>Grid (schema/energy.json).</summary>
    public static class Grid
    {
        public const string Name = "Grid";
        public const string Net = "net";
        public const string State = "state";
    }

    /// <summary>Grids (schema/energy.json).</summary>
    public static class Grids
    {
        public const string Name = "Grids";
        public const string Nets = "nets";
    }

    /// <summary>Assembly (schema/production.json).</summary>
    public static class Assembly
    {
        public const string Name = "Assembly";
        public const string On = "on";
        public const string Progress = "progress";
    }

    /// <summary>Armed (schema/combat.json).</summary>
    public static class Armed
    {
        public const string Name = "Armed";
        public const string Target = "target";
        public const string Chase = "chase";
        public const string Cooldown = "cooldown";
        public const string Stuck = "stuck";
        public const string Ordered = "ordered";
    }

    /// <summary>Shot (schema/combat.json).</summary>
    public static class Shot
    {
        public const string Name = "Shot";
        public const string Player = "player";
        public const string Source = "source";
        public const string Target = "target";
        public const string FromX = "fromX";
        public const string FromY = "fromY";
        public const string ToX = "toX";
        public const string ToY = "toY";
        public const string Age = "age";
        public const string Life = "life";
        public const string Blocked = "blocked";
        public const string Air = "air";
    }

    /// <summary>Blast (schema/combat.json).</summary>
    public static class Blast
    {
        public const string Name = "Blast";
        public const string Size = "size";
        public const string Age = "age";
        public const string Life = "life";
        public const string Lift = "lift";
    }

    /// <summary>Burst (schema/combat.json).</summary>
    public static class Burst
    {
        public const string Name = "Burst";
        public const string Radius = "radius";
        public const string Damage = "damage";
        public const string Core = "core";
    }

    /// <summary>Shield (schema/defense.json).</summary>
    public static class Shield
    {
        public const string Name = "Shield";
        public const string Charge = "charge";
        public const string Hit = "hit";
        public const string Down = "down";
    }

    /// <summary>Magazine (schema/defense.json).</summary>
    public static class Magazine
    {
        public const string Name = "Magazine";
        public const string Ammo = "ammo";
    }

    /// <summary>Research (schema/research.json).</summary>
    public static class Research
    {
        public const string Name = "Research";
        public const string Speed = "speed";
        public const string Lack = "lack";
        public const string Cause = "cause";
        public const string Good = "good";
    }

    /// <summary>Upgrade (schema/research.json).</summary>
    public static class Upgrade
    {
        public const string Name = "Upgrade";
        public const string Progress = "progress";
        public const string Scale = "scale";
    }

    /// <summary>Science (schema/research.json).</summary>
    public static class Science
    {
        public const string Name = "Science";
        public const string Done = "done";
        public const string Plan = "plan";
        public const string Progress = "progress";
    }

    /// <summary>Silo (schema/missiles.json).</summary>
    public static class Silo
    {
        public const string Name = "Silo";
        public const string Progress = "progress";
        public const string Ticks = "ticks";
        public const string Cost = "cost";
        public const string Ready = "ready";
        public const string Start = "start";
        public const string ToX = "toX";
        public const string ToY = "toY";
    }

    /// <summary>Missile (schema/missiles.json).</summary>
    public static class Missile
    {
        public const string Name = "Missile";
        public const string FromX = "fromX";
        public const string FromY = "fromY";
        public const string ToX = "toX";
        public const string ToY = "toY";
        public const string Born = "born";
        public const string X = "x";
        public const string Y = "y";
        public const string Player = "player";
        public const string Mark = "mark";
        public const string Hunted = "hunted";
        public const string Blind = "blind";
        public const string CourseX = "courseX";
        public const string CourseY = "courseY";
    }

    /// <summary>Chaser (schema/missiles.json).</summary>
    public static class Chaser
    {
        public const string Name = "Chaser";
        public const string FromX = "fromX";
        public const string FromY = "fromY";
        public const string ToX = "toX";
        public const string ToY = "toY";
        public const string Lift = "lift";
        public const string Born = "born";
        public const string Arrive = "arrive";
        public const string Player = "player";
        public const string Missile = "missile";
    }

    /// <summary>Interceptor (schema/missiles.json).</summary>
    public static class Interceptor
    {
        public const string Name = "Interceptor";
        public const string Progress = "progress";
        public const string Ticks = "ticks";
        public const string Missiles = "missiles";
    }

    /// <summary>Scan (schema/missiles.json).</summary>
    public static class Scan
    {
        public const string Name = "Scan";
        public const string X = "x";
        public const string Y = "y";
        public const string Tick = "tick";
        public const string Wait = "wait";
        public const string Dark = "dark";
    }

    /// <summary>Mark (schema/missiles.json).</summary>
    public static class Mark
    {
        public const string Name = "Mark";
        public const string Kind = "kind";
        public const string Until = "until";
        public const string Player = "player";
    }

    /// <summary>Tactics (schema/tactics.json).</summary>
    public static class Tactics
    {
        public const string Name = "Tactics";
        public const string Stance = "stance";
        public const string Leg = "leg";
        public const string Away = "away";
        public const string HomeX = "homeX";
        public const string HomeY = "homeY";
        public const string Resupply = "resupply";
        public const string Patrol = "patrol";
    }

    /// <summary>Player (schema/players.json).</summary>
    public static class Player
    {
        public const string Name = "Player";
        public const string Id = "id";
        public const string Credits = "credits";
        public const string Earned = "earned";
        public const string Away = "away";
        public const string Safe = "safe";
        public const string Online = "online";
        public const string Rewards = "rewards";
        public const string Camera = "camera";
    }

    /// <summary>Diplomacy (schema/players.json).</summary>
    public static class Diplomacy
    {
        public const string Name = "Diplomacy";
        public const string Offered = "offered";
        public const string Asked = "asked";
    }

    /// <summary>Doomed (schema/players.json).</summary>
    public static class Doomed
    {
        public const string Name = "Doomed";
        public const string Left = "left";
        public const string Scuttled = "scuttled";
    }
}
