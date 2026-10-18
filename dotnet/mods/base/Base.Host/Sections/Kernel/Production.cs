using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Kernel;

/// <summary>
/// Производство юнитов (production.ts): очередь заказов у MCV и производящих зданий (Producer.queue — номера Proto
/// юнитов i32 подряд, первый строится; Producer.current — он же или −1), лимит армии (SUPPLY, rules.armyLimit), точка
/// сбора. Скорость при нехватке энергии даёт раздел Energy через <see cref="Speed"/>.
/// </summary>
internal static class Production
{
    /// <summary>Сколько заказов помещается в очередь (QUEUE_LIMIT).</summary>
    public const int QueueLimit = 5;

    /// <summary>Среди скольких ближайших тайлов ищется выход для готового юнита (EXIT_CANDIDATES).</summary>
    private const int ExitCandidates = 24;

    /// <summary>Скорость производства здания (энергия, зона): 1 — полная, 0 — стоит; null — всегда полная.</summary>
    public static Func<Call, uint, double>? Speed { get; set; }

    /// <summary>Что лежит в Producer: переезжает с превращением.</summary>
    public sealed record State(double Progress, double Current, bool Rally, double RallyX, double RallyY, byte[] Queue);

    /// <summary>Состояние производства сущности; нет Producer — null.</summary>
    public static State? Take(Call call, uint entity) => call.Has(entity, S.Producer)
        ? new State(call.Num(entity, S.Producer, S.ProducerProgress), call.Num(entity, S.Producer, S.ProducerCurrent, -1), call.Flag(entity, S.Producer, S.ProducerRally),
            call.Num(entity, S.Producer, S.ProducerRallyX), call.Num(entity, S.Producer, S.ProducerRallyY), call.Bytes(entity, S.Producer, S.ProducerQueue) ?? [])
        : null;

    /// <summary>Кладёт состояние производства (сущность с Producer).</summary>
    public static void Put(Call call, uint entity, State? state)
    {
        if (state is null || !call.Has(entity, S.Producer))
            return;
        call.Set(entity, S.Producer).Number(S.ProducerProgress, state.Progress).Number(S.ProducerCurrent, state.Current).Flag(S.ProducerRally, state.Rally)
            .Number(S.ProducerRallyX, state.RallyX).Number(S.ProducerRallyY, state.RallyY).Bytes(S.ProducerQueue, state.Queue);
    }

    /// <summary>Очередь производителя: виды юнитов, первый строится.</summary>
    public static List<string> Queue(Call call, uint entity)
    {
        var list = new List<string>();
        foreach (var id in Accounts.Ids(call.Bytes(entity, S.Producer, S.ProducerQueue)))
            if (Core.ProtoName(id, "units") is { } type)
                list.Add(type);
        return list;
    }

    /// <summary>Пишет очередь и current — первый в ней или −1.</summary>
    public static void SetQueue(Call call, uint entity, IReadOnlyList<string> queue, double? progress = null)
    {
        var ids = queue.Select(Core.UnitProto).ToList();
        var writer = call.Set(entity, S.Producer).Bytes(S.ProducerQueue, Accounts.Pack(ids)).Number(S.ProducerCurrent, ids.Count > 0 ? ids[0] : -1);
        if (progress is { } value)
            writer.Number(S.ProducerProgress, value);
    }

    /// <summary>
    /// Кого производит сущность сейчас (producibleBy): готовое здание — кто записан в его виде; под разбором — никого;
    /// MCV — свёрнутая база — никого: очередь в нём едет и стоит до разворота.
    /// </summary>
    public static string[] Producible(Call call, uint entity)
    {
        if (!call.Has(entity, S.Producer) || call.Has(entity, S.Site) || !call.Has(entity, S.Building))
            return [];
        return Factory.Building(call.BuildingType(entity))?.Produces ?? [];
    }

    /// <summary>Свёрнутая ли это база: машина с очередью главного здания (isPacked).</summary>
    public static bool Packed(Call call, uint entity) => call.Has(entity, S.Unit) && call.Has(entity, S.Producer);

    /// <summary>Вес юнита в лимите армии (SUPPLY).</summary>
    public static double Supply(string type) => Factory.Unit(type)?.Supply ?? 0;

    /// <summary>Сколько тиков строится юнит (buildTicks).</summary>
    public static int BuildTicks(string type) => Core.Ticks(Factory.Unit(type)?.BuildTime ?? 0);

    /// <summary>Занято в лимите армии игрока: его юниты и заказы во всех его очередях (armyOf).</summary>
    public static double Army(Call call, uint player)
    {
        double used = 0;
        foreach (var unit in Core.All(S.Unit, S.Owner))
            if (call.OwnerOf(unit) == player && call.UnitType(unit) is { } type)
                used += Supply(type);
        foreach (var producer in Core.All(S.Producer, S.Owner))
            if (call.OwnerOf(producer) == player)
                foreach (var type in Queue(call, producer))
                    used += Supply(type);
        return used;
    }

    /// <summary>Не влезет ли юнит в лимит армии (overLimit); лимит 0 — его нет.</summary>
    public static bool OverLimit(Call call, uint player, string type) =>
        Core.Rules.ArmyLimit > 0 && Army(call, player) + Supply(type) > Core.Rules.ArmyLimit;

    /// <summary>Каких зданий из требований юнита у игрока нет готовыми (missingRequirements).</summary>
    public static List<string> MissingRequirements(Call call, uint player, string type)
    {
        var needs = Factory.Unit(type)?.Requires ?? [];
        if (needs.Length == 0)
            return [];
        var have = new HashSet<string>();
        foreach (var building in Core.All(S.Building, S.Owner))
            if (call.OwnerOf(building) == player && !call.Has(building, S.Site) && call.BuildingType(building) is { } kind)
                have.Add(kind);
        return [.. needs.Where(need => !have.Contains(need))];
    }

    /// <summary>Тема, которую надо исследовать, чтобы заказать юнит (missingResearch); null — нечего.</summary>
    public static string? MissingResearch(Call call, uint player, string type)
    {
        var topic = Factory.Unit(type)?.Research;
        return Accounts.Researched(call, player, topic) ? null : topic;
    }

    /// <summary>
    /// Заказывает юнит (orderUnit): производитель свой и производит такой, очередь не полна, требования и тема есть, лимит
    /// армии не превышен, кредиты списаны сразу. Материалы тратятся в начале работы.
    /// </summary>
    public static bool Order(Call call, uint player, uint entity, string type)
    {
        if (!call.Has(entity, S.Producer) || !call.IsOwn(player, entity))
            return false;
        var queue = Queue(call, entity);
        if (!Producible(call, entity).Contains(type) || queue.Count >= QueueLimit)
            return false;
        if (MissingRequirements(call, player, type).Count > 0 || MissingResearch(call, player, type) is not null)
            return false;
        if (OverLimit(call, player, type) || !Accounts.Pay(call, player, Factory.Unit(type)!.Cost))
            return false;
        queue.Add(type);
        SetQueue(call, entity, queue);
        return true;
    }

    /// <summary>Отменяет последний заказ и возвращает за него кредиты; начатый — и материалы на склад (cancelUnit).</summary>
    public static bool Cancel(Call call, uint player, uint entity)
    {
        if (!call.Has(entity, S.Producer) || !call.IsOwn(player, entity))
            return false;
        var queue = Queue(call, entity);
        if (queue.Count == 0)
            return false;
        var progress = call.Num(entity, S.Producer, S.ProducerProgress);
        var started = queue.Count == 1 && progress > 0;
        var type = queue[^1];
        queue.RemoveAt(queue.Count - 1);
        SetQueue(call, entity, queue, queue.Count == 0 ? 0 : null);
        Accounts.Add(call, player, Factory.Unit(type)!.Cost);
        if (started && call.Has(entity, S.Stock))
            foreach (var (good, amount) in Factory.Unit(type)!.Materials)
                if (S.Stock.Find(good) is { } field)
                    call.Set(entity, S.Stock).Number(field, call.Num(entity, S.Stock, field) + amount);
        return true;
    }

    /// <summary>Точка сбора своего производящего здания (rally): тайл на карте.</summary>
    public static bool Rally(Call call, uint player, uint building, int x, int y)
    {
        if (!Placement.InBounds(x, y) || !call.Has(building, S.Producer) || !call.Has(building, S.Building) || !call.IsOwn(player, building))
            return false;
        call.Set(building, S.Producer).Flag(S.ProducerRally, true).Number(S.ProducerRallyX, x).Number(S.ProducerRallyY, y);
        return true;
    }

    /// <summary>Хватает ли на складе материалов первого заказа (missingFor пуст).</summary>
    private static bool HasMaterials(Call call, uint entity, string type)
    {
        foreach (var (good, amount) in Factory.Unit(type)!.Materials)
            if (amount - (S.Stock.Find(good) is { } field ? call.Num(entity, S.Stock, field) : 0) > 1e-6)
                return false;
        return true;
    }

    /// <summary>Тайл, вокруг которого выходят готовые: центр здания или место машины (exitOf).</summary>
    private static (int X, int Y) ExitOf(Call call, uint entity)
    {
        var (x, y) = call.At(entity);
        if (Factory.Building(call.BuildingType(entity)) is not { } spec || !call.Has(entity, S.Building))
            return ((int)Math.Floor(x), (int)Math.Floor(y));
        return ((int)x + (int)spec.Width / 2, (int)y + (int)spec.Height / 2);
    }

    /// <summary>Ближайший к точке проходимый тайл без юнита; сама точка не в счёт (emptyTileNear).</summary>
    private static (int X, int Y)? EmptyTileNear(Call call, int x, int y)
    {
        var taken = new HashSet<(int, int)>();
        foreach (var unit in Tools.Near(x, y, 24, S.Unit))
        {
            var (px, py) = call.At(unit);
            taken.Add(((int)Math.Floor(px), (int)Math.Floor(py)));
        }
        foreach (var born in call.Stage.Spawned)
            if (call.Has(born, S.Unit))
            {
                var (px, py) = call.At(born);
                taken.Add(((int)Math.Floor(px), (int)Math.Floor(py)));
            }
        foreach (var tile in Tools.FreeTiles(x, y, ExitCandidates, 1))
            if (!taken.Contains(tile))
                return tile;
        return null;
    }

    /// <summary>Раз в тик (produceUnits): продвигает производство и выпускает готовых рядом с производителем.</summary>
    public sealed class ProduceUnits : ISystem
    {
        private Column _current, _progress;

        public void Bind(SystemBinding binding)
        {
            _current = binding.Column(S.Producer.Name, S.ProducerCurrent.Name);
            _progress = binding.Column(S.Producer.Name, S.ProducerProgress.Name);
        }

        public void Run(SystemContext ctx)
        {
            var current = ctx.Column(_current);
            var progress = ctx.Column(_progress);
            var entities = ctx.Entities;
            Call? call = null;
            var ready = new List<uint>();
            for (var row = 0; row < entities.Length; row++)
            {
                if (current[row] < 0)
                    continue;
                call ??= Call.Of(ctx);
                var entity = entities[row];
                // Пока здание сворачивается, разбирается, свёрнуто в MCV или машина разворачивается, производство стоит.
                if (call.Has(entity, S.Converting) || call.Has(entity, S.Site) || Packed(call, entity))
                    continue;
                if (Core.ProtoName((int)current[row], "units") is not { } type)
                    continue;
                var needed = BuildTicks(type);
                if (progress[row] <= 0)
                {
                    // Заказ начинается, когда его материалы на месте, и сразу их тратит.
                    if (!HasMaterials(call, entity, type))
                        continue;
                    var materials = Factory.Unit(type)!.Materials;
                    if (materials.Count > 0)
                    {
                        if (call.Has(entity, S.Stock))
                        {
                            var writer = call.Set(entity, S.Stock);
                            foreach (var (good, amount) in materials)
                                if (S.Stock.Find(good) is { } field)
                                    writer.Number(field, call.Num(entity, S.Stock, field) - amount);
                        }
                        // Материалы потрачены — заказ начат, даже если энергии сейчас нет.
                        progress[row] = 1e-9;
                    }
                }
                if (progress[row] < needed)
                    progress[row] += Speed?.Invoke(call, entity) ?? 1;
                if (progress[row] >= needed)
                    ready.Add(entity);
            }
            if (call is null)
                return;
            // Юниты появляются после обхода.
            foreach (var entity in ready)
            {
                var (x, y) = ExitOf(call, entity);
                // Выйти некуда — готовый ждёт внутри, очередь стоит.
                if (EmptyTileNear(call, x, y) is not { } tile)
                    continue;
                var player = call.OwnerOf(entity);
                var queue = Queue(call, entity);
                var unit = Factory.SpawnUnit(call, queue[0], player, tile.X + 0.5, tile.Y + 0.5);
                queue.RemoveAt(0);
                SetQueue(call, entity, queue);
                progress[ctx.RowOf(entity)] = 0;
                // Есть точка сбора — готовый едет туда, на свободное место рядом с ней.
                if (call.Flag(entity, S.Producer, S.ProducerRally))
                    Moves.Send(call, unit, (int)call.Num(entity, S.Producer, S.ProducerRallyX), (int)call.Num(entity, S.Producer, S.ProducerRallyY));
                Accounts.Reward(call, player, "unit");
            }
        }
    }
}
