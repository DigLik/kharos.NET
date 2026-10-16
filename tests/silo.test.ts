import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import {
  BUILDINGS, Burst, Doomed, GLARE, Glare, Health, Inventory, LAUNCH_MARK, Mark, Missile, Off, Silo, WARNING, canPlace, createSim, creditsOf, isUnlocked, protect, protectionLeft, shownTo, warheadLack,
  type BuildingType, type Sim,
} from '../src/sim'
import { STORES, placeBuilding, storeFor } from '../src/sim/buildings'
import { buildingHp } from '../src/sim/combat'
import { addCredits } from '../src/sim/economy'
import type { Amounts, Ware } from '../src/sim/resources'
import { spawnUnit } from '../src/sim/units'
import { wireOf } from '../src/net/wire'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { protection: 0, techTree: false } }
const TICK = 1 / 20
const { warhead } = BUILDINGS.silo

const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/**
 * База игрока 1 в ряд в одной сети труб: главное здание, две электростанции, шахта и все хранилища. stash кладёт
 * готовое в хранилища сети.
 */
function base(sim = createSim(options)) {
  addCredits(sim, 1, 10000)
  const layout: [BuildingType, number][] = [['command', 0], ['generator', 4], ['generator', 7], ['silo', 10]]
  let at = 14
  for (const type of STORES) {
    layout.push([type, at])
    at += BUILDINGS[type].width + 1
  }
  for (let y = -100; y < 100; y++) {
    for (let x = -100; x < 100; x++) {
      if (!layout.every(([type, dx]) => canPlace(sim, type, x + dx, y))) continue
      let piped = true
      for (let dx = 0; dx < at && piped; dx++) piped = canPlace(sim, 'pipe', x + dx, y - 1)
      if (!piped) continue
      const buildings = layout.map(([type, dx]) => placeBuilding(sim.world, type, x + dx, y, 1))
      for (let dx = 0; dx < at; dx++) placeBuilding(sim.world, 'pipe', x + dx, y - 1, 1)
      const shelves = buildings.slice(4)
      const stash = (items: Amounts) => {
        for (const [item, amount] of Object.entries(items) as [Ware, number][]) sim.world.get(shelves[STORES.indexOf(storeFor(item)!)], Inventory)!.items[item] = amount
      }
      const stock = (item: Ware) => sim.world.get(shelves[STORES.indexOf(storeFor(item)!)], Inventory)!.items[item] ?? 0
      return { sim, x, y, silo: buildings[3], stash, stock }
    }
  }
  throw new Error('Не нашлось места под базу')
}

/** База с готовой боеголовкой в шахте. */
function armed() {
  const found = base()
  Object.assign(found.sim.world.get(found.silo, Silo)!, { ready: true })
  return found
}

/** Ровный кусок земли в стороне от базы, где можно поставить здание, — левый верхний тайл. */
function spot(sim: Sim, fromX: number, fromY: number, type: BuildingType = 'generator') {
  for (let distance = 0; distance < 200; distance++) {
    for (let dy = -distance; dy <= distance; dy++) {
      for (let dx = -distance; dx <= distance; dx++) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) !== distance) continue
        if (canPlace(sim, type, fromX + dx, fromY + dy)) return { x: fromX + dx, y: fromY + dy }
      }
    }
  }
  throw new Error('Не нашлось места')
}

const missiles = (sim: Sim) => [...sim.world.query(Missile)].map(([entity]) => entity)
/** Метки вида kind. Строка обхода — один массив на весь обход: собирать надо по ходу, а не разворачивая обход. */
const marks = (sim: Sim, kind: 'launch' | 'strike') => {
  const found: Entity[] = []
  for (const [entity, mark] of sim.world.query(Mark)) if (mark.kind === kind) found.push(entity)
  return found
}

test('шахта собирает боеголовку за кредиты, стержни и компоненты из сети и за время при полной энергии', () => {
  const { sim, silo, stash, stock } = base()
  expect(warheadLack(sim, 1, silo)).toBe('materials')
  stash({ rods: 25, parts: 12 })
  expect(warheadLack(sim, 1, silo)).toBeUndefined()
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'warhead', silo })
  sim.advance(TICK)
  expect(creditsOf(sim, 1)).toBeCloseTo(credits - warhead.cost, 0)
  expect(stock('rods')).toBe(5)
  expect(stock('parts')).toBe(2)
  expect(warheadLack(sim, 1, silo)).toBe('busy')
  seconds(sim, warhead.seconds - 1)
  expect(sim.world.get(silo, Silo)!.ready).toBe(false)
  seconds(sim, 2)
  expect(sim.world.get(silo, Silo)!.ready).toBe(true)
  // Одна за раз: с готовой новую не начать.
  expect(warheadLack(sim, 1, silo)).toBe('busy')
})

test('выключенная шахта сборку не ведёт, отмена возвращает цену', () => {
  const { sim, silo, stash, stock } = base()
  stash({ rods: 20, parts: 10 })
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'warhead', silo })
  sim.send(1, { type: 'work', building: silo, on: false })
  seconds(sim, 5)
  expect(sim.world.has(silo, Off)).toBe(true)
  expect(sim.world.get(silo, Silo)!.progress).toBe(0)
  sim.send(1, { type: 'cancelWarhead', silo })
  sim.advance(TICK)
  expect(sim.world.get(silo, Silo)!.ticks).toBe(0)
  // Главное здание за эти секунды принесло кредит-другой.
  expect(Math.abs(creditsOf(sim, 1) - credits)).toBeLessThan(5)
  expect(stock('rods')).toBe(20)
  expect(stock('parts')).toBe(10)
})

test('пуск: ракета стартует через срок пуска, отменённый пуск оставляет боеголовку в шахте', () => {
  const { sim, x, y, silo } = armed()
  sim.send(1, { type: 'launch', silo, x: x + 100, y })
  seconds(sim, warhead.launch / 2)
  sim.send(1, { type: 'abortLaunch', silo })
  seconds(sim, warhead.launch)
  expect(missiles(sim)).toEqual([])
  expect(sim.world.get(silo, Silo)!).toMatchObject({ ready: true, start: -1 })

  sim.send(1, { type: 'launch', silo, x: x + 100, y })
  seconds(sim, warhead.launch - 1)
  expect(missiles(sim)).toEqual([])
  seconds(sim, 1.1)
  expect(missiles(sim).length).toBe(1)
  expect(sim.world.get(silo, Silo)!.ready).toBe(false)
  // Цель вне карты не принимается.
  Object.assign(sim.world.get(silo, Silo)!, { ready: true })
  sim.send(1, { type: 'launch', silo, x: 100000, y })
  sim.advance(TICK)
  expect(sim.world.get(silo, Silo)!.start).toBe(-1)
})

test('место пуска видят игроки с радаром, и только LAUNCH_MARK секунд', () => {
  const { sim, x, y, silo } = armed()
  // У игрока 2 далеко есть радар, у игрока 3 — только электростанция.
  const far = spot(sim, x + 300, y, 'radar')
  placeBuilding(sim.world, 'radar', far.x, far.y, 2)
  const other = spot(sim, x - 300, y)
  placeBuilding(sim.world, 'generator', other.x, other.y, 3)
  sim.send(1, { type: 'launch', silo, x: x + 200, y })
  seconds(sim, warhead.launch + 0.1)
  const [mark] = marks(sim, 'launch')
  expect(mark).toBeDefined()
  expect(shownTo(sim, 2, mark)).toBe(true)
  expect(shownTo(sim, 3, mark)).toBe(false)
  expect(shownTo(sim, 1, mark)).toBe(true)
  seconds(sim, LAUNCH_MARK)
  expect(marks(sim, 'launch')).toEqual([])
})

test('ракета летит со скоростью вида, её видят все, точку удара весь полёт — те, чьё под ней, и взрыв бьёт всех', () => {
  const { sim, x, y, silo } = armed()
  // Цель — в 160 тайлах: 20 секунд полёта. У цели — здание и юнит игрока 2, на краю радиуса — его же здание.
  const target = spot(sim, x + 170, y)
  const near = placeBuilding(sim.world, 'generator', target.x, target.y, 2)
  const unit = spawnUnit(sim, 'tank', 2, target.x + 3, target.y + 1)
  const edge = spot(sim, target.x + 10, target.y)
  const rim = placeBuilding(sim.world, 'generator', edge.x, edge.y, 2)
  const away = spot(sim, target.x, target.y + 60)
  placeBuilding(sim.world, 'generator', away.x, away.y, 3)
  const toX = target.x + 1
  const toY = target.y + 1
  sim.send(1, { type: 'launch', silo, x: toX, y: toY })
  seconds(sim, warhead.launch + 0.1)
  const [missile] = missiles(sim)
  const state = sim.world.get(missile, Missile)!
  const flight = Math.hypot(toX + 0.5 - state.fromX, toY + 0.5 - state.fromY) / warhead.speed
  expect(flight).toBeGreaterThan(WARNING + 2)

  // Чужому ракета уходит без точки удара.
  const foreign = JSON.parse(wireOf(sim.world, missile, 2, sim.time.tick).parts.get(Missile.key)!)
  expect(foreign.blind).toBe(true)
  expect(Math.hypot(foreign.toX - foreign.fromX, foreign.toY - foreign.fromY)).toBeCloseTo(1, 1)
  const own = JSON.parse(wireOf(sim.world, missile, 1, sim.time.tick).parts.get(Missile.key)!)
  expect(own.toX).toBe(toX + 0.5)

  // Ракету видят все, где бы она ни была; точку удара — со старта, но только тот, чьё под ней.
  const [strike] = marks(sim, 'strike')
  expect(strike).toBeDefined()
  for (const player of [1, 2, 3]) expect(shownTo(sim, player, missile)).toBe(true)
  expect(sim.vision.sees(3, state.x, state.y)).toBe(false)
  expect(shownTo(sim, 2, strike)).toBe(true)
  expect(shownTo(sim, 3, strike)).toBe(false)
  expect(sim.vision.sees(3, toX, toY)).toBe(false)

  // Игрок 3 завёл юнит под удар — через секунду точка видна и ему; увёл — пропала.
  const scout = spawnUnit(sim, 'tank', 3, target.x + 4, target.y + 4)
  seconds(sim, 1.1)
  expect(shownTo(sim, 3, strike)).toBe(true)
  sim.world.destroy(scout)
  seconds(sim, 1.1)
  expect(shownTo(sim, 3, strike)).toBe(false)

  seconds(sim, flight - 2.2)
  expect(missiles(sim)).toEqual([])
  expect(marks(sim, 'strike')).toEqual([])
  sim.advance(TICK)
  expect(sim.world.has(near, Health)).toBe(false)
  expect(sim.world.has(unit, Health)).toBe(false)
  // На краю радиуса урон меньше полного, но есть.
  const left = sim.world.get(rim, Health)?.value ?? 0
  expect(left).toBeLessThan(1)
  expect(1 - left).toBeLessThan(warhead.blast.damage / buildingHp('generator'))
  expect([...sim.world.query(Burst)].length).toBe(0)
  // Вспышка открывает круг взрыва всем, даже игроку 3, чьё далеко, — и гаснет через GLARE секунд.
  expect(sim.vision.sees(3, toX, toY)).toBe(true)
  expect(sim.vision.sees(3, toX + warhead.blast.radius - 1, toY)).toBe(true)
  seconds(sim, GLARE)
  expect(sim.vision.sees(3, toX, toY)).toBe(false)
  expect([...sim.world.query(Glare)].length).toBe(0)
})

test('шахта с готовой боеголовкой при разрушении рвётся, как реактор; пустая — нет', () => {
  const { sim, x, y, silo } = armed()
  const own = placeBuilding(sim.world, 'wall', x + 9, y + 1, 1)
  sim.world.add(silo, Doomed({ left: 1 }))
  sim.advance(TICK)
  expect([...sim.world.query(Burst)].length).toBe(1)
  sim.advance(TICK)
  expect(sim.world.get(own, Health)?.value ?? 0).toBeLessThan(1)

  const empty = base()
  empty.sim.world.add(empty.silo, Doomed({ left: 1 }))
  empty.sim.advance(TICK)
  expect([...empty.sim.world.query(Burst)].length).toBe(0)
})

test('пуск снимает с игрока защиту новичка', () => {
  const sim = createSim({ ...options, rules: { protection: 10, techTree: false } })
  const { silo, x, y } = base(sim)
  Object.assign(sim.world.get(silo, Silo)!, { ready: true })
  protect(sim, 1)
  expect(protectionLeft(sim, 1)).toBeGreaterThan(0)
  sim.send(1, { type: 'launch', silo, x: x + 50, y })
  sim.advance(TICK)
  expect(protectionLeft(sim, 1)).toBe(0)
})

test('шахту открывают техцентр, завод стержней и «Боеголовки» после «Атома»', () => {
  expect(BUILDINGS.silo.warhead.blast.radius).toBe(12)
  const sim = createSim({ ...options, rules: { protection: 0, techTree: true } })
  expect(isUnlocked(sim, 1, 'silo')).toBe(false)
})

test('сборка, пуск и ракета в полёте переживают сохранение', () => {
  const { sim, x, y, silo } = armed()
  sim.send(1, { type: 'launch', silo, x: x + 200, y })
  seconds(sim, warhead.launch + 2)
  const [missile] = missiles(sim)
  const before = { ...sim.world.get(missile, Missile)! }
  const loaded = createSim(throughJson(sim.save()))
  expect(loaded.world.get(missile as Entity, Missile)).toEqual(before)
  expect(marks(loaded, 'launch').length).toBe(1)
})
