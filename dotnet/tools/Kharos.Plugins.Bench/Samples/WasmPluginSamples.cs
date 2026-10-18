namespace Kharos.Plugins.Bench.Samples;

/// <summary>
/// Набор скомпилированных текстовых WebAssembly модулей (WAT) для проведения тестов безопасности, валидации и бенчмарков.
/// </summary>
public static class WasmPluginSamples
{
    /// <summary>
    /// Высокопроизводительный вычислительный модуль для побайтовой трансформации буфера.
    /// Выполняет побайтовую операцию XOR с константой 0x5A (90).
    /// Сигнатура функции: process_buffer(inPtr: i32, inLen: i32, outPtr: i32, outMaxLen: i32) -> i32.
    /// </summary>
    public const string TransformModuleWat = @"
(module
  (memory (export ""memory"") 2)
  (func (export ""process_buffer"") (param $inPtr i32) (param $inLen i32) (param $outPtr i32) (param $outMaxLen i32) (result i32)
    (local $i i32)
    (local $b i32)

    (if (i32.gt_u (local.get $inLen) (local.get $outMaxLen))
      (then (return (i32.const -1)))
    )

    (local.set $i (i32.const 0))
    (block $break
      (loop $loop
        (if (i32.ge_u (local.get $i) (local.get $inLen))
          (then (br $break))
        )

        (local.set $b
          (i32.load8_u (i32.add (local.get $inPtr) (local.get $i)))
        )

        (i32.store8 
          (i32.add (local.get $outPtr) (local.get $i))
          (i32.xor (local.get $b) (i32.const 90))
        )

        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $loop)
      )
    )
    (local.get $inLen)
  )
)
";

    /// <summary>
    /// Модуль, демонстрирующий безопасный вызов хост-функции через компоновщик (Linker).
    /// Импортирует функцию env.host_get_timestamp и записывает 64-битный результат (8 байт) в выходной буфер.
    /// </summary>
    public const string HostCallingModuleWat = @"
(module
  (import ""env"" ""host_get_timestamp"" (func $host_now (result i64)))
  (memory (export ""memory"") 2)
  (func (export ""process_buffer"") (param $inPtr i32) (param $inLen i32) (param $outPtr i32) (param $outMaxLen i32) (result i32)
    (local $timestamp i64)

    ;; Вызываем функцию хоста
    (local.set $timestamp (call $host_now))

    ;; Записываем полученный i64 в начало выходного буфера
    (i64.store (local.get $outPtr) (local.get $timestamp))

    (i32.const 8)
  )
)
";

    /// <summary>
    /// Вредоносный модуль с бесконечным циклом для проверки сторожа Epoch Watchdog.
    /// При исполнении блокирует поток управления бесконечной итерацией, провоцируя прерывание по тайм-ауту эпохи.
    /// </summary>
    public const string InfiniteLoopModuleWat = @"
(module
  (memory (export ""memory"") 1)
  (func (export ""process_buffer"") (param $inPtr i32) (param $inLen i32) (param $outPtr i32) (param $outMaxLen i32) (result i32)
    (loop $inf
      (br $inf)
    )
    (i32.const 0)
  )
)
";

    /// <summary>
    /// Вычислительный модуль MemoryPack: считывает структуру PluginPayload (40 байт blittable),
    /// выполняет многоитерационный расчет с плавающей точкой, опрашивает метку времени хоста через host_get_timestamp
    /// и сериализует обновленную структуру PluginPayload в выходной буфер.
    /// </summary>
    public const string ComputeModuleWat = @"
(module
  (import ""env"" ""host_get_timestamp"" (func $host_now (result i64)))
  (memory (export ""memory"") 2)
  (func (export ""process_buffer"") (param $inPtr i32) (param $inLen i32) (param $outPtr i32) (param $outMaxLen i32) (result i32)
    (local $iterations i32)
    (local $multiplier f64)
    (local $res f64)
    (local $k i32)
    (local $now i64)

    ;; Читаем число итераций и множитель из структуры MemoryPack (blittable 40 bytes)
    (local.set $iterations (i32.load (local.get $inPtr)))
    (local.set $multiplier (f64.load (i32.add (local.get $inPtr) (i32.const 8))))

    ;; Выполняем цикл вычислений
    (local.set $res (local.get $multiplier))
    (local.set $k (i32.const 0))
    (block $break
      (loop $loop
        (if (i32.ge_u (local.get $k) (local.get $iterations))
          (then (br $break))
        )
        (local.set $res (f64.add (f64.mul (local.get $res) (f64.const 1.0001)) (f64.const 0.05)))
        (local.set $k (i32.add (local.get $k) (i32.const 1)))
        (br $loop)
      )
    )

    ;; Получаем timestamp хоста
    (local.set $now (call $host_now))

    ;; Записываем MemoryPack PluginPayload (40 bytes struct layout)
    (i32.store (local.get $outPtr) (local.get $iterations))
    (f64.store (i32.add (local.get $outPtr) (i32.const 8)) (local.get $multiplier))
    (f64.store (i32.add (local.get $outPtr) (i32.const 16)) (local.get $res))
    (i64.store (i32.add (local.get $outPtr) (i32.const 24)) (local.get $now))
    (i32.store8 (i32.add (local.get $outPtr) (i32.const 32)) (i32.const 1)) ;; Success = true

    (i32.const 40)
  )
)
";

    /// <summary>
    /// Модуль, вызывающий безопасную хост-функцию логирования (env.host_log)
    /// для отправки структурированных сообщений в Microsoft.Extensions.Logging.
    /// </summary>
    public const string LoggingModuleWat = @"
(module
  (import ""env"" ""host_log"" (func $host_log (param i32 i32)))
  (memory (export ""memory"") 1)
  (data (i32.const 1024) ""Execution completed successfully."")
  (func (export ""process_buffer"") (param $inPtr i32) (param $inLen i32) (param $outPtr i32) (param $outMaxLen i32) (result i32)
    ;; Вызов host_log(offset: 1024, length: 33)
    (call $host_log (i32.const 1024) (i32.const 33))
    (i32.const 0)
  )
)
";
}
