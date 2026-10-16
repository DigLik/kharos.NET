import { tileKey } from '@kharos/shared/terrain'
import { alliesIn } from './allies'
import { TRACE_CELL } from './traces'
import { NO_PAVE, keyTile, paveOwnerOf } from './paved'
import type { Sim } from './sim'

/**
 * Что игроки знают о готовом покрытии (слой sim.paving). Покрытие у всех одно, но чужое игрок узнаёт, только увидев:
 * в тумане войны дорога остаётся для него такой, какой он её видел, — как здание-призрак или правка карты. Своё
 * и союзное покрытие он знает всегда. Хост шлёт игроку то, что он узнал, см. host.ts.
 */
export interface PaveMemory {
  /** Что знает игрок: тайл (tileKey) → код покрытия; нет записи — знает, что покрытия нет. */
  known: Map<number, Map<number, number>>
  /**
   * Где знание игрока может расходиться со слоем: изменилось с тех пор, как он смотрел. Разложено по ячейкам
   * PAVE_CELL: сверяются только ячейки в его обзоре, а чужого покрытия вдали у него могут быть десятки тысяч тайлов.
   */
  stale: Map<number, Map<number, Set<number>>>
  /** Что игрок узнал с прошлого takePaveLearned. */
  learned: Map<number, Set<number>>
  /** Тайлы слоя, изменившиеся с прошлого takePaveChanges: их видит редактор, которому видно всё. */
  changed: Set<number>
}

/** В сохранении: что знает каждый игрок — x, y и код подряд. */
export type PaveMemorySave = Record<string, number[]>

/** Раз во сколько тиков игроки узнают увиденное: обзор и так пересчитывается не каждый тик, см. vision.ts. */
const LEARN_TICKS = 4
/** Сторона ячейки, по которым разложено несверенное, — как у следов: ячейки обзора хост считает той же стороны. */
const PAVE_CELL = TRACE_CELL

/** Ячейка PAVE_CELL тайла с ключом key, ключом tileKey от номера ячейки. */
function cellOf(key: number) {
  const { x, y } = keyTile(key)
  return tileKey(Math.floor(x / PAVE_CELL), Math.floor(y / PAVE_CELL))
}

/** Тайл ждёт сверки у игрока. */
function addStale(stale: Map<number, Set<number>>, key: number) {
  const cell = cellOf(key)
  const keys = stale.get(cell)
  if (keys) keys.add(key)
  else stale.set(cell, new Set([key]))
}

export function createPaveMemory(save?: PaveMemorySave): PaveMemory {
  const memory: PaveMemory = { known: new Map(), stale: new Map(), learned: new Map(), changed: new Set() }
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

/**
 * Заводит игроку память: знает он то, что записано, а сверить со слоем надо всё — и записанное, и лежащее. Новый
 * игрок не знает ничего и узнаёт своё сразу, чужое — увидев.
 */
function track(sim: Sim, player: number) {
  const memory = sim.paveMemory
  let stale = memory.stale.get(player)
  if (stale) return stale
  let known = memory.known.get(player)
  if (!known) memory.known.set(player, (known = new Map()))
  stale = new Map()
  for (const key of known.keys()) addStale(stale, key)
  for (const [key] of sim.paving.tiles()) addStale(stale, key)
  memory.stale.set(player, stale)
  return stale
}

/**
 * Система тика, после обзора: изменения слоя ждут сверки у каждого игрока. Своё и союзное игрок узнаёт в тот же тик:
 * достроенный тайл приходит клиенту вместе с концом работы над ним. Чужое — увидев; это сверяется раз в LEARN_TICKS.
 */
export function updatePaveMemory(sim: Sim) {
  const memory = sim.paveMemory
  const changed = sim.paving.drain()
  for (const key of changed) memory.changed.add(key)
  for (const stale of memory.stale.values()) for (const key of changed) addStale(stale, key)
  for (const player of sim.vision.players()) track(sim, player)
  const look = sim.time.tick % LEARN_TICKS === 0
  for (const [player, stale] of memory.stale) {
    if (!stale.size) continue
    // Своё и союзное — сразу, из изменившегося в этом тике.
    if (changed.length) learn(sim, player, stale, changed, false)
    if (!look) continue
    // Чужое — в ячейках, которые игрок сейчас видит.
    for (const cell of sim.vision.seenCells(player, PAVE_CELL)) {
      const keys = stale.get(cell)
      if (keys) learn(sim, player, stale, keys, true)
    }
  }
}

/** Игрок узнаёт тайлы keys из тех, что ждут у него сверки: свои и союзные всегда, чужие — если look и он их видит. */
function learn(sim: Sim, player: number, stale: Map<number, Set<number>>, keys: Iterable<number>, look: boolean) {
  const memory = sim.paveMemory
  const known = memory.known.get(player)!
  const team = alliesIn(sim.world, player)
  const ours = (code: number) => code !== NO_PAVE && (paveOwnerOf(code) === player || team.includes(paveOwnerOf(code)))
  const sees = look ? sim.vision.seer(player) : undefined
  let learned = memory.learned.get(player)
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
    const actual = sim.paving.laid(x, y)
    const was = known.get(key) ?? NO_PAVE
    if (actual === was) {
      settle(key)
      continue
    }
    if (!ours(actual) && !ours(was) && !sees?.(x, y)) continue
    if (actual === NO_PAVE) known.delete(key)
    else known.set(key, actual)
    settle(key)
    if (!learned) memory.learned.set(player, (learned = new Set()))
    learned.add(key)
  }
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
