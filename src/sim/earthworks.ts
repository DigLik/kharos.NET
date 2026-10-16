import type { Entity } from '@kharos/ecs'
import { CLIFF_BIT, DUNE_SHIFT, Terrain, batchLand, peakOf, tileBytes, tileKey, type Dunes } from '../map/terrain'
import { isOwn } from './common'
import { Dig, Owner, Position } from './components'
import { addCredits, pay } from './economy'
import { editTile } from './landMemory'
import type { Sim } from './sim'
import { hasResearch, type Topic } from './topics'
import { inForeignZone } from './zones'

/**
 * Землеройные работы: строители срывают горы и сглаживают обрывы. Гору срывают целиком — она рисуется от вершины,
 * и кусок горы остался бы обрубком; на её месте — скала того же яруса, что вокруг. Обрыв сглаживают по тайлу кромки:
 * кромка становится пологой, и подножие под ней — проездом для техники. Работа — сущность с Dig, её строят строители,
 * как площадку; плата — сразу при заказе. Правка карты, как любая, видна другим, только когда они её увидят, см.
 * landMemory.ts. Открывает их исследование earthworks.
 */
export type DigKind = 'peak' | 'cliff'
export const DIG_KINDS: readonly DigKind[] = ['peak', 'cliff']

/** Цена срытия горы за каждый её тайл. */
export const PEAK_COST = 40
/** Цена сглаживания тайла кромки обрыва. */
export const CLIFF_COST = 25
/** Какое исследование открывает землеройные работы. */
export const DIG_RESEARCH: Topic = 'earthworks'

/** Соседи тайла по восьми направлениям. */
const AROUND = [[-1, -1], [0, -1], [1, -1], [-1, 0], [1, 0], [-1, 1], [0, 1], [1, 1]] as const

const inside = (sim: Sim, x: number, y: number) => x >= sim.bounds.left && y >= sim.bounds.top && x < sim.bounds.right && y < sim.bounds.bottom

/** Ярус тайла; у гор — -1: их байт рельефа занят вершиной. */
function tierAt(sim: Sim, x: number, y: number) {
  const [terrain, , , relief] = tileBytes(sim.land, x, y)
  return terrain === Terrain.Mountain ? -1 : relief & 3
}

/** Тайлы горы с вершиной peak: x и y подряд. */
export function peakTiles(sim: Sim, peak: { x: number; y: number; radius: number }): number[] {
  const tiles: number[] = []
  const span = Math.ceil(peak.radius + 1)
  for (let y = Math.floor(peak.y) - span; y <= Math.floor(peak.y) + span; y++) {
    for (let x = Math.floor(peak.x) - span; x <= Math.floor(peak.x) + span; x++) {
      if (!inside(sim, x, y)) continue
      const other = peakOf(sim.land, x, y)
      if (other && other.x === peak.x && other.y === peak.y) tiles.push(x, y)
    }
  }
  return tiles
}

/** Кромка ли обрыва тайл: обрывистая, а рядом есть тайл ниже — подножие под ней. */
export function isCliffEdge(sim: Sim, x: number, y: number) {
  if (!inside(sim, x, y)) return false
  const [terrain, , , relief] = tileBytes(sim.land, x, y)
  if (terrain === Terrain.Mountain || !(relief & CLIFF_BIT)) return false
  const tier = relief & 3
  return AROUND.some(([dx, dy]) => {
    const other = tierAt(sim, x + dx, y + dy)
    return other >= 0 && other < tier
  })
}

/**
 * Тайлы кромки, которые сгладит рамка tiles (x и y подряд): кромки в рамке и кромки над подножиями в ней — стенку
 * обрыва рисуют на подножии, и рамку игрок тянет по ней. Ключи tileKey, по порядку.
 */
export function cliffEdgesIn(sim: Sim, tiles: readonly number[]): number[] {
  const found = new Set<number>()
  for (let i = 0; i + 1 < tiles.length; i += 2) {
    const x = tiles[i]
    const y = tiles[i + 1]
    if (isCliffEdge(sim, x, y)) found.add(tileKey(x, y))
    const tier = tierAt(sim, x, y)
    for (const [dx, dy] of AROUND) if (tierAt(sim, x + dx, y + dy) > tier && isCliffEdge(sim, x + dx, y + dy)) found.add(tileKey(x + dx, y + dy))
  }
  return [...found]
}

/** Работа, которая уже идёт на тайле: кромку сглаживают или гору, частью которой он стоит, срывают. */
export function digAt(sim: Sim, x: number, y: number): Entity | undefined {
  const peak = peakOf(sim.land, x, y)
  for (const [entity, dig, position] of sim.world.query(Dig, Position)) {
    if (dig.kind === 'cliff' ? position.x === x && position.y === y : !!peak && dig.peakX === peak.x && dig.peakY === peak.y) return entity
  }
  return undefined
}

/** Можно ли игроку начать работу kind на тайле: исследовано, не в чужой зоне, есть что делать и это ещё не делают. */
export function canDig(sim: Sim, player: number, kind: DigKind, x: number, y: number) {
  if (!Number.isInteger(x) || !Number.isInteger(y) || !DIG_KINDS.includes(kind) || !inside(sim, x, y)) return false
  if (!hasResearch(sim, player, DIG_RESEARCH)) return false
  if (kind === 'peak' ? !peakOf(sim.land, x, y) : !isCliffEdge(sim, x, y)) return false
  if (digAt(sim, x, y) !== undefined) return false
  return !inForeignZone(sim, player, x, y, 1, 1)
}

/** Работы, которые закажет рамка tiles: на каждую гору — одна, на тайл кромки — своя; место, вершина и цена. */
export function digsIn(sim: Sim, player: number, kind: DigKind, tiles: readonly number[]) {
  const jobs: { x: number; y: number; peakX: number; peakY: number; cost: number }[] = []
  if (kind === 'cliff') {
    for (const key of cliffEdgesIn(sim, tiles)) {
      const x = (key % 65536) - 32768
      const y = Math.floor(key / 65536) - 32768
      if (canDig(sim, player, kind, x, y)) jobs.push({ x, y, peakX: 0, peakY: 0, cost: CLIFF_COST })
    }
    return jobs
  }
  const seen = new Set<string>()
  for (let i = 0; i + 1 < tiles.length; i += 2) {
    const peak = peakOf(sim.land, tiles[i], tiles[i + 1])
    if (!peak || seen.has(`${peak.x}:${peak.y}`) || !canDig(sim, player, kind, tiles[i], tiles[i + 1])) continue
    seen.add(`${peak.x}:${peak.y}`)
    jobs.push({ x: Math.floor(peak.x), y: Math.floor(peak.y), peakX: peak.x, peakY: peak.y, cost: (peakTiles(sim, peak).length / 2) * PEAK_COST })
  }
  return jobs
}

/**
 * Заказывает землеройные работы в рамке tiles и возвращает первую из них (её сразу дают строителям); undefined — не
 * заказано ничего. Платят за каждую сразу; на что не хватило кредитов — не заказывают.
 */
export function orderDigs(sim: Sim, player: number, kind: DigKind, tiles: readonly number[]): Entity | undefined {
  let first: Entity | undefined
  for (const job of digsIn(sim, player, kind, tiles)) {
    if (!pay(sim, player, job.cost)) continue
    const entity = sim.world.spawn(Position({ x: job.x, y: job.y }), Dig({ kind, cost: job.cost, peakX: job.peakX, peakY: job.peakY }), Owner({ player }))
    first ??= entity
  }
  return first
}

/** Отменяет свою работу на тайле: кредиты возвращаются целиком, как у не начатой площадки. Возвращает, было ли что отменять. */
export function cancelDig(sim: Sim, player: number, x: number, y: number) {
  const entity = digAt(sim, x, y)
  if (entity === undefined || !isOwn(sim, player, entity)) return false
  addCredits(sim, player, sim.world.get(entity, Dig)!.cost)
  sim.world.destroy(entity)
  return true
}

/** Над какими тайлами идёт работа: прямоугольник для строителей; undefined — делать уже нечего (гору сорвали). */
export function digArea(sim: Sim, entity: Entity): { x: number; y: number; width: number; height: number } | undefined {
  const dig = sim.world.get(entity, Dig)
  const position = sim.world.get(entity, Position)
  if (!dig || !position) return undefined
  if (dig.kind === 'cliff') return isCliffEdge(sim, position.x, position.y) ? { x: position.x, y: position.y, width: 1, height: 1 } : undefined
  const peak = peakOf(sim.land, position.x, position.y)
  if (!peak || peak.x !== dig.peakX || peak.y !== dig.peakY) return undefined
  const tiles = peakTiles(sim, peak)
  let left = Infinity
  let top = Infinity
  let right = -Infinity
  let bottom = -Infinity
  for (let i = 0; i < tiles.length; i += 2) {
    left = Math.min(left, tiles[i])
    top = Math.min(top, tiles[i + 1])
    right = Math.max(right, tiles[i] + 1)
    bottom = Math.max(bottom, tiles[i + 1] + 1)
  }
  return { x: left, y: top, width: right - left, height: bottom - top }
}

/** Самый частый ярус соседей горы: скала на её месте ложится вровень с плато вокруг. */
function tierAround(sim: Sim, tiles: readonly number[]) {
  const counts = [0, 0, 0, 0]
  const own = new Set<number>()
  for (let i = 0; i < tiles.length; i += 2) own.add(tileKey(tiles[i], tiles[i + 1]))
  for (let i = 0; i < tiles.length; i += 2) {
    for (const [dx, dy] of AROUND) {
      const x = tiles[i] + dx
      const y = tiles[i + 1] + dy
      if (own.has(tileKey(x, y)) || !inside(sim, x, y)) continue
      const tier = tierAt(sim, x, y)
      if (tier >= 0) counts[tier]++
    }
  }
  let best = 1
  for (let tier = 0; tier < counts.length; tier++) if (counts[tier] > counts[best]) best = tier
  return best
}

/** Работа сделана: гора становится скалой, кромка — пологой. Сущность работы исчезает. */
export function finishDig(sim: Sim, entity: Entity) {
  const { world } = sim
  const dig = world.get(entity, Dig)
  const position = world.get(entity, Position)
  if (dig && position && digArea(sim, entity)) {
    batchLand(sim.land, () => {
      if (dig.kind === 'cliff') {
        const [terrain, biome, , relief] = tileBytes(sim.land, position.x, position.y)
        editTile(sim, position.x, position.y, { terrain: terrain as Terrain, biome, tier: relief & 3, cliff: false, dunes: ((relief >> DUNE_SHIFT) & 3) as Dunes })
        return
      }
      const tiles = peakTiles(sim, peakOf(sim.land, position.x, position.y)!)
      const tier = tierAround(sim, tiles)
      for (let i = 0; i < tiles.length; i += 2) {
        const biome = tileBytes(sim.land, tiles[i], tiles[i + 1])[1]
        editTile(sim, tiles[i], tiles[i + 1], { terrain: Terrain.Rock, biome, tier, cliff: false })
      }
    })
  }
  world.destroy(entity)
}
