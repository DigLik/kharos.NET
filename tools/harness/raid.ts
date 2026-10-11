/**
 * Раздел «набег»: нападение на экономику.
 *
 * Сцена — живая линия добычи на трубах: шахта на металле, плавильня, двор металла, главное здание и электростанция
 * в одной сети. Линия разгоняется и меряется (сколько металла в секунду приходит во двор), потом к ней подходит
 * отряд с одной целью: труба у шахты, сама шахта или двор с накопленным металлом.
 *
 * Строки — отряды разной цены, по три цели на каждый. Меряется: за сколько секунд цель снесена, чего это стоит
 * защитнику (отстроить заново и простой линии, пока один строитель отстраивает), сколько груза осталось на земле —
 * дроп ничей, его увозит любой грузовик, и для нападающего это добыча, — и сколько отряд теряет, если цель
 * стережёт одна турель с полным боезапасом. Набег окупается, когда ущерб и добыча больше потерь.
 */
import type { Entity } from '../../src/ecs'
import {
  BUILDINGS, BUILD_RATE, Building, Drop, Inventory, Owner, Position, RESOURCE_SPECS, UNITS, Unit, WARES, amountOf, isPipe, networkOf, connectAll,
  spawnBattle, type BuildingType, type Sim, type UnitType,
} from '../../src/sim'
import { placeBuilding } from '../../src/sim/buildings'
import { depositIn } from '../../src/sim'
import { TICK, centerOf, clock, newSim, placeNear, placeableNear, printTable, rings, round, seconds, spotNear } from './lib'

/** На сколько тайлов от цели встают атакующие. */
const GAP = 12
/** Сколько секунд отводится набегу. */
const LIMIT = 120
/** Как часто атакующим напоминают цель, в секундах. */
const ORDERS_EVERY = 0.25
const ORDERS_TICKS = Math.round(ORDERS_EVERY / TICK)
/** Разгон линии и окно замера её потока, в секундах. */
const WARMUP = 60
const WINDOW = 60
/** Сколько секунд после набега смотрят, идёт ли металл во двор. */
const AFTER = 20
/** Игрок-защитник: его линия добычи. Атакующие — игрок 1, иначе свои цели не бьются. */
const DEFENDER = 2

/** Отряд атакующих: имя для таблицы и состав. Все юниты отряда одного вида. */
interface Squad {
  name: string
  units: UnitType[]
}

/** Отряды, которыми пробуют набег: от дешёвой пехоты до винтокрылов. */
const SQUADS: Squad[] = [
  { name: '12 пехоты', units: Array<UnitType>(12).fill('infantry') },
  { name: '8 ракетчиков', units: Array<UnitType>(8).fill('rocketeer') },
  { name: '4 багги', units: Array<UnitType>(4).fill('buggy') },
  { name: '4 танка', units: Array<UnitType>(4).fill('tank') },
  { name: '4 винтокрыла', units: Array<UnitType>(4).fill('gunship') },
]

/** Что бьёт отряд. */
type Target = 'pipe' | 'mine' | 'yard'
const TARGETS: { target: Target; name: string }[] = [
  { target: 'pipe', name: 'труба' },
  { target: 'mine', name: 'шахта' },
  { target: 'yard', name: 'двор' },
]

/** Линия добычи: здания сети и труба, которая связывает шахту с остальными. */
interface Line {
  sim: Sim
  mine: Entity
  yard: Entity
  pipe: Entity
}

/** Ставит здание у шахты, не задевая место для грузовиков под ней. */
function placeNearMine(sim: Sim, spot: { x: number; y: number }, type: BuildingType, x: number, y: number) {
  const { width, height } = BUILDINGS[type]
  const mine = BUILDINGS.mine
  const ok = (tx: number, ty: number) => {
    if (!placeableNear(sim, type, tx, ty, 1, 0)) return false
    const apart = tx >= spot.x + mine.width + 2 || tx + width + 2 <= spot.x
    return apart || ty >= spot.y + mine.height + 2 || ty + height + 2 <= spot.y
  }
  const tile = spotNear(x, y, ok, 14)
  return tile && placeBuilding(sim.world, type, tile.x, tile.y, DEFENDER)
}

/** Линия добычи на трубах у ближайшего к началу мира месторождения металла. */
function lineScene(): Line {
  const sim = newSim()
  for (const cell of rings(0, 0, 6)) {
    const spot = depositIn(sim, cell.x, cell.y)
    if (!spot || spot.kind !== 'metal') continue
    // Главное здание — первым: с его сетью автоподключение соединяет остальные.
    const core = placeNearMine(sim, spot, 'command', spot.x + 8, spot.y)
    const mine = placeBuilding(sim.world, 'mine', spot.x, spot.y, DEFENDER)
    const yard = placeNearMine(sim, spot, 'metalYard', spot.x + 4, spot.y + 5)
    const refinery = placeNearMine(sim, spot, 'smelter', spot.x + 4, spot.y - 5)
    const generator = placeNearMine(sim, spot, 'generator', spot.x + 8, spot.y - 5)
    if (!yard || !core || !refinery || !generator) break
    connectAll(sim, DEFENDER)
    const network = networkOf(sim, mine)
    if (!network || !network.buildings.includes(yard) || !network.buildings.includes(refinery)) break
    // Труба у самой шахты: перерезать её — отрезать шахту от плавильни.
    const at = centerOf(sim, mine)
    let pipe: Entity | undefined
    let best = Infinity
    for (const entity of network.buildings) {
      if (!isPipe(sim.world.get(entity, Building)!.type)) continue
      const center = centerOf(sim, entity)
      const distance = Math.hypot(center.x - at.x, center.y - at.y)
      if (distance < best) [pipe, best] = [entity, distance]
    }
    if (pipe === undefined) break
    return { sim, mine, yard, pipe }
  }
  sim.destroy()
  throw new Error('Не нашлось места под линию добычи у месторождения металла')
}

/** Сколько металла лежит во дворе. */
const yardMetal = (sim: Sim, yard: Entity) => amountOf(sim.world.get(yard, Inventory) ?? { items: {}, capacity: 0, accepts: [], limits: {} }, 'metal')

/** Сколько стоит груз, брошенный на землю, по цене продажи. */
function lootValue(sim: Sim) {
  let value = 0
  for (const [, , inventory] of sim.world.query(Drop, Inventory)) {
    for (const ware of WARES) value += amountOf(inventory, ware) * (ware in RESOURCE_SPECS ? RESOURCE_SPECS[ware as keyof typeof RESOURCE_SPECS].price : 0)
  }
  return value
}

/** Живые юниты игрока. */
function unitsOf(sim: Sim, player: number) {
  const units: Entity[] = []
  for (const [entity, , owner] of sim.world.query(Unit, Owner)) if (owner.player === player) units.push(entity)
  return units
}

/** Итог одного набега. */
interface Result {
  /** Поток линии до набега: металла в секунду. */
  flow: number
  /** Секунда, когда цель пала: 0 — не снесли. */
  downAt: number
  /** Поток после набега: идёт ли ещё металл во двор. */
  after: number
  /** Груз на земле после набега, кр по цене продажи. */
  loot: number
  /** Сколько кредитов отряда погибло. */
  lost: number
}

/** Один набег: живая линия, отряд, цель и замер. guarded — цель стережёт турель с полным боезапасом. */
function raid(squad: Squad, target: Target, guarded: boolean): Result {
  const { sim, mine, yard, pipe } = lineScene()
  seconds(sim, WARMUP)
  const before = yardMetal(sim, yard)
  seconds(sim, WINDOW)
  const flow = (yardMetal(sim, yard) - before) / WINDOW

  const aim = target === 'pipe' ? pipe : target === 'mine' ? mine : yard
  const type = sim.world.get(aim, Building)!.type
  const place = { ...sim.world.get(aim, Position)! }
  const center = centerOf(sim, aim)
  if (guarded) {
    const turret = placeNear(sim, 'turret', center.x + 2, center.y + 2, DEFENDER)
    const ammo = turret === undefined ? undefined : sim.world.get(turret, Inventory)
    if (ammo) ammo.items.ammo = ammo.capacity
  }
  spawnBattle(sim, 1, Math.round(center.x), Math.round(center.y), squad.units, [], GAP)
  const cost = (units: Entity[]) => units.reduce((sum, entity) => sum + UNITS[sim.world.get(entity, Unit)!.type].cost, 0)
  const sent = cost(unitsOf(sim, 1))
  const standing = () => {
    for (const [entity, building, position] of sim.world.query(Building, Position)) {
      if (building.type === type && position.x === place.x && position.y === place.y) return entity
    }
    return undefined
  }
  let downAt = 0
  for (let tick = 0; tick < Math.round(LIMIT / TICK) && !downAt; tick++) {
    if (tick % ORDERS_TICKS === 0) {
      const units = unitsOf(sim, 1)
      const alive = standing()
      if (!units.length || alive === undefined) break
      sim.send(1, { type: 'attack', units, target: alive })
    }
    sim.advance(TICK)
    if (standing() === undefined) downAt = (tick + 1) * TICK
  }
  const lost = sent - cost(unitsOf(sim, 1))
  const loot = lootValue(sim)
  // Двор снесли — мерить поток негде: линия без хранилища стоит.
  let after = 0
  if (sim.world.has(yard, Inventory) && target !== 'yard') {
    const left = yardMetal(sim, yard)
    seconds(sim, AFTER)
    after = Math.max(0, (yardMetal(sim, yard) - left) / AFTER)
  }
  sim.destroy()
  return { flow, downAt, after, loot, lost }
}

/** Печатает таблицу набегов. */
export function runRaids() {
  console.log('\nНабег на экономику: шахта, плавильня и двор металла в одной сети труб')
  console.log(`  линия разгоняется ${WARMUP + WINDOW} с, потом отряд в ${GAP} тайлах получает одну цель; «с турелью» — цель стережёт пулемётная турель`)
  const rows: (string | number)[][] = []
  let flow = 0
  const costs: Record<Target, BuildingType> = { pipe: 'pipe', mine: 'mine', yard: 'metalYard' }
  for (const squad of SQUADS) {
    const price = squad.units.reduce((sum, type) => sum + UNITS[type].cost, 0)
    for (const { target, name } of TARGETS) {
      const open = raid(squad, target, false)
      const guarded = raid(squad, target, true)
      flow = open.flow
      const rebuild = BUILDINGS[costs[target]].cost
      // Простой: линия стоит, пока один строитель отстраивает снесённое. Труба режет линию только пока её нет.
      const stopped = target === 'yard' || open.after < open.flow * 0.5
      const idle = stopped ? (rebuild / BUILD_RATE) * open.flow * RESOURCE_SPECS.metal.price : 0
      const damage = rebuild + idle
      const gain = damage + guarded.loot
      rows.push([
        squad.name, price, name,
        open.downAt ? clock(open.downAt) : '—',
        rebuild, round(idle), round(open.loot),
        guarded.downAt ? clock(guarded.downAt) : '—',
        round(guarded.lost),
        !guarded.downAt ? 'отбит' : guarded.lost <= gain ? 'да' : 'нет',
      ])
    }
  }
  printTable(['отряд', 'цена', 'цель', 'снесли', 'стройка', 'простой', 'добыча', 'с турелью', 'потери', 'окупился'], rows, 14, 10)
  console.log('  снесли — за сколько без обороны; стройка — сколько защитнику отстроить; простой — выручка, потерянная, пока')
  console.log('  один строитель отстраивает; добыча — груз на земле по цене продажи: дроп ничей, его увозит любой грузовик;')
  console.log('  с турелью — за сколько снесли под огнём турели, потери — сколько кредитов отряда при этом погибло;')
  console.log('  окупился — потери не больше ущерба (стройка + простой) и добычи; «отбит» — цель под турелью устояла')
  console.log(`  линия добывает металла в секунду: ${round(flow, 2)}, это ${round(flow * RESOURCE_SPECS.metal.price, 1)} кр/с выручки, пока цела`)
}
