import type { Component, Entity, JournalOp } from '@kharos/ecs'
import { CHUNK_SIZE, chunkRevision, getChunk, tileKey, type Land } from '@kharos/shared/terrain'
import { snapshot, updateBuildMemory, type BuildMemory, type Remembered } from '@kharos/sim-ts/buildMemory'
import { BUILDINGS } from '@kharos/sim-ts/buildings'
import { DEPOSIT_TYPES, type DepositLayer } from '@kharos/sim-ts/deposits'
import { powerSupply } from '@kharos/sim-ts/income'
import * as components from '@kharos/sim-ts/components'
import { Attached, Building, Owner, Position } from '@kharos/sim-ts/components'
import { landLayer, packTile } from '@kharos/sim-ts/landMemory'
import { keyTile, type Paving } from '@kharos/sim-ts/paved'
import { paveLayer } from '@kharos/sim-ts/paveMemory'
import { createRecall, updateRecall, type Recall } from '@kharos/sim-ts/recall'
import { TRACE_LIFE, traceCell, type Trace, type TraceKind, type Traces } from '@kharos/sim-ts/traces'
import { UNIT_TYPES } from '@kharos/sim-ts/units'
import type { Vision } from '@kharos/sim-ts/vision'
import { createSim, type Sim, type SimHooks, type SimOptions, type SimSave } from '@kharos/sim-ts/sim'

/**
 * Rust-симуляция за интерфейсом Sim (docs/boundary.md). Внутри — обычная sim-ts: её мир, обзор, земля и памяти служат
 * зеркалами, а Rust (crates/sim-wasm) держит свой мир и повторяет TS-мир операция в операцию — номера, порядок в
 * хранилищах, данные. Системы, уже перенесённые на Rust (crates/sim), идут в Rust на своём месте в тике: перед каждой
 * TS досылает то, что она читает, после — её журнал повторяется в TS-мире. Остальные идут в TS, и после advance Rust
 * догоняет всё.
 */

/** Плоские функции sim.wasm, см. crates/sim-wasm. */
interface Exports {
  memory: WebAssembly.Memory
  alloc(len: number): number
  dealloc(ptr: number, len: number): void
  out_ptr(): number
  out_len(): number
  sim_new(ptr: number, len: number, left: number, top: number, width: number, height: number, boundsLeft: number, boundsTop: number, boundsRight: number, boundsBottom: number, fog: number): number
  vision_open(id: number, player: number, tick: number): void
  vision_sees(id: number, player: number, x: number, y: number): number
  vision_explored(id: number, player: number, x: number, y: number): number
  vision_cells(id: number, player: number): number
  vision_changed(id: number, player: number): number
  vision_seen(id: number, player: number, size: number): number
  vision_map(id: number, player: number): number
  vision_explore(id: number, player: number, ptr: number, len: number): void
  vision_players(id: number): number
  vision_forget(id: number, player: number): void
  vision_reset(id: number): void
  memory_input(id: number, ptr: number, len: number): void
  memory_take(id: number): number
  build_memory_input(id: number, ptr: number, len: number): number
  build_memory_take(id: number): number
  traces_add(id: number, ptr: number, len: number): number
  traces_take(id: number): number
  traces_pending(id: number): number
  paving_take(id: number): number
  sim_power(id: number, ptr: number, len: number): void
  sim_rules(id: number, ptr: number, len: number): number
  sim_free(id: number): void
  sim_apply(id: number, ptr: number, len: number): number
  sim_journal(id: number): number
  sim_snapshot(id: number): number
  sim_order(id: number, ptr: number, len: number): number
  sim_systems(): number
  sim_run(id: number, ptr: number, len: number, tick: number, step: number): number
  sim_layers(id: number, ptr: number, len: number): number
  sim_layer(id: number, ptr: number, len: number, owner: number): number
  sim_chunk(id: number, chunkX: number, chunkY: number): number
  land_area(ptr: number, len: number, left: number, top: number, right: number, bottom: number): number
}

/** Экземпляр sim.wasm: один на сколько угодно симуляций. */
export interface SimWasm {
  readonly exports: Exports
}

const encoder = new TextEncoder()
const decoder = new TextDecoder()

/** Заводит sim.wasm из байтов сборки (`bun run build:wasm`). Синхронно: так его можно завести и внутри createSim. */
export function loadSimWasm(bytes: BufferSource): SimWasm {
  // Тригонометрия и гипотенуза — от JS (crates/engine/src/math.rs): так Rust считает их ровно как TS рядом.
  const math = { hypot: Math.hypot, sin: Math.sin, cos: Math.cos, atan2: Math.atan2 }
  const instance = new WebAssembly.Instance(new WebAssembly.Module(bytes), { math })
  return { exports: instance.exports as unknown as Exports }
}

/** Зовёт функцию wasm с текстом на входе: текст кладётся в выделенный буфер и освобождается после. */
const withText = <R>(api: Exports, text: string, call: (ptr: number, len: number) => R): R => withBytes(api, encoder.encode(text), call)

/** Зовёт функцию wasm с байтами на входе: они кладутся в выделенный буфер и освобождаются после. */
function withBytes<R>(api: Exports, bytes: Uint8Array, call: (ptr: number, len: number) => R): R {
  const ptr = api.alloc(Math.max(bytes.length, 1))
  // Вид на память — заново после каждого вызова, растущего память: старый отваливается (docs/engine.md, §5).
  new Uint8Array(api.memory.buffer, ptr, bytes.length).set(bytes)
  try {
    return call(ptr, bytes.length)
  } finally {
    api.dealloc(ptr, Math.max(bytes.length, 1))
  }
}

/** Ответ wasm: текст из общего буфера ответа. */
const answer = (api: Exports, ptr: number) => decoder.decode(new Uint8Array(api.memory.buffer, ptr, api.out_len()))

/**
 * Земля из генератора на Rust: чанки области тайлов [left, right) × [top, bottom) подряд, построчно, по 4096 байт, с
 * выводимым — как getChunk в TS. Для сверки генераторов.
 */
export function rustLand(wasm: SimWasm, config: object, bounds: { left: number; top: number; right: number; bottom: number }): Uint8Array {
  const api = wasm.exports
  const ptr = withText(api, JSON.stringify(config), (at, len) => api.land_area(at, len, bounds.left, bounds.top, bounds.right, bounds.bottom))
  return new Uint8Array(api.memory.buffer, ptr, api.out_len()).slice()
}

/** Изменение слоя (docs/layers.md): блок слоя целиком — чанк сетки или ячейка предметов. */
export interface LayerChange {
  layer: string
  /** Игрок; -1 — слой мира. */
  owner: number
  x: number
  y: number
  bytes: Uint8Array
}

/**
 * Изменения одним буфером, как `layers::encode` в crates/engine: число, затем на каждое — имя слоя (u8 длина, байты),
 * владелец (i32), x, y блока (i32), длина (u32) и байты; всё little-endian. Один формат на зеркало, сохранение и сеть.
 */
export function encodeChanges(changes: readonly LayerChange[]): Uint8Array {
  const names = changes.map(({ layer }) => encoder.encode(layer))
  const size = changes.reduce((sum, { bytes }, i) => sum + 1 + names[i].length + 16 + bytes.length, 4)
  const out = new Uint8Array(size)
  const view = new DataView(out.buffer)
  view.setUint32(0, changes.length, true)
  let at = 4
  changes.forEach(({ owner, x, y, bytes }, i) => {
    out[at++] = names[i].length
    out.set(names[i], at)
    at += names[i].length
    view.setInt32(at, owner, true)
    view.setInt32(at + 4, x, true)
    view.setInt32(at + 8, y, true)
    view.setUint32(at + 12, bytes.length, true)
    out.set(bytes, at + 16)
    at += 16 + bytes.length
  })
  return out
}

/** Чанк земли изменением слоя land. */
const landChange = (land: Land, x: number, y: number): LayerChange => ({ layer: 'land', owner: -1, x, y, bytes: getChunk(land, x, y) })

/** Изменения из буфера `layers::encode`, см. encodeChanges. */
export function decodeChanges(bytes: Uint8Array): LayerChange[] {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const changes: LayerChange[] = []
  let at = 4
  for (let n = view.getUint32(0, true); n > 0; n--) {
    const name = bytes[at++]
    const layer = decoder.decode(bytes.subarray(at, at + name))
    at += name
    const size = view.getUint32(at + 12, true)
    changes.push({ layer, owner: view.getInt32(at, true), x: view.getInt32(at + 4, true), y: view.getInt32(at + 8, true), bytes: bytes.slice(at + 16, at + 16 + size) })
    at += 16 + size
  }
  return changes
}

/**
 * Слой TS, который зеркало досылает в Rust (docs/layers.md, С2): блоки, изменённые с прошлого раза, и байты блока в
 * формате слоя. Новый слой в зеркале — ещё один такой объект.
 */
interface LayerMirror {
  layer: string
  /** Игрок; -1 — слой мира. */
  owner: number
  /** Блоки, изменённые с прошлого вызова: x, y блока. Нет — слой выводимый: Rust считает его сам, а зеркало только сверяет. */
  take?(): [number, number][]
  /** Все непустые блоки: для начала и сверки. */
  all(): [number, number][]
  /** Байты блока в формате слоя; пустые — блока нет. */
  block(x: number, y: number): Uint8Array
}

/** Чанк тайла по ключу tileKey тайла. */
const chunkOf = (key: number) => {
  const { x, y } = keyTile(key)
  return tileKey(Math.floor(x / CHUNK_SIZE), Math.floor(y / CHUNK_SIZE))
}

/** Чанки по ключам tileKey — координатами. */
const chunkList = (keys: Iterable<number>) => [...keys].map((key): [number, number] => [keyTile(key).x, keyTile(key).y])

/** Занятость (createOccupancy в buildings.ts) — выводимый слой occupancy: номер здания в u32. Только для сверки. */
function occupancyMirror(sim: Sim): LayerMirror {
  return {
    layer: 'occupancy',
    owner: -1,
    all() {
      const keys = new Set<number>()
      for (const [, position, building] of sim.world.query(Position, Building)) {
        const { width, height } = BUILDINGS[building.type]
        for (let y = position.y; y < position.y + height; y++) {
          for (let x = position.x; x < position.x + width; x++) keys.add(tileKey(Math.floor(x / CHUNK_SIZE), Math.floor(y / CHUNK_SIZE)))
        }
      }
      return chunkList(keys)
    },
    block(chunkX, chunkY) {
      const bytes = new Uint8Array(CHUNK_SIZE * CHUNK_SIZE * 4)
      const view = new DataView(bytes.buffer)
      for (let y = 0; y < CHUNK_SIZE; y++) {
        for (let x = 0; x < CHUNK_SIZE; x++) view.setUint32((y * CHUNK_SIZE + x) * 4, sim.occupancy.at(chunkX * CHUNK_SIZE + x, chunkY * CHUNK_SIZE + y) ?? 0, true)
      }
      return bytes
    },
  }
}

/** Покрытие (paved.ts) — слой paving: код тайла (paveCode) в u16. Изменения ловятся на lay, lift и clear. */
function pavingMirror(paving: Paving): LayerMirror {
  let dirty = new Set<number>()
  const { lay, lift, clear } = paving
  paving.lay = (x, y, code) => {
    lay(x, y, code)
    dirty.add(chunkOf(tileKey(x, y)))
  }
  paving.lift = (x, y) => {
    lift(x, y)
    dirty.add(chunkOf(tileKey(x, y)))
  }
  paving.clear = () => {
    for (const [key] of paving.tiles()) dirty.add(chunkOf(key))
    clear()
  }
  return {
    layer: 'paving',
    owner: -1,
    take() {
      const keys = dirty
      dirty = new Set()
      return chunkList(keys)
    },
    all: () => chunkList(new Set(Array.from(paving.tiles(), ([key]) => chunkOf(key)))),
    block(chunkX, chunkY) {
      const bytes = new Uint8Array(CHUNK_SIZE * CHUNK_SIZE * 2)
      const view = new DataView(bytes.buffer)
      let laid = false
      for (let y = 0; y < CHUNK_SIZE; y++) {
        for (let x = 0; x < CHUNK_SIZE; x++) {
          const code = paving.laid(chunkX * CHUNK_SIZE + x, chunkY * CHUNK_SIZE + y)
          if (!code) continue
          view.setUint16((y * CHUNK_SIZE + x) * 2, code, true)
          laid = true
        }
      }
      return laid ? bytes : new Uint8Array(0)
    },
  }
}

/** Системы на Rust, которым нужна доля энергии потребителей (powerSupply): её досылает TS. */
const POWERED = new Set(['scan', 'chargeShields', 'recover', 'refine', 'assemble', 'fight'])

/** Системы памяти слоёв (recall.ts): на Rust они идут через memory_input и memory_take. */
const MEMORY_SYSTEMS = ['updateLandMemory', 'updatePaveMemory', 'updateBuildMemory']

/** Виды следов по номеру, как в слое traces. */
const TRACE_KINDS = Object.keys(TRACE_LIFE) as TraceKind[]

/** Копия знания игроков: тень памяти начинает с того же. */
const copyKnown = ({ known }: Recall) => new Map([...known].map(([player, tiles]) => [player, new Map(tiles)]))

/**
 * Месторождения (deposits.ts) — слой deposits: предметы по клеткам 40×40, у предмета — место (f64, f64), вид (u8,
 * номер в DEPOSIT_TYPES) и запас (f64). Изменения ловятся на взятии и сбросе клеток (cells.set, cells.clear) и по номеру
 * правки слоя: правки (добавить, убрать, загрузить) меняют клетки на месте, и тогда досылаются все — клеток немного, а
 * правки редки.
 */
function depositsMirror(layer: DepositLayer): LayerMirror {
  let dirty = new Set<number>()
  let revision = layer.revision
  const { cells } = layer
  const { set, clear } = cells
  cells.set = (key, spots) => {
    dirty.add(key)
    return set.call(cells, key, spots)
  }
  cells.clear = () => {
    for (const key of cells.keys()) dirty.add(key)
    clear.call(cells)
  }
  return {
    layer: 'deposits',
    owner: -1,
    take() {
      if (layer.revision !== revision) {
        revision = layer.revision
        for (const key of cells.keys()) dirty.add(key)
      }
      const keys = dirty
      dirty = new Set()
      return chunkList(keys)
    },
    all: () => chunkList([...cells].filter(([, spots]) => spots.length).map(([key]) => key)),
    block(cellX, cellY) {
      const spots = cells.get(tileKey(cellX, cellY)) ?? []
      const bytes = new Uint8Array(spots.length * DEPOSIT_ITEM)
      const view = new DataView(bytes.buffer)
      spots.forEach((spot, i) => {
        view.setFloat64(i * DEPOSIT_ITEM, spot.x, true)
        view.setFloat64(i * DEPOSIT_ITEM + 8, spot.y, true)
        view.setUint8(i * DEPOSIT_ITEM + 16, DEPOSIT_TYPES.indexOf(spot.kind))
        view.setFloat64(i * DEPOSIT_ITEM + 17, spot.reserve, true)
      })
      return bytes
    },
  }
}

/** Байт на месторождение в слое: место, вид, запас. */
const DEPOSIT_ITEM = 8 + 8 + 1 + 8

/** Все компоненты симуляции по именам. */
const COMPONENTS: ReadonlyMap<string, Component<any>> = new Map(
  Object.values(components)
    .filter((value): value is Component<any> => typeof value === 'function' && 'key' in value && 'id' in value)
    .map((component) => [component.key, component]),
)

/** Системы, которые уже на Rust: имя в порядке тика → что она читает. */
const systemsOf = (api: Exports) =>
  new Map((JSON.parse(answer(api, api.sim_systems())) as { name: string; reads: string[] }[]).map(({ name, reads }) => [name, new Set(reads)]))

export interface RustSimOptions {
  /** Сверять Rust с TS после каждого advance: снимок мира и порядок каждого хранилища. Для тестов: это дорого. */
  check?: boolean
  /** Какие из перенесённых систем идут в Rust; без поля — все. Пустой список — тик целиком в TS. */
  rust?: readonly string[]
  /** Чем создаётся внутренняя sim-ts: тест подменяет createSim, а обёртке нужна настоящая. */
  create?: (source: SimOptions | SimSave, hooks?: SimHooks) => Sim
}

/** Освобождает мир в wasm, когда обёртку забыли, не вызвав destroy: тесты так делают сплошь. */
const finalizer = new FinalizationRegistry<{ api: Exports; id: number }>(({ api, id }) => api.sim_free(id))

export function createRustSim(wasm: SimWasm, source: SimOptions | SimSave, options: RustSimOptions = {}): Sim {
  const api = wasm.exports
  const available = systemsOf(api)
  const rust = new Map([...available].filter(([name]) => !options.rust || options.rust.includes(name)))
  // Память слоёв сверяет с обзором Rust: без него её считает TS.
  if (!rust.has('vision')) for (const name of MEMORY_SYSTEMS) rust.delete(name)
  // Бой оставляет следы: они в Rust, только если следы держит он.
  if (!rust.has('traces')) rust.delete('fight')
  const sim = (options.create ?? createSim)(source, {
    systems: (list) => list.map(([name, system]) => (rust.has(name) ? [name, () => runSystem(name)] : [name, system])),
  })
  const { world, land } = sim
  const area = land.area!
  const { bounds } = sim
  const fog = sim.options.fog !== false
  const id = withText(api, JSON.stringify(land.config), (ptr, len) =>
    api.sim_new(ptr, len, area.left, area.top, area.width, area.height, bounds.left, bounds.top, bounds.right, bounds.bottom, fog ? 1 : 0),
  )
  if (id < 0) throw new Error(`sim.wasm не принял настройки генератора: ${answer(api, api.out_ptr())}`)
  finalizer.register(sim, { api, id }, sim)

  /** Мир, каким его знает Rust: сущность → компонент → JSON данных. По нему видно, что поменяли на месте. */
  const known = new Map<number, Map<string, string>>()
  /** Операции TS, которых Rust ещё не видел: только состав — данные догоняет сверка. */
  let pending: string[] = []

  const apply = (ops: string[]) => {
    if (!ops.length) return
    const code = withText(api, `[${ops.join(',')}]`, (ptr, len) => api.sim_apply(id, ptr, len))
    if (code !== 0) throw new Error(`sim.wasm не принял журнал: ${code === 1 ? answer(api, api.out_ptr()) : 'нет симуляции'}`)
  }

  const applyLayers = (changes: LayerChange[]) => {
    if (!changes.length) return
    const code = withBytes(api, encodeChanges(changes), (ptr, len) => api.sim_layers(id, ptr, len))
    if (code !== 0) throw new Error(`sim.wasm не принял слои: ${code === 1 ? answer(api, api.out_ptr()) : 'нет симуляции'}`)
  }

  /** Слои TS, которые Rust повторяет. */
  const mirrors: LayerMirror[] = [pavingMirror(sim.paving), occupancyMirror(sim), depositsMirror(sim.deposits)]

  /** Досылает Rust изменённое в слоях: землю и блоки зеркал. */
  const syncLayers = () => {
    syncLand()
    const changes: LayerChange[] = []
    for (const mirror of mirrors) {
      for (const [x, y] of mirror.take?.() ?? []) changes.push({ layer: mirror.layer, owner: mirror.owner, x, y, bytes: mirror.block(x, y) })
    }
    applyLayers(changes)
  }

  /** Правка карты, которую Rust уже знает: чанки с правкой новее — изменённые. */
  let landRevision = land.revision
  /** Досылает Rust чанки земли, изменённые с прошлого раза, — вместе с соседями, у которых пересчиталось выводимое. */
  const syncLand = () => {
    if (land.revision === landRevision) return
    const changes: LayerChange[] = []
    for (const [key, revision] of land.revisions) {
      // Ключ — tileKey(x, y) чанка.
      if (revision > landRevision) changes.push(landChange(land, (key % 65536) - 32768, Math.floor(key / 65536) - 32768))
    }
    landRevision = land.revision
    applyLayers(changes)
  }

  /**
   * Запись операции для Rust и для known. add идёт с данными, set — правкой, по порядку: выводимое в Rust (занятость)
   * читает данные на миг операции, как наблюдатели TS. Правку на месте TS не журналит — её догоняет сверка JSON, поэтому
   * после set значение в known помечается устаревшим.
   */
  const note = (op: JournalOp) => {
    switch (op[0]) {
      case 'spawn':
        known.set(op[1], new Map())
        pending.push(`["spawn",${op[1]}]`)
        return
      case 'destroy':
        known.delete(op[1])
        pending.push(`["destroy",${op[1]}]`)
        return
      case 'next':
        pending.push(`["next",${op[1]}]`)
        return
      case 'add': {
        const data = typeof op[3] === 'string' ? op[3] : JSON.stringify(op[3])
        known.get(op[1])?.set(op[2], data)
        pending.push(`["add",${op[1]},${JSON.stringify(op[2])},${data}]`)
        return
      }
      case 'set':
        known.get(op[1])?.set(op[2], '')
        pending.push(`["set",${op[1]},${JSON.stringify(op[2])},${typeof op[3] === 'string' ? op[3] : JSON.stringify(op[3])}]`)
        return
      case 'remove':
        known.get(op[1])?.delete(op[2])
        pending.push(`["remove",${op[1]},${JSON.stringify(op[2])}]`)
        return
    }
  }

  /**
   * Догоняет Rust: состав — по журналу, данные — сверкой JSON с нынешним TS: только компонентов keys, если они даны
   * (то, что читает следующая система на Rust), иначе — всего, что Rust знает.
   */
  const sync = (keys?: ReadonlySet<string>) => {
    syncLayers()
    const ops = pending
    pending = []
    for (const [entity, data] of known) {
      for (const [key, json] of data) {
        if (keys && !keys.has(key)) continue
        const current = JSON.stringify(world.get(entity as Entity, COMPONENTS.get(key)!))
        if (current === json) continue
        data.set(key, current)
        // Значение целиком: Rust кладёт поля поверх, а набор полей у компонента постоянный.
        ops.push(`["set",${entity},${JSON.stringify(key)},${current}]`)
      }
    }
    apply(ops)
  }

  /** Повторяет в TS-мире журнал Rust: что сделала система на Rust. В журнал TS эти операции не идут — Rust их знает. */
  const pull = () => {
    const ops = JSON.parse(answer(api, api.sim_journal(id))) as Parameters<typeof world.replay>[0][]
    if (!ops.length) return
    world.journal = undefined
    try {
      for (const op of ops) {
        world.replay(op, COMPONENTS)
        switch (op[0]) {
          case 'spawn':
            known.set(op[1], new Map())
            break
          case 'destroy':
            known.delete(op[1])
            break
          case 'remove':
            known.get(op[1])?.delete(op[2])
            break
          case 'add':
          case 'set': {
            // Rust знает значение таким, каким его теперь видит TS: Rust кладёт поля поверх так же.
            const value = world.get(op[1] as Entity, COMPONENTS.get(op[2])!)
            if (value !== undefined) known.get(op[1])?.set(op[2], JSON.stringify(value))
          }
        }
      }
    } finally {
      world.journal = note
    }
  }

  /**
   * Обзор TS, пока обзор считает Rust: в режиме check он повторяет то же — заводит игроков в тот же миг и считается
   * системой vision после Rust, — и сверка сравнивает их. Без check он остаётся тем, чем его загрузил createSim.
   */
  const shadow = sim.vision
  /** Игроки, чей обзор Rust уже завёл: остальных при первом вопросе заводит vision_open — на мире этого мига. */
  const opened = new Set<number>()
  const ensure = (player: number) => {
    if (opened.has(player)) return
    sync()
    api.vision_open(id, player, sim.time.tick)
    opened.add(player)
    if (options.check) shadow.cells(player)
  }
  const numbers = <T extends Float64Array | Uint32Array>(Type: { new (buffer: ArrayBuffer): T }, ptr: number) =>
    new Type(api.memory.buffer.slice(ptr, ptr + api.out_len()))
  const rustPlayers = () => Array.from(numbers(Uint32Array, api.vision_players(id)))

  /** Обзор из Rust за интерфейсом Vision (docs/layers.md, С5): системы TS спрашивают его, а считает Rust. */
  const vision: Vision = {
    sees(player, x, y) {
      ensure(player)
      return api.vision_sees(id, player, x, y) === 1
    },
    seer(player) {
      ensure(player)
      return (x, y) => api.vision_sees(id, player, x, y) === 1
    },
    explored(player, x, y) {
      ensure(player)
      return api.vision_explored(id, player, x, y) === 1
    },
    seesEntity(player, entity) {
      const parent = world.get(entity, Attached)?.parent
      const target = parent === undefined ? entity : (parent as Entity)
      if (world.get(target, Owner)?.player === player) return true
      const position = world.get(target, Position)
      if (!position) return false
      const building = world.get(target, Building)
      if (!building) return vision.sees(player, position.x, position.y)
      const { width: w, height: h } = BUILDINGS[building.type]
      for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (vision.sees(player, position.x + x, position.y + y)) return true
      return false
    },
    cells(player) {
      ensure(player)
      const ptr = api.vision_cells(id, player)
      const cells = new Uint8Array(api.memory.buffer, ptr, api.out_len()).slice()
      return { cells, changed: api.vision_changed(id, player) }
    },
    seenCells(player, size) {
      ensure(player)
      return new Set(numbers(Float64Array, api.vision_seen(id, player, size)))
    },
    exploredIn(player, x, y, w, h) {
      for (let tileY = Math.floor(y); tileY < y + h; tileY++) for (let tileX = Math.floor(x); tileX < x + w; tileX++) if (vision.explored(player, tileX, tileY)) return true
      return false
    },
    map(player) {
      ensure(player)
      return Array.from(numbers(Uint32Array, api.vision_map(id, player)))
    },
    explore(player, map) {
      ensure(player)
      withBytes(api, new Uint8Array(Uint32Array.from(map).buffer), (ptr, len) => api.vision_explore(id, player, ptr, len))
      if (options.check) shadow.explore(player, map)
    },
    players: rustPlayers,
    update: () => runSystem('vision'),
    reset() {
      api.vision_reset(id)
      opened.clear()
      visionChecked.clear()
      if (options.check) shadow.reset()
    },
    forget(player) {
      api.vision_forget(id, player)
      opened.delete(player)
      visionChecked.delete(player)
      if (options.check) shadow.forget(player)
    },
  }

  /** Система на Rust на своём месте в тике; обзор TS в режиме check повторяет её. */
  const runSystem = (name: string) => {
    if (MEMORY_SYSTEMS.includes(name)) return runMemory(name)
    if (name === 'traces') return runTraces()
    runRust(name, rust.get(name)!)
    if (name !== 'vision') return
    for (const player of rustPlayers()) opened.add(player)
    if (options.check) shadow.update()
  }

  /** Счётчики changed обзора обеих сторон на последней сверке: игрок → «Rust TS». */
  const visionChecked = new Map<number, string>()

  /**
   * Сверяет обзор Rust с обзором TS: игроков по порядку и тайлы каждого. Открытое меняется только с changed (разведанное
   * растёт из открытого, explore тоже растит changed), поэтому тайлы сравниваются, только когда счётчик сдвинулся хоть у
   * одной стороны: на большой карте это мегабайт на игрока.
   */
  const checkVision = () => {
    if (!rust.has('vision')) return
    const players = rustPlayers()
    const expected = shadow.players()
    if (players.join(' ') !== expected.join(' ')) throw new Error(`Rust разошёлся с TS: обзор у игроков ${players.join(' ')} против ${expected.join(' ')}`)
    const width = bounds.right - bounds.left
    for (const player of players) {
      const theirs = shadow.cells(player)
      const counters = `${api.vision_changed(id, player)} ${theirs.changed}`
      if (visionChecked.get(player) === counters) continue
      const ptr = api.vision_cells(id, player)
      const ours = new Uint8Array(api.memory.buffer, ptr, api.out_len())
      let at = ours.length === theirs.cells.length ? -1 : 0
      for (let i = 0; i < ours.length && at < 0; i++) if (ours[i] !== theirs.cells[i]) at = i
      if (at >= 0) {
        throw new Error(`Rust разошёлся с TS: обзор игрока ${player}, тайл (${bounds.left + (at % width)}, ${bounds.top + Math.floor(at / width)}): ${ours[at]} против ${theirs.cells[at]}`)
      }
      visionChecked.set(player, counters)
    }
    for (const player of visionChecked.keys()) if (!players.includes(player)) visionChecked.delete(player)
  }

  /**
   * Память о земле и о покрытии (recall.ts) считает Rust (docs/layers.md, С6), а в TS остаётся её зеркало: его читают
   * хост, редактор и сохранение. TS досылает изменённые тайлы слоёв, исходные тайлы земли и то, что забыл редактор; Rust
   * отвечает, что игроки узнали. Записи — по шесть чисел, см. memory_input и memory_take в crates/sim-wasm.
   */
  let memoryOps: number[] = []
  const sendMemory = () => {
    if (!memoryOps.length) return
    const bytes = new Uint8Array(Float64Array.from(memoryOps).buffer)
    memoryOps = []
    withBytes(api, bytes, (ptr, len) => api.memory_input(id, ptr, len))
  }
  /** Тени памяти на TS для режима check: считаются тем же updateRecall, что и в TS-симуляции. */
  const shadows = options.check ? [createRecall(copyKnown(sim.landMemory)), createRecall(copyKnown(sim.paveMemory))] : undefined
  /** Повторяет в TS-памяти то, что узнали игроки в Rust; отдаёт узнанное по игрокам. */
  const pullMemory = () => {
    const learnedNow = new Map<number, Map<number, number>>()
    const records = numbers(Float64Array, api.memory_take(id))
    for (let i = 0; i < records.length; i += 6) {
      const [layer, event, player, x, y, value] = records.subarray(i, i + 6)
      const memory = layer === 0 ? sim.landMemory : sim.paveMemory
      let known = memory.known.get(player)
      if (!known) memory.known.set(player, (known = new Map()))
      if (event === 0) continue
      const key = tileKey(x, y)
      if (event === 1) known.set(key, value)
      else known.delete(key)
      let learned = memory.learned.get(player)
      if (!learned) memory.learned.set(player, (learned = new Set()))
      learned.add(key)
      let now = learnedNow.get(player)
      if (!now) learnedNow.set(player, (now = new Map()))
      now.set(key, event === 1 ? value : -1)
    }
    return learnedNow
  }
  /** Система памяти слоя на Rust: досылает изменённое с прошлого раза и забирает узнанное. */
  const runMemory = (name: string) => {
    if (name === 'updateBuildMemory') return runBuildMemory()
    const land = name === 'updateLandMemory'
    // Земля без правок: как в TS, сверять нечего, а изменённое копится до первой.
    if (land && !sim.landMemory.original.size) return
    const changed = land ? [...sim.landMemory.edited] : sim.paving.drain()
    if (land) sim.landMemory.edited.clear()
    else for (const key of changed) sim.paveMemory.changed.add(key)
    for (const key of changed) memoryOps.push(0, land ? 0 : 1, 0, keyTile(key).x, keyTile(key).y, 0)
    sendMemory()
    runRust(name, rust.get(name)!)
    const learned = pullMemory()
    if (!shadows) return
    const shadow = shadows[land ? 0 : 1]
    updateRecall(sim, shadow, land ? landLayer(sim) : paveLayer(sim), changed)
    checkMemory(name, land ? sim.landMemory : sim.paveMemory, shadow, learned)
  }

  /**
   * Сверяет память Rust (её зеркало в TS) с тенью: игроков, сколько каждый знает и узнанное за этот запуск — тайлы и
   * значения. Знание меняется только узнаванием и забыванием тайлов редактором (оно идёт в обе), так что этого
   * хватает, чтобы они не разошлись незаметно.
   */
  const checkMemory = (name: string, memory: Recall, shadow: Recall, learned: Map<number, Map<number, number>>) => {
    const fail = (what: string) => {
      throw new Error(`Rust разошёлся с TS: ${name}, ${what}`)
    }
    const players = (recall: Recall) => [...recall.known.keys()].sort((a, b) => a - b).join(' ')
    if (players(memory) !== players(shadow)) fail(`игроки ${players(memory)} против ${players(shadow)}`)
    for (const [player, known] of memory.known) {
      const theirs = shadow.known.get(player)!
      if (known.size !== theirs.size) fail(`игрок ${player} знает ${known.size} тайлов против ${theirs.size}`)
      const ours = learned.get(player) ?? new Map<number, number>()
      const expected = shadow.learned.get(player) ?? new Set<number>()
      if (ours.size !== expected.size) fail(`игрок ${player} узнал ${ours.size} тайлов против ${expected.size}`)
      for (const key of expected) {
        const { x, y } = keyTile(key)
        const value = theirs.get(key) ?? -1
        if (ours.get(key) !== value) fail(`игрок ${player}, тайл (${x}, ${y}): ${ours.get(key) ?? 'не узнан'} против ${value}`)
      }
    }
    shadow.learned.clear()
  }

  /**
   * Память о зданиях (buildMemory.ts): кто что запомнил и забыл, решает Rust, а снимок здания снимает TS — на том же
   * мире этого мига, JSON тот же, что в TS-симуляции. TS досылает знание из сохранения и забытое игроками, начавшими
   * заново (`build_memory_input`).
   */
  let buildOps: unknown[] = []
  const sendBuildMemory = () => {
    if (!buildOps.length) return
    const code = withText(api, JSON.stringify(buildOps), (ptr, len) => api.build_memory_input(id, ptr, len))
    buildOps = []
    if (code !== 0) throw new Error(`sim.wasm не принял память о зданиях: ${code === 1 ? answer(api, api.out_ptr()) : 'нет симуляции'}`)
  }
  /** Тень памяти о зданиях на TS для режима check. */
  const buildShadow: BuildMemory | undefined = options.check
    ? { known: new Map([...sim.buildMemory.known].map(([player, known]) => [player, new Map(known)])), watched: new Map() }
    : undefined
  const runBuildMemory = () => {
    sendBuildMemory()
    runRust('updateBuildMemory', rust.get('updateBuildMemory')!)
    const events = JSON.parse(answer(api, api.build_memory_take(id))) as [number, number, number][]
    /** Снятое за запуск: игрокам, потерявшим здание из виду разом, — одна запись, как в TS. */
    const taken = new Map<number, Remembered>()
    const { known } = sim.buildMemory
    for (const [player, entity, remember] of events) {
      let theirs = known.get(player)
      if (!remember) {
        theirs?.delete(entity)
        continue
      }
      if (!theirs) known.set(player, (theirs = new Map()))
      let record = taken.get(entity)
      if (!record) taken.set(entity, (record = snapshot(sim, entity as Entity)))
      theirs.set(entity, record)
    }
    if (!buildShadow) return
    updateBuildMemory(Object.create(sim, { buildMemory: { value: buildShadow } }) as Sim)
    const fail = (what: string) => {
      throw new Error(`Rust разошёлся с TS: updateBuildMemory, ${what}`)
    }
    for (const player of new Set([...known.keys(), ...buildShadow.known.keys()])) {
      const ours = known.get(player) ?? new Map<number, Remembered>()
      const expected = buildShadow.known.get(player) ?? new Map<number, Remembered>()
      if (ours.size !== expected.size) fail(`игрок ${player} помнит ${[...ours.keys()].join(' ')} против ${[...expected.keys()].join(' ')}`)
      for (const [entity, { parts }] of expected) if (ours.get(entity)?.parts !== parts) fail(`игрок ${player}, здание ${entity}: ${ours.get(entity)?.parts} против ${parts}`)
    }
  }

  /**
   * Следы (traces.ts) держит Rust — предметы слоя traces (docs/layers.md, С7). Здесь — обёртка за интерфейсом Traces:
   * следы систем TS (бой) сразу уходят в Rust (`traces_add`), а после системы traces приходят оставленные за тик и
   * убранные (`traces_take`); по ним обёртка держит список и ячейки, которые читает хост. В режиме check прежние следы
   * TS получают те же следы и считают тот же тик, а сверка сравнивает свежие и убранные.
   */
  const shadowTraces = sim.traces
  let traceList: Trace[] = []
  let traceFresh: Trace[] = []
  let traceExpired: number[] = []
  let traceLast = 0
  const traceCells = new Map<number, Trace[]>()
  const traces: Traces = {
    add(trace) {
      const type = 'type' in trace ? UNIT_TYPES.indexOf(trace.type) : 0
      const fields = trace as { entity?: number; facing?: number; player?: number; size?: number }
      const record = [TRACE_KINDS.indexOf(trace.kind), trace.x, trace.y, fields.entity ?? 0, type, fields.facing ?? 0, fields.player ?? 0, fields.size ?? 0, sim.time.tick]
      const ptr = withBytes(api, new Uint8Array(Float64Array.from(record).buffer), (at, len) => api.traces_add(id, at, len))
      const laid = numbers(Float64Array, ptr)
      if (laid.length) keepTrace(traceOf(laid))
      if (options.check) shadowTraces.add(trace)
    },
    all: () => traceList,
    fresh: () => traceFresh,
    expired: () => traceExpired,
    inCell: (cell) => traceCells.get(cell) ?? [],
    update: () => runSystem('traces'),
    since(cursor) {
      let from = traceList.length
      while (from > 0 && traceList[from - 1].id > cursor) from--
      return { traces: traceList.slice(from), cursor: traceLast }
    },
  }
  /** След из записи Rust — с полями в том же порядке, что у TS: хост шлёт его JSON как есть. */
  const traceOf = ([id, tick, kind, entity, type, facing, player, size, x, y]: Float64Array): Trace => {
    switch (TRACE_KINDS[kind]) {
      case 'track':
        return { kind: 'track', entity, type: UNIT_TYPES[type], facing, player, x, y, id, tick }
      case 'scar':
        return { kind: 'scar', x, y, size, id, tick }
      case 'burn':
        return { kind: 'burn', x, y, id, tick }
      default:
        return { kind: 'wreck', type: UNIT_TYPES[type], facing, x, y, id, tick }
    }
  }
  /** Оставленные с прошлого тика системы, по номеру: в конце тика они станут свежими. */
  const traceLaid = new Map<number, Trace>()
  /** След лёг: он в списке и в ячейках сразу, как в TS. */
  const keepTrace = (trace: Trace) => {
    traceList.push(trace)
    traceLaid.set(trace.id, trace)
    traceLast = trace.id
    const cell = traceCell(trace.x, trace.y)
    const bucket = traceCells.get(cell)
    if (bucket) bucket.push(trace)
    else traceCells.set(cell, [trace])
  }
  const runTraces = () => {
    runRust('traces', rust.get('traces')!)
    const records = numbers(Float64Array, api.traces_take(id))
    const count = records[0]
    traceFresh = []
    for (let i = 0; i < count; i++) {
      const record = records.subarray(1 + i * 10, 11 + i * 10)
      if (!traceLaid.has(record[0])) keepTrace(traceOf(record))
      traceFresh.push(traceLaid.get(record[0])!)
    }
    traceLaid.clear()
    traceExpired = Array.from(records.subarray(1 + count * 10))
    if (traceExpired.length) {
      const gone = new Set(traceExpired)
      traceList = traceList.filter((trace) => !gone.has(trace.id))
      for (const [cell, bucket] of traceCells) {
        const left = bucket.filter((trace) => !gone.has(trace.id))
        if (left.length) traceCells.set(cell, left)
        else traceCells.delete(cell)
      }
    }
    if (!options.check) return
    shadowTraces.update()
    const fail = (what: string, ours: unknown, theirs: unknown) => {
      throw new Error(`Rust разошёлся с TS: следы, ${what}: ${JSON.stringify(ours)} против ${JSON.stringify(theirs)}`)
    }
    if (JSON.stringify(traceFresh) !== JSON.stringify(shadowTraces.fresh())) fail('оставленные за тик', traceFresh, shadowTraces.fresh())
    if (traceExpired.join(' ') !== shadowTraces.expired().join(' ')) fail('убранные', traceExpired, shadowTraces.expired())
    if (traceList.length !== shadowTraces.all().length) fail('живых', traceList.length, shadowTraces.all().length)
  }

  /** Правила мира, какими их знает Rust: JSON. */
  let rulesSent = ''
  /**
   * Что Rust пока берёт у TS: правила, если поменялись, и — системам, которым нужна энергия, — долю энергии каждого
   * потребителя. Хозяйства зон считает TS, и доля берётся в тот же миг, когда её спросила бы система в TS.
   */
  const lend = (name: string) => {
    const rules = JSON.stringify(sim.rules)
    if (rules !== rulesSent) {
      const code = withText(api, rules, (ptr, len) => api.sim_rules(id, ptr, len))
      if (code !== 0) throw new Error(`sim.wasm не принял правила: ${code === 1 ? answer(api, api.out_ptr()) : 'нет симуляции'}`)
      rulesSent = rules
    }
    if (!POWERED.has(name)) return
    const pairs: number[] = []
    for (const [entity, share] of powerSupply(sim)) pairs.push(entity, share)
    withBytes(api, new Uint8Array(Float64Array.from(pairs).buffer), (ptr, len) => api.sim_power(id, ptr, len))
  }

  /** Система на Rust на своём месте в тике. */
  const runRust = (name: string, reads: ReadonlySet<string>) => {
    sync(reads)
    lend(name)
    const code = withText(api, name, (ptr, len) => api.sim_run(id, ptr, len, sim.time.tick, sim.time.step))
    if (code !== 0) throw new Error(`sim.wasm: системы ${name} нет (${code})`)
    pull()
    if (name === 'fight') pullFight()
  }

  /**
   * Что бой на Rust сделал вне мира: снятые плиты фундамента — покрытие держит TS — и оставленные следы. Следы обёртка
   * сразу держит в списке, как оставленные из TS, а тень TS в режиме check получает их так же, как получила бы от боя TS.
   */
  const pullFight = () => {
    const lifted = numbers(Float64Array, api.paving_take(id))
    for (let i = 0; i < lifted.length; i += 2) sim.paving.lift(lifted[i], lifted[i + 1])
    const pending = numbers(Float64Array, api.traces_pending(id))
    for (let i = 0; i < pending.length; i += 10) {
      const record = pending.subarray(i, i + 10)
      if (traceLaid.has(record[0])) continue
      const trace = traceOf(record)
      keepTrace(trace)
      if (!options.check) continue
      const { id: _, tick: __, ...laid } = trace
      shadowTraces.add(laid as Parameters<Traces['add']>[0])
    }
  }

  /** Чанки карты, уже сверенные с Rust: номер в области → правка, на которой сверен. */
  const landChecked = new Map<number, number>()

  /** Сверяет землю: каждый чанк карты, собранный в TS, — один раз и снова после правки. */
  const checkLand = () => {
    land.owned.forEach((chunk, n) => {
      if (!chunk) return
      const x = area.left + (n % area.width)
      const y = area.top + Math.floor(n / area.width)
      const revision = chunkRevision(land, x, y)
      if (landChecked.get(n) === revision) return
      const ptr = api.sim_chunk(id, x, y)
      const rust = new Uint8Array(api.memory.buffer, ptr, api.out_len())
      const at = rust.findIndex((byte, i) => byte !== chunk[i])
      if (rust.length !== chunk.length || at >= 0) {
        const tile = Math.floor(at / 4)
        throw new Error(`Rust разошёлся с TS: земля, тайл (${x * CHUNK_SIZE + (tile % CHUNK_SIZE)}, ${y * CHUNK_SIZE + Math.floor(tile / CHUNK_SIZE)}), байт ${at % 4}: ${rust[at]} против ${chunk[at]}`)
      }
      landChecked.set(n, revision)
    })
  }

  /** Сверяет слои зеркал: блоки Rust и TS. Пустой блок (одни нули) и отсутствующий — одно и то же. */
  const checkLayers = () => {
    for (const mirror of mirrors) {
      const ptr = withText(api, mirror.layer, (at, len) => api.sim_layer(id, at, len, mirror.owner))
      const filled = (bytes: Uint8Array) => bytes.some((byte) => byte !== 0)
      const rust = new Map(
        decodeChanges(new Uint8Array(api.memory.buffer, ptr, api.out_len()))
          .filter(({ bytes }) => filled(bytes))
          .map(({ x, y, bytes }) => [`${x}, ${y}`, bytes]),
      )
      const ts = new Map(mirror.all().map(([x, y]) => [`${x}, ${y}`, mirror.block(x, y)]))
      for (const [block, bytes] of ts) if (!filled(bytes)) ts.delete(block)
      for (const block of new Set([...rust.keys(), ...ts.keys()])) {
        const a = rust.get(block)
        const b = ts.get(block)
        const at = !a || !b ? -1 : a.findIndex((byte, i) => byte !== b[i])
        if (!a || !b || at >= 0) throw new Error(`Rust разошёлся с TS: слой ${mirror.layer}, блок (${block}): ${!a ? 'нет в Rust' : !b ? 'нет в TS' : `байт ${at}: ${a[at]} против ${b[at]}`}`)
      }
    }
  }

  /** Сверяет Rust с TS: снимок, порядок каждого хранилища, землю и слои. Бросает на первом расхождении. */
  const check = () => {
    checkLand()
    checkLayers()
    checkVision()
    const keys = [...COMPONENTS.keys()]
    const rust = JSON.parse(answer(api, api.sim_snapshot(id))) as { next: number; entities: [number, Record<string, unknown>][] }
    const ts = world.snapshot([...COMPONENTS.values()])
    if (rust.next !== ts.next) throw new Error(`Rust разошёлся с TS: next ${rust.next} против ${ts.next}`)
    const byId = new Map(rust.entities)
    if (byId.size !== ts.entities.length) throw new Error(`Rust разошёлся с TS: сущностей ${byId.size} против ${ts.entities.length}`)
    for (const [entity, data] of ts.entities) {
      const other = byId.get(entity)
      for (const key of keys) {
        const a = JSON.stringify(data[key])
        const b = JSON.stringify(other?.[key])
        if (a !== b) throw new Error(`Rust разошёлся с TS: сущность ${entity}, ${key}: ${b} против ${a}`)
      }
    }
    for (const [key, component] of COMPONENTS) {
      const order = JSON.parse(withText(api, key, (ptr, len) => answer(api, api.sim_order(id, ptr, len)))) as number[]
      // Строка запроса — один массив на весь обход: номер берётся сразу.
      const expected: number[] = []
      for (const [entity] of world.query(component)) expected.push(entity)
      if (JSON.stringify(order) !== JSON.stringify(expected)) throw new Error(`Rust разошёлся с TS: порядок ${key}: ${order.join(' ')} против ${expected.join(' ')}`)
    }
  }

  // Начало. Земля: из сохранения — вся карта (Rust собрал бы её генератором, а мир уже жил), новая — только правленое.
  const loaded = 'land' in source
  const initial: LayerChange[] = []
  for (let n = 0; n < area.width * area.height; n++) {
    const x = area.left + (n % area.width)
    const y = area.top + Math.floor(n / area.width)
    if (loaded || land.revisions.has(tileKey(x, y))) initial.push(landChange(land, x, y))
  }
  // Слои зеркал — все непустые блоки: сохранение уже легло в TS.
  for (const mirror of mirrors) {
    if (!mirror.take) continue
    mirror.take()
    for (const [x, y] of mirror.all()) initial.push({ layer: mirror.layer, owner: mirror.owner, x, y, bytes: mirror.block(x, y) })
  }
  applyLayers(initial)
  // Мир: всё, что уже есть, — сущности по возрастанию, затем каждое хранилище в его порядке.
  const start: string[] = []
  for (const entity of world.all) {
    known.set(entity, new Map())
    start.push(`["spawn",${entity}]`)
  }
  start.push(`["next",${world.snapshot([]).next}]`)
  for (const [key, component] of COMPONENTS) {
    for (const [entity, data] of world.query(component)) {
      const json = JSON.stringify(data)
      known.get(entity)!.set(key, json)
      start.push(`["add",${entity},${JSON.stringify(key)},${json}]`)
    }
  }
  apply(start)
  world.journal = note
  if (rust.has('vision')) {
    for (const player of shadow.players()) {
      api.vision_open(id, player, sim.time.tick)
      opened.add(player)
      withBytes(api, new Uint8Array(Uint32Array.from(shadow.map(player)).buffer), (ptr, len) => api.vision_explore(id, player, ptr, len))
    }
    ;(sim as { vision: Vision }).vision = vision
  }
  if (MEMORY_SYSTEMS.some((name) => rust.has(name))) {
    // Память: исходные тайлы и знание игроков из сохранения, дальше — исходные тайлы по мере правок и забытое редактором.
    const { original } = sim.landMemory
    for (const [key, { tile }] of original) memoryOps.push(1, 0, 0, keyTile(key).x, keyTile(key).y, packTile(tile))
    ;[sim.landMemory, sim.paveMemory].forEach(({ known }, layer) => {
      for (const [player, tiles] of known) for (const [key, value] of tiles) memoryOps.push(3, layer, player, keyTile(key).x, keyTile(key).y, value)
    })
    const { set, delete: remove } = original
    original.set = (key, value) => {
      memoryOps.push(1, 0, 0, value.x, value.y, packTile(value.tile))
      return set.call(original, key, value)
    }
    original.delete = (key) => {
      memoryOps.push(2, 0, 0, keyTile(key).x, keyTile(key).y, 0)
      if (shadows) for (const known of shadows[0].known.values()) known.delete(key)
      return remove.call(original, key)
    }
  }
  if (rust.has('updateBuildMemory')) {
    for (const [player, known] of sim.buildMemory.known) for (const [entity, { x, y, type }] of known) buildOps.push(['know', player, entity, x, y, type])
    // Игрок начинает заново (forgetBuildings) — Rust и тень забывают его тоже.
    const { known } = sim.buildMemory
    const remove = known.delete
    known.delete = (player) => {
      buildOps.push(['forget', player])
      buildShadow?.known.delete(player)
      buildShadow?.watched.delete(player)
      return remove.call(known, player)
    }
  }
  if (rust.has('traces')) (sim as { traces: Traces }).traces = traces
  if (options.check) check()

  const advance = sim.advance
  sim.advance = (seconds) => {
    const ticks = advance(seconds)
    sync()
    if (options.check) check()
    return ticks
  }
  const destroy = sim.destroy
  sim.destroy = () => {
    destroy()
    world.journal = undefined
    finalizer.unregister(sim)
    api.sim_free(id)
  }
  return sim
}
