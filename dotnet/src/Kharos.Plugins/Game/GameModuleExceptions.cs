using Wasmtime;

namespace Kharos.Plugins.Game;

/// <summary>Ошибка игрового мода.</summary>
public class ModException(string modId, string message, Exception? inner = null) : Exception($"мод {modId}: {message}", inner)
{
    /// <summary>Мод.</summary>
    public string ModId { get; } = modId;
}

/// <summary>Модуль просит импорты, которых нет в белом списке: не загружается.</summary>
public sealed class ModImportException(string modId, IReadOnlyList<string> forbidden)
    : ModException(modId, $"запрещённые импорты: {string.Join(", ", forbidden)}")
{
    /// <summary>Запрещённые импорты «модуль.имя».</summary>
    public IReadOnlyList<string> Forbidden { get; } = forbidden;
}

/// <summary>Модуль не держит ABI: нет экспортов, не та версия, неверный манифест или кадр.</summary>
public sealed class ModAbiException(string modId, string message) : ModException(modId, message);

/// <summary>Экспорт мода вернул код ошибки (ModAbi.Error*): исключение в коде мода, неверный номер, нет памяти.</summary>
public sealed class ModCallException(string modId, int code, string message) : ModException(modId, $"код {code}: {message}")
{
    /// <summary>Код ответа экспорта (отрицательный).</summary>
    public int Code { get; } = code;
}

/// <summary>
/// Ловушка wasm: прерывание сторожем эпох (перерасход времени), выход за память, unreachable. Экземпляр после неё
/// негоден — его надо выбросить (мод без состояния: новый экземпляр ничего не теряет).
/// </summary>
public sealed class ModTrapException(string modId, string message, bool timeout, Exception inner) : ModException(modId, message, inner)
{
    /// <summary>Прервано по времени.</summary>
    public bool Timeout { get; } = timeout;

    /// <summary>Код ловушки Wasmtime, если есть.</summary>
    public TrapCode? Code { get; } = (inner as TrapException)?.Type;
}

/// <summary>Мод выключен после <see cref="GameModuleOptions.MaxStrikes"/> перерасходов времени.</summary>
public sealed class ModDisabledException(string modId, int strikes) : ModException(modId, $"выключен после {strikes} перерасходов времени");
