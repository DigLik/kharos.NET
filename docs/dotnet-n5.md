# Н5. Механики по разделам

План переноса всех механик `packages/sim-ts` (39 систем, около 15,8 тыс. строк, 54 компонента, 48 команд) в движок и
мод `base` на .NET. Приёмка — сценарные тесты TS (`packages/tests`) и числа `tools/harness` в допуске. Общий план —
`docs/dotnet.md`, §6, Н5.

## Н5.0. Главные решения

**[решено, 2026-10-18]**

| # | Вопрос | Решение |
|---|---|---|
| Н5-1 | Модель компонентов | Модель TS: имена, поля и смысл из `components.ts`. Правки под столбцы: тип вида — в `Proto.id`, поля `prev*` убираются (сглаживает клиент), вложенное — в столбцы или байты. Компоненты пилота (`Drive`, `Unit.radius`, `Building.width`, сущности `Deposit`) уходят. |
| Н5-2 | Движок или мод | Движок — то, что каждый тик идёт по всему миру или стоит на геометрии: пути, ход, носители, занятость, следы, обзор, память, квоты поиска. Остальные 32 системы из 39 — мод `base`. |
| Н5-3 | Доступ к чужим сущностям | Кадр системы как рабочий набор, побочные таблицы, пакетное чтение, кэш, производный от мира (с ключом-ревизией), `Stage` для чтения своих записей, инструменты движка (Н5.2). |
| Н5-4 | Свои записи в той же системе | В TS изменения видны сразу, у нас команды применяются после системы. SDK даёт `Stage` — сцену поверх кадра и инструментов: свои записи и новые сущности читаются в том же вызове. |
| Н5-5 | Тесты | Хост грузит `base` в процесс обычной сборкой .NET (режим CLR, своя collectible AssemblyLoadContext): быстро, с отладчиком, без сборки wasm. Те же сценарии гоняются и на `host.wasm` — локально на Windows и в WSL (`KHAROS_MODS=wasm`), не в CI. Тригонометрия мода — только `DetMath`, поэтому режимы совпадают до бита. В игре мод — только wasm (Р4). |
| Н5-6 | Общие файлы | Схема, ABI, порядок тика, регистрация разделов и заготовки инструментов делаются в волне 0 одним владельцем; дальше каждый агент правит только свою папку. |

## Н5.1. Компоненты

Здание стоит по левому верхнему тайлу, как в TS. «Вид» — номер вида (`Proto.id`) из данных: груз, оружие, тема,
ходовая. «Байты» — поле вида Bytes, формат — MemoryPack или плоский LE.

**Компоненты движка.** Есть `Position`, `Facing`, `Owner`, `Proto`, `Sight`; добавляются:

| Компонент | Поля | Зачем движку |
|---|---|---|
| `Footprint` | `w, h, solid`, `open` | Занятость тайлов (`solid` у готового здания), открытые ворота, центр для обзора, расстояние до края в поиске |
| `Body` | `radius, mass, rooted` | Столкновения, расталкивание, поиск; `rooted` — TS `Converting` |
| `Mover` | `chassis` (запись `moves.json`), `speed, turn` | Ход и сетки проходимости по ходовой (`docs/movement.md`) |
| `Path` | `goalX, goalY, wait, tries, near, roads, direct, stuck, best, slot`; `points` — байты f64 | Пути и ход; мод ставит цель или берёт путь у `Plan` |
| `Mount` | `parent, along, across, angle` | TS `Attached` и `Turret.angle`; уничтожение носителя уносит турели |
| `Team` | `player, head` | Союзы: союзны те, у кого одинаковый `head ≥ 0` |
| `Sighting` | `rule` (seen/allies/everyone/listed/points), `ax..cy`; `viewers` — байты | Правила `sightingOf` из TS для `VisionView` |
| `Reveal` | `radius, until, all` | Вспышки обзора: `Glare` (всем) и радар `Scan` (хозяину) |

**Компоненты мода** (54 из TS; `Ghost` — только клиент, Н6):

| TS | .NET | Столбцы; байты | Заметки |
|---|---|---|---|
| Unit | `Unit` (метка) | — | `type` → Proto; `facing` → Facing |
| Building | `Building` | `phase, level` | размер → Footprint |
| Health | `Health` | `value, max, repairable, regen, hit` | |
| Repair | `Repair` | `radius, rate, target` | скрыт от чужих |
| Player | `Player` | `id, credits, earned, away, safe, online`; `rewards`, `camera` | хозяин и союзники |
| Player.research/plan/progress | `Science` | `done`, `plan`, `progress` (байты) | на сущности счёта |
| Player.allies/head/offered/asked | `Team` + `Diplomacy` | `offered`, `asked` (байты) | |
| Producer | `Producer` | `progress, current, rally, rallyX, rallyY`; `queue` (i32 видов) | скрыт от чужих |
| Converting / Deploys | `Converting`, `Deploys` | `left, total` / `x, y, misses` | `Deploys` скрыт |
| Site | `Site` | `progress, demolish, undo` | |
| Builds / Fetch | `Builds`, `Fetch` | `site, ordered, misses` / `from, to, resource, amount, full, misses` | скрыты |
| Deposit | `Deposit` | `mined, spent` | месторождение — предмет слоя `deposits` |
| Hauler | `Hauler` + `Carry` | 14 чисел, `filter` — маска; `route`, `serve` | `order` → `Carry`; скрыты |
| Inventory | `Stock` (+`Limits`) | `capacity, reserve`, по столбцу на груз (16) | `accepts/limits` из вида; скрыт, кроме Drop |
| Wanted | `Wanted` | по столбцу на груз | скрыт |
| Beam | `Beam` | `radius, rate, give, take`; `links` | `links` — для клиента |
| Assembly | `Assembly` | `on, progress` | рецепт — из вида |
| Trade / Transfer | `Trade`, `Transfer` | как в TS | груз `Transfer` — в `Stock` |
| Armed | `Armed` | `target` (скрыт), `chase, cooldown, stuck, ordered` | |
| Shot / Blast / Burst | `Shot`, `Blast`, `Burst` | как в TS без `prev*` | `weapon` → Proto |
| Turret / Attached / Carrier | → `Mount`, Proto | — | |
| Pave / Dig | `Pave`, `Dig` | `kind` и остальное | готовое покрытие — слой `paving` |
| Drop, Off, Busy | метки | — | |
| Tactics | `Tactics` | `stance, leg, away, homeX, homeY, resupply`; `patrol` | скрыт |
| Magazine, Unreachable, Accumulator, Interceptor | как в TS | | скрыты |
| Shield, Reactor, Upgrade, Gate, Doomed | как в TS | | `Gate.open` дублируется в `Footprint.open` |
| Research | `Research` | `speed, lack` | |
| Scan | `Scan` | `x, y, tick, dark`; `wait` скрыт | вспышка → `Reveal` |
| Silo | `Silo` | `start` открыт, остальное скрыто | |
| Missile / Chaser | `Missile`, `Chaser` | `toX, toY, hunted` скрыты; `courseX/Y` | |
| Glare | → `Reveal` | | |
| Mark | `Mark` | `kind, until, player` | зрители — `Sighting.listed` |
| Orders | `Orders` | `list` (байты) | |
| Batch | `Batch` | `from, to, resource, amount, sent, arrive, push`; `path` | союзники |

**Расширения схемы** (волна 0): компоненты-метки без полей; атрибуты `"@net": "all|owner|none"`, `"@private": [поля]`
(скрытое видят хозяин и союзники, как `PRIVATE` и `seenBy` в TS), `"@save": false`; объявления из
`mods/base/schema/*.json` по файлу на раздел складываются с `mod.json`.

## Н5.2. Что где считается

Замер Н2: система в wasm медленнее C# на JIT в 1,7 раза, вызов инструмента — 0,3 мкс плюс вход и выход. Что каждый тик
идёт по всему миру и опирается на геометрию или поиск — нативная система движка; системы мода идут по своим
подмножествам. Ориентир при 10 тыс. сущностей: движок 3–6 мс, мод — около 32 систем, меньше 20 мс из 50 (ворота замера
после С5а).

| № | TS | Где | Раздел | Заметки |
|---|---|---|---|---|
| 1 | commands | движок + мод | Kernel/Commands | очередь Shift — тот же диспетчер |
| 2–3 | deployOrders, convert | мод | Kernel/Conversion | |
| 4–7 | burnFuel, markBusy, storeEnergy, openBooks | мод | Energy | хозяйства зон — кэш на тик |
| 8 | produceUnits | мод | Kernel/Production | FreeTiles, OpenSpawn |
| 9 | refine | мод | Mining | |
| 10 | assemble | мод | Production | |
| 11–12 | research, triggers | мод | Research | |
| 13 | scan | мод | Missiles | пишет Reveal |
| 14 | gates | мод | Construction | пишет Footprint.open |
| 15–16 | planPaths, moveUnits | **движок** | `engine.paths`, `engine.move` | перенос `path.rs`, `walk.rs`, `movement.rs` |
| 17 | followCarriers | **движок** | `engine.mounts` | |
| 18 | construct | мод | Construction | |
| 19 | pipeFlow | мод | Pipes | граф сети — из кэша зон |
| 20–21 | haul, fetchMaterials | мод | Logistics | побочные таблицы, Stand с квотой |
| 22–23 | trade, ferry | мод | Trade | |
| 24 | launchMissiles | мод | Missiles | |
| 25 | fight | мод | Combat | побочная таблица целей, Ray |
| 26, 28 | patrol, followOrders | мод | Tactics | |
| 27, 29, 31 | supply, restTurrets, chargeShields | мод | Defense | |
| 30 | recover | мод | Combat | |
| 32 | earn | мод | Energy | |
| 33 | traces | **движок** | `engine.traces` | следы боя мод кладёт командой Item |
| 34 | vision | **движок** | `engine.vision` | |
| 35, 37 | updateLandMemory, updatePaveMemory | **движок** | `engine.recallLand`, `engine.recallPaving` | перенос `recall.rs` |
| 36 | renewDeposits | мод | Mining | |
| 38 | updateBuildMemory | **движок** | `engine.memory` | память в сохранении |
| 39 | closeBooks | мод | Energy | |

Вспомогательные модули: `path.ts` и проходимость из `units.ts` — движок; `canPlace`, `canBuild`, уровни — мод в
`Base.Shared/Rules` (ими пользуется и клиент); `zones.ts` — мод, кэш по ревизии; слои покрытия, месторождений, следов,
памяти — движок, правила и цены — мод; `inventory/logistics/hauling/routes/carrying/fetching/pipes/piping` — мод, подъезд
к зданию — инструмент `Stand`; `editor.ts`, `editOps.ts` — приказы `edit.*` с правом редактора (ими же пользуются тесты).

Как мод дёшево читает чужое: `ctx.RowOf(entity)` в кадре; побочные таблицы `.Table(...)`/`.WriteTable(...)`; пакетное
`Tools.Columns`/`Tools.BytesMany`; кэш в экземпляре с ключом (тик, ревизия) — не состояние в смысле Р3; `Stage`;
инструменты движка.

## Н5.3. Движок, ABI и SDK

**Инструменты** (`kh.tool`, номера — в `ModAbi.cs`, волна 0): 11 Columns, 12 BytesMany, 13 Revision, 14 Children,
15 Sees, 16 Tiles, 17 Occupant, 18 Layer, 19 Items, 20 Peak, 21 Walkable, 22 Plan (путь с квотой тика), 23 GroupMove,
24 FreeTiles, 25 Reach, 26 OpenSpawn, 27 Stand (подъезд с карманом и квотой), 28 Clear, 29 Ray (стена на линии
выстрела).

**Команды движка** (новые op): `0x08 Layer` (ячейка или прямоугольник сетки), `0x09 Item` (предмет слоя, удаление),
`0x0A Land` (правка тайла или вершины горы, рассылается клиентам), `0x0B SignalTo` (списку игроков), `0x0C SignalAt`
(тем, кто видит точку); уничтожение носителя уносит сущности с `Mount.parent`.

**SDK и планировщик:** `.Without(component)`; `.Table()`, `.WriteTable()`, `ctx.RowOf`; `Stage`; `DetMath` в SDK (тот
же исходник) плюс `Atan2` и `Hypot` с семантикой JS; `OrderContext.Rights` (Remote, Admin, Editor — приказы с целью по
номеру от клиента отвергаются, как `fromClient` в TS); экспорт `kh_query` и `Host.Query` для тестов, админа и редактора;
`HostOptions.Settings` (JSON поверх записи `games.json`); ключи систем движка `engine.*` для `After`/`Before`; режим CLR
(`HostOptions.ModMode = Wasm|Clr`, `IModInstance`).

## Н5.4. Приказы

48 команд TS — приказы мода с теми же именами (`order, move, rally, deploy, …, dig`); `assist, haul, pickup, supply,
attack` — только изнутри. Плюс `stop`, `ally`, `breakAlly`, `exitAlly` и `edit.*`. Payload — MemoryPack-классы в
`Base.Shared/Orders/<Раздел>Orders.cs` (общий проект для Base.Host, Base.Client, тестов и Balance). Логика `accept`
(приказ точкой → `resolveOrder` → приказы с целью; Shift → `Orders.list`) — в `Kernel/Commands`. Сигналы —
`Base.Shared/Signals/*.cs`, отказы и события юнитов — адресно (`SignalTo`).

## Н5.5. Данные

`data/tables/*.json` переезжают в `data/*.json` как есть; пилотные `units/buildings/turrets/deposits/mechanics.json`
удаляются, поля подачи пилота (`motor`, `pitch`, `exit`) — подобъектом `look`. `rules.json` — `DEFAULT_RULES`; правила
местности — в `moves.json` (ходовые `legs`, `wheels`, `tracks`, `air`). `games.json`: `skirmish` как в TS (1024²,
1500 кредитов, стартовый набор), `sandbox`, `test` (без автостарта), `remember: ["Building"]`. `lands.json`: `default` =
генератор TS. Типы таблиц — `Base.Shared/Data/*.cs` с генерацией кода JSON (AOT). Константы модулей TS остаются
константами C#.

## Н5.6. Тесты и harness

- Обвязка `tests/Kharos.Base.Scenario` (для тестов и `tools/Kharos.Balance`): `Sim.Create(options)`, `Advance`,
  `Seconds`, `Until`, типизированные `Send` и `Query`, чтение и запись компонентов по именам, `ThroughSave()`, правки
  `edit.*`, `Builders.AddBuilders` (перенос `builders.ts`). Режим CLR по умолчанию, `KHAROS_MODS=wasm` — на `host.wasm`.
- `X.test.ts` → `tests/Kharos.Base.Tests/Sim/<Раздел>/XTests.cs`, `[Fact(DisplayName = "<заголовок TS дословно>")]`;
  `tools/parity.ps1` сверяет заголовки. Тест, ждущий другой раздел, — `Skip = "ждёт С2b"`.
- Около 490 тестов из 630 (остальное — клиент Н6, уже покрыто в Н1/Н4 или устарело) по пакетам: Д1 Ход (units,
  terrainSpeed, army-paths), Д3/Д4 Слои и обзор (traces, landMemory, vision, weather), Б1 Ядро (core, sim, rally),
  С1 Стройка (construction, cliffs, paving, earthworks, zones, gates, fortify, buildings), С2а Добыча (mining, drops,
  renewal, depositLayer), С2b Логистика (request, routes, carrying, fetching, unreachable, approach, economy), С2c Трубы
  (pipes, sandbox), С2d Торговля (ferry, gift), С3 Энергия (income, busy, reactor, lithium), С4 Производство (assembly,
  army, gameplay), С5 Бой (combat, defense, towers, shields, supply, target, repairStation), С6 Исследования (research,
  upgrades), С7 Ракеты (silo, interceptor), С8 Тактика (tactics, orders), С9 Игроки (allies, shelter, defeat, editor,
  showcase), П0.1 Данные (base).
- Harness: в `tools/harness/lib.ts` — вывод `KHAROS_HARNESS_JSON=1`, эталоны в
  `tests/Kharos.Base.Tests/Balance/golden/*.json`; перенос в `tools/Kharos.Balance`; `BalanceTests` сверяют с допуском
  (доли ±5 %, время ±2 с, счёт ±1) — запускаются вручную, не в каждом прогоне.

## Н5.7. Волны и владение файлами

Общие файлы делаются в волне 0 одним владельцем и потом не правятся: `ModAbi.cs` (все номера), `EngineTools` и
`ModTools` (разбор по номеру → `partial`-методы, заглушка каждого — в файле владельца `Tools/Tool.<Имя>.cs`), SDK
`Tools.<Имя>.cs`, `BaseMod.cs` (список разделов), `Sections/<X>/<X>Section.cs` с `Setup`, `Order.cs` (порядок тика),
`schema/<раздел>.json`, `Base.Shared/Orders/<раздел>.cs`; `Kernel/*` после Б1 заморожен; `docs/` правит координатор.

| Волна | Пакет | Владеет | Зависит от | Модель |
|---|---|---|---|---|
| 0 | П0.1+П0.5 Схема, данные, каркас base | `mods/base/schema/*`, `data/*`, `Base.Shared`, `BaseMod.cs`, `Order.cs`, заглушки разделов; метки и атрибуты в Content и Schema | — | Opus |
| 0 | П0.2+П0.3 ABI, SDK, режим CLR | `ModAbi.cs`, SDK, Scheduler, CommandBuffer, заглушки инструментов, `Host.Query`, Settings, `IModInstance`, `ClrModule` | — | Opus |
| 0 | П0.4 Обвязка сценариев | `tests/Kharos.Base.Scenario`, `tools/parity.ps1`, JSON-вывод harness, эталоны | П0.2, П0.3 | Sonnet |
| 1 | Д1 Ход и пути | `Engine/Movement/*`, `engine.paths`, `engine.move`, инструменты 21–28 | П0.2 | Opus |
| 1 | Д2 Занятость и носители | занятость, `engine.mounts`, инструменты 14, 16, 17 | П0.2 | Sonnet |
| 1 | Д3 Слои для модов | op Layer, Item, Land, инструменты 18–20, `engine.traces`, `engine.recall*` | П0.2 | Sonnet |
| 1 | Д4 Обзор и видимость | Vision, Sighting, скрытые поля, SignalTo/At, `engine.memory`, инструмент 15 | П0.1, П0.2 | Opus |
| 1 | Д5 Пакетное чтение и замер | инструменты 11–13, `Bench --tick` | П0.2 | Sonnet |
| 1–2 | Б1 Ядро base | `Kernel/*` | П0.*; тесты — после Д1, Д2 | Opus |
| 2 | С1 Стройка | `Sections/Construction` | Б1, Д3 | Opus |
| 2 | С2а Добыча | `Sections/Mining` | Б1, Д3 | Sonnet |
| 2 | К Клиент | `Base.Client` на новой схеме | Б1 | Sonnet |
| 3 | С2b Логистика | `Sections/Logistics` | С2а, С1 | Opus |
| 3 | С3 Энергия | `Sections/Energy` | С1 | Opus |
| 4 | С2c Трубы | `Sections/Pipes` | С2b | Opus |
| 4 | С2d Торговля | `Sections/Trade` | С2b | Sonnet |
| 4 | С4 Производство | `Sections/Production` | С2b, С3 | Sonnet |
| 4 | С5а Бой | `Sections/Combat` | Б1, С1 | Opus |
| 5 | С5b Щиты и боеприпасы | `Sections/Defense` | С5а, С2b | Sonnet |
| 5 | С6 Исследования и уровни | `Sections/Research` | С2b, С3 | Opus |
| 5 | С8 Тактика | `Sections/Tactics` | С5а | Sonnet |
| 6 | С7 Ракеты | `Sections/Missiles` | С5а, Д4 | Sonnet |
| 6 | С9 Игроки | `Sections/Players` | Д4, все | Opus |
| 6 | H Баланс | `tools/Kharos.Balance` | все | Sonnet |

У каждого раздела свои тесты в `tests/Kharos.Base.Tests/Sim/<Раздел>/`. Проверка — на Windows и в WSL, CI — только для
macOS и в финале.

## Н5.8. Клиент и сеть

Base.Client переходит на новую схему (`Drive` → `Mover`/`Path`, `Unit.radius` → `Body.radius`, `Building.width/height`
→ `Footprint`, новая раскладка `Producer`, месторождения из слоя через `Tools.Items`, правый щелчок шлёт `order`) —
полная подача в Н6. Сеть: видимость по Sighting, союзники по `Team`, `@private` и `@net owner` вырезаются для чужих
(`Path` — только хозяину), сигналы адресные, правки земли рассылаются, память о зданиях — по игрокам и переживает
переподключение.

## Н5.9. Риски и порядок

| Риск | Что делаем |
|---|---|
| В TS изменения видны сразу, у нас — после системы | `Stage` с первого дня; сценарии |
| Точность хода | перенос с Rust, совпавшего с TS до бита; JsMath и DetMath |
| Порядок тика | один `Order.cs` по `sim.ts` |
| Расхождение wasm и CLR | DetMath; прогон сценариев на wasm локально |
| Разделы зависят друг от друга | ядро первым, Skip с меткой раздела, волны по зависимостям |
| Схему перекраивают по ходу | вся схема в волне 0, правки — только владельцем |
| Кэши мода при сбросе экземпляра | ключи по Revision; тест «экземпляр выброшен посреди партии» |
| Цена побочных таблиц на 100 тыс. | ворота замера после С5а; горячее — в движок |
| Вес `host.wasm` и память | замер в волне 0, при росте — лимит 256 МБ |

Порядок: схема, ABI, порядок тика и каркас (волна 0) → ход (Д1) параллельно с ядром (Б1) → стройка и добыча, затем
клиент → логистика и энергия → трубы, торговля, производство, бой, замер → исследования и тактика → ракеты, игроки,
баланс.
