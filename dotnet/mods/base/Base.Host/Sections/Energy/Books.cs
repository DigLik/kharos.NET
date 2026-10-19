using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Shared.Data;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Energy.Refs;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Хозяйства энергосетей (income.ts): у каждой сети (<see cref="Zones.Grids"/>) энергия своя. Потребитель без дела
/// (без метки Busy) просит меньше, станции перегружаются по полной нагрузке; доход генераторов материи — только при
/// главном здании в сети; подмога главного здания — одна на игрока.
/// <para>
/// Книги тика (openBooks — closeBooks): между ними хозяйства считаются раз, как в TS, и заново — только когда
/// перестроились сети. Вне тика (приказы, правки, запросы) — каждый раз заново. Книги — в экземпляре мода; выбросили
/// экземпляр посреди тика — посчитаются заново по тому же миру.
/// </para>
/// </summary>
internal static class Books
{
    /// <summary>Хозяйство одной сети (Economy в income.ts).</summary>
    public sealed class Economy
    {
        public double Produced, Demand, Peak, Spare, Income, Crowd, Stored;

        public EconomyInfo Info() => new()
        {
            Produced = Produced, Demand = Demand, Peak = Peak, Spare = Spare, Income = Income, Crowd = Crowd, Stored = Stored,
        };

        /// <summary>Перегруз сети (overloadOf).</summary>
        public double Overload => PowerRules.OverloadOf(Produced, Peak, Spare);
    }

    /// <summary>Сеть игрока с её хозяйством.</summary>
    public readonly record struct Book(uint Player, Zones.Zone Zone, Economy Economy);

    /// <summary>Что нехватка делает со зданием (PowerState).</summary>
    public const string Overload = "overload", Starved = "starved";

    private static bool _open;
    private static ulong _tick;
    private static IReadOnlyDictionary<uint, List<Zones.Zone>>? _grids;
    private static List<uint>? _signature;
    private static List<Book>? _books;
    private static Dictionary<uint, double>? _supply;

    /// <summary>Тик начался (openBooks): до <see cref="Close"/> хозяйства считаются раз.</summary>
    public static void Open(ulong tick)
    {
        _open = true;
        _tick = tick;
        _grids = null;
        _signature = null;
        _books = null;
        _supply = null;
    }

    /// <summary>Тик кончился (closeBooks).</summary>
    public static void Close()
    {
        _open = false;
        _grids = null;
        _signature = null;
        _books = null;
        _supply = null;
    }

    /// <summary>Открыты ли книги этому вызову; перестроились сети — посчитанное забыто.</summary>
    private static bool Ledger(Call call)
    {
        if (!_open || !call.Writes || call.Tick != _tick)
            return false;
        var grids = Zones.Grids();
        if (ReferenceEquals(grids, _grids))
            return true;
        // Кэш зон пересчитан (сменился состав мира), но сами сети могли остаться теми же.
        var signature = Signature(grids);
        if (_signature is null || !_signature.SequenceEqual(signature))
        {
            _books = null;
            _supply = null;
        }
        _grids = grids;
        _signature = signature;
        return true;
    }

    /// <summary>Состав сетей подряд: игрок, число сетей, у каждой — число зданий и сами здания.</summary>
    private static List<uint> Signature(IReadOnlyDictionary<uint, List<Zones.Zone>> grids)
    {
        var list = new List<uint>();
        foreach (var (player, zones) in grids)
        {
            list.Add(player);
            list.Add((uint)zones.Count);
            foreach (var zone in zones)
            {
                list.Add((uint)zone.Buildings.Count);
                list.AddRange(zone.Buildings);
            }
        }
        return list;
    }

    // ===== Здания =====

    /// <summary>Вид и запись здания; не здание — null.</summary>
    public static (string Type, BuildingSpec Spec)? Kind(Call call, uint entity) =>
        call.BuildingType(entity) is { } type && Factory.Building(type) is { } spec ? (type, spec) : null;

    /// <summary>Горит ли в реакторе стержень (isBurning): у других зданий — false.</summary>
    public static bool Burning(Call call, uint entity) => call.Num(entity, S.Reactor, R.ReactorLeft) > 0 && !call.Has(entity, R.Off);

    /// <summary>Выключил ли игрок потребителя (switchedOff): у завода изделий — свой флаг on.</summary>
    public static bool SwitchedOff(Call call, uint entity) =>
        call.Has(entity, S.Assembly) ? !call.Flag(entity, S.Assembly, R.AssemblyOn) : call.Has(entity, R.Off);

    /// <summary>Сколько энергии даёт (больше нуля, с уровнем) или просит здание (powerAt); реактор — только пока горит.</summary>
    public static double PowerAt(Call call, uint entity, BuildingSpec spec)
    {
        var power = spec.Power ?? 0;
        if (spec.Burns is not null && !Burning(call, entity))
            return 0;
        // Выработка растёт с уровнем; у радара — потребление: дальше разведывает — больше ест.
        return power > 0 || (spec.Scan ?? 0) != 0 ? power * BuildRules.LevelFactor(call.Num(entity, S.Building, S.BuildingLevel, 1)) : power;
    }

    public static double PowerAt(Call call, uint entity) => Kind(call, entity) is { } kind ? PowerAt(call, entity, kind.Spec) : 0;

    /// <summary>Приносит ли здание доход только при главном здании в сети: все доходные, кроме самого главного.</summary>
    private static bool NeedsCore(string type, BuildingSpec spec) => type != BuildRules.Core && (spec.Income ?? 0) != 0;

    /// <summary>Есть ли в сети главное здание.</summary>
    public static bool HasCore(Call call, Zones.Zone zone)
    {
        foreach (var entity in zone.Buildings)
            if (call.BuildingType(entity) == BuildRules.Core)
                return true;
        return false;
    }

    // ===== Хозяйства =====

    /// <summary>
    /// Хозяйство сети (economyOfZone). stored — считать ли отданное накопителями: сами они решают, сколько отдать, по
    /// хозяйству без себя.
    /// </summary>
    public static Economy OfZone(Call call, Zones.Zone zone, bool stored = true)
    {
        var economy = new Economy();
        var core = HasCore(call, zone);
        // Доход потребителей при полной энергии.
        double powered = 0;
        foreach (var entity in zone.Buildings)
        {
            if (Kind(call, entity) is not { } kind)
                continue;
            var (type, spec) = kind;
            if (stored && spec.Battery is { } battery && call.Has(entity, S.Accumulator))
            {
                var flow = call.Num(entity, S.Accumulator, R.AccumulatorFlow);
                if (flow > 0)
                    economy.Stored += flow;
                // Заряженный накопитель мог бы отдать ещё: под полную нагрузку этот запас считается.
                economy.Spare += Math.Max(0, Math.Min(battery.Rate - Math.Max(0, flow), call.Num(entity, S.Accumulator, R.AccumulatorCharge) / Core.Step));
            }
            var power = PowerAt(call, entity, spec);
            var income = core || !NeedsCore(type, spec) ? spec.Income ?? 0 : 0;
            // Выключенный потребитель энергии не просит и ничего не даёт.
            if (power < 0 && SwitchedOff(call, entity))
                continue;
            // Повреждённая электростанция даёт энергии во столько же раз меньше, во сколько упала её прочность.
            if (power > 0)
                economy.Produced += power * call.Num(entity, S.Health, S.HealthValue, 1);
            if (power < 0)
            {
                // Тесное здание просит тем больше, чем их уже в сети: первое — одну норму, второе — две.
                var norms = spec.Crowding ? ++economy.Crowd : 1;
                economy.Peak -= power * norms;
                economy.Demand -= (PowerRules.AlwaysBusy(spec) || call.Has(entity, R.Busy) ? power : PowerRules.IdlePower(power)) * norms;
                powered += income;
            }
            else
                economy.Income += income;
        }
        economy.Produced += economy.Stored;
        if (powered != 0)
            economy.Income += powered * Math.Min(1, economy.Produced / economy.Demand);
        return economy;
    }

    /// <summary>Сети всех игроков с хозяйствами (books), у каждого — в порядке сетей; подмога — первой сети с главным зданием.</summary>
    public static List<Book> All(Call call)
    {
        var ledger = Ledger(call);
        if (ledger && _books is { } cached)
            return cached;
        var result = new List<Book>();
        var coreIncome = Factory.Building(BuildRules.Core)?.Income ?? 0;
        foreach (var (player, zones) in Zones.Grids())
        {
            var relief = Accounts.Credits(call, player) < PowerRules.ReliefBelow ? PowerRules.ReliefIncome - coreIncome : 0;
            foreach (var zone in zones)
            {
                var economy = OfZone(call, zone);
                if (relief > 0 && HasCore(call, zone))
                {
                    economy.Income += relief;
                    relief = 0;
                }
                result.Add(new Book(player, zone, economy));
            }
        }
        if (ledger)
            _books = result;
        return result;
    }

    /// <summary>Хозяйства сетей игрока (zoneEconomies).</summary>
    public static List<Economy> OfPlayer(Call call, uint player) => [.. All(call).Where(book => book.Player == player).Select(book => book.Economy)];

    /// <summary>Итог по всем сетям игрока (economyOf): доход — то, что он получает; энергия — для справки.</summary>
    public static Economy Total(Call call, uint player)
    {
        var total = new Economy();
        foreach (var economy in OfPlayer(call, player))
        {
            total.Produced += economy.Produced;
            total.Demand += economy.Demand;
            total.Peak += economy.Peak;
            total.Spare += economy.Spare;
            total.Income += economy.Income;
            total.Crowd += economy.Crowd;
            total.Stored += economy.Stored;
        }
        return total;
    }

    /// <summary>
    /// Доля полной скорости каждого потребителя (powerSupply): 1 — энергии хватает. Потребителя вне сетей и выключенного
    /// здесь нет — он не работает вовсе.
    /// </summary>
    public static Dictionary<uint, double> Supply(Call call)
    {
        var ledger = Ledger(call);
        if (ledger && _supply is { } cached)
            return cached;
        var supply = new Dictionary<uint, double>();
        foreach (var (_, zone, economy) in All(call))
        {
            var share = economy.Demand > 0 ? Math.Min(1, economy.Produced / economy.Demand) : 1;
            foreach (var entity in zone.Buildings)
                if (PowerAt(call, entity) < 0 && !SwitchedOff(call, entity))
                    supply[entity] = share;
        }
        if (ledger)
            _supply = supply;
        return supply;
    }

    /// <summary>Доля энергии потребителя: вне сетей и выключенный — 0.</summary>
    public static double ShareOf(Call call, uint entity) => Supply(call).GetValueOrDefault(entity, 0);

    /// <summary>Здания, которым не хватает энергии (powerStates): станции — по полной нагрузке, потребители — по нынешней.</summary>
    public static Dictionary<uint, string> States(Call call)
    {
        var states = new Dictionary<uint, string>();
        foreach (var (_, zone, economy) in All(call))
        {
            var overloaded = economy.Overload > 0;
            var shortage = economy.Demand > economy.Produced;
            if (!overloaded && !shortage)
                continue;
            foreach (var entity in zone.Buildings)
            {
                var power = PowerAt(call, entity);
                if (power > 0 && overloaded)
                    states[entity] = Overload;
                else if (power < 0 && shortage && !SwitchedOff(call, entity))
                    states[entity] = Starved;
            }
        }
        return states;
    }

    /// <summary>
    /// Станции под нагрузкой (loadedPlants): в каждой сети — столько, сколько нужно на спрос сверх отданного накопителями,
    /// начиная с ближайших к середине потребителей. Только картинка: энергия у сети общая.
    /// </summary>
    public static List<uint> Loaded(Call call)
    {
        var loaded = new List<uint>();
        foreach (var (_, zone, economy) in All(call))
        {
            var need = economy.Demand - economy.Stored;
            if (need <= 1e-9)
                continue;
            double x = 0, y = 0;
            var count = 0;
            var plants = new List<(uint Entity, double Output, double X, double Y)>();
            foreach (var entity in zone.Buildings)
            {
                if (Kind(call, entity) is not { Spec: var spec })
                    continue;
                var power = PowerAt(call, entity, spec);
                if (power == 0 || (power < 0 && SwitchedOff(call, entity)))
                    continue;
                var (px, py) = call.At(entity);
                var (cx, cy) = (px + spec.Width / 2, py + spec.Height / 2);
                if (power > 0)
                {
                    plants.Add((entity, power * call.Num(entity, S.Health, S.HealthValue, 1), cx, cy));
                    continue;
                }
                x += cx;
                y += cy;
                count++;
            }
            if (count == 0)
                continue;
            x /= count;
            y /= count;
            plants.Sort((a, b) =>
            {
                var order = DetMath.Hypot(a.X - x, a.Y - y).CompareTo(DetMath.Hypot(b.X - x, b.Y - y));
                return order != 0 ? order : a.Entity.CompareTo(b.Entity);
            });
            foreach (var plant in plants)
            {
                if (need <= 1e-9)
                    break;
                loaded.Add(plant.Entity);
                need -= plant.Output;
            }
        }
        return loaded;
    }

    /// <summary>
    /// Станции сетей, где энергии не хватило бы и целым станциям (overbuiltPlants): чинить их бесполезно, пока перегруз
    /// не снят.
    /// </summary>
    public static HashSet<uint> Overbuilt(Call call)
    {
        var plants = new HashSet<uint>();
        foreach (var (_, zone, economy) in All(call))
        {
            var found = new List<uint>();
            double capacity = 0;
            foreach (var entity in zone.Buildings)
            {
                var power = PowerAt(call, entity);
                if (power <= 0)
                    continue;
                found.Add(entity);
                capacity += power;
            }
            if (economy.Peak > capacity + economy.Spare)
                plants.UnionWith(found);
        }
        return plants;
    }

    /// <summary>Доходные здания в сетях без главного: денег они не приносят (coreless).</summary>
    public static List<uint> Coreless(Call call)
    {
        var idle = new List<uint>();
        foreach (var (_, zone, _) in All(call))
        {
            if (HasCore(call, zone))
                continue;
            foreach (var entity in zone.Buildings)
                if (Kind(call, entity) is { } kind && NeedsCore(kind.Type, kind.Spec) && !idle.Contains(entity))
                    idle.Add(entity);
        }
        return idle;
    }
}
