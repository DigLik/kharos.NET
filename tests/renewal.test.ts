import { expect, test } from 'bun:test'
import { DEFAULT_CONFIG, isBuildable, terrainAt } from '@kharos/shared/terrain'
import { Deposit, Position, createSim, depositAt, type DepositSpot, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { addDeposit, fillDeposits, knowsDeposit, takeReserve } from '../src/sim/deposits'
import { spawnUnit } from '../src/sim/units'
import { throughJson } from './throughJson'

const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}
const spots = (sim: Sim) => [...sim.deposits.cells.values()].flat()

/** Мир 256×256 со всеми месторождениями; смена — через полминуты, чтобы не ждать в тесте 15 минут. */
function world(fog = false, depositRenewal = 0.5) {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256, fog, rules: { depositRenewal, techTree: false, protection: 0 } })
  fillDeposits(sim)
  return sim
}

/** Месторождение ближе всех к середине карты: вокруг него есть куда отвести юнит, не уходя за край. */
const central = (sim: Sim) => spots(sim).sort((a, b) => Math.hypot(a.x, a.y) - Math.hypot(b.x, b.y))[0]

const exhaust = (sim: Sim, spot: DepositSpot) => takeReserve(sim, spot.x, spot.y, spot.reserve)

test('выработанное месторождение уходит, а через depositRenewal минут в другой клетке появляется новое', () => {
  const sim = world()
  const count = spots(sim).length
  const spot = central(sim)
  exhaust(sim, spot)
  seconds(sim, 1)
  expect(depositAt(sim, spot.x, spot.y)).toBeNull()
  expect(sim.world.count(Deposit)).toBe(0)
  expect(spots(sim).length).toBe(count - 1)

  seconds(sim, 28)
  expect(spots(sim).length).toBe(count - 1)
  seconds(sim, 2)
  expect(spots(sim).length).toBe(count)
  const fresh = spots(sim).find((other) => other.found)!
  expect(fresh).toBeDefined()
  for (let y = fresh.y; y < fresh.y + 2; y++) for (let x = fresh.x; x < fresh.x + 2; x++) expect(isBuildable(terrainAt(sim.land, x, y))).toBe(true)
  expect(sim.deposits.due).toEqual([])
})

test('новое месторождение не появляется у зданий', () => {
  const sim = world(false, 0.05)
  // Здания по всей карте с шагом 12 тайлов, до самого края: свободного от них на 12 тайлов места нет нигде.
  const { left, top, right, bottom } = sim.bounds
  for (let y = top; y < bottom; y += 12) for (let x = left; x < right; x += 12) placeBuilding(sim.world, 'metalYard', x, y, 1)
  const count = spots(sim).length
  exhaust(sim, central(sim))
  seconds(sim, 10)
  expect(spots(sim).length).toBe(count - 1)
  expect(sim.deposits.due.length).toBe(1)
})

test('под шахтой выработанное месторождение остаётся, пока шахта стоит; смена отсчитывается от выработки', () => {
  const sim = world()
  const spot = central(sim)
  const mine = placeBuilding(sim.world, 'mine', spot.x, spot.y, 1)
  exhaust(sim, spot)
  seconds(sim, 1)
  expect(depositAt(sim, spot.x, spot.y)).toBe(spot)
  expect(sim.deposits.due.length).toBe(1)
  // Новое появилось, хотя шахта стоит: месторождений на карте не меньше, чем было.
  seconds(sim, 30)
  expect(sim.deposits.due.length).toBe(0)
  sim.world.destroy(mine)
  seconds(sim, 1)
  expect(depositAt(sim, spot.x, spot.y)).toBeNull()
  expect(sim.deposits.due.length).toBe(0)
})

test('без правила месторождения конечны', () => {
  const sim = world(false, 0)
  const spot = central(sim)
  exhaust(sim, spot)
  seconds(sim, 5)
  expect(depositAt(sim, spot.x, spot.y)).toBe(spot)
  expect(sim.deposits.due).toEqual([])
})

test('туман: ушедшее помнят, пока не увидят его место; новое знает только тот, кто его видел', () => {
  const sim = world(true)
  const spot = central(sim)
  const scout = spawnUnit(sim, 'infantry', 1, spot.x, spot.y)
  seconds(sim, 1)
  expect(knowsDeposit(sim, 1, spot)).toBe(true)
  // Ушёл — разведанное помнит, но не видит.
  const put = (x: number, y: number) => Object.assign(sim.world.get(scout, Position)!, { x, y })
  put(spot.x + 40, spot.y)
  seconds(sim, 1)
  exhaust(sim, spot)
  seconds(sim, 1)
  expect(depositAt(sim, spot.x, spot.y)).toBeNull()
  expect(sim.deposits.ghosts).toMatchObject([{ x: spot.x, y: spot.y, kind: spot.kind, players: [1] }])

  // Новое месторождение на разведанной земле: игрок о нём не знает, пока не увидит.
  const fresh = { x: spot.x + 3, y: spot.y, kind: spot.kind, reserve: 500, found: [] }
  addDeposit(sim, fresh)
  const placed = depositAt(sim, fresh.x, fresh.y)!
  expect(sim.vision.exploredIn(1, fresh.x, fresh.y, 2, 2)).toBe(true)
  expect(knowsDeposit(sim, 1, placed)).toBe(false)

  // Сохранение помнит и тех, кто помнит ушедшее, и тех, кто нашёл новое.
  const again = createSim(throughJson(sim.save()))
  expect(again.deposits.ghosts).toEqual(sim.deposits.ghosts)
  expect(depositAt(again, fresh.x, fresh.y)).toEqual(placed)
  expect(again.deposits.spawned).toBe(sim.deposits.spawned)
  expect(again.deposits.due).toEqual(sim.deposits.due)

  // Вернулся — увидел, что старого нет, и нашёл новое.
  put(spot.x + 1, spot.y + 1)
  // Проверка — раз в секунду, обзор — с запаздыванием в несколько тиков: за две секунды обе успевают.
  seconds(sim, 2)
  expect(sim.deposits.ghosts).toEqual([])
  expect(knowsDeposit(sim, 1, placed)).toBe(true)
  expect(knowsDeposit(sim, 2, placed)).toBe(false)
})
