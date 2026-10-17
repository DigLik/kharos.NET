import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Biome, biomeAt } from '@kharos/shared/terrain'
import { Accumulator, Building, Health, Inventory, Position, TOPICS, Trade, buyResearch, createSim, depositIn, grantResearch, isWalkable, powerStates, powerSupply, zoneEconomies, type Sim } from '@kharos/sim-ts'
import { BUILDINGS, placeBuilding } from '@kharos/sim-ts/buildings'
import { addCredits } from '@kharos/sim-ts/economy'
import { buy } from '@kharos/sim-ts/trade'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { protection: 0, techTree: false } }
const TICK = 1 / 20
const { capacity, rate } = BUILDINGS.accumulator.battery!

const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Симуляция и левый верхний тайл ровного проходимого поля. */
function field(width = 20, height = 10) {
  const sim = createSim(options)
  for (let y = -60; y < 60; y++) {
    search: for (let x = -60; x < 60; x++) {
      for (let dy = 0; dy < height; dy++) {
        for (let dx = 0; dx < width; dx++) if (!isWalkable(sim, x + dx, y + dy)) continue search
      }
      return { sim, x, y }
    }
  }
  throw new Error('В мире не нашлось ровного поля')
}

/** Электростанция (10), радар (−3) и накопитель вплотную: излишек 7. */
function base(charge = 0) {
  const { sim, x, y } = field()
  const generator = placeBuilding(sim.world, 'generator', x + 4, y + 2, 1)
  const radar = placeBuilding(sim.world, 'radar', x + 6, y + 2, 1)
  const cell = placeBuilding(sim.world, 'accumulator', x + 8, y + 2, 1)
  sim.world.get(cell, Accumulator)!.charge = charge
  return { sim, x, y, generator, radar, cell }
}

test('накопитель копит излишек энергии зоны и не больше своей ёмкости', () => {
  const { sim, cell } = base()
  seconds(sim, 10)
  // Излишек 7 в секунду — меньше скорости накопителя: за 10 секунд — 70.
  expect(sim.world.get(cell, Accumulator)!.charge).toBeCloseTo(70, 0)
  expect(sim.world.get(cell, Accumulator)!.flow).toBeCloseTo(-7)
  // Копит только излишек: радар работает в полную силу.
  expect(zoneEconomies(sim, 1)[0].demand).toBe(3)
  seconds(sim, 100)
  expect(sim.world.get(cell, Accumulator)!.charge).toBe(capacity)
  expect(sim.world.get(cell, Accumulator)!.flow).toBe(0)
})

test('без электростанции накопитель держит зону, пока не кончится заряд', () => {
  const { sim, generator, radar, cell } = base(30)
  sim.world.destroy(generator)
  sim.advance(TICK)
  // Отдаёт ровно сколько просят: радар работает в полную силу, и перегруза нет.
  expect(sim.world.get(cell, Accumulator)!.flow).toBeCloseTo(3)
  expect(zoneEconomies(sim, 1)[0].stored).toBeCloseTo(3)
  expect(powerSupply(sim).get(radar)).toBe(1)
  expect(powerStates(sim).has(radar)).toBe(false)
  // 30 единиц на 3 в секунду — 10 секунд; потом радару энергии нет.
  seconds(sim, 9)
  expect(powerSupply(sim).get(radar)).toBe(1)
  seconds(sim, 2)
  expect(sim.world.get(cell, Accumulator)!.charge).toBe(0)
  expect(powerSupply(sim).get(radar)).toBe(0)
  expect(powerStates(sim).get(radar)).toBe('starved')
})

test('накопитель отдаёт не быстрее своей скорости: остальное — нехватка', () => {
  const { sim, x, y, generator, cell } = base(capacity)
  sim.world.destroy(generator)
  // Радар и 12 турелей без цели, по 1 в простое, — 3 + 12 = 15: больше, чем накопитель отдаёт за секунду.
  for (let i = 0; i < 12; i++) placeBuilding(sim.world, 'laserTurret', x + 4 + (i % 6), y + 5 + Math.floor(i / 6), 1)
  sim.advance(TICK)
  expect(sim.world.get(cell, Accumulator)!.flow).toBe(rate)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(rate)
  expect(zoneEconomies(sim, 1)[0].demand).toBe(15)
})

test('накопитель отдаёт ровно нехватку зоны без себя: в счёт идут прочность станций, уровни, теснота, простой и стержни', () => {
  type Setup = (sim: Sim, x: number, y: number) => void
  const cases: [string, Setup, number][] = [
    // Станция вполсилы даёт 5, радар просит 3, четыре лазерные турели без дела — по одной: нехватка 2.
    ['повреждённая станция', (sim, x, y) => {
      sim.world.get(placeBuilding(sim.world, 'generator', x + 4, y + 2, 1), Health)!.value = 0.5
      placeBuilding(sim.world, 'radar', x + 6, y + 2, 1)
      for (let i = 0; i < 4; i++) placeBuilding(sim.world, 'laserTurret', x + 4 + i, y + 5, 1)
    }, 2],
    // Станция второго уровня — 15, радар второго уровня — 4,5, три генератора материи теснят друг друга — 3 + 6 + 9: нехватка 7,5.
    ['уровни и теснота', (sim, x, y) => {
      sim.world.get(placeBuilding(sim.world, 'generator', x + 4, y + 2, 1), Building)!.level = 2
      sim.world.get(placeBuilding(sim.world, 'radar', x + 6, y + 2, 1), Building)!.level = 2
      for (let i = 0; i < 3; i++) placeBuilding(sim.world, 'matter', x + 4 + i * 2, y + 5, 1)
    }, 7.5],
    // Реактор без стержня не даёт ничего: радар — весь на накопителе.
    ['реактор без стержня', (sim, x, y) => {
      placeBuilding(sim.world, 'reactor', x + 3, y + 2, 1)
      placeBuilding(sim.world, 'radar', x + 6, y + 2, 1)
    }, 3],
    // Разрядные турели без дела просят по одной из трёх: четыре — 4, и радар 3.
    ['простой', (sim, x, y) => {
      placeBuilding(sim.world, 'radar', x + 6, y + 2, 1)
      for (let i = 0; i < 4; i++) placeBuilding(sim.world, 'arcTurret', x + 4 + i, y + 5, 1)
    }, 7],
  ]
  for (const [name, setup, flow] of cases) {
    const { sim, x, y } = field()
    setup(sim, x, y)
    const cell = placeBuilding(sim.world, 'accumulator', x + 8, y + 2, 1)
    sim.world.get(cell, Accumulator)!.charge = capacity / 2
    sim.advance(TICK)
    expect([name, sim.world.get(cell, Accumulator)!.flow]).toEqual([name, flow])
  }
})

test('два накопителя делят нехватку: второй отдаёт только то, чего не покрыл первый', () => {
  const { sim, generator, cell } = base(30)
  sim.world.destroy(generator)
  const second = placeBuilding(sim.world, 'accumulator', sim.world.get(cell, Position)!.x, sim.world.get(cell, Position)!.y + 2, 1)
  sim.world.get(second, Accumulator)!.charge = 30
  sim.advance(TICK)
  expect(sim.world.get(cell, Accumulator)!.flow + sim.world.get(second, Accumulator)!.flow).toBeCloseTo(3)
})

test('заряд накопителя переживает сохранение', () => {
  const { sim, cell } = base(250)
  const loaded = createSim(throughJson(sim.save()))
  expect(loaded.world.get(cell, Accumulator)!.charge).toBe(250)
})

test('литий покупается с орбиты, только когда изучена его выпарка; уран — никогда', () => {
  const { sim, x, y } = field()
  sim.rules.techTree = true
  addCredits(sim, 1, 50000)
  const port = placeBuilding(sim.world, 'spaceport', x + 4, y + 2, 1)
  placeBuilding(sim.world, 'generator', x + 7, y + 2, 1)
  grantResearch(sim, 1, TOPICS.filter((topic) => topic !== 'lithiumOre'))
  expect(buy(sim, 1, port, 'uranium', 10)).toBe(false)
  expect(buyResearch(sim, 1, 'lithium')).toBe('lithiumOre')
  expect(buy(sim, 1, port, 'lithium', 10)).toBe(false)
  expect(sim.world.has(port, Trade)).toBe(false)
  // Металл и прочее темы для покупки не просят.
  expect(buyResearch(sim, 1, 'metal')).toBeUndefined()
  grantResearch(sim, 1, ['lithiumOre'])
  expect(buy(sim, 1, port, 'lithium', 10)).toBe(true)
  seconds(sim, 25)
  expect(sim.world.get(port, Inventory)!.items.lithium).toBe(10)
})

test('у каждого биома свой приз: харит — в эрге, литий — на солончаках, уран — в рыжих пустошах', () => {
  const sim = createSim(options)
  const count: Record<number, Record<string, number>> = {}
  for (let cellY = -25; cellY < 25; cellY++) {
    for (let cellX = -25; cellX < 25; cellX++) {
      const spot = depositIn(sim, cellX, cellY)
      if (!spot) continue
      const biome = biomeAt(sim.land, spot.x, spot.y)
      count[biome] ??= {}
      count[biome][spot.kind] = (count[biome][spot.kind] ?? 0) + 1
    }
  }
  const share = (biome: Biome, kind: string) => {
    const all = Object.values(count[biome] ?? {}).reduce((sum, n) => sum + n, 0)
    return (count[biome]?.[kind] ?? 0) / all
  }
  const others = (home: Biome, kind: string) => Math.max(...[Biome.Erg, Biome.SaltFlats, Biome.RedWastes, Biome.Marsh].filter((biome) => biome !== home).map((biome) => share(biome, kind)))
  expect(share(Biome.Erg, 'kharite')).toBeGreaterThan(2 * others(Biome.Erg, 'kharite'))
  expect(share(Biome.SaltFlats, 'lithium')).toBeGreaterThan(3 * others(Biome.SaltFlats, 'lithium'))
  expect(share(Biome.RedWastes, 'uranium')).toBeGreaterThan(3 * others(Biome.RedWastes, 'uranium'))
})
