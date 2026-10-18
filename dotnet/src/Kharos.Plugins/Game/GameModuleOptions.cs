namespace Kharos.Plugins.Game;

/// <summary>Запись журнала мода: kh.log или вывод мода в stdout/stderr.</summary>
/// <param name="ModId">Мод.</param>
/// <param name="Level">0 — отладка, 1 — сведения, 2 — предупреждение, 3 — ошибка.</param>
/// <param name="Message">Текст.</param>
public readonly record struct ModLogEntry(string ModId, int Level, string Message);

/// <summary>
/// Инструмент движка для kh.tool: байты запроса → байты ответа. Вернуть длину ответа (больше output — ничего не
/// записывать, мод позовёт снова с местом побольше) или отрицательный код (ModAbi.Error*).
/// </summary>
public delegate int ModTool(ReadOnlySpan<byte> input, Span<byte> output);

/// <summary>Все инструменты одним обработчиком (<see cref="GameModuleInstance.Tools"/>): номер, запрос, место под ответ.</summary>
public delegate int ModToolHandler(int id, ReadOnlySpan<byte> input, Span<byte> output);

/// <summary>Пределы и настройки игровых модов — одни на хосте и на клиенте (docs/dotnet.md, §4.2).</summary>
public sealed class GameModuleOptions
{
    /// <summary>Предел линейной памяти экземпляра. Рантайм NativeAOT-LLVM занимает ~50 МБ сразу, поэтому 64–256 МБ.</summary>
    public long MemoryLimitBytes { get; set; } = 128L << 20;

    /// <summary>Сколько может идти один вызов системы, события или приказа, мс.</summary>
    public int CallTimeoutMs { get; set; } = 50;

    /// <summary>Сколько может идти загрузка экземпляра (запуск рантайма и kh_load), мс.</summary>
    public int LoadTimeoutMs { get; set; } = 2000;

    /// <summary>Шаг сторожа эпох, мс: точность прерывания.</summary>
    public int WatchdogIntervalMs { get; set; } = 5;

    /// <summary>После стольких перерасходов времени мод выключается.</summary>
    public int MaxStrikes { get; set; } = 3;

    /// <summary>Журнал модов; null — молча.</summary>
    public Action<ModLogEntry>? Log { get; set; }

    /// <summary>Инструменты по номеру (kh.tool) для экземпляров без своего <see cref="GameModuleInstance.Tools"/>.</summary>
    public IDictionary<int, ModTool> Tools { get; } = new Dictionary<int, ModTool>();

    internal void Validate()
    {
        if (MemoryLimitBytes < 1 << 20)
            throw new ArgumentOutOfRangeException(nameof(MemoryLimitBytes), "предел памяти — от 1 МБ");
        if (CallTimeoutMs <= 0 || LoadTimeoutMs <= 0 || WatchdogIntervalMs <= 0 || MaxStrikes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CallTimeoutMs), "времена и число перерасходов — положительные");
    }
}

/// <summary>
/// Случайные числа мода (kh.random): SplitMix64, как Scheduler.NextRandom движка. Состояние — у мира: движок
/// подменяет источник (<see cref="GameModuleInstance.RandomSource"/>), иначе — своё зерно экземпляра.
/// </summary>
public sealed class ModRandom(ulong seed)
{
    /// <summary>Состояние генератора.</summary>
    public ulong State { get; set; } = seed;

    /// <summary>Следующее в [0, 1).</summary>
    public double Next()
    {
        State += 0x9E3779B97F4A7C15;
        var z = State;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        z ^= z >> 31;
        return (z >> 11) / (double)(1UL << 53);
    }
}

/// <summary>Тик, в котором идёт вызов: пишется в общий блок мода перед каждым вызовом.</summary>
/// <param name="Tick">Номер тика.</param>
/// <param name="Dt">Длина тика, секунды.</param>
/// <param name="NextEntity">Номер следующей сущности мира: с него мод раздаёт номера Spawn подряд.</param>
/// <param name="Rights">Права приказа или запроса (ModAbi.Rights*): клиент по сети, администратор, редактор.</param>
public readonly record struct ModCall(ulong Tick, double Dt, uint NextEntity, uint Rights = 0);
