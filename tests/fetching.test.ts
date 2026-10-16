import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Terrain, isCliffFoot, terrainAt } from '../src/map/terrain'
import { BUILDINGS, Builds, Fetch, Inventory, Site, Unit, amountOf, createSim, siteAt, spawnStartingUnits, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { put } from '../src/sim/inventory'
import { networkOf } from '../src/sim/zones'
import { incomingTo } from '../src/sim/logistics'
import { addBuilders } from './builders'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false } }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}
const unitsOf = (sim: Sim, type: string) => {
  const found: Entity[] = []
  for (const [entity, unit] of sim.world.query(Unit)) if (unit.type === type) found.push(entity)
  return found
}

/**
 * Главное здание игрока 1 на просторной скале, один строитель, грузовиков нет, а поодаль, не вплотную и без трубы, —
 * склад металла с запасом: его стройке никто не привезёт, кроме строителя.
 */
function start() {
  const sim = createSim(options)
  const rock = (x: number, y: number) => {
    for (let tileY = y; tileY < y + 16; tileY++) {
      for (let tileX = x; tileX < x + 16; tileX++) if (terrainAt(sim.land, tileX, tileY) !== Terrain.Rock || isCliffFoot(sim.land, tileX, tileY)) return false
    }
    return true
  }
  for (let y = 0; y < 400; y++) {
    for (let x = 0; x < 400; x++) {
      if (!rock(x, y)) continue
      spawnStartingUnits(sim, 1, x + 2, y + 2)
      sim.send(1, { type: 'deploy', unit: unitsOf(sim, 'mcv')[0] })
      seconds(sim, 6)
      for (const truck of unitsOf(sim, 'truck')) sim.world.destroy(truck)
      for (const builder of unitsOf(sim, 'builder')) sim.world.destroy(builder)
      const [builder] = addBuilders(sim, 1, x + 2, y + 2, 1)
      const yard = placeBuilding(sim.world, 'metalYard', x + 8, y + 11, 1)
      put(sim.world.get(yard, Inventory)!, 'metal', 50)
      return { sim, builder, yard, site: { x: x + 9, y: y + 3 } }
    }
  }
  throw new Error('В мире не нашлось места под базу')
}

test('строитель сам возит материал со склада на свою стройку, рейсами по кузову, и достраивает её', () => {
  const { sim, builder, yard, site } = start()
  sim.send(1, { type: 'build', building: 'spaceport', x: site.x, y: site.y, builders: [builder] })
  seconds(sim, 1)
  const building = siteAt(sim, site.x, site.y)!
  expect(networkOf(sim, building)).not.toBe(networkOf(sim, yard))
  // Пока стройка не встала без материала, строитель строит, а не едет.
  let fetched = false
  let carried = 0
  for (let i = 0; i < Math.round(120 / TICK) && sim.world.has(building, Site); i++) {
    sim.advance(TICK)
    const fetch = sim.world.get(builder, Fetch)
    if (fetch) {
      fetched = true
      expect(fetch.to).toBe(building)
      // Что везёт строитель, учитывают заявки: грузовики и трубы того же не повезут.
      expect(incomingTo(sim, building, 'metal')).toBeGreaterThan(0)
    }
    carried = Math.max(carried, amountOf(sim.world.get(builder, Inventory)!, 'metal'))
  }
  expect(fetched).toBe(true)
  // Кузов — 10, космопорту нужно 20: два рейса.
  expect(carried).toBeLessThanOrEqual(10 + 1e-6)
  expect(sim.world.has(building, Site)).toBe(false)
  expect(amountOf(sim.world.get(yard, Inventory)!, 'metal')).toBeCloseTo(50 - BUILDINGS.spaceport.materials.metal, 3)
  // Построил — и снова свободен, кузов пуст.
  expect(sim.world.has(builder, Fetch)).toBe(false)
  expect(amountOf(sim.world.get(builder, Inventory)!, 'metal')).toBeCloseTo(0, 3)
})

test('приказ идти снимает поручение; свободный строитель с остатком отвозит его в хранилище', () => {
  const { sim, builder, yard, site } = start()
  sim.send(1, { type: 'build', building: 'spaceport', x: site.x, y: site.y, builders: [builder] })
  // Ждём, пока наберёт груз.
  for (let i = 0; i < Math.round(60 / TICK) && !(sim.world.get(builder, Fetch)?.full ?? false); i++) sim.advance(TICK)
  expect(sim.world.get(builder, Fetch)?.full).toBe(true)
  const carried = amountOf(sim.world.get(builder, Inventory)!, 'metal')
  expect(carried).toBeGreaterThan(0)
  // Стройку отменили: увести строителя — поручение снято, груз в кузове.
  const building = siteAt(sim, site.x, site.y)!
  sim.send(1, { type: 'cancelBuild', site: building })
  sim.send(1, { type: 'move', units: [builder], x: site.x, y: site.y + 6 })
  sim.advance(TICK)
  expect(sim.world.has(builder, Fetch)).toBe(false)
  expect(sim.world.has(builder, Builds)).toBe(false)
  // Доехал и стоит без работы — везёт остаток назад на склад.
  seconds(sim, 40)
  expect(amountOf(sim.world.get(builder, Inventory)!, 'metal')).toBeCloseTo(0, 3)
  expect(amountOf(sim.world.get(yard, Inventory)!, 'metal')).toBeCloseTo(50, 3)
})

test('поручение строителя переживает сохранение', () => {
  const { sim, builder, site } = start()
  sim.send(1, { type: 'build', building: 'spaceport', x: site.x, y: site.y, builders: [builder] })
  for (let i = 0; i < Math.round(60 / TICK) && !sim.world.has(builder, Fetch); i++) sim.advance(TICK)
  const fetch = sim.world.get(builder, Fetch)
  expect(fetch).toBeDefined()
  const copy = createSim(throughJson(sim.save()))
  expect(copy.world.get(builder, Fetch)).toEqual(fetch!)
})
