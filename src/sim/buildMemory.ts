import type { Entity } from '../ecs'
import { BUILDINGS, type BuildingType } from './buildings'
import { Building, Owner, Position, SAVED } from './components'
import type { Sim } from './sim'
import { TRACE_CELL } from './traces'

/**
 * Какими игроки помнят чужие здания. Здание, ушедшее в туман войны, остаётся для игрока на карте таким, каким он видел
 * его последний раз, — призраком, — пока его место снова не окажется в обзоре. Подключённому это показывает его
 * клиент (memory в replica.ts), но клиент помнит только то, что видел сам с момента входа. Здесь то же помнит
 * симуляция — за всех, в сети они или нет, — и хост отдаёт это при входе, см. host.ts: иначе после переподключения
 * разведанные базы пропадали бы с карты.
 */
export interface BuildMemory {
  /** Что помнит игрок: номер сущности → здание, каким оно было, когда ушло из его обзора. */
  known: Map<number, Map<number, Remembered>>
  /** Чужие здания, которые игрок видел при прошлой сверке: ушедшее из обзора запоминается. В сохранение не идёт. */
  watched: Map<number, Set<number>>
}

/**
 * Запомненное здание: где оно, какого вида и parts — JSON его компонентов из SAVED по имени, целиком. Что из них
 * чужому знать не положено, отсекает хост, когда шлёт, см. rememberedWire в wire.ts. Запись не меняется, и у игроков,
 * потерявших здание из виду разом, она общая.
 */
export interface Remembered {
  x: number
  y: number
  type: BuildingType
  parts: string
}

/**
 * В сохранении: shapes — запомненные здания (Remembered.parts) без повторов: одно и то же здание обычно помнят
 * одинаково; known — по игроку номер сущности и место её записи в shapes подряд.
 */
export interface BuildMemorySave {
  shapes: string[]
  known: Record<string, number[]>
}

/** Раз во сколько тиков игроки сверяют увиденное: обзор и так пересчитывается не каждый тик, см. vision.ts. */
const LEARN_TICKS = 4

export function createBuildMemory(save?: BuildMemorySave): BuildMemory {
  const memory: BuildMemory = { known: new Map(), watched: new Map() }
  if (!save) return memory
  const shapes = save.shapes.map((parts): Remembered => {
    const data = JSON.parse(parts) as { Position: { x: number; y: number }; Building: { type: BuildingType } }
    return { ...data.Position, type: data.Building.type, parts }
  })
  for (const [player, flat] of Object.entries(save.known)) {
    const known = new Map<number, Remembered>()
    for (let i = 0; i + 1 < flat.length; i += 2) if (shapes[flat[i + 1]]) known.set(flat[i], shapes[flat[i + 1]])
    memory.known.set(Number(player), known)
  }
  return memory
}

export function saveBuildMemory(memory: BuildMemory): BuildMemorySave {
  const places = new Map<string, number>()
  const save: BuildMemorySave = { shapes: [], known: {} }
  for (const [player, known] of memory.known) {
    if (!known.size) continue
    const flat: number[] = []
    for (const [id, { parts }] of known) {
      let place = places.get(parts)
      if (place === undefined) places.set(parts, (place = save.shapes.push(parts) - 1))
      flat.push(id, place)
    }
    save.known[player] = flat
  }
  return save
}

/** Видит ли игрок хоть один тайл основания здания вида type с левым верхним углом в at. */
function inSight(sees: (x: number, y: number) => boolean, at: { x: number; y: number }, type: BuildingType) {
  const { width, height } = BUILDINGS[type]
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) if (sees(at.x + x, at.y + y)) return true
  return false
}

/** Здание как оно есть сейчас. */
function snapshot(sim: Sim, entity: Entity): Remembered {
  const data: Record<string, object> = {}
  for (const component of SAVED) {
    const value = sim.world.get(entity, component)
    if (value !== undefined) data[component.key] = value
  }
  const { x, y } = sim.world.get(entity, Position)!
  return { x, y, type: sim.world.get(entity, Building)!.type, parts: JSON.stringify(data) }
}

/**
 * Система тика, после обзора: что ушло из обзора игрока, запоминается; что снова в обзоре, помнить незачем; чего
 * больше нет, а место его в обзоре — забывается: игрок видит, что здание снесли. Здания ищутся только в ячейках
 * обзора: на карте их тысячи.
 */
export function updateBuildMemory(sim: Sim) {
  // Без тумана всё видно всегда: помнить нечего.
  if (sim.options.fog === false || sim.time.tick % LEARN_TICKS !== 0) return
  const { world, vision, occupancy } = sim
  const memory = sim.buildMemory
  /** Снятое в этой сверке: игрокам, потерявшим здание из виду разом, — одна запись. */
  const taken = new Map<number, Remembered>()
  for (const player of vision.players()) {
    const sees = vision.seer(player)
    const seen = new Set<number>()
    for (const cell of vision.seenCells(player, TRACE_CELL)) {
      for (const entity of occupancy.inCell(cell) ?? []) {
        if (seen.has(entity) || world.get(entity, Owner)?.player === player) continue
        if (inSight(sees, world.get(entity, Position)!, world.get(entity, Building)!.type)) seen.add(entity)
      }
    }
    let known = memory.known.get(player)
    // Снесённое на глазах не запоминается.
    for (const id of memory.watched.get(player) ?? []) {
      if (seen.has(id) || !world.has(id as Entity, Building)) continue
      if (!known) memory.known.set(player, (known = new Map()))
      let record = taken.get(id)
      if (!record) taken.set(id, (record = snapshot(sim, id as Entity)))
      known.set(id, record)
    }
    for (const [id, data] of known ?? []) {
      // Здания не двигаются: живое в обзоре — в seen, и проверять место нужно только у тех, которых больше нет.
      if (seen.has(id)) known!.delete(id)
      else if (!world.has(id as Entity, Building) && inSight(sees, data, data.type)) known!.delete(id)
    }
    memory.watched.set(player, seen)
  }
}

/** Игрок начинает заново: что он помнил, больше ничего не значит. */
export function forgetBuildings(memory: BuildMemory, player: number) {
  memory.known.delete(player)
  memory.watched.delete(player)
}
