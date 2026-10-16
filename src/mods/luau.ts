/**
 * Машина Luau для модов: экземпляр luau.wasm (сборка — `bun tools/luau.ts`, привязка — luau.cpp). Своя у каждого мода:
 * память экземпляра общая только с его же скриптом, и сбой одного мода других не задевает. Без DOM и файлов: байты
 * wasm приносит тот, кто знает платформу (сервер читает файл, воркер — по адресу из сборки).
 *
 * Значения между скриптом и хостом — как в JSON (ModValue): так ими обменивается любая будущая машина, не только Luau.
 */

/** Значение, которое ходит между модом и хостом. Массив в Luau — таблица с ключами 1..n, словарь — со строковыми. */
export type ModValue = null | boolean | number | string | ModValue[] | { [key: string]: ModValue }

/** Чем кончился запуск скрипта: fault — машина сломалась (ловушка wasm, сбой распределителя) и больше не работает. */
export type RunStatus = 'ok' | 'error' | 'timeout' | 'memory' | 'fault'

export interface RunResult {
  status: RunStatus
  error?: string
}

/** Функция хоста: бросает Error — скрипт получает ошибку с её текстом в месте вызова. */
export type HostFunction = (args: ModValue[]) => ModValue | undefined

export interface MachineOptions {
  /** Предел памяти скрипта в байтах. */
  memory: number
  /** События, на которые можно подписаться kharos.on. */
  events: readonly string[]
  /** Функции таблицы kharos, по имени. */
  functions: Readonly<Record<string, HostFunction>>
  /** Куда идёт print. */
  log(text: string): void
}

/** Машина одного мода. */
export interface Machine {
  /** Компилирует и выполняет код мода: в нём он подписывается на события. budget — срок в мс. */
  load(name: string, source: string, budget: number): RunResult
  /** Зовёт подписчиков события; данные приходят им таблицей только для чтения. budget — срок на всех, в мс. */
  emit(event: string, payload: ModValue, budget: number): RunResult
  /**
   * Столбец номер slot (с 1) в bytes байт — buffer Luau, который получают системы. Вернёт вид на его байты: память
   * машины может вырасти, поэтому вид берётся заново перед каждой записью и чтением. Проба: в схеме API пока нет.
   */
  buffer(slot: number, bytes: number): () => Float32Array
  /** Проход системы мода name над count сущностями. budget — срок в мс. */
  system(name: string, count: number, budget: number): RunResult
  /** Сколько байт занял скрипт. */
  readonly memory: number
  /** Сломана ли машина: тогда каждый вызов — fault. */
  readonly broken: boolean
}

const STATUS: readonly RunStatus[] = ['ok', 'error', 'timeout', 'memory']

/** Типы значений из luau.cpp. */
const enum Kind {
  Nil = 0,
  Boolean = 1,
  Number = 2,
  String = 3,
  Table = 4,
}

/** Сколько имён помнит кэш байтов машины. */
const MAX_NAMES = 1024

/** Глубже не вкладываются таблицы в данных: защита от циклов и от переполнения стека. */
const MAX_DEPTH = 32

interface Exports {
  memory: WebAssembly.Memory
  km_scratch(size: number): number
  km_create(limit: number): number
  km_event(): void
  km_function(id: number): void
  km_seal(): void
  km_load(nameLength: number, size: number, budget: number): number
  km_emit(budget: number): number
  km_buffer(slot: number, size: number): number
  km_system(count: number, budget: number): number
  km_error(): number
  km_length(): number
  km_memory(): number
  km_top(): number
  km_pop(count: number): void
  km_type(index: number): number
  km_number(index: number): number
  km_boolean(index: number): number
  km_string(index: number): number
  km_next(index: number): number
  km_push_nil(): void
  km_push_boolean(value: number): void
  km_push_number(value: number): void
  km_push_string(size: number): void
  km_push_table(array: number, fields: number): void
  km_set_field(): void
  km_set_index(index: number): void
  km_freeze(): void
  _initialize?(): void
}

const encoder = new TextEncoder()
const decoder = new TextDecoder()

/** Компилирует luau.wasm; один модуль годится на сколько угодно машин. */
export const compileLuau = (bytes: Uint8Array<ArrayBuffer> | ArrayBuffer) => WebAssembly.compile(bytes)

/** Заводит машину Luau из скомпилированного luau.wasm. */
export function createMachine(module: WebAssembly.Module, options: MachineOptions): Machine {
  let api!: Exports
  let broken = false
  const names = Object.keys(options.functions)
  const functions = names.map((name) => options.functions[name])

  const bytes = () => new Uint8Array(api.memory.buffer)
  const read = (pointer: number, size: number) => decoder.decode(bytes().subarray(pointer, pointer + size))
  /**
   * Имена — полей, событий, функций — повторяются из раза в раз: их байты кэшируются. Кодировать короткую строку
   * заново на каждое поле каждого события в V8 дороже, чем отдать само событие.
   */
  const encodedNames = new Map<string, Uint8Array>()
  const nameBytes = (text: string) => {
    let bytes = encodedNames.get(text)
    if (!bytes) {
      bytes = encoder.encode(text)
      if (encodedNames.size < MAX_NAMES) encodedNames.set(text, bytes)
    }
    return bytes
  }
  /**
   * Кладёт строки подряд в общий буфер; zero — каждой нужен ноль на конце, cached — это имена (по умолчанию — когда
   * нужен ноль). Вернёт длины в байтах.
   */
  const write = (texts: readonly string[], zero: boolean, cached = zero) => {
    const encoded = texts.map((text) => (cached ? nameBytes(text) : encoder.encode(text)))
    const total = encoded.reduce((sum, item) => sum + item.length + (zero ? 1 : 0), 0)
    const pointer = api.km_scratch(Math.max(total, 1))
    if (!pointer) throw new Error('out of memory')
    const view = bytes()
    let at = pointer
    for (const item of encoded) {
      view.set(item, at)
      at += item.length
      if (zero) view[at++] = 0
    }
    return encoded.map((item) => item.length)
  }

  const readValue = (index: number, depth: number): ModValue => {
    switch (api.km_type(index)) {
      case Kind.Nil:
        return null
      case Kind.Boolean:
        return api.km_boolean(index) !== 0
      case Kind.Number:
        return api.km_number(index)
      case Kind.String:
        return read(api.km_string(index), api.km_length())
      case Kind.Table:
        return readTable(index, depth)
      default:
        throw new Error('unsupported value (only nil, boolean, number, string and table)')
    }
  }
  const readTable = (index: number, depth: number): ModValue => {
    if (depth >= MAX_DEPTH) throw new Error('table is nested too deep')
    const keys: (string | number)[] = []
    const values: ModValue[] = []
    api.km_push_nil()
    while (api.km_next(index)) {
      const top = api.km_top()
      const kind = api.km_type(top - 1)
      if (kind === Kind.Number) keys.push(api.km_number(top - 1))
      else if (kind === Kind.String) keys.push(read(api.km_string(top - 1), api.km_length()))
      else {
        api.km_pop(2)
        throw new Error('table keys must be strings or numbers')
      }
      try {
        values.push(readValue(top, depth + 1))
      } catch (error) {
        api.km_pop(2)
        throw error
      }
      api.km_pop(1)
    }
    // Массив — ключи ровно 1..n: так его отдаёт и пустая таблица.
    if (keys.every((key) => typeof key === 'number') && keys.every((_, i) => keys.includes(i + 1))) {
      const list: ModValue[] = []
      keys.forEach((key, i) => (list[(key as number) - 1] = values[i]))
      return list
    }
    const object: Record<string, ModValue> = {}
    keys.forEach((key, i) => (object[String(key)] = values[i]))
    return object
  }

  const pushValue = (value: ModValue | undefined, depth: number, freeze: boolean) => {
    if (value === null || value === undefined) api.km_push_nil()
    else if (typeof value === 'boolean') api.km_push_boolean(value ? 1 : 0)
    else if (typeof value === 'number') api.km_push_number(value)
    else if (typeof value === 'string') api.km_push_string(write([value], false)[0])
    else {
      if (depth >= MAX_DEPTH) throw new Error('table is nested too deep')
      if (Array.isArray(value)) {
        api.km_push_table(value.length, 0)
        value.forEach((item, i) => {
          pushValue(item, depth + 1, freeze)
          api.km_set_index(i + 1)
        })
      } else {
        const entries = Object.entries(value)
        api.km_push_table(0, entries.length)
        for (const [key, item] of entries) {
          pushValue(item, depth + 1, freeze)
          write([key], true)
          api.km_set_field()
        }
      }
      if (freeze) api.km_freeze()
    }
  }

  const call = (id: number, count: number) => {
    try {
      const args: ModValue[] = []
      for (let i = 1; i <= count; i++) args.push(readValue(i, 0))
      const result = functions[id](args)
      if (result === undefined) return 0
      pushValue(result, 0, false)
      return 1
    } catch (error) {
      const text = error instanceof Error ? error.message : String(error)
      return -(write([text], false)[0] + 1)
    }
  }

  const fail = (): never => {
    throw new Error('luau: unexpected call')
  }
  const env: Record<string, Record<string, (...args: never[]) => unknown>> = {
    kharos: {
      now: () => performance.now(),
      call,
      log: (pointer: number, size: number) => options.log(read(pointer, size)),
    },
  }
  // Остальное, что просит сборка Emscripten (WASI, рост памяти), — заглушки: файлов и часов у машины нет. Часы Luau
  // спрашивает, заводя начальные числа хешей и math.random: ноль делает машину предсказуемой — случайность модам даст
  // движок, с сидом мира.
  const stubs: Record<string, (...args: never[]) => unknown> = {
    emscripten_notify_memory_growth: () => {},
    clock_time_get: (_clock: number, _precision: bigint, pointer: number) => {
      new DataView(api.memory.buffer).setBigUint64(pointer, 0n, true)
      return 0
    },
  }
  for (const { module: from, name, kind } of WebAssembly.Module.imports(module)) {
    if (kind !== 'function' || env[from]?.[name]) continue
    env[from] ??= {}
    env[from][name] = stubs[name] ?? fail
  }

  const guard = (run: () => RunResult): RunResult => {
    if (broken) return { status: 'fault', error: 'machine is broken' }
    try {
      return run()
    } catch (error) {
      broken = true
      return { status: 'fault', error: error instanceof Error ? error.message : String(error) }
    }
  }
  const result = (code: number): RunResult => {
    if (code === 0) return { status: 'ok' }
    return { status: STATUS[code] ?? 'error', error: read(api.km_error(), api.km_length()) }
  }

  const instance = new WebAssembly.Instance(module, env as unknown as ConstructorParameters<typeof WebAssembly.Instance>[1])
  api = instance.exports as unknown as Exports
  api._initialize?.()
  if (!api.km_create(options.memory)) throw new Error('luau: no memory for the machine')
  for (const event of options.events) {
    write([event], true)
    api.km_event()
  }
  names.forEach((name, id) => {
    write([name], true)
    api.km_function(id)
  })
  api.km_seal()

  return {
    load: (name, source, budget) =>
      guard(() => {
        const [nameLength, size] = write([name, source], true, false)
        return result(api.km_load(nameLength, size, budget))
      }),
    emit: (event, payload, budget) =>
      guard(() => {
        pushValue(payload, 0, true)
        write([event], true)
        return result(api.km_emit(budget))
      }),
    buffer(slot, size) {
      const pointer = api.km_buffer(slot, size)
      if (!pointer) throw new Error('out of memory')
      return () => new Float32Array(api.memory.buffer, pointer, size >> 2)
    },
    system: (name, count, budget) =>
      guard(() => {
        write([name], true)
        return result(api.km_system(count, budget))
      }),
    get memory() {
      return broken ? 0 : api.km_memory()
    },
    get broken() {
      return broken
    },
  }
}
