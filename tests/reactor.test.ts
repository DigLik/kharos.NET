import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Building, Burst, Doomed, Health, Inventory, Off, Reactor, createSim, isWalkable, powerSupply, requestsOf, zoneEconomies, type Sim } from '@kharos/sim-ts'
import { BUILDINGS, isUnlocked, levelGain, placeBuilding } from '@kharos/sim-ts/buildings'
import { buildingHp } from '@kharos/sim-ts/combat'
import { setWorking } from '@kharos/sim-ts/assembly'
import { put } from '@kharos/sim-ts/inventory'
import { spawnUnit } from '@kharos/sim-ts/units'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { protection: 0, techTree: false } }
const TICK = 1 / 20
const { power } = BUILDINGS.reactor
const { seconds: ROD_SECONDS } = BUILDINGS.reactor.burns
const { radius, damage } = BUILDINGS.reactor.meltdown

const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Симуляция и левый верхний тайл ровного проходимого поля. */
function field(width = 24, height = 12) {
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

/** Реактор со стержнями на складе и радар (−3) рядом: без реактора зоне энергии взять неоткуда. */
function base(rods = 0) {
  const { sim, x, y } = field()
  const reactor = placeBuilding(sim.world, 'reactor', x + 4, y + 2, 1)
  const radar = placeBuilding(sim.world, 'radar', x + 8, y + 2, 1)
  if (rods) put(sim.world.get(reactor, Inventory)!, 'rods', rods)
  return { sim, x, y, reactor, radar }
}

const rodsIn = (sim: Sim, reactor: number) => sim.world.get(reactor, Inventory)!.items.rods ?? 0

test('реактор без стержней энергии не даёт', () => {
  const { sim, radar } = base()
  sim.advance(TICK)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(0)
  expect(powerSupply(sim).get(radar)).toBe(0)
})

test('реактор жжёт стержень в минуту и, пока тот горит, даёт полную выработку', () => {
  const { sim, reactor, radar } = base(2)
  sim.advance(TICK)
  // Первый стержень взят в работу сразу.
  expect(rodsIn(sim, reactor)).toBe(1)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(power)
  expect(powerSupply(sim).get(radar)).toBe(1)
  // Догорел первый — без перерыва горит второй.
  seconds(sim, ROD_SECONDS)
  expect(rodsIn(sim, reactor)).toBe(0)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(power)
  // Догорел и второй — реактор встал.
  seconds(sim, ROD_SECONDS)
  expect(sim.world.get(reactor, Reactor)!.left).toBe(0)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(0)
})

test('выключенный реактор не вырабатывает и стержень не жжёт, включённый — продолжает', () => {
  const { sim, reactor } = base(1)
  sim.advance(TICK)
  const left = sim.world.get(reactor, Reactor)!.left
  expect(setWorking(sim, 1, reactor, false)).toBe(true)
  seconds(sim, 5)
  expect(sim.world.has(reactor, Off)).toBe(true)
  expect(sim.world.get(reactor, Reactor)!.left).toBe(left)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(0)
  expect(setWorking(sim, 1, reactor, true)).toBe(true)
  sim.advance(TICK)
  expect(zoneEconomies(sim, 1)[0].produced).toBe(power)
})

test('реактор сам заказывает стержни у зоны, когда склад не полон', () => {
  const { sim, reactor } = base(1)
  const wanted = requestsOf(sim, 1).filter((request) => request.to === reactor)
  expect(wanted.map((request) => request.resource)).toEqual(['rods'])
  expect(wanted[0].amount).toBe(BUILDINGS.reactor.inventory - 1)
})

test('разрушенный реактор с топливом взрывается и бьёт всех вокруг, своих тоже', () => {
  const { sim, x, y, reactor } = base(2)
  sim.advance(TICK)
  // Свой генератор вплотную и чужой юнит рядом, а дальняя стена — за радиусом взрыва.
  const own = placeBuilding(sim.world, 'generator', x + 7, y + 3, 1)
  const enemy = spawnUnit(sim, 'infantry', 2, x + 3, y + 3)
  const far = placeBuilding(sim.world, 'wall', x + 5 + radius + 3, y + 3, 1)
  sim.world.add(reactor, Doomed({ left: 1 }))
  sim.advance(TICK)
  expect(sim.world.has(reactor, Building)).toBe(false)
  // Топливо сгорело в аварии, а не упало на землю.
  expect([...sim.world.query(Burst)].length).toBe(1)
  sim.advance(TICK)
  expect([...sim.world.query(Burst)].length).toBe(0)
  expect(sim.world.has(enemy, Health)).toBe(false)
  const hp = buildingHp('generator')
  expect(sim.world.get(own, Health)!.value).toBeLessThan(1 - damage * 0.3 / hp)
  expect(sim.world.get(far, Health)!.value).toBe(1)
})

test('пустой реактор при разрушении не взрывается', () => {
  const { sim, x, y, reactor } = base()
  const own = placeBuilding(sim.world, 'generator', x + 7, y + 3, 1)
  sim.world.add(reactor, Doomed({ left: 1 }))
  seconds(sim, 0.5)
  expect(sim.world.has(reactor, Building)).toBe(false)
  expect(sim.world.get(own, Health)!.value).toBe(1)
})

test('реактор не улучшают, а без дерева технологий — открыт; с деревом нужен «Атом»', () => {
  expect(levelGain('reactor')).toBeUndefined()
  const sim = createSim({ ...options, rules: { protection: 0, techTree: true } })
  expect(isUnlocked(sim, 1, 'reactor')).toBe(false)
})

test('горящий стержень переживает сохранение', () => {
  const { sim, reactor } = base(1)
  seconds(sim, 10)
  const left = sim.world.get(reactor, Reactor)!.left
  const loaded = createSim(throughJson(sim.save()))
  expect(loaded.world.get(reactor, Reactor)!.left).toBe(left)
})
