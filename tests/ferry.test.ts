import { afterEach, beforeEach, expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import type { HudState } from '../src/game/hud'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { createHost, type Host } from '../src/net/host'
import { decodeServer } from '../src/net/protocol'
import { createReplica, type Replica } from '../src/net/replica'
import {
  BUILDINGS, Drop, Inventory, Position, Transfer, amountOf, canFerry, canPlace, canSell, createSim, creditsOf, offerAlliance, sendFerry,
  type BuildingType, type Command, type Sim,
} from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { addCredits } from '../src/sim/economy'
import { SELL_SECONDS, flightTicks, sell } from '../src/sim/trade'
import { commandsOf, type FerryDraft, type Page } from '../src/ui/commands'

const STEP = 1 / 20
const options = { generator: DEFAULT_SETTINGS.generator, size: 256, fog: false, rules: { protection: 0, techTree: false } }
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / STEP); i++) sim.advance(STEP)
}
const ally = (sim: Sim, a: number, b: number) => {
  offerAlliance(sim, a, b)
  expect(offerAlliance(sim, b, a)).toBe('allied')
}

/**
 * База игрока на ровном месте: главное здание и здания из списка в ряд, над рядом — труба, чтобы всё было одной
 * сетью. away — место другой базы: рядом с ней не селимся.
 */
function base(sim: Sim, player: number, extra: BuildingType[], away?: { x: number; y: number }) {
  const row: { type: BuildingType; dx: number }[] = []
  let at = 0
  for (const type of ['command', ...extra] as BuildingType[]) {
    row.push({ type, dx: at })
    at += BUILDINGS[type].width + 1
  }
  for (let y = -120; y < 120; y++) {
    for (let x = -120; x < 120; x++) {
      if (away && Math.hypot(x - away.x, y - away.y) < 60) continue
      if (!row.every(({ type, dx }) => canPlace(sim, type, x + dx, y))) continue
      let piped = true
      for (let dx = 0; dx < at && piped; dx++) piped = canPlace(sim, 'pipe', x + dx, y - 1)
      if (!piped) continue
      const buildings = row.map(({ type, dx }) => placeBuilding(sim.world, type, x + dx, y, player))
      for (let dx = 0; dx < at; dx++) placeBuilding(sim.world, 'pipe', x + dx, y - 1, player)
      return { x, y, buildings, core: buildings[0] }
    }
  }
  throw new Error('Не нашлось места под базу')
}

test('корабль с подарком выходит только к союзнику: не себе, не пустым, не в долг и только с готового свободного порта', () => {
  const sim = createSim(options)
  const one = base(sim, 1, ['spaceport', 'metalYard'])
  const two = base(sim, 2, ['metalYard'], one)
  const [core, port, yard] = one.buildings
  expect(two.core).toBeDefined()
  addCredits(sim, 1, 1000)
  // Игроки 2 и 3 — с пустыми счетами: без Player их и в союз не позвать.
  addCredits(sim, 2, 0)
  addCredits(sim, 3, 0)
  sim.world.get(yard, Inventory)!.items.metal = 200

  // Не союзник — подарок не летит.
  expect(sendFerry(sim, 1, port, 2, 100, {})).toBe(false)
  ally(sim, 1, 2)
  // Себе и не союзнику нельзя; пустой корабль и долг — тоже не отправка.
  expect(sendFerry(sim, 1, port, 1, 100, {})).toBe(false)
  expect(sendFerry(sim, 1, port, 3, 100, {})).toBe(false)
  expect(sendFerry(sim, 1, port, 2, 0, {})).toBe(false)
  expect(sendFerry(sim, 1, port, 2, -5, {})).toBe(false)
  expect(sendFerry(sim, 1, port, 2, 5000, {})).toBe(false)
  expect(creditsOf(sim, 1)).toBe(1000)
  // Союзнику без главного здания выгружать некуда: подарок отклоняется, и деньги остаются при нём.
  ally(sim, 1, 3)
  expect(sendFerry(sim, 1, port, 3, 50, {})).toBe(false)
  expect(creditsOf(sim, 1)).toBe(1000)

  expect(canFerry(sim, 1, port)).toBe(true)
  expect(sendFerry(sim, 1, port, 2, 100, { metal: 200 })).toBe(true)
  // Отдаётся сразу: деньги списаны, груз ушёл из хранилищ, и всё это уже в пути.
  expect(creditsOf(sim, 1)).toBe(900)
  expect(amountOf(sim.world.get(yard, Inventory)!, 'metal')).toBe(0)
  const transfer = sim.world.get(port, Transfer)!
  expect(transfer).toMatchObject({ to: 2, credits: 100, goods: { metal: 200 }, done: false })
  expect(transfer.left).toBe(transfer.total)
  expect(transfer.left).toBeGreaterThan(0)
  expect(transfer.x).toBe(sim.world.get(two.core, Position)!.x + 1)
  // Пока корабль летит, космопорт занят: ни продать, ни послать второй подарок.
  expect(canSell(sim, 1, port)).toBe(false)
  expect(sell(sim, 1, port, 'metal', 10)).toBe(false)
  expect(sendFerry(sim, 1, port, 2, 0, { metal: 1 })).toBe(false)
  expect(core).toBe(one.core)
})

test('корабль долетает: деньги и груз — получателю, а что не вошло в хранилища — падает у его главного здания', () => {
  const sim = createSim(options)
  // Два двора у отправителя: трюм корабля не бездонный, и в подарок уходит не больше 500 одного груза.
  const one = base(sim, 1, ['spaceport', 'metalYard', 'metalYard'])
  const two = base(sim, 2, ['metalYard'], one)
  const [, port, firstYard, secondYard] = one.buildings
  const yard = two.buildings[1]
  addCredits(sim, 1, 1000)
  addCredits(sim, 2, 0)
  sim.world.get(firstYard, Inventory)!.items.metal = 300
  sim.world.get(secondYard, Inventory)!.items.metal = 300
  ally(sim, 1, 2)
  const before = creditsOf(sim, 2)

  expect(sendFerry(sim, 1, port, 2, 250, { metal: 600 })).toBe(true)
  const transfer = sim.world.get(port, Transfer)!
  expect(transfer.goods.metal).toBe(500)
  expect(amountOf(sim.world.get(firstYard, Inventory)!, 'metal') + amountOf(sim.world.get(secondYard, Inventory)!, 'metal')).toBe(100)

  seconds(sim, flightTicks(sim, port, SELL_SECONDS) * STEP)
  // Подарок пришёл целиком; сверх него у получателя капает свой доход с главного здания.
  expect(creditsOf(sim, 2)).toBeGreaterThanOrEqual(before + 250)
  // Двор получателя берёт 400 — больше в него не влезет; остаток ложится на землю у главного здания.
  expect(amountOf(sim.world.get(yard, Inventory)!, 'metal')).toBe(400)
  const spill = [...sim.world.query(Drop)][0]
  expect(spill).toBeDefined()
  expect(amountOf(sim.world.get(spill[0], Inventory)!, 'metal')).toBe(100)
  expect(sim.world.get(spill[0], Position)).toMatchObject({ x: transfer.x, y: transfer.y })
  expect(transfer.done).toBe(true)
  expect(transfer.credits).toBe(0)
})

test('недолёт переживает сохранение: после загрузки корабль довозит и деньги, и груз', () => {
  const sim = createSim(options)
  const one = base(sim, 1, ['spaceport', 'metalYard'])
  const two = base(sim, 2, ['metalYard'], one)
  const [, port, yard] = one.buildings
  const theirYard = two.buildings[1]
  addCredits(sim, 1, 1000)
  addCredits(sim, 2, 0)
  sim.world.get(yard, Inventory)!.items.metal = 60
  ally(sim, 1, 2)
  expect(sendFerry(sim, 1, port, 2, 40, { metal: 60 })).toBe(true)
  seconds(sim, 3)
  const left = sim.world.get(port, Transfer)!.left
  expect(left).toBeGreaterThan(0)
  expect(left).toBeLessThan(flightTicks(sim, port, SELL_SECONDS))

  const before = creditsOf(sim, 2)
  const loaded = createSim(sim.save())
  expect(loaded.world.get(port, Transfer)!.left).toBe(left)
  seconds(loaded, left * STEP + STEP)
  expect(creditsOf(loaded, 2)).toBeGreaterThanOrEqual(before + 40)
  expect(amountOf(loaded.world.get(theirYard, Inventory)!, 'metal')).toBe(60)
  expect(loaded.world.get(port, Transfer)!.done).toBe(true)
})

const random = Math.random
beforeEach(() => {
  let seed = 7
  Math.random = () => (seed = (seed * 16807) % 2147483647) / 2147483647
})
afterEach(() => {
  Math.random = random
})

function join(host: Host, name: string) {
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

const run = (host: Host, clients: { sim: Replica }[], seconds: number) => {
  for (let i = 0; i < seconds / STEP; i++) {
    host.advance(STEP)
    for (const { sim } of clients) sim.advance(STEP)
  }
}

test('/give переводит деньги союзнику сразу, а чужим и в долг — отказывает', () => {
  const host = createHost(createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 }))
  const first = join(host, 'Вася')
  const second = join(host, 'Петя')
  const all = [first, second]
  run(host, all, 0.5)
  addCredits(host.sim, 1, 1000)

  first.say('/give Петя 300')
  expect(first.chat).toContain('giveNoAlly')
  first.say('/ally Петя')
  second.say('/ally Вася')
  expect(first.chat).toContain('allyFormed')
  const before = [creditsOf(host.sim, 1), creditsOf(host.sim, 2)]

  first.say('/give Петя 300')
  expect(first.chat).toContain('giveSent')
  expect(second.chat).toContain('giveGot')
  expect(creditsOf(host.sim, 1)).toBe(before[0] - 300)
  expect(creditsOf(host.sim, 2)).toBe(before[1] + 300)
  // Дробные кредиты, перевод себе и больше, чем есть, — не перевод.
  first.say('/give Петя 0.5')
  expect(creditsOf(host.sim, 1)).toBe(before[0] - 300)
  expect(creditsOf(host.sim, 2)).toBe(before[1] + 300)
  first.say('/give Вася 10')
  expect(first.chat).toContain('giveSelf')
  first.say('/give Петя 999999')
  expect(first.chat).toContain('giveShort')
  expect(creditsOf(host.sim, 1)).toBe(before[0] - 300)
})

test('о корабле с подарком весть идёт обеим сторонам: вышел — и прибыл', () => {
  const host = createHost(createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 }))
  const first = join(host, 'Вася')
  const second = join(host, 'Петя')
  const all = [first, second]
  run(host, all, 0.5)
  const one = base(host.sim, 1, ['spaceport'])
  base(host.sim, 2, [], one)
  const port = one.buildings[1] as Entity
  addCredits(host.sim, 1, 500)
  first.say('/ally Петя')
  second.say('/ally Вася')
  run(host, all, 0.2)
  const before = creditsOf(host.sim, 2)

  first.sim.send(1, { type: 'ferry', port, to: 2, credits: 100, goods: {} })
  run(host, all, 0.5)
  expect(first.chat).toContain('ferrySent')
  expect(second.chat).toContain('ferryComing')
  expect(host.sim.world.get(port, Transfer)!.done).toBe(false)

  run(host, all, SELL_SECONDS + 1)
  expect(first.chat).toContain('ferryArrived')
  expect(second.chat).toContain('ferryGot')
  // Пришло ровно сто и ни копейкой меньше: своя казна у получателя ещё и доход с главного здания даёт.
  expect(creditsOf(host.sim, 2)).toBeGreaterThanOrEqual(before + 100)
  // Корабль долетел и ушёл: компонент снят, космопорт снова свободен.
  expect(host.sim.world.has(port, Transfer)).toBe(false)
  expect(canSell(host.sim, 1, port)).toBe(true)
})

/** Панель космопорта с мирным состоянием, союзником и запасом металла. */
function panel(trade: HudState['trade'], players: unknown[]) {
  const state = {
    trade, credits: 1000, units: [], building: 'spaceport',
    stock: { items: [{ resource: 'metal', amount: 130 }], capacity: 500 },
    demolish: { buildings: [7], refund: 100 },
    players,
  } as unknown as HudState
  let ferry: FerryDraft | null = null
  let page: Page = 'root'
  const sent: Command[] = []
  const nothing = () => {}
  const actions = {
    send: (command: Command) => void sent.push(command), place: nothing, pave: nothing, route: nothing, serve: nothing, patrol: nothing, aim: nothing,
    open: (next: Page) => void (page = next), pick: nothing, draft: (next: FerryDraft | null) => void (ferry = next),
  }
  const grid = (at: Page) => commandsOf(state, at, actions, null, ferry)
  const press = (at: Page, label: string) => grid(at).find((slot) => slot?.label === label)!.run()
  return { state, sent, grid, press, ferry: () => ferry, page: () => page }
}

test('панель космопорта: подарок союзнику набирается грузом и деньгами и уходит кораблём', () => {
  const { sent, grid, press, ferry, page } = panel({ port: 7, order: null, offers: [], purchases: [], transfer: null }, [
    { player: 1, name: 'Вася', own: true, relation: null },
    { player: 2, name: 'Петя', own: false, relation: 'ally' },
    // Тот, кто союз только предложил, ещё не союзник: в списке получателей его нет.
    { player: 3, name: 'Коля', own: false, relation: 'allyAsked' },
  ])

  // В корне космопорта — «Союзнику», и набор начинается с выбора союзника.
  expect(grid('root')[0]).toMatchObject({ label: 'Союзнику' })
  expect(grid('root')[10]).toMatchObject({ label: 'Разобрать' })
  press('root', 'Союзнику')
  expect(page()).toBe('ally')
  expect(grid('ally').filter(Boolean).map((slot) => slot!.label)).toEqual(['Петя', 'Назад'])
  press('ally', 'Петя')
  expect(page()).toBe('ferryGoods')
  expect(ferry()).toEqual({ to: 2, name: 'Петя', credits: 0, goods: {}, editing: null, amount: 0 })
  // Пустой корабль не отправить.
  expect(grid('ferryGoods')[8]).toMatchObject({ label: 'Отправить: Петя', disabled: true, title: 'Корабль пуст: положите груз или деньги' })

  // Груз: шаги по десять и сотне, «Всё» — весь запас, «Добавить» кладёт набранное в корабль.
  press('ferryGoods', 'Металл ×130')
  expect(ferry()!.editing).toEqual({ kind: 'good', good: 'metal' })
  press('ferryGoods', '+100')
  expect(ferry()!.amount).toBe(100)
  press('ferryGoods', 'Всё')
  expect(ferry()!.amount).toBe(130)
  press('ferryGoods', 'Добавить ×130')
  expect(ferry()).toMatchObject({ goods: { metal: 130 }, editing: null, amount: 0 })
  expect(grid('ferryGoods')[8]).toMatchObject({ label: 'Отправить: Петя', disabled: false })

  // Деньги: сотнями, не больше счёта; подпись ячейки показывает уже набранное.
  press('ferryGoods', 'Деньги')
  expect(ferry()!.editing).toEqual({ kind: 'credits' })
  press('ferryGoods', '+1000')
  expect(ferry()!.amount).toBe(1000)
  press('ferryGoods', 'Добавить ×1000')
  expect(ferry()).toMatchObject({ credits: 1000, editing: null })
  expect(grid('ferryGoods')[9]).toMatchObject({ label: 'Деньги ×1000' })

  // Отправка: получатель, деньги и груз уходят командой, черновик и страница сбрасываются.
  press('ferryGoods', 'Отправить: Петя')
  expect(sent).toEqual([{ type: 'ferry', port: 7, to: 2, credits: 1000, goods: { metal: 130 } }])
  expect(ferry()).toBeNull()
  expect(page()).toBe('root')
})

test('космопорт занят или союзников нет: подарок не начать', () => {
  const players = [
    { player: 1, name: 'Вася', own: true, relation: null },
    { player: 2, name: 'Петя', own: false, relation: 'ally' },
  ]
  const transfer: NonNullable<HudState['trade']>['transfer'] = { to: 2, credits: 100, goods: [{ resource: 'metal', amount: 50 }], flight: 0.5 }
  const busy = panel({ port: 7, order: null, offers: [], purchases: [], transfer }, players)
  expect(busy.grid('root')[0]).toMatchObject({ label: 'Союзнику', disabled: true, title: 'Космопорт занят: корабль уже в пути' })
  const lone = panel({ port: 7, order: null, offers: [], purchases: [], transfer: null }, players.slice(0, 1))
  expect(lone.grid('root')[0]).toMatchObject({ label: 'Союзнику', disabled: true, title: 'Союзников нет: предложите союз в списке по Tab' })
})

test('панель подарка не обещает взять больше вместимости корабля', () => {
  const players = [
    { player: 1, name: 'Вася', own: true, relation: null },
    { player: 2, name: 'Петя', own: false, relation: 'ally' },
  ]
  const view = panel({ port: 7, order: null, offers: [], purchases: [], transfer: null }, players)
  view.state.stock!.items[0].amount = 700
  view.press('root', 'Союзнику')
  view.press('ally', 'Петя')
  view.press('ferryGoods', 'Металл ×700')
  view.press('ferryGoods', 'Всё')
  expect(view.ferry()!.amount).toBe(500)
  expect(view.grid('ferryGoods').find((slot) => slot?.label === 'Всё')).toMatchObject({ title: '×500' })
})
