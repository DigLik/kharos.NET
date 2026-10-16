import type { Component, Entity, JournalOp } from '@kharos/ecs'
import { CHUNK_SIZE, chunkRevision, getChunk, tileKey, type Land } from '@kharos/shared/terrain'
import * as components from '@kharos/sim-ts/components'
import { keyTile, type Paving } from '@kharos/sim-ts/paved'
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
  sim_new(ptr: number, len: number, left: number, top: number, width: number, height: number): number
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
  const instance = new WebAssembly.Instance(new WebAssembly.Module(bytes), {})
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
  /** Блоки, изменённые с прошлого вызова: x, y блока. */
  take(): [number, number][]
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
  const sim = (options.create ?? createSim)(source, {
    systems: (list) => list.map(([name, system]) => (rust.has(name) ? [name, () => runRust(name, rust.get(name)!)] : [name, system])),
  })
  const { world, land } = sim
  const area = land.area!
  const id = withText(api, JSON.stringify(land.config), (ptr, len) => api.sim_new(ptr, len, area.left, area.top, area.width, area.height))
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
  const mirrors: LayerMirror[] = [pavingMirror(sim.paving)]

  /** Досылает Rust изменённое в слоях: землю и блоки зеркал. */
  const syncLayers = () => {
    syncLand()
    const changes: LayerChange[] = []
    for (const mirror of mirrors) {
      for (const [x, y] of mirror.take()) changes.push({ layer: mirror.layer, owner: mirror.owner, x, y, bytes: mirror.block(x, y) })
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

  /** Запись операции для Rust и для known. Данные add не шлются: их догонит сверка, а «set» не меняет порядка. */
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
      case 'add':
        known.get(op[1])?.set(op[2], '{}')
        pending.push(`["add",${op[1]},${JSON.stringify(op[2])},{}]`)
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

  /** Система на Rust на своём месте в тике. */
  const runRust = (name: string, reads: ReadonlySet<string>) => {
    sync(reads)
    const code = withText(api, name, (ptr, len) => api.sim_run(id, ptr, len, sim.time.tick, sim.time.step))
    if (code !== 0) throw new Error(`sim.wasm: системы ${name} нет (${code})`)
    pull()
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
