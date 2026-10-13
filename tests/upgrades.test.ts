import { expect, test } from 'bun:test'
import type { Entity } from '../src/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import {
  BUILDINGS, BUY_SECONDS, Building, DEPOSIT_KINDS, Inventory, MAX_LEVEL, Position, REFINE_RATE, Site, Trade, Upgrade, amountOf, canPlace, createSim, creditsOf, depositIn, economyOf,
  isUpgradable, levelFactor, levelOf, refundOf, resolveOrder, stockOf, upgradeCost, upgradeLack, type BuildingType, type Good, type Sim,
} from '../src/sim'
import { STORES, placeBuilding, storeFor } from '../src/sim/buildings'
import type { Amounts, Ware } from '../src/sim/resources'
import { addCredits } from '../src/sim/economy'
import { spawnUnit } from '../src/sim/units'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false } }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}
function until(sim: Sim, done: () => boolean, limit = 300) {
  for (let i = 0; i < limit / TICK; i++) {
    if (done()) return
    sim.advance(TICK)
  }
  throw new Error('Не дождались')
}
const good = (sim: Sim, entity: Entity, item: Good) => amountOf(sim.world.get(entity, Inventory)!, item)

/** База игрока 1 в ряд на скале в одной сети труб: главное здание, здания из списка и все хранилища; рядом — строитель. */
function base(extra: BuildingType[]) {
  const sim = createSim(options)
  addCredits(sim, 1, 10000)
  const layout: [BuildingType, number][] = [['command', 0]]
  let at = 4
  for (const type of [...extra, ...STORES]) {
    layout.push([type, at])
    at += BUILDINGS[type].width + 1
  }
  for (let y = -100; y < 100; y++) {
    for (let x = -100; x < 100; x++) {
      if (!layout.every(([type, dx]) => canPlace(sim, type, x + dx, y))) continue
      if (!canPlace(sim, 'command', x, y + 4) || !canPlace(sim, 'command', x + 4, y + 4)) continue
      let piped = true
      for (let dx = 0; dx < at && piped; dx++) piped = canPlace(sim, 'pipe', x + dx, y - 1)
      if (!piped) continue
      const buildings = layout.map(([type, dx]) => placeBuilding(sim.world, type, x + dx, y, 1))
      for (let dx = 0; dx < at; dx++) placeBuilding(sim.world, 'pipe', x + dx, y - 1, 1)
      const shelves = buildings.slice(1 + extra.length)
      const store = (item: Ware) => shelves[STORES.indexOf(storeFor(item)!)]
      /** Кладёт груз в хранилища его вида. */
      const stash = (items: Amounts) => {
        for (const [item, amount] of Object.entries(items) as [Ware, number][]) sim.world.get(store(item), Inventory)!.items[item] = amount
      }
      const builder = spawnUnit(sim, 'builder', 1, x + 5, y + 4)
      return { sim, buildings: buildings.slice(1, 1 + extra.length), store, stash, builder }
    }
  }
  throw new Error('Не нашлось места под базу')
}

const upgrade = (sim: Sim, building: Entity, builder: Entity) => sim.send(1, { type: 'upgrade', building, builders: [builder] })

test('улучшение электростанции: цена списана сразу из сети, строитель поднимает уровень, выработка растёт в полтора раза', () => {
  const { sim, buildings: [plant], stash, builder } = base(['generator'])
  const price = upgradeCost('generator', 1)
  expect(price).toEqual({ credits: 150, materials: { blocks: 10 } })
  stash({ blocks: 25 })
  expect(economyOf(sim, 1).produced).toBe(10)
  const credits = creditsOf(sim, 1)

  upgrade(sim, plant, builder)
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(true)
  expect(credits - creditsOf(sim, 1)).toBe(150)
  expect(stockOf(sim, 1).items.blocks).toBe(15)
  // Пока улучшают, здание работает и второй заказ не берёт.
  expect(economyOf(sim, 1).produced).toBe(10)
  expect(isUpgradable(sim, 1, plant)).toBe(false)

  until(sim, () => levelOf(sim.world, plant) === 2)
  expect(sim.world.has(plant, Upgrade)).toBe(false)
  expect(economyOf(sim, 1).produced).toBe(15)
  // Работа — как стройка на ту же сумму: 150 кредитов одному строителю — 7,5 секунды и дорога.
  expect(sim.time.tick * TICK).toBeGreaterThan(7.5)
  // Разбор возвращает половину цены и вложенного в уровень; уровень переживает сохранение.
  expect(refundOf('generator', 2)).toBe(225)
  const copy = createSim(throughJson(sim.save()))
  expect(levelOf(copy.world, plant)).toBe(2)
  expect(economyOf(copy, 1).produced).toBe(15)
})

test('без готового в сети здания и без кредитов улучшение не заказать; резерв хранилища не трогают', () => {
  const { sim, buildings: [plant], store, stash, builder } = base(['generator'])
  expect(upgradeLack(sim, 1, plant)).toBe('materials')
  upgrade(sim, plant, builder)
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(false)

  stash({ blocks: 12 })
  expect(upgradeLack(sim, 1, plant)).toBeUndefined()
  sim.world.get(store('blocks'), Inventory)!.reserve = 5
  expect(upgradeLack(sim, 1, plant)).toBe('materials')
  sim.world.get(store('blocks'), Inventory)!.reserve = 0

  addCredits(sim, 1, 100 - creditsOf(sim, 1))
  expect(upgradeLack(sim, 1, plant)).toBe('credits')
  upgrade(sim, plant, builder)
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(false)
  expect(stockOf(sim, 1).items.blocks).toBe(12)
  // Главное здание, заводы и турели не улучшают.
  expect(isUpgradable(sim, 1, plant)).toBe(true)
  for (const [entity, building] of sim.world.query(Building)) if (building.type === 'command') expect(isUpgradable(sim, 1, entity)).toBe(false)
})

test('электростанцию без трубы улучшают с готового её зоны: энергосеть та же, а дальнюю — нет', () => {
  const { sim, stash, builder } = base([])
  let core: Entity | undefined
  for (const [entity, building] of sim.world.query(Building)) if (building.type === 'command') core = entity
  const at = sim.world.get(core!, Position)!
  // Под главным зданием, без трубы: своя сеть из одного здания, но круг главного здания её задевает.
  const plant = placeBuilding(sim.world, 'generator', at.x, at.y + 4, 1)
  stash({ blocks: 12 })
  expect(upgradeLack(sim, 1, plant)).toBeUndefined()
  upgrade(sim, plant, builder)
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(true)
  expect(stockOf(sim, 1).items.blocks).toBe(2)
  // Отмена возвращает готовое в хранилища зоны.
  sim.send(1, { type: 'cancelUpgrade', building: plant })
  sim.advance(TICK)
  expect(stockOf(sim, 1).items.blocks).toBe(12)

  // Станция вдали от базы — в своей энергосети, готового там нет.
  let far: Entity | undefined
  for (let dy = 40; dy < 200 && far === undefined; dy++) if (canPlace(sim, 'generator', at.x, at.y + dy)) far = placeBuilding(sim.world, 'generator', at.x, at.y + dy, 1)
  expect(upgradeLack(sim, 1, far!)).toBe('materials')
})

test('отмена улучшения возвращает кредиты и готовое; разбор отменяет его сам', () => {
  const { sim, buildings: [plant, other], stash, builder } = base(['generator', 'generator'])
  stash({ blocks: 20 })
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'upgrade', building: plant, builders: [] })
  sim.send(1, { type: 'upgrade', building: other, builders: [] })
  sim.advance(TICK)
  expect(stockOf(sim, 1).items.blocks ?? 0).toBe(0)
  expect(credits - creditsOf(sim, 1)).toBe(300)

  // Щелчок строителем по улучшаемому зданию — приказ улучшать его.
  const at = sim.world.get(plant, Position)!
  expect(resolveOrder(sim, 1, [builder], { x: at.x + 0.5, y: at.y + 0.5 })).toEqual([{ type: 'assist', units: [builder], site: plant }])

  sim.send(1, { type: 'cancelUpgrade', building: plant })
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(false)
  expect(stockOf(sim, 1).items.blocks).toBe(10)
  expect(credits - creditsOf(sim, 1)).toBe(150)

  sim.send(1, { type: 'demolish', building: other, builders: [builder] })
  sim.advance(TICK)
  expect(sim.world.has(other, Upgrade)).toBe(false)
  expect(sim.world.get(other, Site)?.demolish).toBe(true)
  expect(stockOf(sim, 1).items.blocks).toBe(20)
  expect(creditsOf(sim, 1)).toBe(credits)
  expect(levelOf(sim.world, other)).toBe(1)
})

test('космопорт до третьего уровня: выше — ещё и компоненты, трюм вдвое больше, рейс вдвое короче, дальше не поднять', () => {
  const { sim, buildings: [, port], stash, builder } = base(['generator', 'spaceport'])
  expect(upgradeCost('spaceport', 2)).toEqual({ credits: 450, materials: { blocks: 30, parts: 8 } })
  stash({ blocks: 45 })
  // Заказы без строителя в списке: свободный рядом берётся за улучшение сам.
  void builder
  sim.send(1, { type: 'upgrade', building: port, builders: [] })
  until(sim, () => levelOf(sim.world, port) === 2)
  expect(sim.world.get(port, Inventory)!.capacity).toBe(BUILDINGS.spaceport.inventory * 1.5)
  // Блоков на третий уровень хватает, компонентов нет.
  expect(upgradeLack(sim, 1, port)).toBe('materials')
  stash({ parts: 8 })
  sim.send(1, { type: 'upgrade', building: port, builders: [] })
  until(sim, () => levelOf(sim.world, port) === MAX_LEVEL)
  expect(stockOf(sim, 1).items.parts ?? 0).toBe(0)
  expect(sim.world.get(port, Inventory)!.capacity).toBe(BUILDINGS.spaceport.inventory * 2)
  expect(isUpgradable(sim, 1, port)).toBe(false)
  sim.send(1, { type: 'upgrade', building: port, builders: [] })
  sim.advance(TICK)
  expect(sim.world.has(port, Upgrade)).toBe(false)

  sim.send(1, { type: 'buy', port, resource: 'metal', amount: 10 })
  sim.advance(TICK)
  expect(sim.world.get(port, Trade)!.total).toBe(Math.round(BUY_SECONDS / 2 / TICK))
})

test('уровень ускоряет переработку и добычу и расширяет хранилище', () => {
  const { sim, buildings: [, smelter], store, stash, builder } = base(['generator', 'smelter'])
  // Переработка второго уровня берёт руду в полтора раза быстрее.
  sim.world.get(smelter, Building)!.level = 2
  sim.world.get(smelter, Inventory)!.items.metalOre = 20
  seconds(sim, 2)
  expect(good(sim, smelter, 'metalOre')).toBeCloseTo(20 - REFINE_RATE * levelFactor(2) * 2, 5)

  // Хранилище поднимают по-настоящему: ёмкость растёт, груз на месте.
  const yard = store('metal')
  stash({ blocks: 10, metal: 100 })
  upgrade(sim, yard, builder)
  until(sim, () => levelOf(sim.world, yard) === 2)
  expect(sim.world.get(yard, Inventory)!.capacity).toBe(BUILDINGS.metalYard.inventory * 1.5)
  expect(good(sim, yard, 'metal')).toBeGreaterThanOrEqual(100)

  // Шахта: добыча с месторождения — по уровню.
  const mined = (level: number) => {
    const world = createSim(options)
    for (let cellY = -5; cellY < 5; cellY++) {
      for (let cellX = -5; cellX < 5; cellX++) {
        const spot = depositIn(world, cellX, cellY)
        if (spot?.kind !== 'metal') continue
        const mine = placeBuilding(world.world, 'mine', spot.x, spot.y, 1)
        world.world.get(mine, Building)!.level = level
        seconds(world, 10)
        return good(world, mine, 'metalOre')
      }
    }
    throw new Error('В мире не нашлось месторождения')
  }
  expect(mined(1)).toBeCloseTo(DEPOSIT_KINDS.metal.rate * 10, 5)
  expect(mined(3)).toBeCloseTo(DEPOSIT_KINDS.metal.rate * 2 * 10, 5)
})
