import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { BUILDINGS, GATE_CLOSE, Position, Site, canPlace, createSim, creditsOf, freeCredits, isOpenGate, isWalkable, wallStroke, type Sim } from '../src/sim'
import { addCredits } from '../src/sim/economy'
import { isWall, placeBuilding } from '../src/sim/buildings'
import { Gate } from '../src/sim/components'
import { spawnUnit } from '../src/sim/units'

const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Стена из девяти секций с воротами в одну секцию посередине на ровном месте; (x, y) — тайл ворот. Защиты новичка нет. */
function wall() {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false, protection: 0 } })
  for (let y = -150; y < 150; y++) {
    search: for (let x = -150; x < 150; x++) {
      for (let dy = -12; dy <= 12; dy++) for (let dx = -4; dx <= 4; dx++) if (!canPlace(sim, 'wall', x + dx, y + dy)) continue search
      for (let dx = -4; dx <= 4; dx++) if (dx) placeBuilding(sim.world, 'wall', x + dx, y, 1)
      const gate = placeBuilding(sim.world, 'gate', x, y, 1)
      return { sim, gate, x, y }
    }
  }
  throw new Error('Не нашлось места под стену')
}

test('ворота открыты, пока рядом нет чужих: свои проезжают, а соседняя секция стены — нет', () => {
  const { sim, gate, x, y } = wall()
  seconds(sim, 0.5)
  expect(isOpenGate(sim, gate)).toBe(true)
  expect(isWalkable(sim, x, y)).toBe(true)
  expect(isWalkable(sim, x + 1, y)).toBe(false)
  expect(isWall(sim, gate)).toBe(false)
  const buggy = spawnUnit(sim, 'buggy', 1, x, y - 5)
  sim.send(1, { type: 'move', units: [buggy], x, y: y + 5 })
  seconds(sim, 10)
  expect(sim.world.get(buggy, Position)!.y).toBeGreaterThan(y + 3)
})

test('ворота закрываются перед чужим наземным юнитом и открываются, когда он ушёл; летающий им безразличен', () => {
  const { sim, gate, x, y } = wall()
  seconds(sim, 0.5)
  const drone = spawnUnit(sim, 'drone', 2, x, y + 3)
  seconds(sim, 0.5)
  expect(isOpenGate(sim, gate)).toBe(true)
  sim.world.destroy(drone)

  const foe = spawnUnit(sim, 'truck', 2, x, y + GATE_CLOSE + 4)
  seconds(sim, 0.5)
  expect(isOpenGate(sim, gate)).toBe(true)
  // Чужой грузовик едет в ворота — они закрываются раньше, чем он доедет, и становятся стеной для всех.
  sim.send(2, { type: 'move', units: [foe], x, y: y + 2 })
  seconds(sim, 6)
  expect(sim.world.get(gate, Gate)!.open).toBe(false)
  expect(isWalkable(sim, x, y)).toBe(false)
  expect(isWall(sim, gate)).toBe(true)
  expect(sim.world.get(foe, Position)!.y).toBeGreaterThan(y + 1)
  sim.world.destroy(foe)
  seconds(sim, 0.5)
  expect(isOpenGate(sim, gate)).toBe(true)
})

test('пока в проёме кто-то стоит, ворота не закрываются', () => {
  const { sim, gate, x, y } = wall()
  seconds(sim, 0.5)
  const own = spawnUnit(sim, 'truck', 1, x, y)
  spawnUnit(sim, 'truck', 2, x, y + 4)
  seconds(sim, 1)
  expect(isOpenGate(sim, gate)).toBe(true)
  sim.world.destroy(own)
  seconds(sim, 0.5)
  expect(isOpenGate(sim, gate)).toBe(false)
})

test('ворота тянутся линией, и секции вплотную — одни ворота: чужой у края поднимает все, кроме занятой', () => {
  const { sim, x, y } = wall()
  // Ещё ряд, отдельно от стены: пять секций протяжкой.
  const row = y - 8
  const tiles = [0, 1, 2, 3, 4].flatMap((dx) => [x + dx, row])
  expect(wallStroke(sim, 1, tiles, 'gate').every(Boolean)).toBe(true)
  const credits = freeCredits(sim, 1)
  addCredits(sim, 1, 1000)
  sim.send(1, { type: 'walls', tiles, builders: [], gate: true })
  sim.advance(TICK)
  // Массив строки обхода один на весь обход — собираем сразу.
  const sites: Entity[] = []
  for (const [entity, site] of sim.world.query(Site)) if (site.type === 'gate') sites.push(entity)
  expect(sites).toHaveLength(5)
  expect(freeCredits(sim, 1)).toBeCloseTo(credits + 1000 - 5 * BUILDINGS.gate.cost, 0)

  // Готовые ворота шириной в пять тайлов.
  const wide = [0, 1, 2, 3, 4].map((dx) => placeBuilding(sim.world, 'gate', x + dx, row + 4, 1))
  seconds(sim, 0.5)
  expect(wide.every((gate) => isOpenGate(sim, gate))).toBe(true)
  const own = spawnUnit(sim, 'truck', 1, x + 4, row + 4)
  // Чужой — у левого края, до правого ему дальше порога: одна секция его бы не заметила.
  spawnUnit(sim, 'truck', 2, x - 5, row + 4)
  seconds(sim, 0.5)
  expect(wide.map((gate) => isOpenGate(sim, gate))).toEqual([false, false, false, false, true])
  sim.world.destroy(own)
  seconds(sim, 0.5)
  expect(wide.some((gate) => isOpenGate(sim, gate))).toBe(false)
})
