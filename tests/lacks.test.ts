import { expect, test } from 'bun:test'
import { lacksOf } from '../src/game/lacks'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { BUILDINGS, Inventory, UNITS, canPlace, createSim, type BuildingType, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { Assembly } from '../src/sim/components'
import { addCredits } from '../src/sim/economy'
import { grantResearch } from '../src/sim/research'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Главное здание, электростанция и здания из списка в ряд, под общей трубой. */
function base(types: BuildingType[]) {
  const sim = createSim(options)
  addCredits(sim, 1, 10000)
  grantResearch(sim, 1)
  const layout: [BuildingType, number][] = [['command', 0], ['generator', 4]]
  let at = 7
  for (const type of types) {
    layout.push([type, at])
    at += BUILDINGS[type].width + 1
  }
  for (let y = -100; y < 100; y++) {
    for (let x = -100; x < 100; x++) {
      const piped = Array.from({ length: at }, (_, dx) => canPlace(sim, 'pipe', x + dx, y - 1)).every(Boolean)
      if (!piped || !layout.every(([type, dx]) => canPlace(sim, type, x + dx, y))) continue
      const buildings = layout.map(([type, dx]) => placeBuilding(sim.world, type, x + dx, y, 1))
      for (let dx = 0; dx < at; dx++) placeBuilding(sim.world, 'pipe', x + dx, y - 1, 1)
      return { sim, buildings: buildings.slice(2) }
    }
  }
  throw new Error('Не нашлось места')
}

test('знак нехватки: завод с заказом без материалов ждёт первый недостающий груз; без заказа и выключенный — не ждут', () => {
  const { sim, buildings: [factory, plant] } = base(['factory', 'blockPlant'])
  seconds(sim, 0.5)
  // Заказа нет — заводу ничего не нужно; цех выключен с постройки.
  expect(lacksOf(sim, 1).size).toBe(0)

  const [material] = Object.keys(UNITS.buggy.materials)
  sim.send(1, { type: 'produce', producer: factory, unit: 'buggy' })
  seconds(sim, 0.5)
  expect(lacksOf(sim, 1).get(factory)).toBe(material as never)
  // Чужому игроку о наших складах судить не по чему.
  expect(lacksOf(sim, 2).size).toBe(0)

  // Привезли — завод взялся за заказ и больше ничего не ждёт.
  sim.world.get(factory, Inventory)!.items = { ...UNITS.buggy.materials }
  seconds(sim, 0.5)
  expect(lacksOf(sim, 1).has(factory)).toBe(false)

  // Включённый цех без сырья ждёт первое сырьё рецепта; выключенный — нет.
  sim.send(1, { type: 'work', building: plant, on: true })
  seconds(sim, 0.5)
  expect(sim.world.get(plant, Assembly)!.on).toBe(true)
  expect(lacksOf(sim, 1).has(plant)).toBe(true)
  sim.send(1, { type: 'work', building: plant, on: false })
  seconds(sim, 0.5)
  expect(lacksOf(sim, 1).has(plant)).toBe(false)
})
