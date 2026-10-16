import { tileKey } from '@kharos/shared/terrain'
import { alliesIn } from './allies'
import { keyTile } from './paved'
import type { Sim } from './sim'
import { TRACE_CELL } from './traces'

/**
 * Память запоминаемого слоя мира (docs/layers.md, §2): слой у всех один, но его изменения игрок узнаёт, только увидев,
 * — в тумане войны тайл остаётся для него таким, каким он его видел последним. Своё и союзное он знает всегда. Так
 * помнятся правки земли (landMemory.ts) и покрытие (paveMemory.ts): одно правило на все такие слои.
 *
 * Значение тайла — одно число (у земли — хранимые байты тайла, у покрытия — его код). Помнится только расхождение с
 * общим (RecallLayer.common): тем, что игрок знает, ничего не видев.
 */
export interface Recall {
  /** Что знает игрок иначе, чем общее: тайл (tileKey) → значение. */
  known: Map<number, Map<number, number>>
  /**
   * Где знание игрока может расходиться со слоем: изменилось с тех пор, как он смотрел. Разложено по ячейкам
   * RECALL_CELL: сверяются только ячейки в его обзоре, а изменённого вдали у него могут быть десятки тысяч тайлов.
   */
  stale: Map<number, Map<number, Set<number>>>
  /** Что игрок узнал с прошлого раза: по ним хост шлёт ему изменения, см. host.ts. */
  learned: Map<number, Set<number>>
}

/** Запоминаемый слой: как его читает память. */
export interface RecallLayer {
  /** Значение тайла сейчас. */
  actual(x: number, y: number): number
  /** Что знает о тайле игрок, ничего о нём не узнававший: исходное у правок земли, «ничего» у покрытия. */
  common(key: number, actual: number): number
  /** Чьё это значение — тогда хозяин и союзники знают его сразу; undefined — ничьё. */
  owner?(value: number): number | undefined
  /** Тайлы, где слой может расходиться с общим: их сверяет новый игрок. */
  tiles(): Iterable<number>
}

/** Раз во сколько тиков игроки узнают увиденное: обзор и так пересчитывается раз в столько, см. vision.ts. */
export const LEARN_TICKS = 4
/** Сторона ячейки, по которым разложено несверенное, — как у следов: ячейки обзора хост считает той же стороны. */
export const RECALL_CELL = TRACE_CELL

export const createRecall = (known = new Map<number, Map<number, number>>()): Recall => ({ known, stale: new Map(), learned: new Map() })

/** Ячейка RECALL_CELL тайла с ключом key, ключом tileKey от номера ячейки. */
function cellOf(key: number) {
  const { x, y } = keyTile(key)
  return tileKey(Math.floor(x / RECALL_CELL), Math.floor(y / RECALL_CELL))
}

/** Тайл ждёт сверки у игрока. */
function addStale(stale: Map<number, Set<number>>, key: number) {
  const cell = cellOf(key)
  const keys = stale.get(cell)
  if (keys) keys.add(key)
  else stale.set(cell, new Set([key]))
}

/** Что знает игрок о тайле. */
export const knownValue = (recall: Recall, layer: RecallLayer, player: number, key: number) => {
  const value = recall.known.get(player)?.get(key)
  if (value !== undefined) return value
  const { x, y } = keyTile(key)
  return layer.common(key, layer.actual(x, y))
}

/**
 * Заводит игроку сверку: сверить со слоем надо и то, что он знает, и всё, где слой расходится с общим. Новый игрок не
 * знает ничего и узнаёт своё сразу, чужое — увидев.
 */
function track(recall: Recall, layer: RecallLayer, player: number) {
  let stale = recall.stale.get(player)
  if (stale) return stale
  let known = recall.known.get(player)
  if (!known) recall.known.set(player, (known = new Map()))
  stale = new Map()
  for (const key of known.keys()) addStale(stale, key)
  for (const key of layer.tiles()) addStale(stale, key)
  recall.stale.set(player, stale)
  return stale
}

/**
 * Сверка после обзора: changed — тайлы слоя, изменившиеся с прошлого раза, — ждут сверки у каждого игрока. Своё и
 * союзное игрок узнаёт в тот же тик: достроенное приходит клиенту вместе с концом работы. Чужое — увидев; это
 * сверяется раз в LEARN_TICKS.
 */
export function updateRecall(sim: Sim, recall: Recall, layer: RecallLayer, changed: readonly number[]) {
  for (const stale of recall.stale.values()) for (const key of changed) addStale(stale, key)
  for (const player of sim.vision.players()) track(recall, layer, player)
  const look = sim.time.tick % LEARN_TICKS === 0
  for (const [player, stale] of recall.stale) {
    if (!stale.size) continue
    if (changed.length) learn(sim, recall, layer, player, stale, changed, false)
    if (!look) continue
    for (const cell of sim.vision.seenCells(player, RECALL_CELL)) {
      const keys = stale.get(cell)
      if (keys) learn(sim, recall, layer, player, stale, keys, true)
    }
  }
}

/** Игрок узнаёт тайлы keys из тех, что ждут у него сверки: свои и союзные всегда, чужие — если look и он их видит. */
function learn(sim: Sim, recall: Recall, layer: RecallLayer, player: number, stale: Map<number, Set<number>>, keys: Iterable<number>, look: boolean) {
  const known = recall.known.get(player)!
  const team = layer.owner ? alliesIn(sim.world, player) : []
  const ours = (value: number) => {
    const owner = layer.owner?.(value)
    return owner !== undefined && (owner === player || team.includes(owner))
  }
  const sees = look ? sim.vision.seer(player) : undefined
  let learned = recall.learned.get(player)
  /** Тайл сверен: из несверенного уходит, а пустая ячейка — вместе с ним. */
  const settle = (key: number) => {
    const cell = cellOf(key)
    const keys = stale.get(cell)
    if (!keys) return
    keys.delete(key)
    if (!keys.size) stale.delete(cell)
  }
  for (const key of keys) {
    if (!stale.get(cellOf(key))?.has(key)) continue
    const { x, y } = keyTile(key)
    const actual = layer.actual(x, y)
    const common = layer.common(key, actual)
    const was = known.get(key) ?? common
    if (actual === was) {
      settle(key)
      continue
    }
    if (!ours(actual) && !ours(was) && !sees?.(x, y)) continue
    if (actual === common) known.delete(key)
    else known.set(key, actual)
    settle(key)
    if (!learned) recall.learned.set(player, (learned = new Set()))
    learned.add(key)
  }
}
