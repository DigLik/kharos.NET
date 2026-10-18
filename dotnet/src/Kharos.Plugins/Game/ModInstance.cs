using Kharos.Mod.Abi;

namespace Kharos.Plugins.Game;

/// <summary>Как исполняется код модов хоста (docs/dotnet-n5.md, Н5-5).</summary>
public enum ModMode
{
    /// <summary>host.wasm в Wasmtime с пределами (игра, сервер).</summary>
    Wasm,

    /// <summary>
    /// Сборка .NET мода в процессе хоста, своя collectible AssemblyLoadContext (тесты: быстро, с отладчиком, без сборки
    /// wasm). Пределов времени и памяти нет. Мир — тот же до бита, если мод считает тригонометрию только DetMath.
    /// </summary>
    Clr,
}

/// <summary>Выбор режима модов для тестов и обвязки сценариев.</summary>
public static class ModModes
{
    /// <summary>Переменная окружения: <c>wasm</c> или <c>clr</c> (без учёта регистра).</summary>
    public const string Variable = "KHAROS_MODS";

    /// <summary>Режим из <see cref="Variable"/>; не задан — fallback (сценарии — CLR, docs/dotnet-n5.md, Н5.6).</summary>
    public static ModMode FromEnvironment(ModMode fallback = ModMode.Clr) =>
        Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant() switch
        {
            "wasm" => ModMode.Wasm,
            "clr" => ModMode.Clr,
            _ => fallback,
        };
}

/// <summary>
/// Кадр системы на стороне хоста: главная часть (0) и побочные таблицы (t + 1) — номера сущностей и столбцы f64 одним
/// блоком (ширина × n, как QueryColumns.Data движка). Столбцы из Write и пишущих таблиц экземпляр возвращает сюда же.
/// </summary>
public interface IModFrame
{
    /// <summary>Сущностей в части.</summary>
    int Count(int part);

    /// <summary>Номера сущностей части.</summary>
    ReadOnlySpan<uint> Entities(int part);

    /// <summary>Столбцы части.</summary>
    Span<double> Data(int part);
}

/// <summary>Кадр системы над готовыми массивами (тесты, замеры): parts[i] — (номера, столбцы).</summary>
public sealed class ArrayFrame(params (uint[] Entities, double[] Data)[] parts) : IModFrame
{
    /// <inheritdoc/>
    public int Count(int part) => parts[part].Entities.Length;

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Entities(int part) => parts[part].Entities;

    /// <inheritdoc/>
    public Span<double> Data(int part) => parts[part].Data;
}

/// <summary>
/// Мод хоста, из которого делаются экземпляры: модуль wasm (<see cref="GameModule"/>) или сборка .NET
/// (<see cref="ClrModule"/>). Сбои считаются на мод: после MaxStrikes он выключен.
/// </summary>
public interface IModModule : IDisposable
{
    /// <summary>Имя мода.</summary>
    string ModId { get; }

    /// <summary>Что отдаётся моду в kh_load.</summary>
    ModLoadInfo LoadInfo { get; }

    /// <summary>Сбоев.</summary>
    int Strikes { get; }

    /// <summary>Выключен после MaxStrikes сбоев.</summary>
    bool Disabled { get; }

    /// <summary>Засчитать сбой (ловушка, неверный буфер команд).</summary>
    void Strike();

    /// <summary>Новый экземпляр: kh_load сделан.</summary>
    IModInstance CreateInstance();
}

/// <summary>
/// Экземпляр мода хоста — одно и то же в wasm и в CLR: системы, события, приказы и запросы (kh_*). Ответы — буфер
/// команд движка или ответ запроса, верны до следующего вызова. Вызовы — из одного потока.
/// </summary>
public interface IModInstance : IDisposable
{
    /// <summary>Имя мода.</summary>
    string ModId { get; }

    /// <summary>Что мод объявил в kh_load.</summary>
    ModManifest Manifest { get; }

    /// <summary>Раскладки кадров систем; номер — id системы.</summary>
    IReadOnlyList<ModSystemLayout> Systems { get; }

    /// <summary>Экземпляр негоден (ловушка): выбросить и создать новый.</summary>
    bool Faulted { get; }

    /// <summary>Запуск: рантайм мода (или загрузка сборок) и kh_load, мс.</summary>
    double StartupMs { get; }

    /// <summary>Источник kh.random от мира; null — своё зерно экземпляра.</summary>
    Func<double>? RandomSource { get; set; }

    /// <summary>Инструменты движка (kh.tool) для этого экземпляра.</summary>
    ModToolHandler? Tools { get; set; }

    /// <summary>
    /// Система: части кадра — в мод, kh_system, столбцы из Write и пишущих таблиц — обратно в frame. Возвращает буфер
    /// команд.
    /// </summary>
    ReadOnlySpan<byte> RunSystem(int system, IModFrame frame, in ModCall call);

    /// <summary>kh_event: событие обработчику handler. Возвращает буфер команд.</summary>
    ReadOnlySpan<byte> RaiseEvent(int handler, ReadOnlySpan<byte> payload, in ModCall call);

    /// <summary>kh_command: приказ игрока player (права — call.Rights). Возвращает буфер команд.</summary>
    ReadOnlySpan<byte> RunOrder(int order, uint player, ReadOnlySpan<byte> payload, in ModCall call);

    /// <summary>kh_query: запрос (только чтение, права — call.Rights). Возвращает ответ.</summary>
    ReadOnlySpan<byte> RunQuery(int query, ReadOnlySpan<byte> payload, in ModCall call);
}
