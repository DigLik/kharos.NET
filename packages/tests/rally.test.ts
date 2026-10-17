import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Inventory, Path, Position, Producer, Site, UNITS, Unit, canPlace, createSim, type Sim } from '@kharos/sim-ts'
import { placeBuilding } from '@kharos/sim-ts/buildings'
import { addCredits, rewardsOf } from '@kharos/sim-ts/economy'
import { buildTicks } from '@kharos/sim-ts/production'

test('точка сбора: готовый юнит сам едет к ней; чужому зданию её не поставить', () => {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  addCredits(sim, 1, 10000)
  let spot: { x: number; y: number } | undefined
  for (let y = -60; y < 60 && !spot; y++) for (let x = -60; x < 60 && !spot; x++) if (canPlace(sim, 'command', x, y) && canPlace(sim, 'command', x + 3, y + 3)) spot = { x, y }
  const core = placeBuilding(sim.world, 'command', spot!.x, spot!.y, 1)
  // Чужой не поставит, неверный тайл не примется.
  sim.send(2, { type: 'rally', building: core, x: spot!.x + 8, y: spot!.y })
  sim.advance(1 / 20)
  expect(sim.world.get(core, Producer)!.rally).toEqual([])
  sim.send(1, { type: 'rally', building: core, x: spot!.x + 8, y: spot!.y + 1 })
  sim.send(1, { type: 'produce', producer: core, unit: 'builder' })
  sim.advance(1 / 20)
  expect(sim.world.get(core, Producer)!.rally).toEqual([spot!.x + 8, spot!.y + 1])
  // Массив строки обхода один на весь обход — собираем сразу, иначе в наборе окажется одна сущность.
  const before = new Set<Entity>()
  for (const [entity] of sim.world.query(Unit)) before.add(entity)
  let made: Entity | undefined
  let ticks = 1
  for (; ticks < 20 * 60 && made === undefined; ticks++) {
    sim.advance(1 / 20)
    for (const [entity] of sim.world.query(Unit)) if (!before.has(entity)) made = entity
  }
  expect(made).toBeDefined()
  // Готов ровно к сроку и сразу приносит награду за первый юнит.
  expect(ticks).toBe(buildTicks('builder', 1 / 20))
  expect(rewardsOf(sim, 1)).toEqual(['unit'])
  // Наземный едет к точке напрямую, без поиска пути.
  const path = sim.world.get(made!, Path)!
  expect(path.direct).toBe(true)
  expect(Math.hypot(path.goalX - (spot!.x + 8), path.goalY - (spot!.y + 1))).toBeLessThan(2)
})

/** Главное здание игрока 1 на свободной скале. */
function base() {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  let spot: { x: number; y: number } | undefined
  for (let y = -60; y < 60 && !spot; y++) for (let x = -60; x < 60 && !spot; x++) if (canPlace(sim, 'command', x, y) && canPlace(sim, 'command', x + 3, y + 3)) spot = { x, y }
  return { sim, spot: spot!, core: placeBuilding(sim.world, 'command', spot!.x, spot!.y, 1) }
}

/** Первый новый юнит за seconds секунд. */
function produced(sim: Sim, seconds: number) {
  const before = new Set<Entity>()
  for (const [entity] of sim.world.query(Unit)) before.add(entity)
  for (let i = 0; i < seconds * 20; i++) {
    sim.advance(1 / 20)
    for (const [entity] of sim.world.query(Unit)) if (!before.has(entity)) return entity
  }
  return undefined
}

test('летающий едет точно в точку сбора, даже над зданием; наземный — на свободный тайл рядом', () => {
  const { sim, spot, core } = base()
  const middle = { x: spot.x + 1, y: spot.y + 1 }
  sim.world.get(core, Producer)!.rally = [middle.x, middle.y]
  sim.world.get(core, Producer)!.queue = ['drone', 'builder']
  sim.world.get(core, Inventory)!.items = { ...UNITS.drone.materials }
  const drone = produced(sim, UNITS.drone.buildTime + 1)!
  expect(sim.world.get(drone, Unit)!.type).toBe('drone')
  expect(sim.world.get(drone, Path)).toMatchObject({ goalX: middle.x, goalY: middle.y })
  const builder = produced(sim, UNITS.builder.buildTime + 1)!
  // Тайл сбора наземного может оказаться тем, где он вышел: тогда ехать некуда.
  const path = sim.world.get(builder, Path)
  const goal = path ? { x: path.goalX, y: path.goalY } : { x: Math.floor(sim.world.get(builder, Position)!.x), y: Math.floor(sim.world.get(builder, Position)!.y) }
  expect(goal.x >= spot.x && goal.x < spot.x + 3 && goal.y >= spot.y && goal.y < spot.y + 3).toBe(false)
})

test('производитель вне зоны энергии, которому она нужна, не производит', () => {
  const { sim, spot } = base()
  let far: { x: number; y: number } | undefined
  for (let y = spot.y - 60; y < spot.y + 60 && !far; y++) for (let x = spot.x + 40; x < spot.x + 100 && !far; x++) if (canPlace(sim, 'barracks', x, y)) far = { x, y }
  const barracks = placeBuilding(sim.world, 'barracks', far!.x, far!.y, 1)
  sim.world.get(barracks, Producer)!.queue = ['infantry']
  expect(produced(sim, UNITS.infantry.buildTime + 2)).toBeUndefined()
  expect(sim.world.get(barracks, Producer)).toMatchObject({ queue: ['infantry'], progress: 0 })
})

test('здание под разбором не производит, хотя очередь у него остаётся', () => {
  const { sim, core } = base()
  sim.world.get(core, Producer)!.queue = ['builder']
  sim.world.add(core, Site({ type: 'command', progress: 1000, demolish: true }))
  expect(produced(sim, UNITS.builder.buildTime + 2)).toBeUndefined()
  expect(sim.world.get(core, Producer)).toMatchObject({ queue: ['builder'], progress: 0 })
})
