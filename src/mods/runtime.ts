import { MOD_API, type ApiField, type ApiType, type ModFunctions } from './api'
import { createMachine, type HostFunction, type Machine, type ModValue, type RunResult } from './luau'
import type { SimEvent } from '@kharos/sim-ts/sim'

/** Мод, как его приносит хост: имя (папка) и код. */
export interface ModSource {
  name: string
  source: string
}

export interface ModLimits {
  /** Предел памяти мода, байт. */
  memory: number
  /** Срок на загрузку мода, мс: его код выполняется сразу и подписывается на события. */
  load: number
  /** Срок на одно событие у мода, мс. */
  event: number
  /** Срок на все события тика у мода, мс: что не влезло, мод этого тика не узнает. */
  tick: number
  /** Сколько раз мод может не уложиться в срок или память, прежде чем его отключат. */
  strikes: number
}

export const DEFAULT_MOD_LIMITS: ModLimits = { memory: 16 << 20, load: 200, event: 5, tick: 10, strikes: 3 }

/** Что случилось с модом: ошибки, отключение, его print. */
export interface ModReport {
  mod: string
  kind: 'error' | 'timeout' | 'memory' | 'fault' | 'disabled' | 'log'
  text: string
}

export interface Mods {
  /** Отдаёт модам события последнего продвижения симуляции. */
  dispatch(events: readonly SimEvent[]): void
  /** Моды и их состояние. */
  readonly list: readonly { readonly name: string; readonly enabled: boolean; readonly memory: number }[]
}

interface Loaded {
  name: string
  machine: Machine
  enabled: boolean
  strikes: number
}

const EXPECTED: Record<ApiType, string> = { number: 'number', integer: 'integer', string: 'string', boolean: 'boolean', entity: 'entity id', player: 'player id' }

const typeName = (value: ModValue | undefined) => (value === null || value === undefined ? 'nil' : Array.isArray(value) ? 'table' : typeof value === 'object' ? 'table' : typeof value)

/** Подходит ли значение под тип поля схемы. */
function fits(field: ApiField, value: ModValue | undefined) {
  if (value === null || value === undefined) return field.optional === true
  switch (field.type) {
    case 'string':
      return typeof value === 'string'
    case 'boolean':
      return typeof value === 'boolean'
    case 'number':
      return typeof value === 'number' && Number.isFinite(value)
    default:
      return typeof value === 'number' && Number.isInteger(value)
  }
}

/**
 * Функции схемы — машине: каждая проверяет аргументы по схеме и зовёт то, что дал хост. Ошибки — в духе Luau:
 * «bad argument #1 to 'say' (string expected, got nil)».
 */
function bindFunctions(functions: ModFunctions): Record<string, HostFunction> {
  const bound: Record<string, HostFunction> = {}
  for (const [name, spec] of Object.entries(MOD_API.functions)) {
    const run = functions[name as keyof ModFunctions] as (...args: unknown[]) => ModValue | undefined
    bound[name] = (args) => {
      spec.params.forEach((param: ApiField & { name: string }, i: number) => {
        if (!fits(param, args[i])) throw new Error(`bad argument #${i + 1} to '${name}' (${EXPECTED[param.type]} expected, got ${typeName(args[i])})`)
      })
      const result = run(...args.slice(0, spec.params.length).map((value) => value ?? undefined))
      return result ?? undefined
    }
  }
  return bound
}

/**
 * Заводит моды: у каждого своя машина Luau из module. Код мода выполняется сразу; мод с ошибкой загрузки остаётся в
 * списке выключенным. report узнаёт обо всём, что моды делают не так, и об их print.
 */
export function createMods(module: WebAssembly.Module, sources: readonly ModSource[], functions: ModFunctions, report: (report: ModReport) => void, limits: ModLimits = DEFAULT_MOD_LIMITS): Mods {
  const bound = bindFunctions(functions)
  const events = Object.keys(MOD_API.events)
  const mods: Loaded[] = []

  /** Учитывает итог запуска; превысил срок или память слишком часто — мод выключается. */
  const settle = (mod: Loaded, result: RunResult) => {
    if (result.status === 'ok') return
    report({ mod: mod.name, kind: result.status, text: result.error ?? '' })
    if (result.status === 'error') return
    if (result.status === 'fault' || ++mod.strikes >= limits.strikes) {
      mod.enabled = false
      report({ mod: mod.name, kind: 'disabled', text: result.status === 'fault' ? 'machine broke' : `${mod.strikes} strikes` })
    }
  }

  for (const source of sources) {
    const machine = createMachine(module, { memory: limits.memory, events, functions: bound, log: (text) => report({ mod: source.name, kind: 'log', text }) })
    const mod: Loaded = { name: source.name, machine, enabled: true, strikes: 0 }
    mods.push(mod)
    const result = machine.load(source.name, source.source, limits.load)
    if (result.status !== 'ok') {
      settle(mod, result)
      if (mod.enabled) report({ mod: mod.name, kind: 'disabled', text: 'failed to load' })
      mod.enabled = false
    }
  }

  return {
    dispatch(list) {
      if (!list.length) return
      for (const mod of mods) {
        if (!mod.enabled) continue
        const began = performance.now()
        for (const { type, ...payload } of list) {
          const left = limits.tick - (performance.now() - began)
          if (left <= 0) {
            settle(mod, { status: 'timeout', error: 'tick budget exceeded' })
            break
          }
          const result = mod.machine.emit(type, payload, Math.min(limits.event, left))
          settle(mod, result)
          if (!mod.enabled) break
        }
      }
    },
    get list() {
      return mods.map((mod) => ({ name: mod.name, enabled: mod.enabled, memory: mod.machine.memory }))
    },
  }
}
