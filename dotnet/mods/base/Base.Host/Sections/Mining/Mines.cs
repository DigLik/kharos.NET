using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Добыча (extract в hauling.ts): шахты каждый тик качают руду в свой склад, пока там есть место; что и как быстро —
/// решает месторождение под шахтой (вид даёт руду, rate вида — скорость), а уровень шахты ускоряет добычу. Месторождение
/// при этом убывает; первая добыча заводит ему сущность Deposit со счётом добытого. Вывозят руду грузовики
/// (раздел Logistics).
/// </summary>
internal static class Mines
{
    /// <summary>Система extract: на каждом готовом здании-шахте.</summary>
    public sealed class Extract : ISystem
    {
        private Column _x, _y, _proto, _level;
        private HashSet<int>? _mines;

        public void Bind(SystemBinding binding)
        {
            _x = binding.Column(Names.Position.Name, Names.Position.X);
            _y = binding.Column(Names.Position.Name, Names.Position.Y);
            _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
            _level = binding.Column(Names.Building.Name, Names.Building.Level);
        }

        public void Run(SystemContext ctx)
        {
            _mines ??= Deposits.ExtractorProtos();
            var call = Call.Of(ctx);
            var xs = ctx.Column(_x);
            var ys = ctx.Column(_y);
            var protos = ctx.Column(_proto);
            var levels = ctx.Column(_level);
            for (var row = 0; row < ctx.Count; row++)
            {
                if (!_mines.Contains((int)protos[row]))
                    continue;
                var mine = ctx.Entities[row];
                if (!call.Has(mine, S.Stock) || Deposits.At((int)xs[row], (int)ys[row]) is not { } spot || Goods.OreOf(spot.Name) is not { } ore)
                    continue;
                // Шахта уровнем выше качает быстрее — и месторождение под ней кончается раньше.
                var rate = Deposits.Rate(spot.Name, levels[row]);
                var wanted = Math.Min(rate * ctx.Dt, Stocks.Room(call, mine, ore));
                Stocks.Put(call, mine, ore, Deposits.Take(call, spot, Deposits.Entity(call, spot.X, spot.Y), wanted));
            }
        }
    }
}
