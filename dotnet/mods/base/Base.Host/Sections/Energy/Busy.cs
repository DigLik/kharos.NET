using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Energy.Refs;
using KernelProduction = Base.Host.Sections.Kernel.Production;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Занятые потребители (busy.ts, markBusy). Здание с делом просит у сети всю свою энергию, без дела — треть (см.
/// <see cref="Books"/>). Есть ли дело, хост решает раз в <see cref="PowerRules.BusyTicks"/> тиков и ставит метку Busy:
/// клиент считает хозяйство сам по своей копии мира. «Дело» от энергии не зависит, иначе в сети без неё метка мигала бы.
/// Кончив дело, здание ещё <see cref="PowerRules.BusyHold"/> секунд значится занятым. Когда кого последний раз видели за
/// работой, — в памяти мода (ключ <see cref="Key"/>): только те, кто ещё держит метку, — старше срока всё равно что нет.
/// </summary>
internal sealed class Busy : ISystem
{
    /// <summary>Ключ памяти раздела: пары «здание u32, тик u64».</summary>
    public const string Key = "energy.busy";

    private const double Epsilon = 1e-6;

    /// <summary>Сборка цеха: погрешность места под готовое (EPSILON в assembly.ts).</summary>
    private const double AssemblyEpsilon = 1e-9;

    private Column _proto;

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
    }

    public void Run(SystemContext ctx)
    {
        var call = Call.Of(ctx);
        var memory = Memory.Of(call);
        var before = memory.Get(Key);
        var last = Read(before);
        var hold = (ulong)Math.Round(PowerRules.BusyHold / Core.Step);
        var protos = ctx.Column(_proto);
        var alive = new HashSet<uint>();
        var changed = new List<uint>();
        for (var row = 0; row < ctx.Count; row++)
        {
            if (Core.ProtoName((int)protos[row], "buildings") is not { } type || Factory.Building(type) is not { } spec)
                continue;
            // Кто работает всегда, тому метка не нужна: хозяйство знает это по виду здания.
            if ((spec.Power ?? 0) >= 0 || PowerRules.AlwaysBusy(spec))
                continue;
            var entity = ctx.Entities[row];
            alive.Add(entity);
            var off = call.Has(entity, S.Site) || Books.SwitchedOff(call, entity);
            if (!off && Working(call, entity, spec))
                last[entity] = ctx.Tick;
            // Выключенному и недостроенному держать метку незачем.
            var busy = !off && last.TryGetValue(entity, out var since) && ctx.Tick - since < hold;
            if (busy != call.Has(entity, R.Busy))
                changed.Add(entity);
        }
        foreach (var entity in changed)
        {
            if (call.Has(entity, R.Busy))
                call.Remove(entity, R.Busy);
            else
                call.Add(entity, R.Busy);
        }
        foreach (var entity in last.Keys.ToList())
            if (!alive.Contains(entity) || ctx.Tick - last[entity] >= hold)
                last.Remove(entity);
        var after = Write(last);
        if (!after.AsSpan().SequenceEqual(before ?? []))
            memory.Put(call, Key, after);
    }

    private static Dictionary<uint, ulong> Read(byte[]? bytes)
    {
        var last = new Dictionary<uint, ulong>();
        for (var at = 0; bytes is not null && at + 12 <= bytes.Length; at += 12)
            last[BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at))] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 4));
        return last;
    }

    private static byte[] Write(Dictionary<uint, ulong> last)
    {
        var bytes = new byte[last.Count * 12];
        var at = 0;
        foreach (var (entity, tick) in last.OrderBy(pair => pair.Key))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), entity);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 4), tick);
            at += 12;
        }
        return bytes;
    }

    /// <summary>Есть ли у готового включённого потребителя дело (working). Вид, о котором тут ничего не сказано, занят всегда.</summary>
    private static bool Working(Call call, uint entity, BuildingSpec spec)
    {
        // Переработка: есть руда и место под готовое.
        if (spec.Refines is { } ore)
            return call.Has(entity, S.Stock) && Stocks.Amount(call, entity, ore) > Epsilon && Goods.ResourceOf(ore) is { } product && Stocks.Room(call, entity, product) > Epsilon;
        // Цех: сборка идёт или её есть из чего начать; готовое, которому некуда лечь, — простой.
        if (call.Has(entity, S.Assembly))
        {
            if (spec.Assembles is not { } made || !Core.Tables.Products.TryGetValue(made, out var recipe))
                return false;
            var room = Stocks.Room(call, entity, made) >= recipe.Yield - AssemblyEpsilon;
            var progress = call.Num(entity, S.Assembly, R.AssemblyProgress);
            if (progress > 0)
                return progress < recipe.Recipe.Values.Sum() - Epsilon || room;
            return call.Has(entity, S.Stock) && room && recipe.Recipe.All(pair => Stocks.Amount(call, entity, pair.Key) >= pair.Value - Epsilon);
        }
        // Производство: заказ начат или его материалы на месте.
        if (spec.Produces.Length > 0)
        {
            var queue = KernelProduction.Queue(call, entity);
            if (queue.Count == 0 || call.Has(entity, S.Converting))
                return false;
            return call.Num(entity, S.Producer, S.ProducerProgress) > 0 || HasMaterials(call, entity, queue[0]);
        }
        // Лаборатория: ведёт тему или вела бы, будь энергия.
        if (spec.Researches)
            return call.Has(entity, R.Research) && (call.Num(entity, R.Research, R.ResearchSpeed) > 0 || call.Text(entity, R.Research, R.ResearchLack) == "power");
        if (spec.Repair is not null)
            return call.Num(entity, S.Repair, R.RepairTarget) != 0;
        if (spec.Warhead is not null)
            return call.Has(entity, S.Silo)
                && ((call.Num(entity, S.Silo, R.SiloTicks) > 0 && !call.Flag(entity, S.Silo, R.SiloReady)) || call.Num(entity, S.Silo, R.SiloStart) >= 0);
        if (spec.Intercept is not null)
            return call.Num(entity, S.Interceptor, R.InterceptorTicks) > 0;
        // Космопорт: идёт рейс или подарок союзнику.
        if (spec.Trades)
            return Base.Host.Sections.Trade.Trading.Busy(call, entity);
        // Щит: купол не полон — заряжается.
        if (spec.Shield is { } shield)
            return call.Num(entity, S.Shield, R.ShieldCharge) < shield.Capacity - Epsilon;
        // Турель на энергии: есть цель или оружие ещё перезаряжается.
        if (spec.Mounts.Length > 0)
            return Factory.TurretsOf(call, entity).Any(turret =>
                call.Has(turret, S.Armed) && (call.Num(turret, S.Armed, R.ArmedTarget) != 0 || call.Num(turret, S.Armed, R.ArmedCooldown) > 0));
        return true;
    }

    /// <summary>Материалы первого заказа на складе (missingFor пуст).</summary>
    private static bool HasMaterials(Call call, uint entity, string unit)
    {
        foreach (var (good, amount) in Factory.Unit(unit)?.Materials ?? [])
            if (amount - Stocks.Amount(call, entity, good) > Epsilon)
                return false;
        return true;
    }
}
