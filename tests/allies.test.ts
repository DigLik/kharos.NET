import { afterEach, beforeEach, expect, test } from 'bun:test'
import { DEFAULT_CONFIG } from '../src/map/terrain'
import { createHost, type Host } from '../src/net/host'
import { decodeServer } from '../src/net/protocol'
import { createReplica, type Replica } from '../src/net/replica'
import { Armed, Batch, Health, Owner, Path, Player, Position, Unit, allied, alliesOf, breakAlliance, canAttack, createSim, isOpenGate, offerAlliance, wipePlayer, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { Repair } from '../src/sim/components'
import { addCredits } from '../src/sim/economy'
import { spawnUnit } from '../src/sim/units'
import { pathOf, wireOf } from '../src/net/wire'

const STEP = 1 / 20
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
const unitsOf = (sim: Sim, player: number) => {
  const units: number[] = []
  for (const [entity, , owner] of sim.world.query(Unit, Owner)) if (owner.player === player) units.push(entity)
  return units
}

test('союз — предложение и согласие; выйти может любой, а начавший заново теряет союзы', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  for (const player of [1, 2, 3, 4]) addCredits(sim, player, 100)
  sim.advance(STEP)
  const info = (player: number) => {
    for (const [, data] of sim.world.query(Player)) if (data.id === player) return data
    throw new Error('Игрока нет')
  }
  expect(offerAlliance(sim, 1, 1)).toBe('refused')
  expect(offerAlliance(sim, 1, 2)).toBe('offered')
  expect(offerAlliance(sim, 1, 2)).toBe('pending')
  expect(info(2).asked).toEqual([1])
  expect(allied(sim, 1, 2)).toBe(false)
  // Встречное предложение — согласие.
  expect(offerAlliance(sim, 2, 1)).toBe('allied')
  expect(allied(sim, 1, 2) && allied(sim, 2, 1)).toBe(true)
  expect(info(1)).toMatchObject({ allies: [2], offered: [], asked: [] })
  expect(offerAlliance(sim, 1, 2)).toBe('already')
  // Союз — группа: принявший союз у одного становится союзником всех, с кем тот в союзе.
  offerAlliance(sim, 2, 3)
  offerAlliance(sim, 3, 2)
  expect(allied(sim, 1, 3) && allied(sim, 3, 1)).toBe(true)
  expect(info(1)).toMatchObject({ allies: [2, 3] })
  expect(info(3).allies).toEqual([1, 2])
  // Разрыв снимает одну пару: группа распадается, но не вся — оставшиеся связи целы.
  expect(breakAlliance(sim, 3, 2)).toBe('left')
  expect(allied(sim, 2, 3)).toBe(false)
  expect(allied(sim, 1, 3) && allied(sim, 1, 2)).toBe(true)
  expect(info(3).allies).toEqual([1])
  expect(info(2).allies).toEqual([1])
  expect(breakAlliance(sim, 3, 2)).toBe('nothing')
  offerAlliance(sim, 4, 1)
  expect(breakAlliance(sim, 1, 4)).toBe('declined')
  expect(info(1).asked).toEqual([])
  expect(info(4).offered).toEqual([])
  // Начавший заново — новый игрок для всех: союзы и предложения пропадают у обеих сторон.
  offerAlliance(sim, 3, 2)
  wipePlayer(sim, 2)
  expect(allied(sim, 1, 2)).toBe(false)
  expect(allied(sim, 3, 2)).toBe(false)
  expect(info(1).allies).toEqual([3])
  expect(info(3).offered).toEqual([])
})

test('вступление в группу: принявший союз у одного — союзник и всем остальным, и предложения ждать больше нечего', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  for (const player of [1, 2, 3, 4]) addCredits(sim, player, 100)
  const info = (player: number) => {
    for (const [, data] of sim.world.query(Player)) if (data.id === player) return data
    throw new Error('Игрока нет')
  }
  offerAlliance(sim, 1, 2)
  offerAlliance(sim, 2, 1)
  // Четвёртый предложил первому, а третий — третьему: предложение, ставшее союзом, из списков уходит.
  offerAlliance(sim, 4, 1)
  offerAlliance(sim, 3, 2)
  expect(info(4)).toMatchObject({ offered: [1], asked: [] })
  expect(info(2).asked).toEqual([3])
  expect(offerAlliance(sim, 2, 3)).toBe('allied')
  expect(info(2).allies).toEqual([1, 3])
  expect(info(3).allies).toEqual([1, 2])
  expect(info(2)).toMatchObject({ offered: [], asked: [] })
  // Предложение четвёртого — предложение всей группе: принимает первый, и четвёртый входит в неё целиком.
  expect(info(1).asked).toEqual([4])
  expect(offerAlliance(sim, 1, 4)).toBe('allied')
  expect(info(4).allies).toEqual([1, 2, 3])
  expect(info(3).allies).toEqual([1, 2, 4])
})

test('сохранение прежних версий с попарными союзами при загрузке достраивается до групп', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  for (const player of [1, 2, 3]) addCredits(sim, player, 100)
  sim.advance(STEP)
  const account = (player: number) => {
    for (const [entity, data] of sim.world.query(Player)) if (data.id === player) return { entity, data }
    throw new Error('Игрока нет')
  }
  // Как в старом сохранении: пары есть, а что союзник союзника тоже союзник — не записано.
  sim.world.set(account(1).entity, Player, { allies: [2] })
  sim.world.set(account(2).entity, Player, { allies: [1, 3] })
  sim.world.set(account(3).entity, Player, { allies: [2] })
  const loaded = createSim(sim.save())
  const allies = (player: number) => {
    for (const [, data] of loaded.world.query(Player)) if (data.id === player) return data.allies
    throw new Error('Игрока нет')
  }
  expect(allies(1)).toEqual([2, 3])
  expect(allies(2)).toEqual([1, 3])
  expect(allies(3)).toEqual([1, 2])
  expect(allied(loaded, 1, 3)).toBe(true)
})

test('союзников не атакуют — ни сами, ни по приказу, — и ворота перед ними не закрываются', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256, fog: false, rules: { protection: 0, techTree: false } })
  const own = spawnUnit(sim, 'infantry', 1, 0, 0)
  const other = spawnUnit(sim, 'infantry', 2, 3, 0)
  const gate = placeBuilding(sim.world, 'gate', 1, 3, 1)
  addCredits(sim, 1, 100)
  addCredits(sim, 2, 100)
  offerAlliance(sim, 1, 2)
  offerAlliance(sim, 2, 1)
  for (let i = 0; i < 100; i++) sim.advance(STEP)
  expect(canAttack(sim, 1, other)).toBe(false)
  expect(sim.world.get(own, Armed)!.target).toBe(-1)
  expect(isOpenGate(sim, gate)).toBe(true)
  expect(unitsOf(sim, 1).length + unitsOf(sim, 2).length).toBe(2)
  // Союз расторгнут — снова враги.
  breakAlliance(sim, 2, 1)
  for (let i = 0; i < 10; i++) sim.advance(STEP)
  expect(canAttack(sim, 1, other)).toBe(true)
  expect(sim.world.get(own, Armed)!.target).toBe(other)
  expect(isOpenGate(sim, gate)).toBe(false)
})

test('разведка у союзников общая: после /ally каждый видит базу другого со складами, чужой — по-прежнему нет', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host, 'Вася')
  const second = join(host, 'Петя')
  const third = join(host, 'Коля')
  const all = [first, second, third]
  run(host, all, 0.5)
  expect(unitsOf(first.sim, 2)).toEqual([])

  first.say('/ally Петя')
  expect(first.chat).toContain('allyOffered')
  expect(second.chat).toContain('allyAsked')
  second.say('/ally Вася')
  expect(first.chat).toContain('allyFormed')
  expect(second.chat).toContain('allyFormed')
  run(host, all, 0.5)
  // Все юниты союзника — как на ладони, и место вокруг них открыто в тумане; третий их не видит.
  const theirs = unitsOf(host.sim, 2)
  expect(unitsOf(first.sim, 2).sort()).toEqual([...theirs].sort())
  const at = host.sim.world.get(theirs[0], Position)!
  expect(first.sim.vision.sees(1, at.x, at.y)).toBe(true)
  expect(unitsOf(third.sim, 2)).toEqual([])
  expect(third.sim.vision.sees(3, at.x, at.y)).toBe(false)
  // Союзнику сущность идёт целиком, как хозяину: чем занят строитель, видно ему, но не чужому.
  const builder = theirs.find((entity) => host.sim.world.has(entity as never, Repair))!
  expect(first.sim.world.has(builder as never, Repair)).toBe(true)
  expect(wireOf(host.sim.world, builder as never, 3, 0).parts.has(Repair.key)).toBe(false)

  second.say('/unally Вася')
  expect(first.chat).toContain('allyLeftBy')
  run(host, all, 0.5)
  expect(unitsOf(first.sim, 2)).toEqual([])
  expect(first.sim.vision.sees(1, at.x, at.y)).toBe(false)
})

test('союзнику идут счёт игрока и потоки в его трубах, а третьему — нет', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host, 'Вася')
  const second = join(host, 'Петя')
  const third = join(host, 'Коля')
  const all = [first, second, third]
  run(host, all, 0.5)
  const account = (source: Replica | Sim, player: number) => {
    for (const [, data] of source.world.query(Player)) if (data.id === player) return data
    return undefined
  }
  // Свой счёт есть у каждого, чужой не видит никто: счёт игрока — только ему и его союзникам.
  expect(account(first.sim, 1)).toBeDefined()
  expect(account(first.sim, 2)).toBeUndefined()
  addCredits(host.sim, 2, 500)
  first.say('/ally Петя')
  second.say('/ally Вася')
  expect(first.chat).toContain('allyFormed')
  run(host, all, 0.5)
  expect(account(first.sim, 2)?.credits).toBe(account(host.sim, 2)?.credits)
  // Счёт меняется на ходу: союзнику он идёт так же, как свой, — не только в миг заключения союза.
  const before = account(host.sim, 2)!.credits
  addCredits(host.sim, 2, 300)
  // Что течёт по трубам второго: союзнику видно, куда и что он возит. Пачка в пути — руками, мир тут не при чём.
  host.sim.world.spawn(
    Batch({ from: 1, to: 2, resource: 'metal', amount: 4, sent: host.sim.time.tick, arrive: host.sim.time.tick + 100000 }),
    Owner({ player: 2 }),
  )
  run(host, all, 0.5)
  expect(account(first.sim, 2)?.credits).toBe(account(host.sim, 2)?.credits)
  expect(account(first.sim, 2)!.credits).toBeGreaterThanOrEqual(before + 300)
  expect(account(third.sim, 2)).toBeUndefined()
  expect([...first.sim.world.query(Batch)].length).toBe(1)
  expect([...third.sim.world.query(Batch)].length).toBe(0)
})

test('путь юнита союзника уходит союзнику, а чужому — нет', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  addCredits(sim, 1, 100)
  addCredits(sim, 2, 100)
  const unit = spawnUnit(sim, 'harvester', 2, 5, 5)
  sim.world.set(unit, Path, { points: [5, 5, 9, 9], goalX: 9, goalY: 9 })
  // Путь — только своим: по нему видно, куда идёт чужой отряд.
  expect(pathOf(sim.world, unit, 1)).toBeUndefined()
  expect(pathOf(sim.world, unit, 1, [2])).toBe('{"points":[5,5,9,9]}')
  offerAlliance(sim, 1, 2)
  expect(offerAlliance(sim, 2, 1)).toBe('allied')
  expect(pathOf(sim.world, unit, 1, alliesOf(sim, 1))).toBe('{"points":[5,5,9,9]}')
  expect(wireOf(sim.world, unit, 1, 0, alliesOf(sim, 1)).parts.has(Path.key)).toBe(true)
  expect(wireOf(sim.world, unit, 3, 0, alliesOf(sim, 3)).parts.has(Path.key)).toBe(false)
})

test('стена союзника стрельбе не мешает, а чужая — по-прежнему укрытие', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256, fog: false, rules: { protection: 0, techTree: false } })
  const victim = spawnUnit(sim, 'infantry', 2, 3, 0)
  spawnUnit(sim, 'infantry', 1, 0, 0)
  const wall = placeBuilding(sim.world, 'wall', 1, 0, 3)
  for (const player of [1, 2, 3]) addCredits(sim, player, 100)
  // Стена третьего — чужая для стрелка: пули достаются ей, пехотинец за ней цел.
  for (let i = 0; i < 60; i++) sim.advance(STEP)
  expect(sim.world.get(victim, Health)!.value).toBe(1)
  expect(sim.world.get(wall, Health)!.value).toBeLessThan(1)
  // Союз — и та же стена не мешает: своя стена — бруствер, через неё стреляют, см. combat.ts.
  offerAlliance(sim, 1, 3)
  expect(offerAlliance(sim, 3, 1)).toBe('allied')
  for (let i = 0; i < 50; i++) sim.advance(STEP)
  expect(sim.world.get(victim, Health)!.value).toBeLessThan(0.9)
})
