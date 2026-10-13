import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { canPlace, createSim, Path, Position } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { orderGroupMove, spawnUnit } from '../src/sim/units'

test('большая армия сразу трогается, каждый юнит — к своему месту, и доходит', () => {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 1024 })
  const units: number[] = []
  for (let i = 0; i < 400; i++) units.push(spawnUnit(sim, i % 2 ? 'infantry' : 'buggy', 1, -40 + (i % 20), -10 + Math.floor(i / 20)))
  orderGroupMove(sim, units, 40, 0)
  const goals = new Set<string>()
  for (const entity of units) {
    const path = sim.world.get(entity, Path)
    if (!path) continue
    goals.add(`${path.goalX},${path.goalY}`)
    // Пути приказ не ищет: юниты едут напрямую.
    expect(path.direct).toBe(true)
  }
  // Почти у всех есть путь, и места не повторяются: армии хватает колец вокруг цели.
  expect(goals.size).toBeGreaterThan(380)
  for (let i = 0; i < 20 * 60; i++) sim.advance(1 / 20)
  const arrived = units.filter((entity) => Math.hypot(sim.world.get(entity, Position)!.x - 40, sim.world.get(entity, Position)!.y) < 30)
  expect(arrived.length).toBeGreaterThan(360)
})

test('упёршись в стену, юнит сразу ищет путь, а не скользит вдоль неё до угла', () => {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false, protection: 0 } })
  let at: { x: number; y: number } | undefined
  find: for (let y = -150; y < 150; y++) {
    for (let x = -150; x < 150; x++) {
      let flat = true
      for (let dy = -10; dy <= 10 && flat; dy++) for (let dx = -25; dx <= 25 && flat; dx++) flat = canPlace(sim, 'wall', x + dx, y + dy)
      if (flat) {
        at = { x, y }
        break find
      }
    }
  }
  if (!at) throw new Error('Не нашлось ровного места под стену')
  const { x, y } = at
  // Стена в 41 секцию поперёк дороги; цель за ней, наискось — юнит упирается и скользит к правому краю.
  for (let dx = -20; dx <= 20; dx++) placeBuilding(sim.world, 'wall', x + dx, y, 1)
  const buggy = spawnUnit(sim, 'buggy', 1, x - 3, y - 6)
  orderGroupMove(sim, [buggy], x + 3, y + 6)
  expect(sim.world.get(buggy, Path)!.direct).toBe(true)
  let planned = -1
  let arrived = -1
  for (let tick = 0; tick < 20 * 30 && arrived < 0; tick++) {
    sim.advance(1 / 20)
    const path = sim.world.get(buggy, Path)
    if (planned < 0 && path && !path.direct) planned = tick
    const { x: px, y: py } = sim.world.get(buggy, Position)!
    if (Math.hypot(px - x - 3.5, py - y - 6.5) < 1.5) arrived = tick
  }
  // До стены — около секунды; путь ищется сразу, как упёрся. Раньше — у края стены, через 7,5 с.
  expect(planned).toBeGreaterThan(0)
  expect(planned).toBeLessThan(40)
  expect(arrived).toBeGreaterThan(0)
  expect(arrived).toBeLessThan(220)
})
