using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Energy.Refs;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Реакторы (reactors.ts): включённый жжёт стержень за стержнем со своего склада и, пока горит, вырабатывает энергию;
/// без стержней и выключенный — ничего. Стержень в работе у выключенного не тает: выключатель ставит работу на паузу.
/// Разрушенный реактор с топливом взрывается (<see cref="Meltdown"/>).
/// </summary>
internal sealed class Reactors : ISystem
{
    private Column _left;

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _left = binding.Column(Names.Reactor.Name, Names.Reactor.Left);
    }

    /// <summary>burnFuel: раз в тик, до энергии сетей — горящий берёт следующий стержень, как только догорит нынешний.</summary>
    public void Run(SystemContext ctx)
    {
        var left = ctx.Column(_left);
        Call? call = null;
        for (var row = 0; row < ctx.Count; row++)
        {
            if (left[row] > 0)
                left[row]--;
            if (left[row] > 0)
                continue;
            var entity = ctx.Entities[row];
            call ??= Call.Of(ctx);
            if (Books.Kind(call, entity)?.Spec.Burns is not { } burns || Stocks.Amount(call, entity, burns.Good) < 1)
                continue;
            Stocks.Take(call, entity, burns.Good, 1);
            left[row] = Core.Ticks(burns.Seconds);
        }
    }

    /// <summary>
    /// Авария (meltdown): здание с топливом (BuildingSpec.meltdown) оставляет на своём месте взрыв Burst — его урон
    /// по кругу считает ближайший бой (С5а). Топливо сгорает и на землю не падает; пустой реактор не взрывается, шахта —
    /// только с готовой боеголовкой. Зовут те, кто разрушает здание: бой и перегруз.
    /// </summary>
    public static void Meltdown(Call call, uint entity)
    {
        if (Books.Kind(call, entity) is not { Spec: var spec } || spec.Meltdown is not { } blast || !call.Has(entity, S.Position))
            return;
        if (spec.Burns is { } burns)
        {
            var rods = call.Has(entity, S.Stock) ? Stocks.Amount(call, entity, burns.Good) : 0;
            if (rods <= 0 && call.Num(entity, S.Reactor, R.ReactorLeft) <= 0)
                return;
            if (rods > 0)
                Stocks.Take(call, entity, burns.Good, rods);
        }
        else if (!call.Flag(entity, S.Silo, R.SiloReady))
            return;
        var (x, y) = call.At(entity);
        var burst = call.Spawn();
        call.Add(burst, S.Position).Number(S.PositionX, x + spec.Width / 2).Number(S.PositionY, y + spec.Height / 2);
        call.Add(burst, R.Burst).Number(R.BurstRadius, blast.Radius).Number(R.BurstDamage, blast.Damage);
    }
}
