import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Terrain, terrainAt } from '@kharos/shared/terrain'
import { Hauler, Inventory, amountOf, createSim, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { put } from '../src/sim/inventory'
import { apply } from '../src/sim/commands'
import { flowsOf } from '../src/sim/logistics'
import { spawnUnit } from '../src/sim/units'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false } }
const run = (sim: Sim, ticks: number) => {
  for (let i = 0; i < ticks; i++) sim.advance(sim.time.step)
}

function rock(sim: Sim, width: number, height: number) {
  for (let y = -200; y < 200; y++) {
    for (let x = -200; x < 200; x++) {
      let ok = true
      for (let ty = y; ty < y + height && ok; ty++) for (let tx = x; tx < x + width && ok; tx++) ok = terrainAt(sim.land, tx, ty) === Terrain.Rock
      if (ok) return { x, y }
    }
  }
  throw new Error('Не нашлось скалы')
}

/** Два склада металла поодаль, без труб, и два грузовика между ними. */
function setup() {
  const sim = createSim(options)
  const { x, y } = rock(sim, 30, 10)
  const from = placeBuilding(sim.world, 'metalYard', x, y + 3, 1)
  const to = placeBuilding(sim.world, 'metalYard', x + 18, y + 3, 1)
  put(sim.world.get(from, Inventory)!, 'metal', 100)
  const trucks = [spawnUnit(sim, 'truck', 1, x + 10, y + 1), spawnUnit(sim, 'truck', 1, x + 10, y + 8)]
  return { sim, from, to, trucks }
}

test('ручной приказ: грузовики перевозят заданное рейсами — в любой склад, где груз помещается, — и снова свободны', () => {
  const { sim, from, to, trucks } = setup()
  // Склад склада не заказывает: металл между ними возят только по приказу.
  expect(apply(sim, 1, { type: 'carry', units: trucks, from, to, resource: 'metal', amount: 60 })).toBe(true)
  run(sim, 2)
  // Что везут, видят заявки: того же не повезёт другой.
  expect(flowsOf(sim).incoming.get(to)?.metal).toBeGreaterThan(0)
  run(sim, 3000)
  expect(amountOf(sim.world.get(to, Inventory)!, 'metal')).toBeCloseTo(60, 3)
  expect(amountOf(sim.world.get(from, Inventory)!, 'metal')).toBeCloseTo(40, 3)
  for (const truck of trucks) {
    expect(sim.world.get(truck, Hauler)!.order).toBeNull()
    expect(amountOf(sim.world.get(truck, Inventory)!, 'metal')).toBeCloseTo(0, 3)
  }
})

test('приказ кончается, когда источник опустел; переживает сохранение; негодный не принимается', () => {
  const { sim, from, to, trucks } = setup()
  sim.world.get(from, Inventory)!.items.metal = 15
  expect(apply(sim, 1, { type: 'carry', units: [trucks[0]], from, to, resource: 'metal', amount: 500 })).toBe(true)
  run(sim, 5)
  const copy = createSim(throughJson(sim.save()))
  expect(copy.world.get(trucks[0], Hauler)!.order).toEqual(sim.world.get(trucks[0], Hauler)!.order)
  run(sim, 3000)
  expect(amountOf(sim.world.get(to, Inventory)!, 'metal')).toBeCloseTo(15, 3)
  expect(sim.world.get(trucks[0], Hauler)!.order).toBeNull()
  // Чужим не приказать, в себя не перевезти, груз, которому некуда, — тоже.
  expect(apply(sim, 2, { type: 'carry', units: trucks, from, to, resource: 'metal', amount: 5 })).toBe(false)
  expect(apply(sim, 1, { type: 'carry', units: trucks, from, to: from, resource: 'metal', amount: 5 })).toBe(false)
  expect(apply(sim, 1, { type: 'carry', units: trucks, from: to, to: from, resource: 'metalOre', amount: 5 })).toBe(false)
})

test('приказ из кузова: грузовик выгружает то, что везёт, туда, куда сказали', () => {
  const { sim, to, trucks } = setup()
  put(sim.world.get(trucks[0], Inventory)!, 'metal', 12)
  expect(apply(sim, 1, { type: 'carry', units: [trucks[0]], from: -1, to, resource: 'metal', amount: 12 })).toBe(true)
  run(sim, 2000)
  expect(amountOf(sim.world.get(to, Inventory)!, 'metal')).toBeCloseTo(12, 3)
  expect(sim.world.get(trucks[0], Hauler)!.order).toBeNull()
})
