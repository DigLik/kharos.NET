import type { Component, Entity, JournalOp } from '@kharos/ecs'
import * as components from '@kharos/sim-ts/components'
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
  sim_new(): number
  sim_free(id: number): void
  sim_apply(id: number, ptr: number, len: number): number
  sim_journal(id: number): number
  sim_snapshot(id: number): number
  sim_order(id: number, ptr: number, len: number): number
  sim_systems(): number
  sim_run(id: number, ptr: number, len: number, tick: number, step: number): number
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
function withText<R>(api: Exports, text: string, call: (ptr: number, len: number) => R): R {
  const bytes = encoder.encode(text)
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
  const id = api.sim_new()
  const available = systemsOf(api)
  const rust = new Map([...available].filter(([name]) => !options.rust || options.rust.includes(name)))
  const sim = (options.create ?? createSim)(source, {
    systems: (list) => list.map(([name, system]) => (rust.has(name) ? [name, () => runRust(name, rust.get(name)!)] : [name, system])),
  })
  const { world } = sim
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

  /** Сверяет Rust с TS: снимок и порядок каждого хранилища. Бросает на первом расхождении. */
  const check = () => {
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

  // Начало: всё, что уже есть в TS-мире, — сущности по возрастанию, затем каждое хранилище в его порядке.
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
