import { expect, test } from 'bun:test'
import type { Entity } from '../src/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { BUILDINGS, Inventory, amountOf, canPlace, createSim, type BuildingType, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { apply } from '../src/sim/commands'
import { Magazine, Wanted } from '../src/sim/components'
import { addCredits } from '../src/sim/economy'
import { connectAll } from '../src/sim/piping'
import { magazineOf } from '../src/sim/supply'
import { spawnUnit } from '../src/sim/units'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
function until(sim: Sim, done: () => boolean, limit = 120) {
  for (let i = 0; i < limit / TICK; i++) {
    if (done()) return
    sim.advance(TICK)
  }
  throw new Error('Не дождались')
}
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Главное здание, электростанции и здания из списка в ряд на скале, каждое — своя сеть; под ними — место для грузовика. */
function base(types: BuildingType[], truck = true) {
  const sim = createSim({ ...options, rules: { protection: 0 } })
  addCredits(sim, 1, 10000)
  const layout: [BuildingType, number][] = [['command', 0], ['generator', 4], ['generator', 7]]
  let at = 10
  for (const type of types) {
    layout.push([type, at])
    at += BUILDINGS[type].width + 4
  }
  for (let y = -100; y < 100; y++) {
    for (let x = -100; x < 100; x++) {
      if (!layout.every(([type, dx]) => canPlace(sim, type, x + dx, y))) continue
      if (!canPlace(sim, 'command', x + 10, y + 4)) continue
      const buildings = layout.map(([type, dx]) => placeBuilding(sim.world, type, x + dx, y, 1))
      if (truck) spawnUnit(sim, 'truck', 1, x + 11, y + 4)
      return { sim, buildings: buildings.slice(3), x, y }
    }
  }
  throw new Error('Не нашлось места')
}

const amount = (sim: Sim, entity: Entity, good: Parameters<typeof amountOf>[1]) => amountOf(sim.world.get(entity, Inventory)!, good)

test('станция запроса: грузовик довозит заказанное, лишнее после уменьшения заказа увозит в хранилище', () => {
  const { sim, buildings: [yard, station] } = base(['metalYard', 'requestStation'])
  sim.world.get(yard, Inventory)!.items.metal = 100
  sim.send(1, { type: 'want', buildings: [station], resource: 'metal', amount: 40 })
  until(sim, () => amount(sim, station, 'metal') >= 40 - 1e-6)
  seconds(sim, 10)
  expect(amount(sim, station, 'metal')).toBeCloseTo(40)
  expect(amount(sim, yard, 'metal')).toBeCloseTo(60)

  sim.send(1, { type: 'want', buildings: [station], resource: 'metal', amount: 10 })
  until(sim, () => amount(sim, station, 'metal') <= 10 + 1e-6)
  seconds(sim, 10)
  expect(amount(sim, station, 'metal')).toBeCloseTo(10)
  expect(amount(sim, yard, 'metal')).toBeCloseTo(90)
})

test('станция запроса в сети труб получает заказ по трубам', () => {
  const { sim, buildings: [yard, station] } = base(['metalYard', 'requestStation'], false)
  connectAll(sim, 1)
  sim.world.get(yard, Inventory)!.items.metal = 100
  sim.send(1, { type: 'want', buildings: [station], resource: 'metal', amount: 30 })
  until(sim, () => amount(sim, station, 'metal') >= 30 - 1e-6, 60)
  seconds(sim, 10)
  expect(amount(sim, station, 'metal')).toBeCloseTo(30)
})

test('станции запроса не берут друг у друга', () => {
  const { sim, buildings: [full, empty] } = base(['requestStation', 'requestStation'])
  sim.send(1, { type: 'want', buildings: [full, empty], resource: 'metal', amount: 50 })
  sim.world.get(full, Inventory)!.items.metal = 50
  seconds(sim, 30)
  expect(amount(sim, full, 'metal')).toBeCloseTo(50)
  expect(amount(sim, empty, 'metal')).toBe(0)
})

test('заказ: не больше склада всем грузом вместе, без руды, только свои станции', () => {
  const { sim, buildings: [station, yard] } = base(['requestStation', 'metalYard'], false)
  const capacity = sim.world.get(station, Inventory)!.capacity
  const wanted = () => sim.world.get(station, Wanted)!.items
  expect(apply(sim, 1, { type: 'want', buildings: [station], resource: 'metal', amount: 70 })).toBe(true)
  apply(sim, 1, { type: 'want', buildings: [station], resource: 'ammo', amount: 1e9 })
  expect(wanted()).toEqual({ metal: 70, ammo: capacity - 70 })
  apply(sim, 1, { type: 'want', buildings: [station], resource: 'metal', amount: 0 })
  expect(wanted()).toEqual({ ammo: capacity - 70 })
  expect(apply(sim, 1, { type: 'want', buildings: [station], resource: 'metalOre', amount: 10 })).toBe(false)
  expect(apply(sim, 1, { type: 'want', buildings: [yard], resource: 'metal', amount: 10 })).toBe(false)
  expect(apply(sim, 2, { type: 'want', buildings: [station], resource: 'metal', amount: 10 })).toBe(false)
})

test('юниты в кругах станции запроса пополняют магазины из её запаса', () => {
  const { sim, buildings: [station], x, y } = base(['requestStation'], false)
  sim.send(1, { type: 'want', buildings: [station], resource: 'ammo', amount: 100 })
  sim.world.get(station, Inventory)!.items.ammo = 100
  const tank = spawnUnit(sim, 'tank', 1, x + 11, y + 3)
  sim.world.get(tank, Magazine)!.ammo = 0
  seconds(sim, 10)
  expect(sim.world.get(tank, Magazine)!.ammo).toBeCloseTo(magazineOf('tank'))
  expect(amount(sim, station, 'ammo')).toBeCloseTo(100 - magazineOf('tank'))
})

test('кнопки заказа: груз, потом количество шагами, не больше склада за вычетом других заказов', async () => {
  const { commandsOf } = await import('../src/ui/commands')
  type Deal = NonNullable<Parameters<typeof commandsOf>[3]>
  const request = { buildings: [5], wanted: { ammo: 60 }, have: { ammo: 20 }, capacity: 100 }
  const state = { request, credits: 0, units: [], building: 'requestStation' } as unknown as Parameters<typeof commandsOf>[0]
  let deal: Deal | null = null
  const sent: unknown[] = []
  const nothing = () => {}
  const actions = { send: (command: unknown) => void sent.push(command), place: nothing, pave: nothing, route: nothing, serve: nothing, patrol: nothing, aim: nothing, deploy: nothing, draft: nothing, tech: nothing, open: nothing, pick: (next: Deal | null) => void (deal = next) } as unknown as Parameters<typeof commandsOf>[2]
  const grid = () => commandsOf(state, 'request', actions, deal)
  expect(commandsOf(state, 'root', actions)[0]).toMatchObject({ label: 'Заказ: 1', active: true })
  expect(grid().find((slot) => slot?.label === 'Боеприпасы ×60')).toBeDefined()
  grid().find((slot) => slot?.label === 'Металл')!.run()
  expect(deal).toEqual({ resource: 'metal', amount: 0 })
  expect(grid()[4]).toMatchObject({ label: '−10', disabled: true })
  grid()[1]!.run()
  grid()[2]!.run()
  expect(sent).toEqual([
    { type: 'want', buildings: [5], resource: 'metal', amount: 40 },
    { type: 'want', buildings: [5], resource: 'metal', amount: 40 },
  ])
})
