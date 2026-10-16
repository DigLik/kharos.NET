import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_CONFIG } from '@kharos/shared/terrain'
import { Drop, Hauler, Inventory, Unreachable, amountOf, createSim, unreachable, type Sim } from '../src/sim'
import { NONE } from '../src/sim/common'
import { placeBuilding } from '../src/sim/buildings'
import { spawnUnit } from '../src/sim/units'

const TICK = 1 / 20

/** Пустая ровная карта. */
const blank = () => createSim({ generator: { ...DEFAULT_CONFIG, blank: true }, size: 256 })

/** Кольцо стен вокруг прямоугольника (x, y, width, height) на расстоянии gap: внутрь технике не въехать. */
function ring(sim: Sim, x: number, y: number, width: number, height: number, gap: number) {
  for (let i = x - gap - 1; i <= x + width + gap; i++) {
    placeBuilding(sim.world, 'wall', i, y - gap - 1, 1)
    placeBuilding(sim.world, 'wall', i, y + height + gap, 1)
  }
  for (let j = y - gap; j < y + height + gap; j++) {
    placeBuilding(sim.world, 'wall', x - gap - 1, j, 1)
    placeBuilding(sim.world, 'wall', x + width + gap, j, 1)
  }
}

/** Грузовик с рудой металла в кузове, который везёт её на переработку to. */
function loaded(sim: Sim, x: number, y: number, to: Entity) {
  const truck = spawnUnit(sim, 'truck', 1, x, y)
  sim.world.get(truck, Inventory)!.items.metalOre = 20
  Object.assign(sim.world.get(truck, Hauler)!, { from: NONE, to, resource: 'metalOre', amount: 20, full: true })
  return truck
}

const run = (sim: Sim, seconds: number) => {
  for (let i = 0; i < seconds / TICK; i++) sim.advance(TICK)
}

test('к обнесённой стеной переработке не подъехать: грузовик бросает рейс, руда ложится на землю, завод отмечен', () => {
  const sim = blank()
  const smelter = placeBuilding(sim.world, 'smelter', 0, 0, 1)
  ring(sim, 0, 0, 3, 3, 2)
  const truck = loaded(sim, -20, 1, smelter)
  run(sim, 20)
  const hauler = sim.world.get(truck, Hauler)!
  expect(unreachable(sim, smelter)).toBe(true)
  expect(hauler.to).toBe(NONE)
  expect(hauler.trapped).toBe(false)
  expect(amountOf(sim.world.get(truck, Inventory)!, 'metalOre')).toBe(0)
  const drops = [...sim.world.query(Drop, Inventory)].map(([, , inventory]) => amountOf(inventory, 'metalOre'))
  expect(drops).toEqual([20])
})

test('руда с недоступного завода едет на другой, куда подъехать можно', () => {
  const sim = blank()
  const walled = placeBuilding(sim.world, 'smelter', 0, 0, 1)
  ring(sim, 0, 0, 3, 3, 2)
  const open = placeBuilding(sim.world, 'smelter', -40, 0, 1)
  // Ближе к грузовику — обнесённый: на него руду и повезут первым делом.
  const truck = loaded(sim, -12, 1, walled)
  run(sim, 60)
  expect(unreachable(sim, walled)).toBe(true)
  expect(unreachable(sim, open)).toBe(false)
  expect(amountOf(sim.world.get(truck, Inventory)!, 'metalOre')).toBe(0)
  expect(amountOf(sim.world.get(open, Inventory)!, 'metalOre')).toBeGreaterThan(0)
  expect([...sim.world.query(Drop)].length).toBe(0)
})

test('запертый грузовик отмечается сам, а здание, к которому он ехал, — нет; груз остаётся в кузове', () => {
  const sim = blank()
  const smelter = placeBuilding(sim.world, 'smelter', 30, 0, 1)
  const truck = loaded(sim, 0, 0, smelter)
  ring(sim, 0, 0, 1, 1, 1)
  run(sim, 20)
  const hauler = sim.world.get(truck, Hauler)!
  expect(hauler.trapped).toBe(true)
  expect(sim.world.has(smelter, Unreachable)).toBe(false)
  expect(amountOf(sim.world.get(truck, Inventory)!, 'metalOre')).toBe(20)
})
