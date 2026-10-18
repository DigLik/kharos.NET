using System.Diagnostics;

namespace Kharos.Engine.World;

/// <summary>
/// Порядок систем и тик (crates/world/src/host.rs). Каждый тик:
/// <list type="number">
/// <item>приказы игроков — обработчики <see cref="OnOrder"/>;</item>
/// <item>системы по порядку, если тик кратен их Every: движок собирает сущности по запросу и отдаёт их столбцы
///   (<see cref="QueryColumns"/>), после системы проверяет её команды, возвращает столбцы из Write, применяет команды
///   и раздаёт события (<see cref="On"/>) — их команды тоже, пока события есть;</item>
/// <item>сигналы копятся для клиентов (<see cref="Signals"/>).</item>
/// </list>
/// Ошибка системы (исключение или неверный буфер команд) не роняет тик: её столбцы и команды отброшены, запись — в
/// <see cref="Errors"/>.
/// </summary>
public sealed class Scheduler
{
    /// <summary>Сколько кругов событий за раз: дальше — они порождают друг друга без конца.</summary>
    public const int MaxEventRounds = 64;

    private readonly Entry[] _order;
    private readonly Dictionary<string, List<(string Label, Action<EventContext> Handler)>> _handlers = [];
    private readonly Dictionary<string, (string Label, Action<OrderContext> Handler)> _orders = [];
    private readonly List<(uint Player, string Name, byte[] Payload)> _queue = [];
    private readonly List<WorldEvent> _events = [];
    private readonly List<Signal> _signals = [];
    private readonly CommandBuffer _commands;

    public Scheduler(GameWorld world, IEnumerable<SystemSpec> systems, ulong seed = 1)
    {
        World = world;
        RandomState = Math.Max(seed, 1);
        _commands = new CommandBuffer(world);
        _order = Order(world, [.. systems]);
    }

    public GameWorld World { get; }

    /// <summary>Номер тика: первый тик — 1. Хранится в сохранении хоста.</summary>
    public ulong Tick { get; set; }

    /// <summary>Длина тика в секундах.</summary>
    public double Dt { get; private set; } = 0.05;

    /// <summary>Состояние случайного ряда (splitmix64) — для сохранения.</summary>
    public ulong RandomState { get; set; }

    /// <summary>Полные имена систем по порядку тика.</summary>
    public IReadOnlyList<string> Names => [.. _order.Select(entry => entry.Spec.Name)];

    /// <summary>Сигналы последнего тика.</summary>
    public IReadOnlyList<Signal> Signals => _signals;

    /// <summary>Ошибки систем и обработчиков; очищает тот, кто их прочёл.</summary>
    public List<SystemError> Errors { get; } = [];

    /// <summary>Обработчик события: по порядку подписки.</summary>
    public void On(string name, Action<EventContext> handler, string label = "")
    {
        if (!_handlers.TryGetValue(name, out var list))
            _handlers[name] = list = [];
        list.Add((label.Length > 0 ? label : name, handler));
    }

    /// <summary>Обработчик приказа игроков; один на имя — поздний заменяет ранний.</summary>
    public void OnOrder(string name, Action<OrderContext> handler, string label = "") => _orders[name] = (label.Length > 0 ? label : name, handler);

    /// <summary>Приказ игрока: выполнится в начале следующего тика.</summary>
    public void Send(uint player, string name, byte[] payload) => _queue.Add((player, name, payload));

    /// <summary>Событие от движка (начало партии, вход игрока): обработчики и их команды — сразу.</summary>
    public void Emit(string name, byte[] payload)
    {
        _events.Add(new WorldEvent(name, payload));
        Settle(name);
    }

    /// <summary>Один тик длиной dt секунд.</summary>
    public void Step(double dt)
    {
        Tick++;
        Dt = dt;
        _signals.Clear();
        var orders = _queue.ToArray();
        _queue.Clear();
        foreach (var (player, name, payload) in orders)
        {
            if (!_orders.TryGetValue(name, out var order))
                continue;
            _commands.Clear();
            var source = $"{order.Label}: приказ {name}";
            try
            {
                order.Handler(new OrderContext(this, _commands, player, name, payload));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Errors.Add(new SystemError(Tick, source, error.Message, error));
                continue;
            }
            ApplyCommands(source);
            Settle(source);
        }
        foreach (var entry in _order)
            if (Tick % (ulong)entry.Spec.Every == 0)
                RunSystem(entry);
    }

    /// <summary>Время систем с начала, мс.</summary>
    public IReadOnlyList<SystemTiming> Timings()
    {
        var ms = 1000.0 / Stopwatch.Frequency;
        return [.. _order.Select(entry => new SystemTiming(entry.Spec.Name, entry.Spent[0] * ms, entry.Spent[1] * ms, entry.Spent[2] * ms, entry.Spent[3] * ms))];
    }

    /// <summary>Следующее случайное в [0, 1): splitmix64, один и тот же ряд на всех платформах.</summary>
    public double NextRandom()
    {
        RandomState += 0x9E3779B97F4A7C15;
        var z = RandomState;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        z ^= z >> 31;
        return (z >> 11) / (double)(1UL << 53);
    }

    private void RunSystem(Entry entry)
    {
        var started = Stopwatch.GetTimestamp();
        _commands.Clear();
        if (entry.Query is { } query)
            query.Gather(entry.Columns);
        var gathered = Stopwatch.GetTimestamp();
        try
        {
            entry.Spec.System.Run(new SystemContext(this, _commands, entry.Spec, entry.Columns));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Errors.Add(new SystemError(Tick, entry.Spec.Name, error.Message, error));
            return;
        }
        var ran = Stopwatch.GetTimestamp();
        try
        {
            Commands.Validate(World, _commands.Written);
        }
        catch (CommandException error)
        {
            Errors.Add(new SystemError(Tick, entry.Spec.Name, error.Message, error));
            return;
        }
        if (entry.Query is { } written && entry.Write.Length > 0 && entry.Columns.Count > 0)
            written.Scatter(entry.Columns, entry.Write);
        var scattered = Stopwatch.GetTimestamp();
        Commands.ApplyValidated(World, _commands.Written, _events, _signals);
        Settle(entry.Spec.Name);
        var settled = Stopwatch.GetTimestamp();
        entry.Spent[0] += gathered - started;
        entry.Spent[1] += ran - gathered;
        entry.Spent[2] += scattered - ran;
        entry.Spent[3] += settled - scattered;
    }

    /// <summary>Применяет буфер обработчика; неверный — в ошибки.</summary>
    private void ApplyCommands(string source)
    {
        try
        {
            Commands.Apply(World, _commands.Written, _events, _signals);
        }
        catch (CommandException error)
        {
            Errors.Add(new SystemError(Tick, source, error.Message, error));
        }
    }

    /// <summary>Раздаёт события, пока они есть; команды каждого обработчика — сразу после него.</summary>
    private void Settle(string after)
    {
        for (var round = 0; round < MaxEventRounds; round++)
        {
            if (_events.Count == 0)
                return;
            var events = _events.ToArray();
            _events.Clear();
            foreach (var @event in events)
            {
                if (!_handlers.TryGetValue(@event.Name, out var list))
                    continue;
                foreach (var (label, handler) in list.ToArray())
                {
                    _commands.Clear();
                    var source = $"{label}: событие {@event.Name}";
                    try
                    {
                        handler(new EventContext(this, _commands, @event));
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        Errors.Add(new SystemError(Tick, source, error.Message, error));
                        continue;
                    }
                    ApplyCommands(source);
                }
            }
        }
        if (_events.Count > 0)
        {
            Errors.Add(new SystemError(Tick, after, "события порождают друг друга без конца"));
            _events.Clear();
        }
    }

    /// <summary>Система в порядке тика: запрос собран заранее, буферы столбцов — свои.</summary>
    private sealed class Entry(SystemSpec spec, Query? query, int[] write)
    {
        public SystemSpec Spec { get; } = spec;
        public Query? Query { get; } = query;
        public int[] Write { get; } = write;
        public QueryColumns Columns { get; } = new();
        public long[] Spent { get; } = new long[4];

        /// <summary>Имена, которыми на систему ссылаются: свои и тех, кого она заменила.</summary>
        public List<string> Names { get; } = [spec.Name, spec.Key];

        public List<string> After { get; } = [.. spec.After];
        public List<string> Before { get; } = [.. spec.Before];

        public bool Matches(string name) => Names.Contains(name);
    }

    /// <summary>Замены, затем порядок: after и before — ключи или полные имена; при равенстве — порядок объявления.</summary>
    private static Entry[] Order(GameWorld world, SystemSpec[] specs)
    {
        var entries = new List<Entry>();
        foreach (var spec in specs)
        {
            if (spec.Every < 1)
                throw new ScheduleException($"{spec.Name}: every — не меньше 1");
            var schema = world.Schema;
            foreach (var name in spec.Query.Concat(spec.Write).Concat(spec.Where.Select(where => where.Component)))
                if (schema.Find(name) is null)
                    throw new ScheduleException($"{spec.Name}: нет компонента {name}");
            if (spec.Write.FirstOrDefault(name => !spec.Query.Contains(name)) is { } missing)
                throw new ScheduleException($"{spec.Name}: write.{missing} нет в query");
            Query? query;
            try
            {
                query = spec.Query.Count > 0 ? new Query(world, spec.Query, spec.Where) : null;
            }
            catch (ArgumentException error)
            {
                throw new ScheduleException($"{spec.Name}: {error.Message}");
            }
            if (query is null && spec.Where.Count > 0)
                throw new ScheduleException($"{spec.Name}: where без query");
            var entry = new Entry(spec, query, [.. spec.Write.Select(schema.Id)]);
            if (spec.Instead is { } target)
            {
                var found = entries.FindAll(other => other.Matches(target));
                if (found.Count != 1)
                    throw new ScheduleException(found.Count == 0 ? $"{spec.Name}: instead — нет системы {target}" : $"{spec.Name}: instead — систем {target} несколько, нужно полное имя");
                var replaced = found[0];
                entry.Names.AddRange(replaced.Names);
                entry.After.AddRange(replaced.After);
                entry.Before.AddRange(replaced.Before);
                entries[entries.IndexOf(replaced)] = entry;
            }
            else
            {
                entries.Add(entry);
            }
        }
        var count = entries.Count;
        var needs = new List<int>[count];
        for (var index = 0; index < count; index++)
            needs[index] = [];
        for (var index = 0; index < count; index++)
        {
            var entry = entries[index];
            foreach (var name in entry.After)
            {
                var found = Find(entries, index, name);
                if (found.Count == 0)
                    throw new ScheduleException($"{entry.Spec.Name}: after — нет системы {name}");
                needs[index].AddRange(found);
            }
            foreach (var name in entry.Before)
            {
                var found = Find(entries, index, name);
                if (found.Count == 0)
                    throw new ScheduleException($"{entry.Spec.Name}: before — нет системы {name}");
                foreach (var other in found)
                    needs[other].Add(index);
            }
        }
        var done = new bool[count];
        var order = new List<Entry>(count);
        while (order.Count < count)
        {
            var next = -1;
            for (var index = 0; index < count && next < 0; index++)
                if (!done[index] && needs[index].TrueForAll(need => done[need]))
                    next = index;
            if (next < 0)
            {
                var stuck = Enumerable.Range(0, count).Where(index => !done[index]).Select(index => entries[index].Spec.Name);
                throw new ScheduleException($"системы ждут друг друга по кругу: {string.Join(", ", stuck)}");
            }
            done[next] = true;
            order.Add(entries[next]);
        }
        return [.. order];
    }

    private static List<int> Find(List<Entry> entries, int self, string name)
    {
        var found = new List<int>();
        for (var other = 0; other < entries.Count; other++)
            if (other != self && entries[other].Matches(name))
                found.Add(other);
        return found;
    }
}
