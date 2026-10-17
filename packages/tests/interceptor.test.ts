import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import {
  AIRBURST, BUILDINGS, Blast, Burst, CRUISE, Chaser, Health, Interceptor, Inventory, Mark, Missile, Off, Silo, canPlace, createSim, creditsOf, interceptorLack, isUnlocked, offerAlliance,
  type BuildingType, type Sim,
} from '@kharos/sim-ts'
import { placeBuilding, storeFor } from '@kharos/sim-ts/buildings'
import { addCredits } from '@kharos/sim-ts/economy'
import type { Amounts, Ware } from '@kharos/sim-ts/resources'
import { wireOf } from '@kharos/protocol/wire'
import { throughJson } from './throughJson'
import type { Entity } from '@kharos/ecs'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { protection: 0, techTree: false } }
const TICK = 1 / 20
const warhead = BUILDINGS.silo.warhead!
const intercept = BUILDINGS.interceptor.intercept!

const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/**
 * База игрока в ряд в одной сети труб: главное здание, две электростанции, здание kind и хранилища компонентов
 * и топлива — как можно ближе к (nearX, nearY). stash кладёт готовое в хранилища сети.
 */
function base(sim: Sim, player: number, kind: BuildingType, nearX: number, nearY: number) {
  addCredits(sim, player, 10000)
  const layout: [BuildingType, number][] = [['command', 0], ['generator', 4], ['generator', 7], [kind, 10]]
  let at = 10 + BUILDINGS[kind].width + 1
  for (const type of ['partsLocker', 'fuelTank', 'rodRack'] as BuildingType[]) {
    layout.push([type, at])
    at += BUILDINGS[type].width + 1
  }
  for (let distance = 0; distance < 300; distance++) {
    for (let dy = -distance; dy <= distance; dy++) {
      for (let dx = -distance; dx <= distance; dx++) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) !== distance) continue
        const x = nearX + dx
        const y = nearY + dy
        if (!layout.every(([type, shift]) => canPlace(sim, type, x + shift, y))) continue
        let piped = true
        for (let shift = 0; shift < at && piped; shift++) piped = canPlace(sim, 'pipe', x + shift, y - 1)
        if (!piped) continue
        const buildings = layout.map(([type, shift]) => placeBuilding(sim.world, type, x + shift, y, player))
        for (let shift = 0; shift < at; shift++) placeBuilding(sim.world, 'pipe', x + shift, y - 1, player)
        const shelves = buildings.slice(4)
        const shelf = (item: Ware) => shelves[layout.slice(4).findIndex(([type]) => type === storeFor(item))]
        const stash = (items: Amounts) => {
          for (const [item, amount] of Object.entries(items) as [Ware, number][]) sim.world.get(shelf(item), Inventory)!.items[item] = amount
        }
        const stock = (item: Ware) => sim.world.get(shelf(item), Inventory)!.items[item] ?? 0
        return { x, y, building: buildings[3], core: buildings[0], stash, stock }
      }
    }
  }
  throw new Error('Не нашлось места под базу')
}

/** Игрок 1 с готовой боеголовкой и игрок 2 с перехватчиком, в котором loaded ракет, в distance тайлах восточнее. */
function standoff(distance = 260, loaded = 1) {
  const sim = createSim(options)
  const attacker = base(sim, 1, 'silo', -120, 0)
  Object.assign(sim.world.get(attacker.building, Silo)!, { ready: true })
  const defender = base(sim, 2, 'interceptor', attacker.x + distance, attacker.y)
  Object.assign(sim.world.get(defender.building, Interceptor)!, { missiles: loaded })
  // Перехватчик не собирает новую ракету сам: в сети нет ни компонентов, ни топлива.
  const center = { x: defender.x + 11, y: defender.y + 1 }
  return { sim, attacker, defender, center }
}

const missiles = (sim: Sim) => [...sim.world.query(Missile)].map(([entity]) => entity)
const chasers = (sim: Sim) => [...sim.world.query(Chaser)].map(([entity]) => entity)

/** Пускает ракету игрока 1 в тайл (x, y) и ждёт старта. */
function fire(sim: Sim, silo: Entity, x: number, y: number) {
  sim.send(1, { type: 'launch', silo, x, y })
  seconds(sim, warhead.launch + 0.1)
  expect(missiles(sim).length).toBe(1)
}

test('перехватчик сам собирает ракеты по одной, пока их меньше четырёх, — из компонентов и топлива сети', () => {
  const sim = createSim(options)
  const { building, stash, stock } = base(sim, 1, 'interceptor', 0, 0)
  sim.advance(TICK)
  expect(interceptorLack(sim, building)).toBe('materials')
  stash({ parts: 6, fuel: 15 })
  const credits = creditsOf(sim, 1)
  sim.advance(TICK)
  expect(creditsOf(sim, 1)).toBeCloseTo(credits - intercept.cost, 0)
  expect(stock('parts')).toBe(2)
  expect(stock('fuel')).toBe(5)
  expect(interceptorLack(sim, building)).toBeUndefined()
  seconds(sim, intercept.seconds - 1)
  expect(sim.world.get(building, Interceptor)!.missiles).toBe(0)
  seconds(sim, 2)
  expect(sim.world.get(building, Interceptor)!.missiles).toBe(1)
  // Собирает дальше, пока не наберёт четыре, — и тогда встаёт.
  stash({ parts: 20, fuel: 50 })
  seconds(sim, intercept.seconds * (intercept.capacity - 1) + 3)
  expect(intercept.capacity).toBe(4)
  expect(sim.world.get(building, Interceptor)!.missiles).toBe(4)
  expect(interceptorLack(sim, building)).toBeUndefined()
  expect(stock('parts')).toBe(20 - 4 * (intercept.capacity - 1))
  seconds(sim, intercept.seconds)
  expect(stock('parts')).toBe(20 - 4 * (intercept.capacity - 1))
  expect(sim.world.get(building, Interceptor)!.ticks).toBe(0)
})

test('выключенный перехватчик ракету не собирает и не стреляет', () => {
  const { sim, attacker, defender, center } = standoff()
  sim.send(2, { type: 'work', building: defender.building, on: false })
  sim.advance(TICK)
  expect(sim.world.has(defender.building, Off)).toBe(true)
  fire(sim, attacker.building, center.x, center.y)
  seconds(sim, 40)
  expect(missiles(sim)).toEqual([])
  expect(sim.world.get(defender.core, Health)?.value ?? 0).toBeLessThan(1)

  const sim2 = createSim(options)
  const empty = base(sim2, 1, 'interceptor', 0, 0)
  empty.stash({ parts: 4, fuel: 10 })
  sim2.send(1, { type: 'work', building: empty.building, on: false })
  seconds(sim2, 1)
  expect(interceptorLack(sim2, empty.building)).toBe('off')
  expect(empty.stock('parts')).toBe(4)
})

test('ракета, которая метит в круг перехватчика, сбита в воздухе: удара нет, метка точки удара пропадает', () => {
  const { sim, attacker, defender, center } = standoff()
  fire(sim, attacker.building, center.x, center.y)
  const [missile] = missiles(sim)
  const state = sim.world.get(missile, Missile)!
  const flight = Math.hypot(state.toX - state.fromX, state.toY - state.fromY) / warhead.speed
  let chased = false
  let marked = false
  let burst: { size: number; lift: number } | undefined
  for (let i = 0; i < Math.round(flight / TICK) + 40; i++) {
    sim.advance(TICK)
    for (const [, blast] of sim.world.query(Blast)) burst ??= { size: blast.size, lift: blast.lift }
    chased ||= chasers(sim).length > 0
    for (const [, mark] of sim.world.query(Mark)) marked ||= mark.kind === 'strike'
  }
  expect(chased).toBe(true)
  expect(missiles(sim)).toEqual([])
  expect(chasers(sim)).toEqual([])
  for (const [, mark] of sim.world.query(Mark)) expect(mark.kind).not.toBe('strike')
  expect([...sim.world.query(Burst)].length).toBe(0)
  expect(sim.world.get(defender.core, Health)?.value).toBe(1)
  expect(marked).toBe(true)
  expect(sim.world.get(defender.building, Interceptor)!.missiles).toBe(0)
  // Сбитая далеко от цели гибнет на высоте полёта.
  expect(burst).toEqual({ size: AIRBURST, lift: CRUISE })
})

test('сбивает, когда ракета входит в круг, — и на краю круга успевает до удара', () => {
  const { sim, attacker, center, defender } = standoff()
  // Точка удара — на краю круга со стороны пуска: ракета входит в круг почти над ней.
  const toX = Math.floor(center.x - intercept.radius + 2)
  const toY = Math.floor(center.y)
  const near = placeBuilding(sim.world, 'wall', toX, toY + 1, 2)
  fire(sim, attacker.building, toX, toY)
  let entered = -1
  let downed = -1
  let lift = -1
  for (let i = 0; i < 1200 && downed < 0; i++) {
    for (const [, blast] of sim.world.query(Blast)) lift = blast.lift
    const [missile] = missiles(sim)
    if (missile === undefined) downed = sim.time.tick
    else if (entered < 0 && Math.hypot(sim.world.get(missile, Missile)!.x - center.x, sim.world.get(missile, Missile)!.y - center.y) <= intercept.radius) entered = sim.time.tick
    sim.advance(TICK)
  }
  expect(entered).toBeGreaterThan(0)
  expect(downed - entered).toBeLessThan(Math.round(0.5 / TICK))
  sim.advance(TICK)
  for (const [, blast] of sim.world.query(Blast)) lift = blast.lift
  // Над самой целью ракета уже снижается: сбитая гибнет ниже высоты полёта.
  expect(lift).toBeGreaterThan(0)
  expect(lift).toBeLessThan(CRUISE)
  expect(sim.world.get(near, Health)?.value).toBe(1)
  expect(sim.world.get(defender.building, Interceptor)!.missiles).toBe(0)
})

test('из двух перехватчиков, до которых ракета достаёт, стреляет ближний', () => {
  // Шахта — в кругах обоих перехватчиков: ракета в них с первого тика.
  const { sim, attacker, defender, center } = standoff(40)
  const second = base(sim, 2, 'interceptor', defender.x + 30, defender.y)
  Object.assign(sim.world.get(second.building, Interceptor)!, { missiles: 1 })
  const guards = [defender, second].map((guard) => ({ guard, x: guard.x + 11, y: guard.y + 1 }))
  const silo = { x: attacker.x + 11.5, y: attacker.y + 1.5 }
  for (const { x, y } of guards) expect(Math.hypot(x - silo.x, y - silo.y)).toBeLessThan(intercept.radius)
  const [near, far] = [...guards].sort((a, b) => Math.hypot(a.x - silo.x, a.y - silo.y) - Math.hypot(b.x - silo.x, b.y - silo.y))
  fire(sim, attacker.building, center.x, center.y)
  seconds(sim, 30)
  expect(missiles(sim)).toEqual([])
  expect(sim.world.get(near.guard.building, Interceptor)!.missiles).toBe(0)
  expect(sim.world.get(far.guard.building, Interceptor)!.missiles).toBe(1)
})

test('не стреляет по ракете, чья точка удара вне круга, по своей и по союзной', () => {
  // Цель за кругом, хотя курс идёт через него.
  const passing = standoff(160)
  const { sim, attacker, center } = passing
  fire(sim, attacker.building, Math.floor(center.x + intercept.radius + 30), Math.floor(center.y))
  seconds(sim, 30)
  expect(chasers(sim)).toEqual([])
  expect(sim.world.get(passing.defender.building, Interceptor)!.missiles).toBe(1)

  // Союзник.
  const friends = standoff()
  offerAlliance(friends.sim, 1, 2)
  offerAlliance(friends.sim, 2, 1)
  fire(friends.sim, friends.attacker.building, friends.center.x + 20, friends.center.y)
  seconds(friends.sim, 40)
  expect(friends.sim.world.get(friends.defender.building, Interceptor)!.missiles).toBe(1)
  expect([...friends.sim.world.query(Missile)].length).toBe(0)
})

/** Залп двух шахт игрока 1 по базе перехватчика с loaded ракетами. */
function salvo(loaded: number) {
  const found = standoff(260, loaded)
  const { sim, attacker, center } = found
  const second = base(sim, 1, 'silo', attacker.x, attacker.y + 30)
  Object.assign(sim.world.get(second.building, Silo)!, { ready: true })
  sim.send(1, { type: 'launch', silo: attacker.building, x: center.x - 2, y: center.y })
  sim.send(1, { type: 'launch', silo: second.building, x: center.x, y: center.y })
  seconds(sim, warhead.launch + 0.1)
  expect(missiles(sim).length).toBe(2)
  seconds(sim, 40)
  expect(missiles(sim)).toEqual([])
  return found
}

test('одна ракета — одна сбитая: при одной ракете вторая из залпа долетает', () => {
  const { sim, defender } = salvo(1)
  // Вторая ракета снесла базу вместе с самим перехватчиком.
  expect(sim.world.get(defender.core, Health)?.value ?? 0).toBeLessThan(1)
})

test('с запасом ракет перехватчик сбивает весь залп: по ракете на каждую', () => {
  const { sim, defender } = salvo(4)
  expect(sim.world.get(defender.core, Health)?.value).toBe(1)
  expect(sim.world.get(defender.building, Interceptor)!.missiles).toBe(2)
})

test('заряжен ли перехватчик, чужим не видно; ракета-перехватчик видна', () => {
  const { sim, attacker, defender, center } = standoff()
  expect(wireOf(sim.world, defender.building, 1, sim.time.tick).parts.has(Interceptor.key)).toBe(false)
  expect(wireOf(sim.world, defender.building, 2, sim.time.tick).parts.has(Interceptor.key)).toBe(true)
  fire(sim, attacker.building, center.x, center.y)
  for (let i = 0; i < 1200 && !chasers(sim).length; i++) sim.advance(TICK)
  const [chaser] = chasers(sim)
  expect(chaser).toBeDefined()
  expect(JSON.parse(wireOf(sim.world, chaser, 2, sim.time.tick).parts.get(Chaser.key)!).arrive).toBeGreaterThan(sim.time.tick - 1)
})

test('перехватчик открывает радар, без своей темы', () => {
  const sim = createSim({ ...options, rules: { protection: 0, techTree: true } })
  expect(isUnlocked(sim, 1, 'interceptor')).toBe(false)
  base(sim, 1, 'radar', 0, 0)
  expect(isUnlocked(sim, 1, 'interceptor')).toBe(true)
})

test('сборка ракеты и перехват в полёте переживают сохранение', () => {
  const { sim, attacker, center } = standoff()
  fire(sim, attacker.building, center.x, center.y)
  for (let i = 0; i < 1200 && !chasers(sim).length; i++) sim.advance(TICK)
  const [missile] = missiles(sim)
  const hunted = sim.world.get(missile, Missile)!.hunted
  expect(hunted).toBeGreaterThan(sim.time.tick - 1)
  const loaded = createSim(throughJson(sim.save()))
  expect(loaded.world.get(missile, Missile)!.hunted).toBe(hunted)
  seconds(loaded, 5)
  expect([...loaded.world.query(Missile)].length).toBe(0)
})
