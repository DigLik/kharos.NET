namespace Kharos.Engine.World;

/// <summary>Система: чистая функция над столбцами запроса — столбцы на вход, изменённые столбцы и команды на выход.</summary>
public interface ISystem
{
    void Run(SystemContext ctx);
}

/// <summary>Система из делегата.</summary>
public sealed class DelegateSystem(Action<SystemContext> run) : ISystem
{
    public void Run(SystemContext ctx) => run(ctx);
}

/// <summary>
/// Объявление системы (docs/mods.md, «Системы»). Порядок — по <see cref="After"/> и <see cref="Before"/> (ключ системы
/// или полное имя), иначе — по порядку объявления. <see cref="Instead"/> — встать на место системы с этим именем.
/// </summary>
public sealed class SystemSpec
{
    /// <summary>Полное имя: «раздел/вид.ключ», для сообщений и порядка.</summary>
    public required string Name { get; init; }

    /// <summary>Короткое имя для <see cref="After"/>/<see cref="Before"/>; по умолчанию — часть имени после последней точки.</summary>
    public string Key
    {
        get => field ?? Name[(Name.LastIndexOf('.') + 1)..];
        init;
    }

    /// <summary>Сущности со всеми этими компонентами; их столбцы уходят системе. Пусто — система без сущностей.</summary>
    public IReadOnlyList<string> Query { get; init; } = [];

    /// <summary>И с такими значениями полей-столбцов.</summary>
    public IReadOnlyList<(string Component, string Field, FieldValue Value)> Where { get; init; } = [];

    /// <summary>Чьи столбцы после системы вернуть в мир — подмножество <see cref="Query"/>.</summary>
    public IReadOnlyList<string> Write { get; init; } = [];

    /// <summary>Раз в сколько тиков: идёт на тиках, кратных Every.</summary>
    public int Every { get; init; } = 1;

    public IReadOnlyList<string> After { get; init; } = [];
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>
    /// Заменить систему с этим именем: та не идёт, эта встаёт на её место в порядке объявления, наследует её
    /// After/Before, и ссылки на прежнее имя ведут к ней. Замена уже заменённой заменяет заменившую — поздний мод главнее.
    /// </summary>
    public string? Instead { get; init; }

    public required ISystem System { get; init; }
}

/// <summary>Ошибка системы, обработчика или приказа: её команды и столбцы отброшены, тик идёт дальше.</summary>
public sealed record SystemError(ulong Tick, string Source, string Message, Exception? Exception = null);

/// <summary>Время системы с начала, мс: сборка столбцов, сама система, возврат столбцов, команды и события.</summary>
public readonly record struct SystemTiming(string Name, double Gather, double Run, double Scatter, double Commands);

/// <summary>Порядок систем не сложился: нет системы по имени, круг, неверное объявление.</summary>
public sealed class ScheduleException(string message) : Exception(message);

/// <summary>Что видит система, обработчик события или приказа.</summary>
public abstract class Context
{
    private readonly List<uint> _near = [];

    internal Context(Scheduler scheduler, CommandBuffer commands)
    {
        Scheduler = scheduler;
        Commands = commands;
    }

    public Scheduler Scheduler { get; }

    /// <summary>Мир сейчас: на чтение. Менять — командами; столбцы идущей системы сюда ещё не вернулись.</summary>
    public GameWorld World => Scheduler.World;

    public ulong Tick => Scheduler.Tick;
    public double Dt => Scheduler.Dt;

    /// <summary>Команды — применятся после системы, по порядку.</summary>
    public CommandBuffer Commands { get; }

    /// <summary>Случайное в [0, 1), одинаковое у всех, кто считает тот же мир.</summary>
    public double Random() => Scheduler.NextRandom();

    /// <summary>Сущности с местом в круге (x, y, r) и со всеми components — по возрастанию номеров. Список общий: до следующего вызова.</summary>
    public IReadOnlyList<uint> Near(double x, double y, double radius, params ReadOnlySpan<int> components)
    {
        _near.Clear();
        World.Near(x, y, radius, components, _near);
        return _near;
    }
}

/// <summary>Контекст системы: столбцы запроса и команды.</summary>
public sealed class SystemContext : Context
{
    internal SystemContext(Scheduler scheduler, CommandBuffer commands, SystemSpec spec, QueryColumns columns) : base(scheduler, commands)
    {
        Spec = spec;
        Columns = columns;
    }

    public SystemSpec Spec { get; }
    public QueryColumns Columns { get; }
    public int Count => Columns.Count;
    public ReadOnlySpan<uint> Entities => Columns.Entities;

    /// <summary>Столбец поля компонента запроса: читать и писать (вернутся компоненты из Write).</summary>
    public Span<double> Column(int component, string field) => Columns.Column(component, field);
}

/// <summary>Контекст обработчика события.</summary>
public sealed class EventContext : Context
{
    internal EventContext(Scheduler scheduler, CommandBuffer commands, WorldEvent @event) : base(scheduler, commands) => Event = @event;

    public WorldEvent Event { get; }
    public string Name => Event.Name;
    public ReadOnlySpan<byte> Payload => Event.Payload;
}

/// <summary>Контекст приказа игрока. Проверять аргументы и права — дело обработчика: приходит что угодно.</summary>
public sealed class OrderContext : Context
{
    internal OrderContext(Scheduler scheduler, CommandBuffer commands, uint player, string name, byte[] payload) : base(scheduler, commands)
    {
        Player = player;
        Name = name;
        PayloadBytes = payload;
    }

    public uint Player { get; }
    public string Name { get; }
    private byte[] PayloadBytes { get; }
    public ReadOnlySpan<byte> Payload => PayloadBytes;
}
