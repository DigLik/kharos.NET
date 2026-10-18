using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host.Sections.Mining;

/// <summary>Что смена месторождений помнит между тиками (DepositLayer.due и spawned): память мода, ключ «mining».</summary>
[MemoryPackable]
internal sealed partial class RenewalState
{
    /// <summary>Тики, к которым должны появиться новые месторождения на смену выработанным, по возрастанию.</summary>
    public ulong[] Due { get; set; } = [];

    /// <summary>Сколько мест перепробовано под новые месторождения: от этого числа идёт их случайный выбор.</summary>
    public int Spawned { get; set; }

    private const string Key = "mining";

    /// <summary>Состояние из памяти вызова; нет — пустое.</summary>
    public static RenewalState Of(Call call) =>
        Memory.Of(call).Get(Key) is { Length: > 0 } bytes ? MemoryPackSerializer.Deserialize<RenewalState>(bytes) ?? new RenewalState() : new RenewalState();

    /// <summary>Кладёт состояние в память мода (применится командой после вызова).</summary>
    public void Save(Call call) => Memory.Of(call).Put(call, Key, MemoryPackSerializer.Serialize(this));
}

/// <summary>
/// Смена месторождений (renewal.ts, §9 п. 2 docs/design.md). Выработанное месторождение уходит с карты, как только на нём
/// нет шахты, а через rules.depositRenewal минут после выработки в случайной пустой клетке карты появляется новое — тем же
/// генератором: вид по биому, запас по виду. Месторождений на карте столько же, сколько было, но лежат они уже не там:
/// за ними приходится идти, и разведка не кончается.
/// <para>
/// Что игрок знает о месторождениях в тумане (нашёл новое, помнит ушедшее — DepositSpot.found и ghosts в TS), здесь не
/// считается: это память запоминаемого слоя игрока (<c>deposits</c> с remember в layers.json), дело движка (docs/layers.md).
/// От тумана остаётся одно: новое месторождение не встаёт на глазах у игроков.
/// </para>
/// </summary>
internal static class Renewal
{
    /// <summary>Раз в сколько тиков всё это проверяется: месторождения уходят и появляются за минуты, а не за тики (CHECK_TICKS).</summary>
    public const int CheckTicks = 20;

    /// <summary>Не ближе скольких тайлов к чужим и своим зданиям и стройкам появляется новое месторождение: не посреди базы (CLEARANCE).</summary>
    public const int Clearance = 12;

    /// <summary>Сколько клеток пробуется за проверку под одно новое месторождение; не нашлось — до следующей проверки (TRIES).</summary>
    public const int Tries = 24;

    /// <summary>Сдвиг сида: места новых месторождений не повторяют места генератора (SALT).</summary>
    public const int Salt = 40507;

    /// <summary>Основание зданий и площадок не шире этого: вокруг месторождения ищем и те, чей угол дальше.</summary>
    private const double BaseReach = 8;

    /// <summary>Система renewDeposits: на сущностях Deposit — то, что уже начали добывать.</summary>
    public sealed class RenewDeposits : ISystem
    {
        private Column _x, _y, _mined, _spent;

        public void Bind(SystemBinding binding)
        {
            _x = binding.Column(Names.Position.Name, Names.Position.X);
            _y = binding.Column(Names.Position.Name, Names.Position.Y);
            _mined = binding.Column(Names.Deposit.Name, Names.Deposit.Mined);
            _spent = binding.Column(Names.Deposit.Name, Names.Deposit.Spent);
        }

        public void Run(SystemContext ctx)
        {
            if (ctx.Tick % CheckTicks != 0 || Core.Rules.DepositRenewal <= 0 || S.Deposits is null)
                return;
            var call = Call.Of(ctx);
            var state = RenewalState.Of(call);
            var gone = new HashSet<(int, int)>();
            var changed = Retire(ctx, call, state, gone);
            changed |= Spawn(call, state, ctx.Tick, gone);
            if (changed)
                state.Save(call);
        }

        /// <summary>Выработанным месторождениям заказывает смену, а те, на которых нет шахты, убирает с карты (retire).</summary>
        private bool Retire(SystemContext ctx, Call call, RenewalState state, HashSet<(int, int)> gone)
        {
            var xs = ctx.Column(_x);
            var ys = ctx.Column(_y);
            var mined = ctx.Column(_mined);
            var spent = ctx.Column(_spent);
            var due = state.Due.ToList();
            var changed = false;
            for (var row = 0; row < ctx.Count; row++)
            {
                if (Deposits.At((int)xs[row], (int)ys[row]) is not { } spot || spot.Reserve - mined[row] > 0)
                    continue;
                if (spent[row] == 0)
                {
                    spent[row] = 1;
                    due.Add(ctx.Tick + (ulong)Core.Ticks(Core.Rules.DepositRenewal * 60));
                    due.Sort();
                    changed = true;
                }
                if (Deposits.HasMine(call, spot))
                    continue;
                Deposits.Drop(call, spot.X, spot.Y);
                gone.Add((spot.X, spot.Y));
            }
            state.Due = [.. due];
            return changed;
        }

        /// <summary>Кладёт новые месторождения, чей срок подошёл (spawn).</summary>
        private static bool Spawn(Call call, RenewalState state, ulong tick, HashSet<(int, int)> gone)
        {
            var due = state.Due.ToList();
            var (count, spawned) = (due.Count, state.Spawned);
            var taken = new HashSet<(int, int)>();
            while (due.Count > 0 && due[0] <= tick)
            {
                if (RenewedSpot(call, state, taken, gone) is not { } spot)
                    break;
                due.RemoveAt(0);
                Deposits.Add(call, spot);
            }
            state.Due = [.. due];
            return due.Count != count || spawned != state.Spawned;
        }

        /// <summary>
        /// Место под новое месторождение: случайная клетка карты, где месторождения нет; в клетке — место, где можно
        /// строить, вдали от зданий и, если есть туман, не на глазах у игроков. Не нашлось за <see cref="Tries"/> клеток —
        /// null (renewedSpot). Слой в этом вызове ещё прежний: занятые за вызов клетки — taken, ушедшие месторождения — gone.
        /// </summary>
        private static Spot? RenewedSpot(Call call, RenewalState state, HashSet<(int, int)> taken, HashSet<(int, int)> gone)
        {
            var seed = Generator.Seed + Salt;
            var (left, top, right, bottom) = Generator.Bounds;
            var (cellLeft, cellTop) = (Generator.FloorDiv(left, Deposits.Cell), Generator.FloorDiv(top, Deposits.Cell));
            var columns = Generator.FloorDiv(right - 1, Deposits.Cell) - cellLeft + 1;
            var rows = Generator.FloorDiv(bottom - 1, Deposits.Cell) - cellTop + 1;
            IReadOnlyList<uint> players = Generator.Fog ? Accounts.Players(call) : [];
            for (var attempt = 0; attempt < Tries; attempt++)
            {
                var n = state.Spawned++;
                var cellX = cellLeft + (int)Math.Floor(Generator.Hash(n, 0, seed) * columns);
                var cellY = cellTop + (int)Math.Floor(Generator.Hash(n, 1, seed) * rows);
                if (taken.Contains((cellX, cellY)) || Deposits.In(cellX, cellY).Any(spot => !gone.Contains((spot.X, spot.Y))))
                    continue;
                var spot = Generator.Place(cellX, cellY, k => Generator.Hash(n, k + 2, seed), (x, y) => Clear(call, x, y) && Unseen(players, x, y));
                if (spot is null)
                    continue;
                taken.Add((cellX, cellY));
                return spot;
            }
            return null;
        }

        /// <summary>Дальше ли месторождение 2×2 с левым верхним тайлом (x, y) от любого основания здания и площадки, чем <see cref="Clearance"/>.</summary>
        private static bool Clear(Call call, int x, int y)
        {
            foreach (var entity in Tools.Near(x + Deposits.Size / 2.0, y + Deposits.Size / 2.0, Clearance + Deposits.Size + BaseReach * 1.5, S.Footprint))
            {
                var (bx, by) = call.At(entity);
                var (bw, bh) = (call.Num(entity, S.Footprint, S.FootprintW), call.Num(entity, S.Footprint, S.FootprintH));
                var dx = Math.Max(Math.Max(bx - (x + Deposits.Size), x - (bx + bw)), 0);
                var dy = Math.Max(Math.Max(by - (y + Deposits.Size), y - (by + bh)), 0);
                if (Math.Sqrt(dx * dx + dy * dy) < Clearance)
                    return false;
            }
            return true;
        }

        /// <summary>Никто из игроков не видит ни тайла месторождения сейчас (seesDeposit); players пусто — тумана нет.</summary>
        private static bool Unseen(IReadOnlyList<uint> players, int x, int y)
        {
            foreach (var player in players)
                for (var tileY = y; tileY < y + Deposits.Size; tileY++)
                    for (var tileX = x; tileX < x + Deposits.Size; tileX++)
                        if (Tools.Sees(player, tileX + 0.5, tileY + 0.5) == 2)
                            return false;
            return true;
        }
    }
}
