using System.Runtime.InteropServices;
using MemoryPack;

namespace Kharos.Plugins.Models;

/// <summary>
/// Универсальный запрос вычислений, содержащий строковый идентификатор, скалярные параметры и произвольный массив бинарных данных.
/// </summary>
[MemoryPackable]
public sealed partial class ComputeRequest
{
    /// <summary>
    /// Уникальный идентификатор запроса для сквозной трассировки (Correlation ID).
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// Количество итераций алгоритма, которое плагин должен выполнить.
    /// </summary>
    public int Iterations { get; set; }

    /// <summary>
    /// Численный коэффициент-множитель для математических преобразований.
    /// </summary>
    public double Multiplier { get; set; } = 1.0;

    /// <summary>
    /// Входной массив данных для обработки плагином.
    /// </summary>
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// Универсальный ответ вычислений, возвращаемый модулем плагина.
/// </summary>
[MemoryPackable]
public sealed partial class ComputeResponse
{
    /// <summary>
    /// Идентификатор соответствующего запроса.
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// Признак успешности завершения обработки.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Рассчитанное скалярное числовое значение.
    /// </summary>
    public double CalculatedValue { get; set; }

    /// <summary>
    /// Массив обработанных плагином данных.
    /// </summary>
    public byte[] ProcessedData { get; set; } = [];

    /// <summary>
    /// Текстовое диагностическое сообщение или статус выполнения.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Пакетный запрос телеметрических данных сенсоров и устройств IoT.
/// </summary>
[MemoryPackable]
public sealed partial class TelemetryBatchRequest
{
    /// <summary>
    /// Идентификатор сенсора или источника телеметрии.
    /// </summary>
    public long DeviceId { get; set; }

    /// <summary>
    /// Временная метка сбора пакета в формате UTC тиков.
    /// </summary>
    public long TimestampUtc { get; set; }

    /// <summary>
    /// Массив последовательных показаний датчика.
    /// </summary>
    public float[] Readings { get; set; } = [];
}

/// <summary>
/// Результат аналитической обработки и фильтрации пакета телеметрических данных.
/// </summary>
[MemoryPackable]
public sealed partial class TelemetryBatchResponse
{
    /// <summary>
    /// Идентификатор обработанного устройства.
    /// </summary>
    public long DeviceId { get; set; }

    /// <summary>
    /// Отфильтрованное среднее арифметическое показаний сенсора.
    /// </summary>
    public float FilteredAverage { get; set; }

    /// <summary>
    /// Количество обнаруженных аномалий и выбросов в пакете.
    /// </summary>
    public int AnomaliesDetected { get; set; }

    /// <summary>
    /// Флаг срабатывания аварийного порога предупреждения.
    /// </summary>
    public bool AlertTriggered { get; set; }
}

/// <summary>
/// Компактная blittable-структура данных для предельно высокой скорости обмена (100 000+ вызовов/сек).
/// Фиксированный размер: 40 байт. Порядок полей строго соответствует смещениям в WebAssembly WAT модулях:
/// <list type="bullet">
///   <item><description>offset 0:  <see cref="Iterations"/> (i32, 4 байта)</description></item>
///   <item><description>offset 8:  <see cref="Multiplier"/> (f64, 8 байт)</description></item>
///   <item><description>offset 16: <see cref="Result"/> (f64, 8 байт)</description></item>
///   <item><description>offset 24: <see cref="Timestamp"/> (i64, 8 байт)</description></item>
///   <item><description>offset 32: <see cref="Success"/> (bool, 1 байт)</description></item>
/// </list>
/// </summary>
[MemoryPackable]
[StructLayout(LayoutKind.Sequential)]
public partial struct PluginPayload
{
    /// <summary>
    /// Число итераций вычислений (смещение 0).
    /// </summary>
    public int Iterations;

    /// <summary>
    /// Множитель алгоритма (смещение 8).
    /// </summary>
    public double Multiplier;

    /// <summary>
    /// Результирующее значение вычислений (смещение 16).
    /// </summary>
    public double Result;

    /// <summary>
    /// Временная метка хоста в тиках UTC (смещение 24).
    /// </summary>
    public long Timestamp;

    /// <summary>
    /// Флаг успешного выполнения вычислений (смещение 32).
    /// </summary>
    public bool Success;
}
