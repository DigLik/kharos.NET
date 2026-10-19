using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Мир глазами логистики на один вызов: склады, здания, площадки, дропы, отметки, заказы. В приказах и запросах всё
/// читается вызовом (<see cref="Call"/>, сцена со своими записями). В системах раздела — снимком побочных таблиц кадра
/// (<see cref="Snapshot"/>): о складах (всё, у чего есть Stock) снимок знает всё нужное, и чтение не стоит ни одного
/// вызова инструмента; о прочем спрашивается вызов. Свои записи складов лежат поверх снимка: склад пишется только
/// через <see cref="Set"/> — команда вызова и запись поверх снимка разом.
/// </summary>
internal sealed class View
{
    /// <summary>Снимок побочных таблиц кадра (мир до вызова). Сведения о сущности со складом — полные.</summary>
    public sealed class Snapshot
    {
        /// <summary>Склады: capacity, reserve и грузы подряд (<see cref="Width"/> на сущность).</summary>
        public readonly Dictionary<uint, int> StockRow = [];
        public double[] Stock = [];
        public readonly Dictionary<uint, (double X, double Y)> Position = [];
        public readonly Dictionary<uint, uint> Owner = [];
        /// <summary>Здания и площадки со складом: номер вида.</summary>
        public readonly Dictionary<uint, int> Building = [], Site = [];
        public readonly Dictionary<uint, bool> SiteDemolish = [];
        public readonly HashSet<uint> Drop = [], Converting = [], Off = [];
        public readonly Dictionary<uint, double> Until = [], Producer = [];
        public readonly Dictionary<uint, (int Proto, double Radius)> Unit = [];
        public readonly Dictionary<uint, double[]> Limits = [], Wanted = [];
        public readonly Dictionary<uint, (int Good, double Wanted, double Total, bool Buy)> Trade = [];
        public readonly Dictionary<uint, bool> Assembly = [];
        public readonly Dictionary<uint, (double Radius, double Rate, bool Give, bool Take)> Beam = [];
    }

    /// <summary>Столбцов склада на сущность в снимке: capacity, reserve и грузы.</summary>
    public static int Width => 2 + Cargo.Count;

    public readonly Call Call;
    private readonly Snapshot? _snap;
    private readonly Dictionary<uint, double[]> _written = [];
    private readonly Dictionary<uint, double> _marks = [];

    public View(Call call, Snapshot? snapshot = null)
    {
        Call = call;
        _snap = snapshot;
    }

    public ulong Tick => Call.Tick;

    /// <summary>Есть снимок кадра (система раздела).</summary>
    public bool Framed => _snap is not null;

    /// <summary>Знает ли снимок сущность: склад до вызова, не создана в этом вызове.</summary>
    private bool Known(uint entity) =>
        _snap is not null && _snap.StockRow.ContainsKey(entity) && (Call.Stage.Spawned.Count == 0 || !Call.Stage.Spawned.Contains(entity));

    // ===== Склад =====

    /// <summary>Строка склада: свои записи, снимок или null — читать вызовом.</summary>
    private double[]? Row(uint entity, out int at)
    {
        at = 0;
        if (_written.TryGetValue(entity, out var row))
            return row;
        if (Known(entity))
        {
            at = _snap!.StockRow[entity] * Width;
            return _snap.Stock;
        }
        return null;
    }

    /// <summary>Есть ли склад. Со снимком о складах до вызова он знает всё: спрашивать вызов — только о созданных в этом вызове.</summary>
    public bool HasStock(uint entity)
    {
        if (entity == 0)
            return false;
        if (_written.ContainsKey(entity) || Known(entity))
            return true;
        if (_snap is null)
            return Call.Has(entity, S.Stock);
        return Call.Stage.Spawned.Count > 0 && Call.Stage.Spawned.Contains(entity) && Call.Has(entity, S.Stock);
    }

    public double Amount(uint entity, int good)
    {
        if (good < 0 || entity == 0)
            return 0;
        return Row(entity, out var at) is { } row ? row[at + 2 + good] : Stocks.Amount(Call, entity, good);
    }

    public double Capacity(uint entity) => Row(entity, out var at) is { } row ? row[at] : Call.Num(entity, S.Stock, S.StockCapacity);

    public double Reserve(uint entity) => Row(entity, out var at) is { } row ? row[at + 1] : Call.Num(entity, S.Stock, L.StockReserve);

    /// <summary>Сколько всего лежит на складе (loadOf).</summary>
    public double Load(uint entity)
    {
        var sum = 0.0;
        for (var good = 0; good < Cargo.Count; good++)
            sum += Amount(entity, good);
        return sum;
    }

    /// <summary>Первый груз, которого на складе хоть сколько-то, кроме except (carriedBy); пусто — −1.</summary>
    public int Carried(uint entity, int except = -1)
    {
        for (var good = 0; good < Cargo.Count; good++)
            if (good != except && Amount(entity, good) > Cargo.Epsilon)
                return good;
        return -1;
    }

    /// <summary>Предел груза; нет — −1.</summary>
    public double Limit(uint entity, int good)
    {
        if (Known(entity))
            return _snap!.Limits.TryGetValue(entity, out var limits) ? limits[good] : -1;
        return Stocks.LimitOf(Call, entity, good);
    }

    /// <summary>Принимает ли склад груз вообще (accepts): дроп — всё, площадка — то, на что задан предел, остальное — по виду.</summary>
    public bool Accepts(uint entity, int good)
    {
        if (IsDrop(entity))
            return true;
        if (IsSite(entity))
            return Limit(entity, good) >= 0;
        var accepts = IsUnit(entity) ? Factory.Unit(UnitType(entity))?.Accepts : Building(entity)?.Accepts;
        return accepts is not { Length: > 0 } || Array.IndexOf(accepts, Cargo.Name(good)) >= 0;
    }

    /// <summary>Сколько груза ещё поместится (roomFor).</summary>
    public double Room(uint entity, int good)
    {
        if (good < 0 || !HasStock(entity) || !Accepts(entity, good))
            return 0;
        var limit = Limit(entity, good);
        var left = limit < 0 ? double.PositiveInfinity : limit - Amount(entity, good);
        return Math.Max(0, Math.Min(left, Capacity(entity) - Load(entity)));
    }

    /// <summary>Кладёт груза ровно столько: команда вызова и запись поверх снимка.</summary>
    public void Set(uint entity, int good, double amount)
    {
        if (!_written.TryGetValue(entity, out var row))
        {
            row = new double[Width];
            if (Row(entity, out var at) is { } source)
                Array.Copy(source, at, row, 0, Width);
            else
            {
                row[0] = Call.Num(entity, S.Stock, S.StockCapacity);
                row[1] = Call.Num(entity, S.Stock, L.StockReserve);
                for (var g = 0; g < Cargo.Count; g++)
                    row[2 + g] = Stocks.Amount(Call, entity, g);
            }
            _written[entity] = row;
        }
        row[2 + good] = amount;
        Call.Set(entity, S.Stock).Number(Goods.StockField(good), amount);
    }

    /// <summary>Склад записан мимо <see cref="Set"/> (бросок груза на землю): дальше читать его вызовом.</summary>
    public void Forget(uint entity)
    {
        _written.Remove(entity);
        _snap?.StockRow.Remove(entity);
    }

    /// <summary>Кладёт до amount, сколько поместится (put).</summary>
    public double Put(uint entity, int good, double amount)
    {
        var moved = Math.Max(0, Math.Min(amount, Room(entity, good)));
        if (moved != 0)
            Set(entity, good, Amount(entity, good) + moved);
        return moved;
    }

    /// <summary>Забирает до amount, сколько есть (take).</summary>
    public double Take(uint entity, int good, double amount)
    {
        var have = Amount(entity, good);
        var moved = Math.Max(0, Math.Min(amount, have));
        if (moved != 0)
            Set(entity, good, have - moved);
        return moved;
    }

    // ===== Сущность =====

    /// <summary>Склады, уничтоженные за вызов (пустые дропы): снимок о них ещё помнит.</summary>
    public readonly HashSet<uint> Gone = [];

    /// <summary>Жива ли сущность: склад из снимка жив, пока его не уничтожили в этом вызове.</summary>
    public bool Alive(uint entity) => Known(entity) ? !Gone.Contains(entity) : Call.Alive(entity);

    /// <summary>Хозяин; ничья — 0.</summary>
    public uint Owner(uint entity) => Known(entity) ? _snap!.Owner.GetValueOrDefault(entity) : Call.OwnerOf(entity);

    /// <summary>Своё ли (isOwn).</summary>
    public bool IsOwn(uint player, uint entity) => entity != 0 && player != 0 && Owner(entity) == player;

    public (double X, double Y) At(uint entity) => Known(entity) ? _snap!.Position[entity] : Call.At(entity);

    /// <summary>Вид здания с компонентом Building (готового или под стройкой); иначе null.</summary>
    public string? BuildingType(uint entity)
    {
        if (Known(entity))
            return _snap!.Building.TryGetValue(entity, out var proto) ? Core.ProtoName(proto, "buildings") : null;
        return Call.Has(entity, S.Building) ? Call.BuildingType(entity) : null;
    }

    /// <summary>Вид здания с компонентом Building (Building.type); нет — null.</summary>
    public BuildingSpec? Building(uint entity) => BuildingType(entity) is { } type ? Factory.Building(type) : null;

    public bool IsSite(uint entity) => Known(entity) ? _snap!.Site.ContainsKey(entity) : Call.Has(entity, S.Site);

    /// <summary>Вид площадки; не площадка — null (Site.type).</summary>
    public BuildingSpec? SiteSpec(uint entity)
    {
        if (Known(entity))
            return _snap!.Site.TryGetValue(entity, out var proto) ? Factory.Building(Core.ProtoName(proto, "buildings")) : null;
        return Call.Has(entity, S.Site) ? Factory.Building(Call.BuildingType(entity)) : null;
    }

    /// <summary>Площадка под разбором.</summary>
    public bool Demolish(uint entity) => Known(entity) ? _snap!.SiteDemolish.GetValueOrDefault(entity) : Call.Flag(entity, S.Site, L.SiteDemolish);

    /// <summary>Своё готовое здание (isReady).</summary>
    public bool IsReady(uint player, uint entity) => BuildingType(entity) is not null && !IsSite(entity) && IsOwn(player, entity);

    public bool IsDrop(uint entity) => Known(entity) ? _snap!.Drop.Contains(entity) : Call.Has(entity, S.Drop);

    public bool IsUnit(uint entity) => Known(entity) ? _snap!.Unit.ContainsKey(entity) : Call.Has(entity, S.Unit);

    public string? UnitType(uint entity)
    {
        if (Known(entity))
            return _snap!.Unit.TryGetValue(entity, out var unit) ? Core.ProtoName(unit.Proto, "units") : null;
        return Call.UnitType(entity);
    }

    /// <summary>Радиус тела юнита.</summary>
    public double Radius(uint entity)
    {
        if (Known(entity) && _snap!.Unit.TryGetValue(entity, out var unit))
            return unit.Radius;
        return Call.Num(entity, S.Body, L.BodyRadius, Factory.Unit(UnitType(entity))?.Radius ?? 0);
    }

    public bool Converting(uint entity) => _snap is not null ? _snap.Converting.Contains(entity) : Call.Has(entity, S.Converting);

    public bool Off(uint entity) => _snap is not null ? _snap.Off.Contains(entity) : Call.Has(entity, L.Off);

    /// <summary>Хранилище ли это (isStore).</summary>
    public bool IsStore(uint entity) => Building(entity)?.Stores == true;

    /// <summary>Станция запроса (isRequester).</summary>
    public bool IsRequester(uint entity) => Building(entity)?.Requester == true;

    // ===== Отметка «не подъехать» =====

    /// <summary>До какого тика к сущности не подъехать; нет отметки — 0.</summary>
    public double Until(uint entity)
    {
        if (_marks.TryGetValue(entity, out var until))
            return until;
        if (_snap is not null)
            return _snap.Until.GetValueOrDefault(entity);
        return Call.Has(entity, L.Unreachable) ? Call.Num(entity, L.Unreachable, L.UnreachableUntil) : 0;
    }

    /// <summary>Не подъехать грузовикам к зданию или дропу (unreachable).</summary>
    public bool Unreachable(uint entity) => Until(entity) > Tick;

    /// <summary>Есть ли отметка (и та, чей срок вышел).</summary>
    public bool Marked(uint entity) => _marks.TryGetValue(entity, out var until) ? until > 0 : _snap is not null ? _snap.Until.ContainsKey(entity) : Call.Has(entity, L.Unreachable);

    /// <summary>Ставит отметку до тика until; 0 — снимает.</summary>
    public void Mark(uint entity, double until)
    {
        var had = Marked(entity);
        _marks[entity] = Math.Max(0, until);
        if (until <= 0)
        {
            if (had)
                Call.Commands.Remove(entity, L.Unreachable);
        }
        else if (had)
            Call.Set(entity, L.Unreachable).Number(L.UnreachableUntil, until);
        else
            Call.Add(entity, L.Unreachable).Number(L.UnreachableUntil, until);
    }

    // ===== Заказы, рецепты, производство =====

    /// <summary>Есть ли у сущности заказ станции запроса.</summary>
    public bool HasWanted(uint entity) => Known(entity) ? _snap!.Wanted.ContainsKey(entity) : Call.Has(entity, S.Wanted);

    /// <summary>Сколько груза станция держит по заказу (wantedOf).</summary>
    public double Wanted(uint entity, int good)
    {
        if (Known(entity))
            return _snap!.Wanted.TryGetValue(entity, out var wanted) ? wanted[good] : 0;
        return L.Wanted[good] is { } field && Call.Has(entity, S.Wanted) ? Call.Num(entity, S.Wanted, field) : 0;
    }

    /// <summary>Заявка космопорта: груз, сколько, полёт (total &gt; 0 — корабль улетел), закупка ли; нет — null.</summary>
    public (int Good, double Wanted, double Total, bool Buy)? Trade(uint entity)
    {
        if (Known(entity))
            return _snap!.Trade.TryGetValue(entity, out var trade) ? trade : null;
        if (!Call.Has(entity, L.Trade))
            return null;
        return (Cargo.Of(Call.Num(entity, L.Trade, L.TradeResource)), Call.Num(entity, L.Trade, L.TradeWanted), Call.Num(entity, L.Trade, L.TradeTotal), Call.Flag(entity, L.Trade, L.TradeBuy));
    }

    /// <summary>Включён ли цех; не цех — null.</summary>
    public bool? Assembly(uint entity)
    {
        if (Known(entity))
            return _snap!.Assembly.TryGetValue(entity, out var on) ? on : null;
        return Call.Has(entity, S.Assembly) ? Call.Flag(entity, S.Assembly, L.AssemblyOn) : null;
    }

    /// <summary>Сырьё рецепта включённого цеха (inputsOf); выключенный или не цех — пусто.</summary>
    public List<int> Inputs(uint entity)
    {
        var list = new List<int>();
        if (Assembly(entity) == true && Building(entity) is { } spec)
            foreach (var name in Cargo.Recipe(spec).Keys)
                if (Cargo.Index(name) is var good and >= 0)
                    list.Add(good);
        return list;
    }

    /// <summary>Прогресс первого заказа производства; производства нет — null.</summary>
    public double? Producing(uint entity)
    {
        if (Known(entity))
            return _snap!.Producer.TryGetValue(entity, out var progress) ? progress : null;
        return Call.Has(entity, S.Producer) ? Call.Num(entity, S.Producer, S.ProducerProgress) : null;
    }

    /// <summary>Луч сущности; нет — null.</summary>
    public (double Radius, double Rate, bool Give, bool Take)? Beam(uint entity)
    {
        if (Known(entity))
            return _snap!.Beam.TryGetValue(entity, out var beam) ? beam : null;
        if (!Call.Has(entity, S.Beam))
            return null;
        return (Call.Num(entity, S.Beam, S.BeamRadius), Call.Num(entity, S.Beam, S.BeamRate), Call.Flag(entity, S.Beam, S.BeamGive), Call.Flag(entity, S.Beam, S.BeamTake));
    }

    // ===== Места =====

    /// <summary>Центр здания или площадки, место юнита и дропа (centerOf).</summary>
    public (double X, double Y) Center(uint entity)
    {
        // Места за вызов не меняются: ход — дело движка, между системами.
        if (_centers.TryGetValue(entity, out var center))
            return center;
        var (x, y) = At(entity);
        var spec = Building(entity) ?? SiteSpec(entity);
        center = spec is null ? (x, y) : (x + spec.Width / 2, y + spec.Height / 2);
        _centers[entity] = center;
        return center;
    }

    private readonly Dictionary<uint, (double X, double Y)> _centers = [];

    /// <summary>Расстояние между центрами (distance в logistics.ts).</summary>
    public double Distance(uint a, uint b)
    {
        var (ax, ay) = Center(a);
        var (bx, by) = Center(b);
        return DetMath.Hypot(ax - bx, ay - by);
    }
}
