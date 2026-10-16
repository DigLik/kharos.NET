import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { createHost } from '../src/net/host'
import { decodeServer, type PlayerInfo } from '@kharos/protocol'
import { Armed, Health, canAttack, createSim, isWalkable, protect, protectionLeft, resolveOrder, type Sim } from '@kharos/sim-ts'
import { addCredits } from '@kharos/sim-ts/economy'
import { spawnUnit } from '@kharos/sim-ts/units'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Симуляция и тайл в ровном проходимом поле. */
function field() {
  const sim = createSim(options)
  for (let y = -40; y < 40; y++) {
    search: for (let x = -40; x < 40; x++) {
      for (let dy = 0; dy < 7; dy++) for (let dx = 0; dx < 24; dx++) if (!isWalkable(sim, x + dx, y + dy)) continue search
      return { sim, x, y: y + 3 }
    }
  }
  throw new Error('В мире не нашлось ровного поля')
}

/** Пехотинец игрока 1 и пехотинец игрока 2 в трёх тайлах друг от друга; оба игрока есть в мире. */
function duel() {
  const { sim, x, y } = field()
  addCredits(sim, 1, 0)
  addCredits(sim, 2, 0)
  const mine = spawnUnit(sim, 'infantry', 1, x, y)
  const foe = spawnUnit(sim, 'infantry', 2, x + 3, y)
  const health = (entity: number) => sim.world.get(entity as never, Health)!.value
  return { sim, x, y, mine, foe, health }
}

test('новичка под защитой не ранят, и сам он не стреляет; срок вышел — бой идёт', () => {
  const { sim, mine, foe, health } = duel()
  sim.rules.protection = 0.1
  protect(sim, 2)
  expect(protectionLeft(sim, 2)).toBe(Math.round(6 / TICK))
  seconds(sim, 5)
  expect(health(mine)).toBe(1)
  expect(health(foe)).toBe(1)
  seconds(sim, 3)
  expect(protectionLeft(sim, 2)).toBe(0)
  expect(health(mine) < 1 || health(foe) < 1).toBe(true)
})

test('по защищённому приказ атаки не даётся: точка по нему — движение', () => {
  const { sim, x, y, mine, foe } = duel()
  protect(sim, 2)
  expect(canAttack(sim, 1, foe)).toBe(false)
  expect(resolveOrder(sim, 1, [mine], { x: x + 3.5, y: y + 0.5 }, undefined, false)[0].type).toBe('move')
  sim.send(1, { type: 'attack', units: [mine], target: foe })
  sim.advance(TICK)
  expect(sim.world.get(mine, Armed)!.ordered).toBe(false)
})

test('приказ атаки снимает свою защиту', () => {
  const { sim, mine, foe, health } = duel()
  protect(sim, 1)
  expect(canAttack(sim, 1, foe)).toBe(true)
  sim.send(1, { type: 'attack', units: [mine], target: foe })
  sim.advance(TICK)
  expect(protectionLeft(sim, 1)).toBe(0)
  seconds(sim, 4)
  expect(health(foe)).toBeLessThan(1)
})

test('без правила защиты нет', () => {
  const { sim } = duel()
  sim.rules.protection = 0
  protect(sim, 1)
  expect(protectionLeft(sim, 1)).toBe(0)
})

test('хост даёт новому игроку защиту и сообщает всем, когда она кончилась', () => {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256, rules: { protection: 0.05 } })
  const host = createHost(sim)
  let roster: PlayerInfo[] = []
  const peer = host.join((data) => {
    const message = decodeServer(data)
    if (message.type === 'players') roster = message.players
  })
  expect(protectionLeft(sim, peer.player)).toBeGreaterThan(0)
  host.advance(TICK)
  expect(roster[0].safe).toBe(true)
  for (let i = 0; i < 4 / TICK; i++) host.advance(TICK)
  expect(protectionLeft(sim, peer.player)).toBe(0)
  expect(roster[0].safe).toBeUndefined()
})
