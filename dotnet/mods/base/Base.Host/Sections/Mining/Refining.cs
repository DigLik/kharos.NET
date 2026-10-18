using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Переработка (refining.ts): здание с refines превращает свою руду со склада в её ресурс со скоростью
/// <see cref="Rate"/>, а готовое кладёт, только пока под него есть место: потерять руду нельзя. При нехватке энергии завод
/// работает медленнее, уровнем выше — быстрее.
/// </summary>
internal static class Refining
{
    /// <summary>Сколько руды переработка принимает в секунду (REFINE_RATE): одного завода хватает на полторы шахты металла.</summary>
    public const double Rate = 1.5;

    /// <summary>Сколько руды уходит на единицу готового ресурса (REFINE_RATIO).</summary>
    public const double Ratio = 1;

    /// <summary>
    /// Доля полной скорости, с которой работает потребитель энергии (powerSupply): 1 — энергии хватает, 0 — завод стоит. Ставит
    /// раздел Energy; пока не поставил, энергия не считается и заводы работают на полной скорости.
    /// </summary>
    public static Func<Call, uint, double>? Supply { get; set; }

    /// <summary>Система refine.</summary>
    public sealed class Refine : ISystem
    {
        private Column _proto, _level;
        private Dictionary<int, string>? _ores;

        public void Bind(SystemBinding binding)
        {
            _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
            _level = binding.Column(Names.Building.Name, Names.Building.Level);
        }

        public void Run(SystemContext ctx)
        {
            // Какую руду перерабатывает вид здания, по номеру Proto.
            _ores ??= Core.Tables.Buildings.Where(pair => pair.Value.Refines is not null)
                .Select(pair => (Id: Core.BuildingProto(pair.Key), Ore: pair.Value.Refines!)).Where(pair => pair.Id >= 0).ToDictionary(pair => pair.Id, pair => pair.Ore);
            if (_ores.Count == 0)
                return;
            var call = Call.Of(ctx);
            var protos = ctx.Column(_proto);
            var levels = ctx.Column(_level);
            for (var row = 0; row < ctx.Count; row++)
            {
                if (!_ores.TryGetValue((int)protos[row], out var ore))
                    continue;
                var entity = ctx.Entities[row];
                if (!call.Has(entity, S.Stock) || Goods.ResourceOf(ore) is not { } product)
                    continue;
                var have = Stocks.Amount(call, entity, ore);
                if (have <= Stocks.Epsilon)
                    continue;
                var supply = Supply?.Invoke(call, entity) ?? 1;
                var wanted = Math.Min(Rate * Deposits.LevelFactor(levels[row]) * ctx.Dt * supply, have);
                // Готовому некуда лечь — завод стоит, пока грузовики не развезут его.
                var taken = Math.Min(wanted, Stocks.Room(call, entity, product) * Ratio);
                if (taken > Stocks.Epsilon)
                    Stocks.Put(call, entity, product, Stocks.Take(call, entity, ore, taken) / Ratio);
            }
        }
    }
}
