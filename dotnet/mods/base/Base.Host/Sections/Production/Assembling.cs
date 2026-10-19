using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Mining;
using Base.Shared;
using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Production;

/// <summary>
/// Сборка изделий (assemble в assembly.ts). Цех собирает изделие из своего вида (spec.Assembles): сырьё рецепта — со склада
/// цеха, секунд на сборку — столько, сколько единиц сырья в рецепте (<see cref="Rate"/> единиц в секунду). Сборка
/// начинается, когда сырья хватает на неё и под готовое есть место, — тогда сырьё сразу уходит в работу. Хранилища полны —
/// грузовикам некуда увезти готовое, склад цеха забит, и цех встаёт сам. При нехватке энергии сборка идёт медленнее, без неё
/// — стоит, но начатое сырьё уже потрачено. Готовое кладётся на склад цеха, откуда его разбирают заявки логистики.
/// <c>Assembly.progress</c> — секунды работы (по ним же Energy решает, занят ли цех).
/// </summary>
internal static class Assembling
{
    /// <summary>Сколько единиц сырья завод изделий перерабатывает в секунду (ASSEMBLE_RATE).</summary>
    public const double Rate = 1;

    /// <summary>Погрешность остатков (EPSILON в assembly.ts).</summary>
    public const double Epsilon = 1e-9;

    /// <summary>Сколько секунд идёт одна сборка изделия (cycleSeconds).</summary>
    public static double Cycle(ProductSpec product) => product.Recipe.Values.Sum() / Rate;

    /// <summary>Изделие и рецепт цеха; не цех изделий — null.</summary>
    public static (string Product, ProductSpec Spec)? RecipeOf(Call call, uint entity) =>
        call.BuildingType(entity) is { } type && Factory.Building(type)?.Assembles is { } made && Core.Tables.Products.TryGetValue(made, out var spec) ? (made, spec) : null;

    /// <summary>Есть ли у цеха место под готовое одной сборки (hasRoom): нет — хранилища полны, и цех стоит.</summary>
    public static bool HasRoom(Call call, uint entity, string product, ProductSpec spec) =>
        Stocks.Room(call, entity, product) >= spec.Yield - Epsilon;

    /// <summary>Можно ли начать сборку: сырьё на месте и под готовое есть место (canStart).</summary>
    public static bool CanStart(Call call, uint entity, string product, ProductSpec spec) =>
        HasRoom(call, entity, product, spec) && spec.Recipe.All(pair => Stocks.Amount(call, entity, pair.Key) >= pair.Value - Epsilon);

    /// <summary>Система assemble: раз в тик цеха собирают изделия.</summary>
    public sealed class Assemble : ISystem
    {
        private Column _proto;
        private FieldRef _on, _progress;
        private Dictionary<int, (string Product, ProductSpec Spec)>? _recipes;

        public void Bind(SystemBinding binding)
        {
            _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
            _on = S.Assembly[Names.Assembly.On];
            _progress = S.Assembly[Names.Assembly.Progress];
        }

        public void Run(SystemContext ctx)
        {
            // Какое изделие собирает вид цеха, по номеру Proto.
            _recipes ??= Core.Tables.Buildings.Where(pair => pair.Value.Assembles is not null && Core.Tables.Products.ContainsKey(pair.Value.Assembles))
                .Select(pair => (Id: Core.BuildingProto(pair.Key), Made: pair.Value.Assembles!)).Where(pair => pair.Id >= 0)
                .ToDictionary(pair => pair.Id, pair => (pair.Made, Core.Tables.Products[pair.Made]));
            var call = Call.Of(ctx);
            var protos = ctx.Column(_proto);
            for (var row = 0; row < ctx.Count; row++)
            {
                if (!_recipes.TryGetValue((int)protos[row], out var recipe))
                    continue;
                var entity = ctx.Entities[row];
                var (product, spec) = recipe;
                var progress = call.Num(entity, S.Assembly, _progress);
                if (!(progress > 0 || (call.Flag(entity, S.Assembly, _on) && CanStart(call, entity, product, spec))))
                    continue;
                if (progress <= 0)
                {
                    foreach (var (resource, amount) in spec.Recipe)
                        Stocks.Take(call, entity, resource, amount);
                    // Сырьё ушло в работу — сборка начата, даже если энергии сейчас нет.
                    progress = Epsilon;
                }
                var cycle = Cycle(spec);
                progress = Math.Min(cycle, progress + ctx.Dt * Books.ShareOf(call, entity));
                // Готовому некуда лечь — сборка ждёт, пока грузовики не освободят склад.
                if (progress >= cycle - Epsilon && HasRoom(call, entity, product, spec))
                {
                    Stocks.Put(call, entity, product, spec.Yield);
                    progress = 0;
                }
                call.Set(entity, S.Assembly).Number(_progress, progress);
            }
        }
    }
}
