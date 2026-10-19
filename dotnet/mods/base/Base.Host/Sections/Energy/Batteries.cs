using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Накопители (storeEnergy в income.ts): раз в тик, до всех потребителей, решают, сколько энергии отдают сети и сколько
/// берут. Не хватает выработки — отдают недостающее, сколько позволяют скорость и заряд; есть излишек — копят его,
/// потребителей не объедая. Отданное входит в выработку сети (Economy.Stored). Накопитель вне сети не отдаёт и не берёт.
/// </summary>
internal sealed class Batteries : ISystem
{
    private Column _charge, _flow;

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _charge = binding.Column(Names.Accumulator.Name, Names.Accumulator.Charge);
        _flow = binding.Column(Names.Accumulator.Name, Names.Accumulator.Flow);
    }

    public void Run(SystemContext ctx)
    {
        var charge = ctx.Column(_charge);
        var flows = ctx.Column(_flow);
        flows.Clear();
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        foreach (var zones in Zones.Grids().Values)
            foreach (var zone in zones)
            {
                Books.Economy? economy = null;
                var balance = 0.0;
                foreach (var entity in zone.Buildings)
                {
                    var row = ctx.RowOf(entity);
                    if (row < 0 || Books.Kind(call, entity)?.Spec.Battery is not { } battery)
                        continue;
                    if (economy is null)
                    {
                        economy = Books.OfZone(call, zone, false);
                        balance = economy.Produced - economy.Demand;
                    }
                    double flow = 0;
                    if (balance < 0 && charge[row] > 0)
                        flow = Math.Min(battery.Rate, Math.Min(-balance, charge[row] / ctx.Dt));
                    else if (balance > 0 && charge[row] < battery.Capacity)
                        flow = -Math.Min(battery.Rate, Math.Min(balance, (battery.Capacity - charge[row]) / ctx.Dt));
                    balance += flow;
                    flows[row] = flow;
                    charge[row] = Math.Max(0, Math.Min(battery.Capacity, charge[row] - flow * ctx.Dt));
                }
            }
    }
}
