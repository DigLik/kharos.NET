import { tileKey } from '@kharos/shared/terrain'
import { NO_PAVE, keyTile, paveOwnerOf } from './paved'
import { createRecall, updateRecall, type Recall, type RecallLayer } from './recall'
import type { Sim } from './sim'

/**
 * Что игроки знают о готовом покрытии (слой sim.paving). Покрытие у всех одно, но чужое игрок узнаёт, только увидев:
 * в тумане войны дорога остаётся для него такой, какой он её видел, — как здание-призрак или правка карты. Своё
 * и союзное покрытие он знает всегда. Это память запоминаемого слоя, см. recall.ts; общее — покрытия нет. Хост шлёт
 * игроку то, что он узнал, см. host.ts.
 */
export interface PaveMemory extends Recall {
  /** Тайлы слоя, изменившиеся с прошлого takePaveChanges: их видит редактор, которому видно всё. */
  changed: Set<number>
}

/** В сохранении: что знает каждый игрок — x, y и код подряд. */
export type PaveMemorySave = Record<string, number[]>

export function createPaveMemory(save?: PaveMemorySave): PaveMemory {
  const memory: PaveMemory = { ...createRecall(), changed: new Set() }
  for (const [player, flat] of Object.entries(save ?? {})) {
    const known = new Map<number, number>()
    for (let i = 0; i + 2 < flat.length; i += 3) known.set(tileKey(flat[i], flat[i + 1]), flat[i + 2])
    memory.known.set(Number(player), known)
  }
  return memory
}

export function savePaveMemory(memory: PaveMemory): PaveMemorySave {
  const save: PaveMemorySave = {}
  for (const [player, known] of memory.known) save[player] = flatOf(known, known.keys())
  return save
}

/** Тайлы keys со знанием known: x, y и код подряд. */
function flatOf(known: ReadonlyMap<number, number>, keys: Iterable<number>) {
  const flat: number[] = []
  for (const key of keys) {
    const { x, y } = keyTile(key)
    flat.push(x, y, known.get(key) ?? NO_PAVE)
  }
  return flat
}

/** Покрытие как запоминаемый слой: общее — покрытия нет, у покрытия есть хозяин. */
export const paveLayer = (sim: Sim): RecallLayer => ({
  actual: (x, y) => sim.paving.laid(x, y),
  common: () => NO_PAVE,
  owner: (code) => (code === NO_PAVE ? undefined : paveOwnerOf(code)),
  tiles: () => Array.from(sim.paving.tiles(), ([key]) => key),
})

/** Система тика, после обзора: игроки узнают изменения покрытия, см. recall.ts. */
export function updatePaveMemory(sim: Sim) {
  const memory = sim.paveMemory
  const changed = sim.paving.drain()
  for (const key of changed) memory.changed.add(key)
  updateRecall(sim, memory, paveLayer(sim), changed)
}

/** Что игрок узнал с прошлого вызова: x, y и код подряд, NO_PAVE — покрытия больше нет. */
export function takePaveLearned(memory: PaveMemory, player: number): number[] {
  const learned = memory.learned.get(player)
  if (!learned?.size) return []
  memory.learned.delete(player)
  return flatOf(memory.known.get(player)!, learned)
}

/** Всё, что игрок знает о покрытии: при подключении. */
export function knownPaving(memory: PaveMemory, player: number): number[] {
  const known = memory.known.get(player)
  return known ? flatOf(known, known.keys()) : []
}

/** Тайлы слоя, изменившиеся с прошлого вызова, какими они стали: для редактора, которому видно всё. */
export function takePaveChanges(sim: Sim): number[] {
  const { changed } = sim.paveMemory
  if (!changed.size) return []
  const flat: number[] = []
  for (const key of changed) {
    const { x, y } = keyTile(key)
    flat.push(x, y, sim.paving.laid(x, y))
  }
  changed.clear()
  return flat
}
