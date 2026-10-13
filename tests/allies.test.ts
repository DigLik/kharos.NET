import { afterEach, beforeEach, expect, test } from 'bun:test'
import { DEFAULT_CONFIG } from '../src/map/terrain'
import { createHost, type Host } from '../src/net/host'
import { decodeServer } from '../src/net/protocol'
import { createReplica, type Replica } from '../src/net/replica'
import { Armed, Batch, Health, Mark, Owner, PING_LIMIT, PING_SECONDS, PING_WINDOW, Path, Player, Position, Unit, allied, alliesOf, allyHeadOf, breakAlliance, canAttack, createSim, exitAlliance, isOpenGate, offerAlliance, wipePlayer, type Sim } from '../src/sim'
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

test('союз — предложение и согласие; главой становится предложивший, и он один распоряжается союзом', () => {
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
  // Встречное предложение — согласие, и главой становится тот, кто предложил первым.
  expect(offerAlliance(sim, 2, 1)).toBe('allied')
  expect(allied(sim, 1, 2) && allied(sim, 2, 1)).toBe(true)
  expect(info(1)).toMatchObject({ allies: [2], head: 1, offered: [], asked: [] })
  expect(allyHeadOf(sim, 1)).toBe(1)
  expect(allyHeadOf(sim, 2)).toBe(1)
  expect(offerAlliance(sim, 1, 2)).toBe('already')
  // Союз — группа: принявший союз у главы становится союзником всех, а главой остаётся глава группы.
  expect(offerAlliance(sim, 1, 3)).toBe('offered')
  expect(offerAlliance(sim, 3, 1)).toBe('allied')
  expect(allied(sim, 3, 2) && allied(sim, 2, 3)).toBe(true)
  expect(info(1)).toMatchObject({ allies: [2, 3], head: 1 })
  expect(info(3)).toMatchObject({ allies: [1, 2], head: 1 })
  // Рядовой союзом не распоряжается: ни приглашать, ни отвечать чужому рядовому, ни исключать.
  expect(offerAlliance(sim, 2, 4)).toBe('fromMember')
  expect(offerAlliance(sim, 4, 2)).toBe('toMember')
  expect(breakAlliance(sim, 2, 1)).toBe('notHead')
  // Глава исключает рядового — тот покидает союз целиком, а связи оставшихся целы.
  expect(breakAlliance(sim, 1, 3)).toBe('kicked')
  expect(info(3)).toMatchObject({ allies: [], head: -1 })
  expect(info(1).allies).toEqual([2])
  expect(info(2).allies).toEqual([1])
  // Рядовой выходит сам — и тоже целиком: с ним рвутся все его связи, и он снова одиночка.
  expect(exitAlliance(sim, 2)).toBe('left')
  expect(info(2)).toMatchObject({ allies: [], head: -1 })
  expect(info(1)).toMatchObject({ allies: [], head: -1 })
  expect(exitAlliance(sim, 2)).toBe('nothing')
  // Глава уходит — союз распускается: без главы он не живёт.
  offerAlliance(sim, 1, 3)
  expect(offerAlliance(sim, 3, 1)).toBe('allied')
  offerAlliance(sim, 1, 4)
  expect(offerAlliance(sim, 4, 1)).toBe('allied')
  expect(info(3).allies).toEqual([1, 4])
  expect(exitAlliance(sim, 1)).toBe('dissolved')
  expect(info(1)).toMatchObject({ allies: [], head: -1 })
  expect(info(3)).toMatchObject({ allies: [], head: -1 })
  expect(info(4)).toMatchObject({ allies: [], head: -1 })
  // Отозвать своё предложение и отклонить чужое может всякий: союза тут ещё нет.
  offerAlliance(sim, 4, 1)
  expect(breakAlliance(sim, 1, 4)).toBe('declined')
  expect(info(1).asked).toEqual([])
  expect(info(4).offered).toEqual([])
  expect(breakAlliance(sim, 1, 4)).toBe('nothing')
  // Начавший заново — новый игрок для всех: союзы и предложения пропадают у обеих сторон.
  offerAlliance(sim, 3, 2)
  wipePlayer(sim, 2)
  expect(allied(sim, 2, 3)).toBe(false)
  expect(info(3).offered).toEqual([])
  expect(info(2).allies).toEqual([])
})

test('вступление в группу: принявший союз у главы — союзник и всем остальным, и предложения ждать больше нечего', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  for (const player of [1, 2, 3, 4]) addCredits(sim, player, 100)
  const info = (player: number) => {
    for (const [, data] of sim.world.query(Player)) if (data.id === player) return data
    throw new Error('Игрока нет')
  }
  offerAlliance(sim, 1, 2)
  offerAlliance(sim, 2, 1)
  // Четвёртый и третий предложили главе: предложение, ставшее союзом, из списков уходит.
  offerAlliance(sim, 4, 1)
  offerAlliance(sim, 3, 1)
  expect(info(4)).toMatchObject({ offered: [1], asked: [] })
  expect(info(1).asked).toEqual([4, 3])
  expect(offerAlliance(sim, 1, 3)).toBe('allied')
  expect(info(2).allies).toEqual([1, 3])
  expect(info(3).allies).toEqual([1, 2])
  expect(info(2)).toMatchObject({ offered: [], asked: [] })
  // Предложение четвёртого — предложение всей группе: принимает глава, и четвёртый входит в неё целиком.
  expect(info(1).asked).toEqual([4])
  expect(offerAlliance(sim, 1, 4)).toBe('allied')
  expect(info(4).allies).toEqual([1, 2, 3])
  expect(info(3).allies).toEqual([1, 2, 4])
  expect(info(4).head).toBe(1)
})

test('сохранение прежних версий с попарными союзами достраивается до групп, а главой становится младший', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  for (const player of [1, 2, 3]) addCredits(sim, player, 100)
  sim.advance(STEP)
  const account = (player: number) => {
    for (const [entity, data] of sim.world.query(Player)) if (data.id === player) return { entity, data }
    throw new Error('Игрока нет')
  }
  // Как в старом сохранении: пары есть, главы нет, а что союзник союзника тоже союзник — не записано.
  sim.world.set(account(1).entity, Player, { allies: [2] })
  sim.world.set(account(2).entity, Player, { allies: [1, 3], offered: [3, 4], asked: [4] })
  sim.world.set(account(3).entity, Player, { allies: [2] })
  const loaded = createSim(sim.save())
  const data = (player: number) => {
    for (const [, info] of loaded.world.query(Player)) if (info.id === player) return info
    throw new Error('Игрока нет')
  }
  expect(data(1).allies).toEqual([2, 3])
  expect(data(2).allies).toEqual([1, 3])
  expect(data(3).allies).toEqual([1, 2])
  expect(allied(loaded, 1, 3)).toBe(true)
  // Глава записанной группы — младший номер: у всех троих он один.
  expect(data(1).head).toBe(1)
  expect(data(2).head).toBe(1)
  expect(data(3).head).toBe(1)
  // Предложение внутрь группы ждать больше нечего, а чужому (четвёртому) — по-прежнему ждёт ответа.
  expect(data(2).offered).toEqual([4])
  expect(data(2).asked).toEqual([4])
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
  // Союзник вышел из союза — снова враги.
  expect(exitAlliance(sim, 2)).toBe('left')
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
  // Союзнику сущность идёт целиком, как хозяину: чем занят строитель, видно ему, но не чужому. Строителей в стартовом
  // наборе нет — союзник заказал одного.
  const builder = spawnUnit(host.sim, 'builder', 2, Math.floor(at.x), Math.floor(at.y))
  run(host, all, 0.5)
  expect(first.sim.world.has(builder as never, Repair)).toBe(true)
  expect(wireOf(host.sim.world, builder as never, 3, 0).parts.has(Repair.key)).toBe(false)

  // Рядовой выходит из союза сам: имя не нужно — /unally без него.
  second.say('/unally')
  expect(first.chat).toContain('allyLeftBy')
  run(host, all, 0.5)
  expect(unitsOf(first.sim, 2)).toEqual([])
  expect(first.sim.vision.sees(1, at.x, at.y)).toBe(false)
})

test('союзом распоряжается глава: рядовой не зовёт и не исключает, выходит сам, а уходит глава — союз распущен', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host, 'Вася')
  const second = join(host, 'Петя')
  const third = join(host, 'Коля')
  const all = [first, second, third]
  run(host, all, 0.5)
  first.say('/ally Петя')
  second.say('/ally Вася')
  expect(first.chat).toContain('allyFormed')
  // Рядовой зовёт третьего — отказ: приглашать может только глава. И исключать — тоже.
  second.say('/ally Коля')
  expect(second.chat).toContain('allyNotHead')
  second.say('/unally Вася')
  expect(second.chat).toContain('allyKickNotHead')
  expect(alliesOf(host.sim, 1)).toEqual([2])
  // Глава исключает рядового — тот покидает союз целиком.
  first.say('/unally Петя')
  expect(first.chat).toContain('allyKicked')
  expect(second.chat).toContain('allyKickedBy')
  expect(alliesOf(host.sim, 2)).toEqual([])
  expect(allyHeadOf(host.sim, 2)).toBe(-1)
  // Одиночке выходить неоткуда — и он слышит об этом.
  third.say('/unally')
  expect(third.chat).toContain('allyNoAlliance')
  // Глава предложил, третий принял — и вышел сам: главе весть, сам он снова один.
  first.say('/ally Коля')
  third.say('/ally Вася')
  expect(third.chat).toContain('allyFormed')
  third.say('/unally')
  expect(third.chat).toContain('allyExited')
  expect(first.chat).toContain('allyLeftBy')
  expect(alliesOf(host.sim, 1)).toEqual([])
  // Глава уходит сам — союз распускается целиком.
  first.say('/ally Коля')
  third.say('/ally Вася')
  first.say('/unally')
  expect(first.chat).toContain('allyDissolved')
  expect(third.chat).toContain('allyDissolvedBy')
  expect(alliesOf(host.sim, 3)).toEqual([])
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
  const unit = spawnUnit(sim, 'truck', 2, 5, 5)
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

test('сигнал союзникам: метку видят сам игрок и союзник где угодно, чужой — нет; сигналами не засыпать, и они гаснут', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host, 'Вася')
  const second = join(host, 'Петя')
  const third = join(host, 'Коля')
  const all = [first, second, third]
  run(host, all, 0.5)
  first.say('/ally Петя')
  second.say('/ally Вася')
  run(host, all, 0.5)
  const pings = (sim: Sim) => {
    const list: { x: number; y: number; player: number }[] = []
    for (const [, mark, position] of sim.world.query(Mark, Position)) if (mark.kind === 'ping') list.push({ x: position.x, y: position.y, player: mark.player })
    return list
  }
  // Точка в тумане у всех троих: сигнал — метка, а не обзор.
  const { left, top, right, bottom } = host.sim.bounds
  let spot = { x: 0, y: 0 }
  search: for (let y = top + 2; y < bottom - 2; y += 7) {
    for (let x = left + 2; x < right - 2; x += 7) {
      if ([1, 2, 3].some((player) => host.sim.vision.sees(player, x, y))) continue
      spot = { x: x + 0.5, y: y + 0.25 }
      break search
    }
  }
  expect(second.sim.vision.sees(2, spot.x, spot.y)).toBe(false)
  first.sim.send(1, { type: 'ping', ...spot })
  run(host, all, 0.3)
  expect(pings(host.sim)).toEqual([{ ...spot, player: 1 }])
  expect(pings(first.sim)).toEqual([{ ...spot, player: 1 }])
  expect(pings(second.sim)).toEqual([{ ...spot, player: 1 }])
  expect(pings(third.sim)).toEqual([])
  // Обзора метка не даёт.
  expect(second.sim.vision.sees(2, spot.x, spot.y)).toBe(false)
  // За карту и чаще положенного — нельзя.
  first.sim.send(1, { type: 'ping', x: 1e6, y: 0 })
  for (let i = 0; i < PING_LIMIT + 2; i++) first.sim.send(1, { type: 'ping', x: i, y: i })
  run(host, all, 0.3)
  expect(pings(host.sim).length).toBe(PING_LIMIT)
  // Прошло окно — можно снова; чужой игрок сигналит своим, и первым двум его метка не идёт.
  run(host, all, PING_WINDOW)
  first.sim.send(1, { type: 'ping', x: 5, y: 5 })
  third.sim.send(3, { type: 'ping', x: 7, y: 7 })
  run(host, all, 0.3)
  expect(pings(host.sim).length).toBe(PING_LIMIT + 2)
  expect(pings(second.sim).some(({ player }) => player === 3)).toBe(false)
  expect(pings(third.sim)).toEqual([{ x: 7, y: 7, player: 3 }])
  // Срок вышел — меток нет ни у кого.
  run(host, all, PING_SECONDS + 0.5)
  expect(pings(host.sim)).toEqual([])
  expect(pings(second.sim)).toEqual([])
})
