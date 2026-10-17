import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Armed, Assembly, Busy, Carrier, Inventory, Interceptor, Off, Producer, Repair, Research, Shield, Silo, Site, Trade, Transfer, UNITS, createSim, type Sim } from '@kharos/sim-ts'
import { cycleSeconds, recipeOf } from '@kharos/sim-ts/assembly'
import { BUILDINGS, placeBuilding, type BuildingType } from '@kharos/sim-ts/buildings'
import { BUSY_TICKS } from '@kharos/sim-ts/busy'

const TICK = 1 / 20
/** Сколько секунд здание значится работающим, кончив дело (HOLD в busy.ts). */
const HOLD = 2

/** Здание каждого случая — на своём месте, вне зон: энергии нет, и его состояние никто, кроме теста, не двигает. */
interface Case {
  type: BuildingType
  /** Что задать зданию перед каждым тиком. */
  set: (sim: Sim, entity: Entity) => void
  busy: boolean
}

const items = (sim: Sim, entity: Entity, goods: Record<string, number>) => {
  sim.world.get(entity, Inventory)!.items = { ...goods }
}
const full = (type: BuildingType) => BUILDINGS[type].inventory!

const CASES: Record<string, Case> = {
  'переработка с рудой': { type: 'smelter', set: (sim, e) => items(sim, e, { metalOre: 5 }), busy: true },
  'переработка без руды': { type: 'smelter', set: (sim, e) => items(sim, e, {}), busy: false },
  'переработка, готовому некуда лечь': { type: 'smelter', set: (sim, e) => items(sim, e, { metalOre: 1, metal: full('smelter') - 1 }), busy: false },
  'выключенная переработка': { type: 'smelter', set: (sim, e) => (items(sim, e, { metalOre: 5 }), sim.world.has(e, Off) || sim.world.add(e, Off())), busy: false },
  'переработка под разбором': {
    type: 'smelter',
    set: (sim, e) => (items(sim, e, { metalOre: 5 }), sim.world.has(e, Site) || sim.world.add(e, Site({ type: 'smelter', progress: 1000, demolish: true }))),
    busy: false,
  },
  'цех посреди сборки': { type: 'blockPlant', set: (sim, e) => (assembly(sim, e, cycleSeconds('blocks') / 2), items(sim, e, { blocks: full('blockPlant') })), busy: true },
  'цех собрал, готовому есть место': { type: 'blockPlant', set: (sim, e) => (assembly(sim, e, cycleSeconds('blocks')), items(sim, e, {})), busy: true },
  'цех собрал, готовому некуда лечь': { type: 'blockPlant', set: (sim, e) => (assembly(sim, e, cycleSeconds('blocks')), items(sim, e, { blocks: full('blockPlant') })), busy: false },
  'цех с сырьём': { type: 'blockPlant', set: (sim, e) => (assembly(sim, e, 0), items(sim, e, Object.fromEntries(recipeOf('blocks')))), busy: true },
  'цех с сырьём, но без места': {
    type: 'blockPlant',
    set: (sim, e) => (assembly(sim, e, 0), items(sim, e, { ...Object.fromEntries(recipeOf('blocks')), blocks: full('blockPlant') })),
    busy: false,
  },
  'цех без сырья': { type: 'blockPlant', set: (sim, e) => (assembly(sim, e, 0), items(sim, e, {})), busy: false },
  'завод с материалами заказа': { type: 'factory', set: (sim, e) => (produce(sim, e, 0), items(sim, e, { ...UNITS.buggy.materials })), busy: true },
  'завод без материалов': { type: 'factory', set: (sim, e) => (produce(sim, e, 0), items(sim, e, {})), busy: false },
  'завод с начатым заказом': { type: 'factory', set: (sim, e) => (produce(sim, e, 1), items(sim, e, {})), busy: true },
  'завод без заказов': { type: 'factory', set: (sim, e) => (items(sim, e, { ...UNITS.buggy.materials }), sim.world.set(e, Producer, { queue: [], progress: 0 })), busy: false },
  'лаборатория ведёт тему': { type: 'lab', set: (sim, e) => research(sim, e, 1, ''), busy: true },
  'лаборатория без энергии': { type: 'lab', set: (sim, e) => research(sim, e, 0, 'power'), busy: true },
  'лаборатория без темы': { type: 'lab', set: (sim, e) => research(sim, e, 0, 'topic'), busy: false },
  'ремонтная станция чинит': { type: 'repairStation', set: (sim, e) => sim.world.set(e, Repair, { target: e }), busy: true },
  'ремонтной станции чинить некого': { type: 'repairStation', set: (sim, e) => sim.world.set(e, Repair, { target: -1 }), busy: false },
  'шахта готовит боеголовку': { type: 'silo', set: (sim, e) => sim.world.set(e, Silo, { ticks: 10, ready: false, start: -1 }), busy: true },
  'шахта с готовой боеголовкой': { type: 'silo', set: (sim, e) => sim.world.set(e, Silo, { ticks: 10, ready: true, start: -1 }), busy: false },
  'шахта запускает': { type: 'silo', set: (sim, e) => sim.world.set(e, Silo, { ticks: 10, ready: true, start: 3 }), busy: true },
  'перехватчик готовит ракету': { type: 'interceptor', set: (sim, e) => sim.world.set(e, Interceptor, { ticks: 5 }), busy: true },
  'перехватчик готов': { type: 'interceptor', set: (sim, e) => sim.world.set(e, Interceptor, { ticks: 0 }), busy: false },
  'космопорт с рейсом': { type: 'spaceport', set: (sim, e) => sim.world.has(e, Trade) || sim.world.add(e, Trade()), busy: true },
  'космопорт с подарком': { type: 'spaceport', set: (sim, e) => sim.world.has(e, Transfer) || sim.world.add(e, Transfer()), busy: true },
  'космопорт без дел': { type: 'spaceport', set: () => {}, busy: false },
  'щит заряжается': { type: 'shieldGenerator', set: (sim, e) => sim.world.set(e, Shield, { charge: 0 }), busy: true },
  'щит полон': { type: 'shieldGenerator', set: (sim, e) => sim.world.set(e, Shield, { charge: BUILDINGS.shieldGenerator.shield!.capacity }), busy: false },
  'турель с целью': { type: 'laserTurret', set: (sim, e) => armed(sim, e, e, 0), busy: true },
  'турель перезаряжается': { type: 'laserTurret', set: (sim, e) => armed(sim, e, -1, 1), busy: true },
  'турель без дела': { type: 'laserTurret', set: (sim, e) => armed(sim, e, -1, 0), busy: false },
}

function assembly(sim: Sim, entity: Entity, progress: number) {
  sim.world.set(entity, Assembly, { recipe: 'blocks', on: true, progress })
}
function produce(sim: Sim, entity: Entity, progress: number) {
  sim.world.set(entity, Producer, { queue: ['buggy'], progress })
}
function research(sim: Sim, entity: Entity, speed: number, lack: string) {
  if (sim.world.has(entity, Research)) sim.world.set(entity, Research, { speed, lack })
  else sim.world.add(entity, Research({ speed, lack }))
}
function armed(sim: Sim, entity: Entity, target: number, cooldown: number) {
  for (const turret of sim.world.get(entity, Carrier)!.turrets) sim.world.set(turret as Entity, Armed, { target, cooldown })
}

/** Ставит здания случаев в ряд и гоняет тики, перед каждым задавая им состояние. */
function run(cases: readonly Case[], ticks: number) {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  const buildings = cases.map(({ type }, i) => placeBuilding(sim.world, type, -120 + i * 6, -120, 1))
  for (let i = 0; i < ticks; i++) {
    cases.forEach(({ set }, j) => set(sim, buildings[j]))
    sim.advance(TICK)
  }
  return { sim, buildings }
}

test('метку Busy получает потребитель, у которого есть дело: каждый вид — по-своему', () => {
  const names = Object.keys(CASES)
  const { sim, buildings } = run(Object.values(CASES), BUSY_TICKS)
  const busy = Object.fromEntries(names.map((name, i) => [name, sim.world.has(buildings[i], Busy)]))
  expect(busy).toEqual(Object.fromEntries(names.map((name) => [name, CASES[name].busy])))
})

test('метку ставят раз в BUSY_TICKS; кончив дело, здание держит её HOLD секунд, а выключенное теряет сразу', () => {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  const [idle, off] = [0, 1].map((i) => placeBuilding(sim.world, 'smelter', -120 + i * 6, -120, 1))
  let ore = true
  const changes: Record<number, number[]> = { [idle]: [], [off]: [] }
  for (let i = 0; i < 20 * 10; i++) {
    if (i === 20) ore = false
    if (i === 20) sim.world.add(off, Off())
    for (const entity of [idle, off]) items(sim, entity, ore || entity === off ? { metalOre: 5 } : {})
    const before = [idle, off].map((entity) => sim.world.has(entity, Busy))
    sim.advance(TICK)
    ;[idle, off].forEach((entity, j) => before[j] !== sim.world.has(entity, Busy) && changes[entity].push(i))
  }
  // Оба получили метку на первой разметке и потеряли по разу.
  expect(changes[idle].length).toBe(2)
  expect(changes[off].length).toBe(2)
  const [on] = changes[idle]
  expect(on).toBeLessThan(BUSY_TICKS)
  // Перемены — только в тики разметки.
  for (const tick of [...changes[idle], ...changes[off]]) expect((tick - on) % BUSY_TICKS).toBe(0)
  // Выключенный теряет метку на первой же разметке после выключения.
  expect(changes[off][1] - 20).toBeLessThan(BUSY_TICKS)
  // Оставшийся без руды держит её HOLD секунд после последней разметки за работой.
  const lastSeen = 20 - ((20 - on) % BUSY_TICKS || BUSY_TICKS)
  expect(changes[idle][1] - lastSeen).toBe(Math.ceil(HOLD / TICK / BUSY_TICKS) * BUSY_TICKS)
})
