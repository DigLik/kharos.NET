// ABI мода (docs/dotnet.md, §4.2; docs/dotnet-n5.md, Н5.3): общий файл Kharos.Mod.Sdk, Kharos.Plugins и движка
// (подключён ссылкой, не сборкой). Здесь — только числа договора. Всё little-endian, адреса — i32 в линейной памяти
// мода (wasm32).
//
// Экспорты мода (их пишет за мододела Kharos.Mod.Sdk, build/ModExports.cs):
//   kh_abi_version() -> i32                    версия ABI, сейчас 1
//   kh_alloc(size) -> ptr                      память мода под данные хоста (0 — нет памяти); kh_free(ptr)
//   kh_shared() -> ptr                         общий блок (ниже), адрес постоянный
//   kh_load(ptr, len) -> i32                   ModLoadInfo (MemoryPack): схема, данные, зерно; в out — ModManifest
//   kh_prepare(system, n) -> ptr               кадр системы на n сущностей (ниже); 0 — ошибка
//   kh_prepare_table(system, table, n) -> ptr  побочная таблица системы на n сущностей; адрес того же кадра; 0 — ошибка
//   kh_system(system, n) -> i32                система над n сущностями кадра (счёт таблиц — в кадре); в out — буфер команд
//   kh_event(handler, ptr, len) -> i32         событие: payload; в out — буфер команд
//   kh_command(order, player, ptr, len) -> i32 приказ игрока (права — SharedRights); в out — буфер команд
//   kh_query(id, ptr, len) -> i32              запрос (Mod.Query; права — SharedRights): только чтение мира, команд
//                                              нет; в out — ответ
//   Клиентские (client.wasm, вместо систем, событий, приказов и запросов): kh_look(look), kh_bank(entry, variant,
//   rate), kh_frame(ptr, len) — форматы в Abi/ClientAbi.cs.
//
// Импорты, которые даёт хост (всё остальное — отказ при загрузке):
//   kh.log(level, ptr, len), kh.random() -> f64, kh.tool(id, inPtr, inLen, outPtr, outCap) -> i32 (у клиента — свой
//   набор инструментов над репликой, ClientAbi.Tool*);
//   wasi_snapshot_preview1.* — заглушки без часов, файлов и среды (см. GameImports в Kharos.Plugins).
//   В режиме CLR (тесты, HostOptions.ModMode) те же три импорта — делегаты хоста (Kharos.Mod.Sdk.Runtime.ClrBridge).
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
    /// <summary>Нет системы, обработчика, приказа, запроса или инструмента с таким номером.</summary>
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
    /// <summary>u32: права приказа или запроса (Rights*). Пишет хост перед вызовом.</summary>
    public const int SharedRights = 36;
    /// <summary>Размер общего блока.</summary>
    public const int SharedSize = 64;

    // Права приказа и запроса (OrderContext.Rights): биты.
    /// <summary>Пришло от клиента по сети: цель по номеру сущности клиент не называет (fromClient в TS).</summary>
    public const uint RightsRemote = 1;
    /// <summary>Отдал администратор.</summary>
    public const uint RightsAdmin = 2;
    /// <summary>Отдал редактор карты (приказы edit.*).</summary>
    public const uint RightsEditor = 4;

    // Кадр системы (kh_prepare): FrameSize байт, следом — по TableSize на побочную таблицу. Столбцы — один блок f64,
    // как QueryColumns.Data движка: компонент за компонентом в порядке query, внутри — поля по Slot, у каждого — n
    // значений подряд (столбец k — с k*n). Побочная таблица устроена так же: свои сущности, свой блок, свой счёт.
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
    /// <summary>i32: сколько у системы побочных таблиц.</summary>
    public const int FrameTables = 20;
    /// <summary>Размер кадра без таблиц.</summary>
    public const int FrameSize = 24;

    // Побочная таблица t — с FrameSize + t·TableSize.
    /// <summary>i32: на сколько сущностей выделено.</summary>
    public const int TableCapacity = 0;
    /// <summary>i32: столбцов f64.</summary>
    public const int TableWidth = 4;
    /// <summary>i32: адрес блока столбцов f64.</summary>
    public const int TableData = 8;
    /// <summary>i32: адрес номеров сущностей u32.</summary>
    public const int TableEntities = 12;
    /// <summary>i32: сущностей в этом вызове. Пишет хост перед kh_system.</summary>
    public const int TableCount = 16;
    /// <summary>Размер записи таблицы.</summary>
    public const int TableSize = 24;

    // Записи буфера команд (как CommandOp движка; форматы — CommandBuffer движка и CommandWriter SDK).
    public const byte OpSpawn = 0x01;
    public const byte OpDestroy = 0x02;
    public const byte OpAdd = 0x03;
    public const byte OpSet = 0x04;
    public const byte OpRemove = 0x05;
    public const byte OpEmit = 0x06;
    public const byte OpSignal = 0x07;
    /// <summary>layer:varint owner:varint channel:u8 x:sint y:sint w:varint h:varint value:f64 — канал сетки в прямоугольнике.</summary>
    public const byte OpLayer = 0x08;
    /// <summary>layer:varint owner:varint mode:u8 x:f64 y:f64, у ItemPush — count:u8 field:f64×count.</summary>
    public const byte OpItem = 0x09;
    /// <summary>kind:u8 x:sint y:sint; LandTile — terrain:u8 biome:u8 (номер 0..3, не упакованный байт) tier:u8 flags:u8 dunes:u8; LandPeak — biome:u8 (номер) px:f64 py:f64 radius:f64.</summary>
    public const byte OpLand = 0x0A;
    /// <summary>name:str payload:bytes count:varint player:varint×count — сигнал этим игрокам.</summary>
    public const byte OpSignalTo = 0x0B;
    /// <summary>name:str payload:bytes x:f64 y:f64 — сигнал тем, кто видит точку.</summary>
    public const byte OpSignalAt = 0x0C;

    /// <summary>Item: положить предмет.</summary>
    public const byte ItemPush = 0;
    /// <summary>Item: убрать предметы, лежащие ровно в точке (x, y).</summary>
    public const byte ItemDelete = 1;
    /// <summary>Land: тайл.</summary>
    public const byte LandTile = 0;
    /// <summary>Land: вершина горы.</summary>
    public const byte LandPeak = 1;
    /// <summary>Land, флаг тайла: обрыв.</summary>
    public const byte LandCliff = 1;

    /// <summary>Тайлов в одной записи Layer не больше.</summary>
    public const int MaxLayerRect = 1 << 20;

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
    // больше outCap, ничего не записано: мод зовёт снова с местом не меньше ответа (тот же вход сразу следом — движок
    // отдаёт готовый ответ, не считая заново). Отрицательный — ошибка: ErrorUnknownId (нет инструмента или он ещё не
    // сделан), ErrorBadInput (вход не разобран, нет компонента или слоя). Мир инструменты не меняют; Plan и Stand
    // тратят квоту поиска тика. Числа — little-endian: u32 счёт, f64 значения, i32 тайлы; компонент — номер из схемы
    // (у Has -1 — «жива ли»), слой — номер из ModLoadInfo.Layers, владелец слоя — 0 (мир) или номер игрока.
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
    //  11 Columns   in: n:u32 entity:u32×n k:u32 (component:u32 field:u32)×k   out: f64×(k·n) — по паре n значений
    //               подряд (флаг 0/1, строка — номер; нет компонента — 0), затем has:u8×(k·n) — есть ли компонент
    //  12 BytesMany in: component:u32 field:u32 n:u32 entity:u32×n   out: на сущность len:i32 и байты; -1 — нет компонента
    //  13 Revision  in: пусто   out: world:u64 positions:u64 land:u64 layers:u64 tick:u64 — ревизии состава мира, мест,
    //               земли и слоёв и тик: ключ кэша, производного от мира
    //  14 Children  in: n:u32 entity:u32×n   out: на сущность count:u32 child:u32×count — с Mount.parent = entity, по
    //               возрастанию номеров
    //  15 Sees      in: player:u32 n:u32 (x:f64 y:f64)×n   out: u8×n — 0 не разведан, 1 разведан, 2 виден (без тумана — 2)
    //  16 Tiles     in: x:i32 y:i32 w:u32 h:u32   out: u8×(w·h) построчно — биты Tile*: OnMap — в карте, Passable — не
    //               гора, CliffFoot — подножие обрыва, Buildable — скала не у подножия, Building — здание (Footprint.solid),
    //               OpenGate — оно открыто (Footprint.open), Unit — центр наземного юнита (Body) в тайле
    //  17 Occupant  in: n:u32 (x:i32 y:i32)×n   out: entity:u32×n — здание (Footprint.solid) на тайле; 0 — свободно
    //  18 Layer     in: layer:u32 owner:u32 channel:u32 x:i32 y:i32 w:u32 h:u32   out: f64×(w·h) построчно
    //  19 Items     in: layer:u32 owner:u32 x:f64 y:f64 r:f64   out: count:u32 (x:f64 y:f64 field:f64×fields)×count —
    //               предметы с местом в круге: по ячейкам (y, затем x), в ячейке — по порядку записи
    //  20 Peak      in: n:u32 (x:i32 y:i32)×n   out: (has:u8 x:f64 y:f64 radius:f64)×n — вершина горы тайла
    //  21 Walkable  in: flags:u32 n:u32 (x:i32 y:i32)×n   out: u8×n — может ли стоять (isWalkable в TS; PlanFoot —
    //               пехота, PlanAir — любой тайл карты)
    //  22 Plan      in: entity:u32 goalX:i32 goalY:i32 near:f64 limit:u32 flags:u32 pool:u32 budget:u32 n:u32
    //               ignore:u32×n   out: status:u8 (Plan*) roads:u8 goalX:i32 goalY:i32 count:u32 (x:f64 y:f64)×count — путь по центрам
    //               тайлов, сглаженный, к цели или к тайлу рядом (orderMove в TS); ignore — стоящие, что не помеха (сам
    //               юнит — всегда); PlanJam — затор (tries ≥ 3); limit 0 — по умолчанию; pool ≠ 0 — норма: все поиски с
    //               этим pool за тик вместе осматривают не больше budget тайлов (withSearchQuota). PlanFound — положить в
    //               Path: points, goalX/Y, wait 0, roads (у наземных), direct и stuck false, best −1, slot false; PlanNone —
    //               снять Path; PlanCut — прежний путь оставить (норма вышла, затор без пути, Body.rooted)
    //  23 GroupMove in: x:i32 y:i32 n:u32 entity:u32×n   out: (goalX:i32 goalY:i32 mode:u8)×n — свой тайл каждому
    //               (orderGroupMove), по порядку списка: mode 0 — летающий, Path из одной точки (центр тайла); 1 — ехать
    //               напрямую (Path.direct), путь — только если упрётся; 2 — не трогать (врос, нет места, нет Position)
    //  24 FreeTiles in: x:i32 y:i32 count:u32 from:u32 flags:u32 radius:f64 n:u32 ignore:u32×n
    //               out: count:u32 (x:i32 y:i32)×count — места через шаг группы (freeTilesNear); стоящие юниты, кроме
    //               ignore, заняты; radius — корпус, под который ищем (< 0 — стоящие не в счёт)
    //  25 Reach     in: x:i32 y:i32 limit:u32 flags:u32   out: count:u32 (x:i32 y:i32)×count — заливка проходимых
    //               тайлов (vehicleReach) по порядку, сам тайл — первым; заливка останавливается, набрав limit
    //  26 OpenSpawn in: x:i32 y:i32 radius:u32   out: x:i32 y:i32 — ближайшее незапертое обрывами место (openSpawn)
    //  27 Stand     in: entity:u32 x:f64 y:f64 w:f64 h:f64 r:f64 reach:f64 pool:u32 budget:u32   out: как у Plan —
    //               подъезд (approach в inventory.ts): место, откуда луч длиной reach достаёт до цели — прямоугольника w×h
    //               от (x, y) или круга радиуса r с центром (x, y); не в кармане меньше 48 тайлов, не на площадке
    //               (Footprint без solid). status: 0 — путь (в Path), 1 — норма pool вышла до поиска (не промах), 2 — места
    //               нет (снять Path, промах), 3 — уже на месте (Path не трогать)
    //  28 Clear     in: x:i32 y:i32 w:u32 h:u32 ignore:u32   out: count:u32 entity:u32×count — наземные юниты (Body) с
    //               центром в прямоугольнике (unitsIn), по возрастанию номеров
    //  29 Ray       in: n:u32 (fromX:f64 fromY:f64 toX:f64 toY:f64)×n   out: wall:u32×n — 0 линия свободна, иначе
    //               здание-стена на линии; RayTerrain — её закрывает местность
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
    public const int ToolColumns = 11;
    public const int ToolBytesMany = 12;
    public const int ToolRevision = 13;
    public const int ToolChildren = 14;
    public const int ToolSees = 15;
    public const int ToolTiles = 16;
    public const int ToolOccupant = 17;
    public const int ToolLayer = 18;
    public const int ToolItems = 19;
    public const int ToolPeak = 20;
    public const int ToolWalkable = 21;
    public const int ToolPlan = 22;
    public const int ToolGroupMove = 23;
    public const int ToolFreeTiles = 24;
    public const int ToolReach = 25;
    public const int ToolOpenSpawn = 26;
    public const int ToolStand = 27;
    public const int ToolClear = 28;
    public const int ToolRay = 29;

    // Биты тайла (Tiles).
    public const byte TileOnMap = 1;
    public const byte TilePassable = 2;
    public const byte TileCliffFoot = 4;
    public const byte TileBuildable = 8;
    public const byte TileBuilding = 16;
    public const byte TileOpenGate = 32;
    public const byte TileUnit = 64;

    // Флаги поиска (Walkable, Plan, FreeTiles, Reach, Stand).
    /// <summary>Пехота: можно и к подножию обрыва.</summary>
    public const uint PlanFoot = 1;
    /// <summary>Затор: путь в обход застрявших (JAM_TRIES в TS).</summary>
    public const uint PlanJam = 2;
    /// <summary>Летающие: годится любой тайл карты.</summary>
    public const uint PlanAir = 4;

    // Ответ Plan.
    public const byte PlanFound = 0;
    /// <summary>Прежний путь оставить: поиск оборвала норма тика, затор без пути или юнит врос; попробовать позже.</summary>
    public const byte PlanCut = 1;
    public const byte PlanNone = 2;

    /// <summary>Ray: линию закрывает местность.</summary>
    public const uint RayTerrain = 0xFFFFFFFF;

    /// <summary>Компонент памяти мода (объявляет хост): mod — имя мода, data — байты.</summary>
    public const string StorageComponent = "Storage";

    /// <summary>Компонент носителя (схема движка): уничтожение parent уносит сущность.</summary>
    public const string MountComponent = "Mount";

    /// <summary>Поле носителя в <see cref="MountComponent"/>.</summary>
    public const string MountParent = "parent";
}
