import { expect, test } from 'bun:test'
import type { Entity } from '../src/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Health, INFANTRY_REGEN, Owner, Position, Site, createSim, isWalkable, type Sim } from '../src/sim'
import { BUILDINGS, placeBuilding } from '../src/sim/buildings'
import { STATION_REGEN } from '../src/sim/combat'
import { activeRepairs, repairLinks } from '../src/sim/construction'
import { addCredits, creditsOf } from '../src/sim/economy'
import { spawnUnit } from '../src/sim/units'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20

const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Симуляция и левый верхний тайл ровного проходимого поля. */
function field(width = 24, height = 14) {
  const sim = createSim(options)
  for (let y = -40; y < 40; y++) {
    search: for (let x = -40; x < 40; x++) {
      for (let dy = 0; dy < height; dy++) {
        for (let dx = 0; dx < width; dx++) if (!isWalkable(sim, x + dx, y + dy)) continue search
      }
      return { sim, x, y }
    }
  }
  throw new Error('В мире не нашлось ровного поля')
}

/** Станция 3×3 в (x + 6, y + 4) и электростанция рядом с ней, в одной зоне. */
function base(powered = true) {
  const { sim, x, y } = field()
  addCredits(sim, 1, 5000)
  const station = placeBuilding(sim.world, 'repairStation', x + 6, y + 4, 1)
  if (powered) placeBuilding(sim.world, 'generator', x + 9, y + 4, 1)
  return { sim, x, y, station, cx: x + 7.5, cy: y + 5.5 }
}

const value = (sim: Sim, entity: Entity) => sim.world.get(entity, Health)!.value
const damage = (sim: Sim, entity: Entity, to = 0.5) => (sim.world.get(entity, Health)!.value = to)

test('станция чинит свою технику в радиусе за кредиты; дальнюю и чужую не чинит', () => {
  const { sim, station, cx, cy } = base()
  const radius = BUILDINGS.repairStation.repair
  const near = spawnUnit(sim, 'tank', 1, Math.floor(cx - radius + 1), Math.floor(cy))
  const far = spawnUnit(sim, 'tank', 1, Math.floor(cx - radius - 4), Math.floor(cy))
  for (const entity of [near, far]) damage(sim, entity)
  const credits = creditsOf(sim, 1)

  seconds(sim, 1)
  expect(activeRepairs(sim).map((link) => link.from)).toEqual([station])
  expect(value(sim, near)).toBeGreaterThan(0.5)
  expect(creditsOf(sim, 1)).toBeLessThan(credits)
  seconds(sim, 40)
  expect(value(sim, near)).toBe(1)
  expect(value(sim, far)).toBe(0.5)

  // Чужого рядом не чинит; своих вооружённых рядом нет, чтобы его не расстреляли.
  const other = base()
  const foe = spawnUnit(other.sim, 'tank', 2, Math.floor(other.cx - 3), Math.floor(other.cy))
  damage(other.sim, foe)
  seconds(other.sim, 2)
  expect(value(other.sim, foe)).toBe(0.5)
})

test('станция чинит и здания рядом, но не строит и не чинит саму себя', () => {
  const { sim, x, y, station } = base()
  const radar = placeBuilding(sim.world, 'radar', x + 2, y + 4, 1)
  damage(sim, radar)
  damage(sim, station)
  // Площадка рядом со станцией: её станция не берёт, даже когда больше чинить нечего.
  const site = sim.world.spawn(Position({ x: x + 5, y: y + 9 }), Site({ type: 'pipe' }), Owner({ player: 1 }))
  seconds(sim, 3)
  expect(value(sim, radar)).toBeGreaterThan(0.5)
  expect(value(sim, station)).toBe(0.5)
  seconds(sim, 30)
  expect(value(sim, radar)).toBe(1)
  expect(repairLinks(sim).filter((link) => link.to === site)).toEqual([])
  expect(sim.world.get(site, Site)!.progress).toBe(0)
})

test('без энергии и выключенная станция не чинит', () => {
  const unpowered = base(false)
  const tank = spawnUnit(unpowered.sim, 'tank', 1, Math.floor(unpowered.cx - 3), Math.floor(unpowered.cy))
  damage(unpowered.sim, tank)
  seconds(unpowered.sim, 2)
  expect(value(unpowered.sim, tank)).toBe(0.5)

  const { sim, station, cx, cy } = base()
  const other = spawnUnit(sim, 'tank', 1, Math.floor(cx - 3), Math.floor(cy))
  sim.send(1, { type: 'work', building: station, on: false })
  damage(sim, other)
  seconds(sim, 2)
  expect(value(sim, other)).toBe(0.5)
  sim.send(1, { type: 'work', building: station, on: true })
  seconds(sim, 2)
  expect(value(sim, other)).toBeGreaterThan(0.5)
})

test('под огнём станция не чинит', () => {
  const { sim, cx, cy } = base()
  const tank = spawnUnit(sim, 'tank', 1, Math.floor(cx - 3), Math.floor(cy))
  damage(sim, tank)
  sim.world.get(tank, Health)!.hit = sim.time.tick
  seconds(sim, sim.rules.repairPause - 0.5)
  expect(value(sim, tank)).toBe(0.5)
  seconds(sim, 1.5)
  expect(value(sim, tank)).toBeGreaterThan(0.5)
})

test('пехота у работающей станции поправляется быстрее, вдали — как обычно', () => {
  const { sim, cx, cy } = base()
  const near = spawnUnit(sim, 'infantry', 1, Math.floor(cx - 3), Math.floor(cy))
  const far = spawnUnit(sim, 'infantry', 1, Math.floor(cx - 13), Math.floor(cy))
  damage(sim, near, 0.2)
  damage(sim, far, 0.2)
  seconds(sim, 2)
  expect(value(sim, far)).toBeCloseTo(0.2 + INFANTRY_REGEN * 2)
  expect(value(sim, near)).toBeCloseTo(0.2 + INFANTRY_REGEN * STATION_REGEN * 2)
})
