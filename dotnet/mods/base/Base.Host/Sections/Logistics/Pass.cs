using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Mining;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Один вызов логистики: мир (<see cref="View"/>), грузовики, поручения строителей, пачки труб и то, что за вызов
/// считается один раз (склады мира по видам, руда шахт, нужды лабораторий, связи лучей). В системе всё это берётся
/// из кадра и побочных таблиц, в приказе и запросе — по миру через вызов, по требованию.
/// </summary>
internal sealed class Pass
{
    public readonly View V;
    private List<Truck>? _trucks;
    private Dictionary<uint, Truck>? _byEntity;
    private List<FetchJob>? _fetchers;
    private List<(uint To, int Good, double Amount)>? _batches;
    private uint[]? _holders;
    private readonly uint[]? _known;
    private readonly Func<Pass, List<Truck>>? _loadTrucks;
    private readonly Func<Pass, List<FetchJob>>? _loadFetchers;

    public Pass(View view, List<Truck>? trucks = null, List<FetchJob>? fetchers = null, List<(uint, int, double)>? batches = null, uint[]? holders = null,
        Func<Pass, List<Truck>>? loadTrucks = null, Func<Pass, List<FetchJob>>? loadFetchers = null)
    {
        V = view;
        _trucks = trucks;
        _fetchers = fetchers;
        _batches = batches;
        _known = holders;
        _loadTrucks = loadTrucks;
        _loadFetchers = loadFetchers;
    }

    public Call Call => V.Call;

    public ulong Tick => V.Tick;

    /// <summary>Пасс приказа или запроса: всё — по миру через вызов.</summary>
    public static Pass Of(Call call) => new(new View(call));

    // ===== Грузовики и поручения =====

    /// <summary>Все грузовики мира (для заявок и потоков), по возрастанию номеров.</summary>
    public List<Truck> Trucks
    {
        get
        {
            _trucks ??= _loadTrucks?.Invoke(this) ?? [.. Core.All(S.Hauler, S.Stock).Select(entity => Truck.Read(V, entity))];
            return _trucks;
        }
    }

    /// <summary>Грузовик по номеру (из загруженных); нет — прочитать и запомнить.</summary>
    public Truck? TruckOf(uint entity)
    {
        if (_byEntity is null)
        {
            var index = new Dictionary<uint, Truck>();
            foreach (var truck in Trucks)
                index[truck.Entity] = truck;
            _byEntity = index;
        }
        if (_byEntity.TryGetValue(entity, out var found))
            return found;
        if (!V.Alive(entity) || !Call.Has(entity, S.Hauler))
            return null;
        found = Truck.Read(V, entity);
        _byEntity[entity] = found;
        _trucks!.Add(found);
        return found;
    }

    /// <summary>Строители с поручением (Fetch).</summary>
    public List<FetchJob> Fetchers => _fetchers ??= _loadFetchers?.Invoke(this) ?? ReadFetchers();

    private List<FetchJob> ReadFetchers()
    {
        var list = new List<FetchJob>();
        foreach (var entity in Core.All(S.Fetch, S.Stock))
            list.Add(new FetchJob(entity, (uint)Call.Num(entity, S.Fetch, L.FetchFrom), (uint)Call.Num(entity, S.Fetch, L.FetchTo),
                Cargo.Of(Call.Num(entity, S.Fetch, L.FetchResource, -1)), Call.Num(entity, S.Fetch, L.FetchAmount), Call.Flag(entity, S.Fetch, L.FetchFull)));
        return list;
    }

    /// <summary>Пачки в трубах, ещё не пришедшие: куда, что, сколько (раздел Pipes; в приказах и запросах — через <see cref="Hooks.Batches"/>).</summary>
    public List<(uint To, int Good, double Amount)> Batches => _batches ??= Hooks.Batches?.Invoke(Call) ?? [];

    /// <summary>Поручение строителя поменялось за вызов: потоки пересчитать с ним.</summary>
    public void Refetch(List<FetchJob> fetchers) => _fetchers = fetchers;

    // ===== Склады мира =====

    /// <summary>Все живые сущности со складом, по возрастанию номеров (и созданные в этом вызове).</summary>
    public uint[] Holders
    {
        get
        {
            if (_holders is null)
            {
                var all = new List<uint>(_known ?? Core.All(S.Stock));
                if (_known is not null && Call.Writes)
                    foreach (var born in Call.Stage.Spawned)
                        if (Call.Has(born, S.Stock))
                            all.Add(born);
                all.RemoveAll(V.Gone.Contains);
                all.Sort();
                _holders = [.. all];
            }
            return _holders;
        }
    }

    private List<uint>? _buildings, _drops;

    /// <summary>Здания со складом (Building и Stock): готовые и под стройкой.</summary>
    public List<uint> Buildings => _buildings ??= [.. Holders.Where(entity => V.BuildingType(entity) is not null)];

    /// <summary>Дропы мира.</summary>
    public List<uint> Drops => _drops ??= [.. Holders.Where(V.IsDrop)];

    /// <summary>Состав складов поменялся за вызов (новый дроп, исчезнувший): списки — заново.</summary>
    public void Spawned()
    {
        _holders = null;
        _buildings = null;
        _drops = null;
    }

    // ===== Руда шахт =====

    private readonly Dictionary<uint, double> _reserves = [];

    /// <summary>Сколько осталось в месторождении под шахтой (reserveLeft): за вызов не меняется — добыча идёт своей системой.</summary>
    public double ReserveLeft(uint mine)
    {
        if (_reserves.TryGetValue(mine, out var left))
            return left;
        var (x, y) = V.At(mine);
        left = double.IsNaN(x) ? 0 : Deposits.ReserveLeft(Call, (int)x, (int)y);
        _reserves[mine] = left;
        return left;
    }

    private readonly Dictionary<uint, int> _ores = [];

    /// <summary>Какая руда лежит под шахтой (mineOre); нет — −1.</summary>
    public int MineOre(uint mine)
    {
        if (_ores.TryGetValue(mine, out var ore))
            return ore;
        var (x, y) = V.At(mine);
        ore = double.IsNaN(x) || Deposits.At((int)x, (int)y) is not { } spot ? -1 : Cargo.Index(Goods.OreOf(spot.Name));
        _ores[mine] = ore;
        return ore;
    }

    // ===== Нужды лабораторий =====

    private readonly Dictionary<uint, IReadOnlyList<(int Good, double Amount)>> _needs = [];

    /// <summary>Сырьё нынешней и головной темы исследований игрока (researchNeeds, раздел Research); не поставлено — пусто.</summary>
    public IReadOnlyList<(int Good, double Amount)> ResearchNeeds(uint player)
    {
        if (_needs.TryGetValue(player, out var needs))
            return needs;
        var list = new List<(int, double)>();
        if (Hooks.ResearchNeeds?.Invoke(Call, player) is { } found)
            foreach (var (good, amount) in found)
                if (Cargo.Index(good) is var index and >= 0)
                    list.Add((index, amount));
        _needs[player] = list;
        return list;
    }

    /// <summary>Нужен ли груз нынешним темам игрока.</summary>
    public bool ResearchNeeds(uint player, int good) => ResearchNeeds(player).Any(need => need.Good == good && need.Amount != 0);

    // ===== Зоны =====

    /// <summary>Сети игрока (zonesOf).</summary>
    public IReadOnlyList<Zones.Zone> ZonesOf(uint player) => Zones.Of(player);

    /// <summary>Сеть здания или подключённой стройки (networkOf); нет — null.</summary>
    public static Zones.Zone? NetworkOf(uint entity) => Zones.NetworkOf(entity);

    // ===== Связи лучей =====

    /// <summary>Связи лучей за вызов: носитель → (с кем, тянет ли, груз), по порядку.</summary>
    public readonly Dictionary<uint, List<(uint Partner, bool Pulling, int Good)>> Links = [];
}
