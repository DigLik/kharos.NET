import type { Entity, World } from '@kharos/ecs'
import { tileKey } from '@kharos/shared/terrain'
import { Owner, Pave, Position } from './components'
import type { Sim } from './sim'

/**
 * Покрытие тайла, которое кладут строители. Фундамент: на нём здания строятся быстрее, на песке он вообще
 * разрешает стройку, а зону строительства не заменяет; ходят по нему чуть быстрее, чем по скале. Дорога: наземные по ней
 * едут быстрее, а положенная на болото — это мост, по которому болото не вязнет и не жжёт.
 * Покрытие — не здание: по нему не стреляют, оно не видит и не мешает ходить.
 */
export type PaveKind = 'foundation' | 'road'
export const PAVE_KINDS: readonly PaveKind[] = ['foundation', 'road']

/** Во сколько раз быстрее, чем по голой скале, наземные идут по дороге и мосту. */
export const ROAD_SPEED = 1.5
/** Во сколько раз быстрее, чем по голой скале, наземные идут по фундаменту: быстрее скалы, но медленнее дороги. */
export const FOUNDATION_SPEED = 1.2

/**
 * Код готового покрытия в слое: вид и хозяин одним числом; NO_PAVE — покрытия нет. Так слой — просто числа по тайлам:
 * их легко хранить, сравнивать и слать.
 */
export const NO_PAVE = 0
export const paveCode = (kind: PaveKind, owner: number) => owner * 2 + PAVE_KINDS.indexOf(kind) + 2
export const paveKindOf = (code: number): PaveKind => PAVE_KINDS[(code - 2) % 2]
export const paveOwnerOf = (code: number) => Math.floor((code - 2) / 2)

/** Тайл по ключу tileKey. */
export const keyTile = (key: number) => ({ x: (key % 65536) - 32768, y: Math.floor(key / 65536) - 32768 })

/**
 * Покрытие мира. Готовое — слой карты: тайл → код (paveCode). Его на большой базе десятки тысяч тайлов, и сущностями
 * оно отягощало бы каждый обход мира и рассылку. Работа над покрытием — укладка и разбор — сущность с Pave на тайле:
 * её строят и разбирают строители, как площадку. Достроенное уходит в слой (settlePave), разбираемое лежит в слое,
 * пока его не разберут до конца.
 */
export interface Paving {
  /** Работа на тайле: покрытие, которое кладут или разбирают, или undefined. */
  at(x: number, y: number): Entity | undefined
  /** Готовое покрытие тайла, см. paveCode; NO_PAVE — нет. Разбираемое ещё лежит и действует. */
  laid(x: number, y: number): number
  /** Кладёт в слой готовое покрытие. */
  lay(x: number, y: number, code: number): void
  /** Убирает готовое покрытие с тайла. */
  lift(x: number, y: number): void
  /** Убирает всё готовое покрытие: копия клиента получает его заново, см. replica.ts. */
  clear(): void
  /** Сколько тайлов готового покрытия. */
  readonly size: number
  /** Готовое покрытие: ключ tileKey и код. */
  tiles(): IterableIterator<[number, number]>
  /** Тайлы слоя, изменившиеся с прошлого вызова (ключи tileKey): по ним игроки узнают новое, см. paveMemory.ts. */
  drain(): number[]
  /**
   * Покрытие, над которым есть работа строителям: укладка и разбор. Строители ищут работу здесь, а не перебором мира.
   */
  unfinished(): Entity[]
  /** Покрытие снова стало работой — его начали разбирать (Pave.remove), см. removePave. */
  reopen(entity: Entity): void
  destroy(): void
}

export function createPaving(world: World): Paving {
  const work = new Map<number, Entity>()
  const layer = new Map<number, number>()
  let changed = new Set<number>()
  /** Где может быть работа: новое и разбираемое; достроенное выпадает, когда его тут застанут. */
  const pending = new Set<Entity>()
  const working = (pave: { done: boolean; remove: boolean }) => !pave.done || pave.remove
  const stop = world.observe([Position, Pave], (entity, position, pave) => {
    const key = tileKey(position.x, position.y)
    work.set(key, entity)
    if (working(pave)) pending.add(entity)
    return () => {
      if (work.get(key) === entity) work.delete(key)
      pending.delete(entity)
    }
  })
  return {
    at: (x, y) => (work.size ? work.get(tileKey(x, y)) : undefined),
    laid: (x, y) => (layer.size ? (layer.get(tileKey(x, y)) ?? NO_PAVE) : NO_PAVE),
    lay(x, y, code) {
      const key = tileKey(x, y)
      if (layer.get(key) === code) return
      layer.set(key, code)
      changed.add(key)
    },
    lift(x, y) {
      const key = tileKey(x, y)
      if (layer.delete(key)) changed.add(key)
    },
    clear() {
      for (const key of layer.keys()) changed.add(key)
      layer.clear()
    },
    get size() {
      return layer.size
    },
    tiles: () => layer.entries(),
    drain() {
      if (!changed.size) return []
      const keys = [...changed]
      changed = new Set()
      return keys
    },
    unfinished() {
      const found: Entity[] = []
      for (const entity of pending) {
        const pave = world.get(entity, Pave)
        if (pave && working(pave)) found.push(entity)
        else pending.delete(entity)
      }
      return found
    },
    reopen: (entity) => void (world.has(entity, Pave) && world.has(entity, Position) && pending.add(entity)),
    destroy: stop,
  }
}

/** Лежит ли на тайле готовое покрытие этого вида. */
export function isPaved(sim: Sim, kind: PaveKind, x: number, y: number) {
  const code = sim.paving.laid(x, y)
  return code !== NO_PAVE && paveKindOf(code) === kind
}

/** Дорога ли на тайле — готовая или та, что кладут: по ней ездят, строить на ней нельзя. */
export function isRoadway(sim: Sim, x: number, y: number) {
  if (isPaved(sim, 'road', x, y)) return true
  const entity = sim.paving.at(x, y)
  const pave = entity === undefined ? undefined : sim.world.get(entity, Pave)
  return pave?.kind === 'road' && !pave.remove
}

/** Хозяин готового покрытия на тайле; undefined — покрытия нет. */
export function paveOwnerAt(sim: Sim, x: number, y: number) {
  const code = sim.paving.laid(x, y)
  return code === NO_PAVE ? undefined : paveOwnerOf(code)
}

/**
 * Достроенное покрытие уходит в слой: сущность работы исчезает, тайл становится готовым покрытием её хозяина.
 * Так же — готовое покрытие, сохранённое сущностью до того, как появился слой.
 */
export function settlePave(sim: Sim, entity: Entity) {
  const { world } = sim
  const pave = world.get(entity, Pave)
  const position = world.get(entity, Position)
  if (!pave || !position) return
  sim.paving.lay(position.x, position.y, paveCode(pave.kind, world.get(entity, Owner)?.player ?? 0))
  world.destroy(entity)
}

/** Слой в сохранение: x, y и код подряд. */
export function savePaving(paving: Paving): number[] {
  const flat: number[] = []
  for (const [key, code] of paving.tiles()) {
    const { x, y } = keyTile(key)
    flat.push(x, y, code)
  }
  return flat
}

/** Кладёт в слой покрытие из сохранения или сообщения: x, y и код подряд; код NO_PAVE снимает покрытие. */
export function loadPaving(paving: Paving, flat: readonly number[]) {
  for (let i = 0; i + 2 < flat.length; i += 3) {
    if (flat[i + 2] === NO_PAVE) paving.lift(flat[i], flat[i + 1])
    else paving.lay(flat[i], flat[i + 1], flat[i + 2])
  }
}
