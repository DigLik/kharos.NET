// ABI мода (docs/dotnet.md, §4.2): общий файл Kharos.Mod.Sdk и Kharos.Plugins (подключён ссылкой, не сборкой).
// Здесь — только числа договора. Всё little-endian, адреса — i32 в линейной памяти мода (wasm32).
//
// Экспорты мода (их пишет за мододела Kharos.Mod.Sdk, build/ModExports.cs):
//   kh_abi_version() -> i32                    версия ABI, сейчас 1
//   kh_alloc(size) -> ptr                      память мода под данные хоста (0 — нет памяти); kh_free(ptr)
//   kh_shared() -> ptr                         общий блок (ниже), адрес постоянный
//   kh_load(ptr, len) -> i32                   ModLoadInfo (MemoryPack): схема, данные, зерно; в out — ModManifest
//   kh_prepare(system, n) -> ptr               кадр системы на n сущностей (ниже); 0 — ошибка
//   kh_system(system, n) -> i32                система над n сущностями кадра; в out — буфер команд
//   kh_event(handler, ptr, len) -> i32         событие: payload; в out — буфер команд
//   kh_command(order, player, ptr, len) -> i32 приказ игрока; в out — буфер команд
//   Клиентские (зарезервированы, Н3): kh_look(kind, canvas), kh_frame(...).
//
// Импорты, которые даёт хост (всё остальное — отказ при загрузке):
//   kh.log(level, ptr, len), kh.random() -> f64, kh.tool(id, inPtr, inLen, outPtr, outCap) -> i32;
//   wasi_snapshot_preview1.* — заглушки без часов, файлов и среды (см. GameImports в Kharos.Plugins).
//
// Буфер команд — байт в байт формат движка (Kharos.Engine.World.CommandBuffer): движок применяет его как есть.

namespace Kharos.Mod.Abi;

/// <summary>Числа договора между модом и хостом.</summary>
internal static class ModAbi
{
    /// <summary>Версия ABI. Растёт при несовместимых изменениях.</summary>
    public const int Version = 1;

    /// <summary>Модуль импортов хоста.</summary>
    public const string ImportModule = "kh";

    // Коды ответа экспортов: 0 — успех, отрицательные — ошибка (текст — в err общего блока).
    public const int Ok = 0;
    /// <summary>Нет системы, обработчика или приказа с таким номером.</summary>
    public const int ErrorUnknownId = -1;
    /// <summary>n больше, чем подготовил kh_prepare.</summary>
    public const int ErrorCapacity = -2;
    /// <summary>Вызов до kh_load.</summary>
    public const int ErrorNotLoaded = -3;
    /// <summary>Не разобраны входные байты (ModLoadInfo, payload).</summary>
    public const int ErrorBadInput = -4;
    /// <summary>Исключение в коде мода.</summary>
    public const int ErrorException = -5;
    /// <summary>Версия ABI хоста и мода разошлись.</summary>
    public const int ErrorAbi = -6;
    /// <summary>Не хватило памяти.</summary>
    public const int ErrorOutOfMemory = -7;

    // Общий блок (kh_shared): 64 байта, выровнен на 8.
    /// <summary>i32: адрес выхода последнего вызова (буфер команд или манифест). Пишет мод.</summary>
    public const int SharedOutPtr = 0;
    /// <summary>i32: длина выхода. Пишет мод.</summary>
    public const int SharedOutLen = 4;
    /// <summary>i32: адрес текста ошибки (UTF-8). Пишет мод.</summary>
    public const int SharedErrPtr = 8;
    /// <summary>i32: длина текста ошибки.</summary>
    public const int SharedErrLen = 12;
    /// <summary>u64: номер тика. Пишет хост перед вызовом.</summary>
    public const int SharedTick = 16;
    /// <summary>f64: длина тика в секундах. Пишет хост.</summary>
    public const int SharedDt = 24;
    /// <summary>u32: номер следующей сущности мира — с него мод раздаёт номера Spawn подряд. Пишет хост.</summary>
    public const int SharedNextEntity = 32;
    /// <summary>Размер общего блока.</summary>
    public const int SharedSize = 64;

    // Кадр системы (kh_prepare): 24 байта. Столбцы — один блок f64, как QueryColumns.Data движка: компонент за
    // компонентом в порядке query, внутри — поля по Slot, у каждого — n значений подряд (столбец k — с k*n).
    /// <summary>i32: номер системы.</summary>
    public const int FrameSystem = 0;
    /// <summary>i32: на сколько сущностей выделено.</summary>
    public const int FrameCapacity = 4;
    /// <summary>i32: столбцов f64 в блоке (ширина запроса).</summary>
    public const int FrameWidth = 8;
    /// <summary>i32: адрес блока столбцов f64 (ширина × ёмкость).</summary>
    public const int FrameData = 12;
    /// <summary>i32: адрес номеров сущностей u32 (ёмкость).</summary>
    public const int FrameEntities = 16;
    /// <summary>Размер кадра.</summary>
    public const int FrameSize = 24;

    // Записи буфера команд (как CommandOp движка).
    public const byte OpSpawn = 0x01;
    public const byte OpDestroy = 0x02;
    public const byte OpAdd = 0x03;
    public const byte OpSet = 0x04;
    public const byte OpRemove = 0x05;
    public const byte OpEmit = 0x06;
    public const byte OpSignal = 0x07;

    // Метки значений полей в Add и Set (как ValueTag движка).
    public const byte ValueNumber = 0x00;
    public const byte ValueFalse = 0x01;
    public const byte ValueTrue = 0x02;
    public const byte ValueStringId = 0x03;
    public const byte ValueStringText = 0x04;
    public const byte ValueBytes = 0x05;

    // Вид поля (как FieldKind движка).
    public const byte KindNumber = 0;
    public const byte KindFlag = 1;
    public const byte KindString = 2;
    public const byte KindBytes = 3;

    /// <summary>Полей в одной записи Add/Set не больше (байт счёта, маска u64 движка).</summary>
    public const int MaxFields = 64;

    // Инструменты движка: kh.tool(id, inPtr, inLen, outPtr, outCap) -> i32. Ответ ≥ 0 — длина результата; если она
    // больше outCap, ничего не записано: мод зовёт снова с местом не меньше ответа (инструменты только читают мир, повтор
    // даёт то же). Отрицательный — ошибка: ErrorUnknownId (нет инструмента), ErrorBadInput (вход не разобран, нет
    // компонента). Числа — little-endian; u32 счёт, f64 значения, номер компонента — u32 из схемы (-1 — «жива ли»).
    //
    //   1 Near      in: m:u32 k:u32 component:u32×k (x:f64 y:f64 r:f64)×m   out: на точку — count:u32 entity:u32×count
    //               сущности с Position в круге и со всеми компонентами, по возрастанию номеров
    //   2 Separate  in: n:u32 strength:f64 flags:u32 x:f64×n y:f64×n radius:f64×n [moving:f64×n, если flags & 1]
    //               out: x:f64×n y:f64×n — расталкивание корпусов (стоящий уступает идущему втрое охотнее)
    //   3 Terrain   in: n:u32 (x:f64 y:f64)×n   out: (terrain:u8 biome:u8 foot:u8)×n; у партии нет местности — пусто
    //   4 Get       in: entity:u32 component:u32   out: нет компонента — пусто; иначе поля по порядку: число, флаг и
    //               строка — f64 (флаг 0/1, строка — номер), байты — len:u32 и байты
    //   5 Has       in: component:i32 n:u32 entity:u32×n   out: u8×n (component -1 — жива ли сущность)
    //   6 Count     in: component:u32   out: count:u32
    //   7 Proto     in: 0:u8 и "раздел/имя" (UTF-8) → out: id:i32 (-1 — нет); 1:u8 id:u32 → out: "раздел/имя" (пусто — нет)
    //   8 Text      in: id:u32   out: строка мира (UTF-8)
    //   9 StringId  in: строка (UTF-8)   out: id:i32 (-1 — такой строки в мире нет)
    //  10 Storage   in: пусто   out: entity:u32 и байты памяти мода (сущность с Storage { mod, data })
    public const int ToolNear = 1;
    public const int ToolSeparate = 2;
    public const int ToolTerrain = 3;
    public const int ToolGet = 4;
    public const int ToolHas = 5;
    public const int ToolCount = 6;
    public const int ToolProto = 7;
    public const int ToolText = 8;
    public const int ToolStringId = 9;
    public const int ToolStorage = 10;

    /// <summary>Компонент памяти мода (объявляет хост): mod — имя мода, data — байты.</summary>
    public const string StorageComponent = "Storage";
}
