import type { Entity, World } from '../ecs'
import { tileKey } from '../map/terrain'
import { Pave, Position } from './components'
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

/** Какое покрытие лежит на каком тайле. Обновляется само: следит за появлением и исчезновением покрытия. */
export interface Paving {
  /** Покрытие тайла, готовое или нет, или undefined. */
  at(x: number, y: number): Entity | undefined
  /**
   * Покрытие, над которым есть работа строителям: ещё не достроено или его разбирают. Готового на большой базе —
   * десятки тысяч тайлов, работы — единицы, и строители ищут её здесь, а не перебором всего покрытия.
   */
  unfinished(): Entity[]
  /** Готовое покрытие снова стало работой — его начали разбирать (Pave.remove), см. removePave. */
  reopen(entity: Entity): void
  destroy(): void
}

export function createPaving(world: World): Paving {
  const tiles = new Map<number, Entity>()
  /** Где может быть работа: новое и разбираемое; достроенное выпадает, когда его тут застанут. */
  const pending = new Set<Entity>()
  const working = (pave: { done: boolean; remove: boolean }) => !pave.done || pave.remove
  const stop = world.observe([Position, Pave], (entity, position, pave) => {
    const key = tileKey(position.x, position.y)
    tiles.set(key, entity)
    if (working(pave)) pending.add(entity)
    return () => {
      if (tiles.get(key) === entity) tiles.delete(key)
      pending.delete(entity)
    }
  })
  return {
    at: (x, y) => (tiles.size ? tiles.get(tileKey(x, y)) : undefined),
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
  const entity = sim.paving.at(x, y)
  if (entity === undefined) return false
  const pave = sim.world.get(entity, Pave)
  return !!pave && pave.done && pave.kind === kind
}

