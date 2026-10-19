using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Logistics;
using Base.Host.Sections.Mining;
using Base.Shared.Rules;

namespace Base.Host.Sections.Research;

/// <summary>
/// Склады сети здания на его нужды (networkStock, networkHas, takeFromNetwork, returnToNetwork в upgrades.ts): что
/// отдают по заявкам склады его сети труб (сеть — кэш зон), без резерва хранилищ; wide — склады всей его энергосети, своя
/// сеть первой. Что склад отдаёт, решает логистика (<see cref="Demand.Offers"/>, <see cref="Demand.GivenBy"/>), так что
/// лаборатория не берёт сырьё цеха и нужное темам другой лаборатории. Один на вызов: нужды лабораторий логистика
/// запоминает на свой проход — после смены выбора темы нужен новый.
/// </summary>
/// <param name="holders">Сущности со складом из таблицы кадра: трубы и станции без склада отбрасываются без вызовов; нет — спрашивается вызов.</param>
internal sealed class Networks(Call call, IReadOnlySet<uint>? holders = null)
{
    private readonly Pass _pass = Pass.Of(call);

    /// <summary>Здания, чьи склады отдают на нужды здания (sourcesOf): его сеть труб; wide — и вся энергосеть.</summary>
    public List<uint> Sources(uint building, bool wide)
    {
        var own = Zones.NetworkOf(building)?.Buildings ?? [];
        var grid = wide ? Zones.GridOf(building)?.Buildings : null;
        IEnumerable<uint> all = grid is null || grid.Count == own.Count ? own : [.. own, .. grid.Where(entity => !own.Contains(entity))];
        return [.. all.Where(entity => holders?.Contains(entity) ?? _pass.V.HasStock(entity))];
    }

    private bool Offers(uint source, int good) => Array.IndexOf(Demand.Offers(_pass, source), good) >= 0;

    /// <summary>Сколько груза сеть здания может отдать на его нужды сразу (networkStock).</summary>
    public double Stock(uint building, string good, bool wide = false)
    {
        var index = Cargo.Index(good);
        if (index < 0)
            return 0;
        var amount = 0.0;
        foreach (var source in Sources(building, wide))
            if (Offers(source, index))
                amount += Demand.GivenBy(_pass.V, source, index);
        return amount;
    }

    /// <summary>Хватает ли в сети здания готового на эту цену; недостачу меньше погрешности прощают (networkHas).</summary>
    public bool Has(uint building, IEnumerable<(string Good, double Amount)> materials, bool wide = false) =>
        materials.All(item => Stock(building, item.Good, wide) >= item.Amount - BuildRules.MaterialEpsilon);

    /// <summary>Забирает готовое со складов сети здания, сначала из хранилищ (takeFromNetwork).</summary>
    public void Take(uint building, IEnumerable<(string Good, double Amount)> materials, bool wide = false)
    {
        var all = Sources(building, wide);
        // Стабильно: хранилища вперёд, порядок сети внутри — как был.
        var sources = all.Where(_pass.V.IsStore).Concat(all.Where(entity => !_pass.V.IsStore(entity))).ToList();
        foreach (var (good, amount) in materials)
        {
            var index = Cargo.Index(good);
            var left = amount;
            foreach (var source in sources)
            {
                if (left <= ResearchRules.Epsilon)
                    break;
                if (index < 0 || !Offers(source, index))
                    continue;
                left -= Stocks.Take(call, source, good, Math.Min(left, Demand.GivenBy(_pass.V, source, index)));
            }
        }
    }

    /// <summary>Возвращает готовое в хранилища сети здания, а чему там нет места — бросает на землю у здания (returnToNetwork).</summary>
    public void Return(uint building, IEnumerable<(string Good, double Amount)> materials, bool wide = false)
    {
        var stores = Sources(building, wide).Where(_pass.V.IsStore).ToList();
        var left = new Dictionary<string, double>();
        foreach (var (good, amount) in materials)
        {
            var rest = amount;
            foreach (var store in stores)
                rest -= Stocks.Put(call, store, good, rest);
            if (rest > ResearchRules.Epsilon)
                left[good] = rest;
        }
        if (left.Count == 0 || call.BuildingType(building) is not { } type || Factory.Building(type) is not { } spec)
            return;
        var (x, y) = call.At(building);
        Drops.Items(call, x + Math.Floor(spec.Width / 2), y + Math.Floor(spec.Height / 2), left);
    }
}
