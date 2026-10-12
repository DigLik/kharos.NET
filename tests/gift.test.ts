import { afterEach, beforeEach, expect, test } from 'bun:test'
import type { HudState } from '../src/game/hud'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { createHost } from '../src/net/host'
import { decodeServer } from '../src/net/protocol'
import { createReplica, type Replica } from '../src/net/replica'
import {
  Doomed, Hauler, Inventory, Owner, Path, Tactics, amountOf, canPlace, createSim, fromClient, giftable, handOver, offerAlliance,
  type BuildingType, type Command, type Sim,
} from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { NONE } from '../src/sim/common'
import { addCredits } from '../src/sim/economy'
import { turretsOf } from '../src/sim/turrets'
import { spawnUnit } from '../src/sim/units'
import { commandsOf, type Page } from '../src/ui/commands'

const STEP = 1 / 20
const options = { generator: DEFAULT_SETTINGS.generator, size: 256, fog: false, rules: { protection: 0, techTree: false } }
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / STEP); i++) sim.advance(STEP)
}
/** Игрока без счёта мир не знает: Player появляется вместе с первым начислением. */
const account = (sim: Sim, ...players: number[]) => {
  for (const player of players) addCredits(sim, player, 0)
}
const ally = (sim: Sim, a: number, b: number) => {
  offerAlliance(sim, a, b)
  expect(offerAlliance(sim, b, a)).toBe('allied')
}
/** Ровное место под здание. */
const spot = (sim: Sim, type: BuildingType) => {
  for (let y = -80; y < 80; y++) for (let x = -80; x < 80; x++) if (canPlace(sim, type, x, y)) return { x, y }
  throw new Error(`Не нашлось места под ${type}`)
}

test('отдать юнитов можно только союзнику и только своих — и только раз', () => {
  const sim = createSim(options)
  account(sim, 1, 2, 3)
  const truck = spawnUnit(sim, 'truck', 1, 4, 4)
  // Не союзник: юниты остаются у хозяина.
  expect(handOver(sim, 1, 2, [truck])).toBe(0)
  expect(sim.world.get(truck, Owner)!.player).toBe(1)
  ally(sim, 1, 2)
  expect(handOver(sim, 1, 1, [truck])).toBe(0)
  // Чужой юнит не отдать, даже если оба — союзники: юнит не его. Третьего зовёт глава — двойка уже рядовая.
  ally(sim, 1, 3)
  expect(handOver(sim, 3, 2, [truck])).toBe(0)
  expect(sim.world.get(truck, Owner)!.player).toBe(1)
  expect(handOver(sim, 1, 2, [truck])).toBe(1)
  expect(sim.world.get(truck, Owner)!.player).toBe(2)
  // Юнит уже не свой: второй раз отдавать нечего.
  expect(handOver(sim, 1, 2, [truck])).toBe(0)
})

test('в список на отдачу не идут чужие, турели на носителе и обречённые; повтор в списке — один раз', () => {
  const sim = createSim(options)
  const tank = spawnUnit(sim, 'tank', 1, 4, 4)
  const [turret] = turretsOf(sim, tank)
  const doomed = spawnUnit(sim, 'truck', 1, 6, 6)
  sim.world.add(doomed, Doomed({ left: 500 }))
  const foreign = spawnUnit(sim, 'truck', 3, 8, 8)
  account(sim, 1, 2)
  ally(sim, 1, 2)
  expect(giftable(sim, 1, [tank, turret, doomed, foreign, tank])).toEqual([tank])
})

test('юниты переходят там же, где стоят: работа брошена, турели уходят за носителем', () => {
  const sim = createSim(options)
  const tank = spawnUnit(sim, 'tank', 1, 4, 4)
  const truck = spawnUnit(sim, 'truck', 1, 6, 6)
  sim.world.set(truck, Hauler, { mine: 11, pickup: 12, from: 13, to: 14, amount: 5, full: true, route: [15, 16], stop: 1, resource: 'metal' })
  sim.world.set(truck, Path, { points: [6, 6, 9, 9], goalX: 9, goalY: 9 })
  sim.world.set(truck, Tactics, { away: true, homeX: 6, homeY: 6 })
  account(sim, 1, 2)
  ally(sim, 1, 2)

  expect(handOver(sim, 1, 2, [tank, truck])).toBe(2)
  // Работа снята, маршрут и патруль забыты, путь брошен — и всё это там же, где юниты стояли.
  expect(sim.world.get(truck, Hauler)).toMatchObject({ mine: NONE, pickup: NONE, from: NONE, to: NONE, amount: 0, full: false, route: [], stop: 0 })
  expect(sim.world.has(truck, Path)).toBe(false)
  expect(sim.world.get(truck, Tactics)!.away).toBe(false)
  expect(sim.world.get(truck, Owner)!.player).toBe(2)
  // Турели не остаются на союзной технике чужими.
  expect(turretsOf(sim, tank).length).toBeGreaterThan(0)
  for (const turret of turretsOf(sim, tank)) expect(sim.world.get(turret, Owner)!.player).toBe(2)
})

test('груз остаётся в кузове и довозится уже новому хозяину — в его хранилище', () => {
  const sim = createSim(options)
  const at = spot(sim, 'metalYard')
  const yard = placeBuilding(sim.world, 'metalYard', at.x, at.y, 2)
  const truck = spawnUnit(sim, 'truck', 1, at.x - 2, at.y)
  sim.world.get(truck, Inventory)!.items.metal = 20
  account(sim, 1, 2)
  ally(sim, 1, 2)

  expect(handOver(sim, 1, 2, [truck])).toBe(1)
  // Груз не вывалился и не пропал: он в кузове, и грузовик уже намерен его сгрузить.
  expect(amountOf(sim.world.get(truck, Inventory)!, 'metal')).toBe(20)
  expect(sim.world.get(truck, Hauler)).toMatchObject({ full: true, resource: 'metal' })
  expect(sim.world.get(truck, Owner)!.player).toBe(2)

  seconds(sim, 6)
  expect(amountOf(sim.world.get(yard, Inventory)!, 'metal')).toBe(20)
  expect(amountOf(sim.world.get(truck, Inventory)!, 'metal')).toBe(0)
})

test('приказ gift из меню юнитов меняет хозяина в ближайшем тике, а несоюзнику — ничего', () => {
  const sim = createSim(options)
  const truck = spawnUnit(sim, 'truck', 1, 4, 4)
  const tank = spawnUnit(sim, 'tank', 1, 6, 6)
  account(sim, 1, 2, 3)
  ally(sim, 1, 2)
  expect(fromClient({ type: 'gift', units: [truck, tank], to: 2 })).toBe(true)

  // Третьему — не союзник: в очередь команда уходит, но мир её отвергает.
  sim.send(1, { type: 'gift', units: [truck], to: 3 })
  sim.advance(STEP)
  expect(sim.world.get(truck, Owner)!.player).toBe(1)

  sim.send(1, { type: 'gift', units: [truck, tank], to: 2 })
  sim.advance(STEP)
  expect(sim.world.get(truck, Owner)!.player).toBe(2)
  expect(sim.world.get(tank, Owner)!.player).toBe(2)
})

const random = Math.random
beforeEach(() => {
  let seed = 7
  Math.random = () => (seed = (seed * 16807) % 2147483647) / 2147483647
})
afterEach(() => {
  Math.random = random
})

test('о передаче юнитов весть идёт обеим сторонам, а чужим — молчание', () => {
  const host = createHost(createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 }))
  const ours = spawnUnit(host.sim, 'truck', 1, 4, 4)
  const first = joinHost(host, 'Вася')
  const second = joinHost(host, 'Петя')
  const all = [first, second]
  run(host, all, 0.5)

  // Не союзник: ни перехода, ни вести.
  first.sim.send(1, { type: 'gift', units: [ours], to: 2 })
  run(host, all, 0.2)
  expect(first.chat).not.toContain('giftSent')
  expect(host.sim.world.get(ours, Owner)!.player).toBe(1)

  first.say('/ally Петя')
  second.say('/ally Вася')
  first.sim.send(1, { type: 'gift', units: [ours], to: 2 })
  run(host, all, 0.2)
  expect(first.chat).toContain('giftSent')
  expect(second.chat).toContain('giftGot')
  expect(host.sim.world.get(ours, Owner)!.player).toBe(2)
  // Пустой отдать нечего — и весть об этом не идёт.
  first.sim.send(1, { type: 'gift', units: [ours], to: 2 })
  run(host, all, 0.2)
  expect(first.chat.filter((line) => line === 'giftSent')).toHaveLength(1)
  expect(second.chat.filter((line) => line === 'giftGot')).toHaveLength(1)
})

function joinHost(host: ReturnType<typeof createHost>, name: string) {
  let replica: Replica | undefined
  const chat: string[] = []
  const peer = host.join((data) => {
    const message = decodeServer(data)
    if (message.type === 'welcome' && !replica) replica = createReplica(message, (reply) => peer.receive(reply), () => peer.leave())
    else replica!.receive(message)
    if (message.type === 'chat') for (const line of message.lines) chat.push(Array.isArray(line.text) ? line.text[0] : line.text)
  }, undefined, name)
  const say = (text: string) => {
    // Чат одного подключения принимается не чаще двух раз в секунду.
    const now = Date.now
    Date.now = () => now() + chat.length * 1000 + said++ * 1000
    peer.receive(JSON.stringify({ type: 'chat', text }))
    Date.now = now
  }
  let said = 1
  return { peer, sim: replica!, chat, say }
}

const run = (host: ReturnType<typeof createHost>, clients: { sim: Replica }[], seconds: number) => {
  for (let i = 0; i < seconds / STEP; i++) {
    host.advance(STEP)
    for (const { sim } of clients) sim.advance(STEP)
  }
}

/** Меню юнитов: выбранные юниты gift (null — ничего не выбрано) и список игроков. */
function menu(gift: number[] | null, players: unknown[]) {
  const state = { gift, players, units: [], credits: 0 } as unknown as HudState
  let page: Page = 'root'
  const sent: Command[] = []
  const nothing = () => {}
  const actions = {
    send: (command: Command) => void sent.push(command), place: nothing, pave: nothing, route: nothing, serve: nothing, patrol: nothing,
    open: (next: Page) => void (page = next), pick: nothing, draft: nothing,
  }
  const grid = (at: Page) => commandsOf(state, at, actions)
  const press = (at: Page, label: string) => grid(at).find((slot) => slot?.label === label)!.run()
  return { sent, grid, press, page: () => page }
}

test('меню юнитов: «Отдать союзнику» ведёт к списку союзников, и клик по имени отдаёт выбранных', () => {
  const { sent, grid, press, page } = menu([12, 13], [
    { player: 1, name: 'Вася', own: true, relation: null },
    { player: 2, name: 'Петя', own: false, relation: 'ally' },
    // Кто союз только предложил — ещё не союзник: в списке получателей его нет.
    { player: 3, name: 'Коля', own: false, relation: 'allyOffered' },
  ])
  expect(grid('root')[11]).toMatchObject({ label: 'Отдать союзнику', disabled: false })
  press('root', 'Отдать союзнику')
  expect(page()).toBe('gift')
  expect(grid('gift').filter(Boolean).map((slot) => slot!.label)).toEqual(['Петя', 'Назад'])
  press('gift', 'Петя')
  expect(sent).toEqual([{ type: 'gift', units: [12, 13], to: 2 }])
  expect(page()).toBe('root')
})

test('меню юнитов: без выбранных юнитов отдавать нечего, а без союзников — некому', () => {
  const players = [
    { player: 1, name: 'Вася', own: true, relation: null },
    { player: 2, name: 'Петя', own: false, relation: 'ally' },
  ]
  // Ничего не выбрано — ячейки передачи нет вовсе.
  expect(menu(null, players).grid('root')[11]).toBeNull()
  // Союзников нет — ячейка есть, но недоступна.
  const lone = menu([12], players.slice(0, 1))
  expect(lone.grid('root')[11]).toMatchObject({ label: 'Отдать союзнику', disabled: true, title: 'Союзников нет: предложите союз в списке по Tab' })
})
