namespace Base.Host.Sections.Logistics;

/// <summary>
/// Что сейчас везут грузовики, трубы и строители (flowsOf): сколько груза едет на склад и сколько ещё заберут со
/// склада. Набравший груз везёт то, что в кузове; ещё не набравший обещал привезти и забрать столько, сколько в его
/// работе. Вклад каждого грузовика помнится отдельно — «без этого грузовика» (except) считается вычитанием.
/// </summary>
internal sealed class Flows
{
    private readonly Dictionary<(uint, int), double> _in = [], _out = [];
    private readonly Dictionary<uint, List<(bool In, uint Entity, int Good, double Amount)>> _by = [];

    /// <summary>Пустые потоки: заявки без учёта того, что уже везут.</summary>
    public static Flows None => new();

    /// <summary>Сколько груза едет на склад (incoming); except — без вклада этого грузовика.</summary>
    public double In(uint to, int good, uint except = 0) => _in.GetValueOrDefault((to, good)) - Own(except, true, to, good);

    /// <summary>Сколько груза обещано забрать со склада (outgoing); except — без вклада этого грузовика.</summary>
    public double Out(uint from, int good, uint except = 0) => _out.GetValueOrDefault((from, good)) - Own(except, false, from, good);

    private double Own(uint truck, bool incoming, uint entity, int good)
    {
        if (truck == 0 || !_by.TryGetValue(truck, out var list))
            return 0;
        var sum = 0.0;
        foreach (var item in list)
            if (item.In == incoming && item.Entity == entity && item.Good == good)
                sum += item.Amount;
        return sum;
    }

    /// <summary>Добавляет к едущему на склад (add в logistics.ts: не больше нуля — не в счёт); by — чей вклад.</summary>
    public void AddIn(uint to, int good, double amount, uint by = 0) => Add(true, to, good, amount, by);

    public void AddOut(uint from, int good, double amount, uint by = 0) => Add(false, from, good, amount, by);

    private void Add(bool incoming, uint entity, int good, double amount, uint by)
    {
        if (amount <= 0 || entity == 0 || good < 0)
            return;
        var map = incoming ? _in : _out;
        map[(entity, good)] = map.GetValueOrDefault((entity, good)) + amount;
        if (by == 0)
            return;
        if (!_by.TryGetValue(by, out var list))
            _by[by] = list = [];
        list.Add((incoming, entity, good, amount));
    }

    /// <summary>Потоки мира: пачки труб, работы и ручные приказы грузовиков, поручения строителей.</summary>
    public static Flows Of(Pass pass)
    {
        var flows = new Flows();
        var view = pass.V;
        foreach (var (to, good, amount) in pass.Batches)
            flows.AddIn(to, good, amount);
        foreach (var truck in pass.Trucks)
        {
            if (truck.From == 0 && truck.To == 0)
                continue;
            var carried = view.Amount(truck.Entity, truck.Resource);
            if (truck.To != 0)
                flows.AddIn(truck.To, truck.Resource, truck.Full ? carried : Math.Max(carried, truck.Amount), truck.Entity);
            if (truck.From != 0 && !truck.Full)
                flows.AddOut(truck.From, truck.Resource, truck.Amount - carried, truck.Entity);
        }
        // Грузовики с ручным приказом: везут в to, а ещё не набравшие обещали забрать из from (carrying.ts).
        foreach (var truck in pass.Trucks)
        {
            if (truck.Order is not { } order)
                continue;
            var carried = view.Amount(truck.Entity, order.Resource);
            var capacity = view.Capacity(truck.Entity);
            flows.AddIn(order.To, order.Resource, order.Full ? carried : Math.Max(carried, Math.Min(order.Left, capacity)), truck.Entity);
            if (order.From != 0 && !order.Full)
                flows.AddOut(order.From, order.Resource, Math.Min(order.Left, capacity) - carried, truck.Entity);
        }
        // Строители, которые сами везут материал на стройку (fetching.ts).
        foreach (var fetch in pass.Fetchers)
        {
            var carried = view.Amount(fetch.Entity, fetch.Resource);
            flows.AddIn(fetch.To, fetch.Resource, fetch.Full ? carried : Math.Max(carried, fetch.Amount), fetch.Entity);
            if (fetch.From != 0 && !fetch.Full)
                flows.AddOut(fetch.From, fetch.Resource, fetch.Amount - carried, fetch.Entity);
        }
        return flows;
    }
}
