import { expect, test } from 'bun:test'
import { DEFAULT_CONFIG } from '../src/map/terrain'
import { createSim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { Hauler, Path } from '../src/sim/components'
import { seekBeam } from '../src/sim/hauling'
import { approach, beamFor } from '../src/sim/inventory'
import { SEARCH_LIMIT, searchedTiles } from '../src/sim/path'
import { spawnUnit } from '../src/sim/units'

test('грузовик едет к зданию дальше окна поиска пути: путь до края окна, остальное — на ходу', () => {
  const sim = createSim({ generator: { ...DEFAULT_CONFIG, blank: true }, size: 1024 })
  const building = placeBuilding(sim.world, 'metalYard', 200, 0, 1)
  const truck = spawnUnit(sim, 'truck', 1, -150, 0)
  expect(approach(sim, truck, building, 3)).toBe(true)
  expect(sim.world.get(truck, Path)?.goalX).toBeGreaterThan(190)
})

test('места в замкнутом кармане между зданиями грузовик не пробует: подъезжает с открытой стороны', () => {
  const sim = createSim({ generator: { ...DEFAULT_CONFIG, blank: true }, size: 256 })
  const building = placeBuilding(sim.world, 'blockPlant', 0, 0, 1)
  placeBuilding(sim.world, 'blockPlant', 0, 2, 1)
  // Карман x = -1, y = 0…3: слева, сверху и снизу — склады, справа — заводы. Он ближе к грузовику, чем открытая сторона.
  for (let x = -2; x <= 3; x++) placeBuilding(sim.world, 'khariteVault', x, -1, 1)
  for (let y = 0; y <= 4; y++) placeBuilding(sim.world, 'khariteVault', -2, y, 1)
  placeBuilding(sim.world, 'khariteVault', -1, 4, 1)
  const truck = spawnUnit(sim, 'truck', 1, -10, 1)
  expect(approach(sim, truck, building, 2)).toBe(true)
  const { goalX, goalY } = sim.world.get(truck, Path)!
  expect(goalX === -1 && goalY >= 0 && goalY <= 3).toBe(false)
})

test('подъезд к зданию за кольцом труб не съедает тик: за тик грузовики осматривают не больше нормы', () => {
  const sim = createSim({ generator: { ...DEFAULT_CONFIG, blank: true }, size: 512 })
  const building = placeBuilding(sim.world, 'metalYard', 0, 0, 1)
  // Кольцо труб вокруг склада: внутри места хватает, снаружи — вся карта, и поиск пути обходит её всю.
  for (let i = -10; i <= 12; i++) {
    for (const [x, y] of [[i, -10], [i, 12], [-10, i], [12, i]]) placeBuilding(sim.world, 'pipe', x, y, 1)
  }
  const trucks = [-30, -40, -50].map((x) => spawnUnit(sim, 'truck', 1, x, 0))
  const before = searchedTiles()
  for (const truck of trucks) seekBeam(sim, truck, building, beamFor(sim, truck, building))
  // Счётчик поиска пути учитывает и тайл, на котором поиск остановился.
  expect(searchedTiles() - before).toBeLessThanOrEqual(SEARCH_LIMIT + 1)
  // Первый искал и промахнулся; остальным не хватило нормы — они ищут в свою следующую очередь, без промаха.
  expect(trucks.map((truck) => sim.world.get(truck, Hauler)!.misses)).toEqual([1, 0, 0])
})
