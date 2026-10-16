import { afterEach, beforeEach, expect, test } from 'bun:test'
import { DEFAULT_CONFIG } from '../src/map/terrain'
import { createHost, type Host, type HostSave } from '../src/net/host'
import { FACING, MOTION_FIELDS, POSITION, SPAN_MOST, decodeDelta, encodeDelta } from '../src/net/codec'
import { decodeServer } from '../src/net/protocol'
import type { WeaponType } from '../src/sim/weapons'
import { createReplica, type Replica } from '../src/net/replica'
import { Armed, Building, Ghost, Health, Inventory, NO_PAVE, Owner, Path, Player, Position, Producer, Unit, canDeploy, createSim, creditsOf, isWalkable, paveCode, shownTo } from '../src/sim'
import { STARTING_CREDITS } from '../src/sim/economy'
import { SAVED, Scan } from '../src/sim/components'
import { connectAll } from '../src/sim/piping'
import { wireOf } from '../src/net/wire'
import type { Entity } from '@kharos/ecs'
import { placeBuilding } from '../src/sim/buildings'
import { spawnUnit } from '../src/sim/units'
import { throughJson } from './throughJson'

const STEP = 1 / 20

// Хост ставит игроков в случайные места: тестам нужны одни и те же при каждом прогоне.
const random = Math.random
beforeEach(() => {
  let seed = 7
  Math.random = () => (seed = (seed * 16807) % 2147483647) / 2147483647
})
afterEach(() => {
  Math.random = random
})

/** Клиент, подключённый к хосту напрямую, без сокета: сообщения ходят так же, текстом. */
function join(host: Host, id?: string, name?: string) {
  let replica: Replica | undefined
  let issued: string | undefined
  const peer = host.join(
    (data) => {
      const message = decodeServer(data)
      if (message.type === 'welcome' && !replica) {
        issued = message.id
        replica = createReplica(message, (reply) => peer.receive(reply), () => peer.leave())
      } else replica!.receive(message)
    },
    id,
    name,
  )
  return { peer, sim: replica!, id: issued! }
}

const unitsOf = (sim: Replica, player: number) => {
  const units: number[] = []
  for (const [entity] of sim.world.query(Unit)) if (sim.world.get(entity, Owner)?.player === player) units.push(entity)
  return units
}

test('клиент получает мир сразу после подключения', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const { peer, sim } = join(host)
  sim.advance(0)
  expect(peer.player).toBe(1)
  expect(unitsOf(sim, 1).length).toBe(unitsOf(host.sim as never, 1).length)
  expect(unitsOf(sim, 1).length).toBeGreaterThan(0)
})

test('клиент получает только то, что видит: чужую базу вдали — нет, а подошедшего врага — да', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  const second = join(host)
  host.advance(STEP)
  first.sim.advance(STEP)
  expect(unitsOf(first.sim, 2)).toEqual([])
  // Игрок 2 подогнал юнита вплотную к юниту игрока 1.
  const [own] = unitsOf(first.sim, 1)
  const [foe] = unitsOf(host.sim as never, 2)
  Object.assign(host.sim.world.get(foe as never, Position)!, first.sim.world.get(own, Position)!)
  host.advance(STEP)
  first.sim.advance(STEP)
  expect(unitsOf(first.sim, 2)).toEqual([foe])
  // Счёт чужого игрока не приходит никогда.
  expect(second.peer.player).toBe(2)
  expect([...first.sim.world.query(Player)].map(([, player]) => player.id)).toEqual([1])
})

test('приказ клиента выполняет сервер, а клиент видит результат', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const { sim } = join(host)
  sim.advance(0)
  // Багги: техника, обрыв ей закрыт, но узкий проход у края ей не тесен — не то что MCV.
  const unit = unitsOf(sim, 1).find((entity) => sim.world.get(entity, Unit)?.type === 'buggy')!
  const from = { ...sim.world.get(unit, Position)! }

  // Куда-нибудь в три тайла, куда технике можно: рядом может быть обрыв.
  const target = [[3, 0], [-3, 0], [0, 3], [0, -3]]
    .map(([dx, dy]) => ({ x: Math.floor(from.x) + dx, y: Math.floor(from.y) + dy }))
    .find(({ x, y }) => isWalkable(host.sim, x, y))!
  sim.send(1, { type: 'move', units: [unit], x: target.x, y: target.y })
  // Сама копия мир не считает: без сервера юнит стоит.
  sim.advance(1)
  expect(sim.world.get(unit, Position)).toEqual(from)

  for (let i = 0; i < 40; i++) {
    host.advance(STEP)
    sim.advance(STEP)
  }
  expect(sim.time.tick).toBe(host.sim.time.tick)
  // Место приходит округлённым до тысячной тайла, см. wire.ts.
  const there = host.sim.world.get(unit as never, Position)!
  expect(sim.world.get(unit, Position)!.x).toBeCloseTo(there.x, 2)
  expect(sim.world.get(unit, Position)!.y).toBeCloseTo(there.y, 2)
  expect(Math.hypot(there.x - from.x, there.y - from.y)).toBeGreaterThan(1)
})

test('чужими юнитами клиент не командует', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  const second = join(host)
  expect(second.peer.player).toBe(2)
  second.sim.advance(0)
  // Юнитов первого игрока второй не видит — они далеко, — но номер юнита можно и угадать.
  expect(unitsOf(second.sim, 1)).toEqual([])
  first.sim.advance(0)
  const [unit] = unitsOf(first.sim, 1)
  const from = { ...first.sim.world.get(unit, Position)! }

  second.sim.send(2, { type: 'move', units: [unit], x: Math.floor(from.x) + 3, y: Math.floor(from.y) })
  for (let i = 0; i < 20; i++) host.advance(STEP)
  first.sim.advance(STEP)
  expect(first.sim.world.get(unit, Position)).toEqual(from)
})

test('вернувшийся игрок получает прежние юниты, а мусор из сети сервер не роняет', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  first.peer.receive('не JSON')
  first.peer.receive('null')
  first.peer.receive('{"type":"command","command":{"type":"move","units":"все"}}')
  first.peer.receive('{"type":"command","command":7}')
  host.advance(STEP)
  const size = host.sim.world.size
  first.peer.leave()
  expect(host.peers).toBe(0)

  const again = join(host, first.id)
  expect(again.peer.player).toBe(1)
  expect(again.id).toBe(first.id)
  // Счёт приходит и вернувшемуся: кредиты на полосе — из копии мира, а не с хоста.
  again.sim.advance(0)
  expect(creditsOf(again.sim as never, 1)).toBe(STARTING_CREDITS)
  expect(host.sim.world.size).toBe(size)
  // Чужой id хост не знает: это новый игрок со своим id.
  const stranger = join(host, 'выдуманный')
  expect(stranger.peer.player).toBe(2)
  expect(stranger.id).not.toBe('выдуманный')
})

test('между тиками копия ведёт alpha, а пропавшее соединение останавливает игру', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const { sim } = join(host)
  sim.advance(0)
  expect(sim.time.alpha).toBe(0)
  sim.advance(STEP / 2)
  expect(sim.time.alpha).toBeCloseTo(0.5)
  sim.advance(STEP)
  expect(sim.time.alpha).toBe(1)
  sim.fail('обрыв')
  expect(() => sim.advance(STEP)).toThrow('обрыв')
})

test('чужое здание, ушедшее в туман, остаётся призраком, пока место не увидят снова', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  join(host)
  host.advance(STEP)
  // Все юниты игрока 1 ходят вместе: сдвинуть его глаза — значит сдвинуть их всех.
  const eyes = unitsOf(host.sim as never, 1).map((entity) => host.sim.world.get(entity as never, Position)!)
  const shift = (dx: number) => eyes.forEach((position) => (position.x += dx))
  const { x, y } = eyes[0]
  const yard = placeBuilding(host.sim.world, 'khariteVault', Math.floor(x) + 2, Math.floor(y), 2)
  const step = () => {
    host.advance(STEP)
    first.sim.advance(STEP)
  }
  // Обзор пересчитывается раз в несколько тиков: столько и ждём, чтобы хост заметил перемену.
  const settle = () => {
    for (let i = 0; i < 5; i++) step()
  }
  step()
  expect(first.sim.world.has(yard, Building)).toBe(true)
  expect(first.sim.world.has(yard, Ghost)).toBe(false)

  // Юниты ушли — хост здание больше не шлёт, а клиент его помнит.
  shift(-60)
  settle()
  expect(first.sim.world.has(yard, Ghost)).toBe(true)

  // Здание снесли в тумане: клиент узнаёт об этом, только вернувшись.
  host.sim.world.destroy(yard as never)
  step()
  expect(first.sim.world.has(yard, Ghost)).toBe(true)
  shift(60)
  settle()
  expect(first.sim.world.alive(yard)).toBe(false)
})

test('вернувшийся игрок видит призраками чужие здания, разведанные раньше, — и после перезапуска сервера', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  join(host)
  host.advance(STEP)
  const eyes = unitsOf(host.sim as never, 1).map((entity) => host.sim.world.get(entity as never, Position)!)
  const shift = (dx: number) => eyes.forEach((position) => (position.x += dx))
  const { x, y } = eyes[0]
  const yard = placeBuilding(host.sim.world, 'khariteVault', Math.floor(x) + 2, Math.floor(y), 2)
  const settle = () => {
    for (let i = 0; i < 8; i++) host.advance(STEP)
  }
  settle()
  shift(-60)
  settle()
  first.peer.leave()

  // Здание в тумане: новая вкладка его не видит, но хост помнит, каким игрок видел его последний раз.
  const again = join(host, first.id)
  again.sim.advance(0)
  expect(again.sim.world.has(yard, Ghost)).toBe(true)
  expect(again.sim.world.get(yard, Building)?.type).toBe('khariteVault')
  expect(again.sim.world.get(yard, Position)).toEqual({ x: Math.floor(x) + 2, y: Math.floor(y) })
  // Чужой склад — тайна хозяина и в памяти.
  expect(host.sim.world.has(yard as never, Inventory)).toBe(true)
  expect(again.sim.world.has(yard, Inventory)).toBe(false)
  again.peer.leave()

  const save = throughJson(host.save()) as HostSave
  const restarted = join(createHost(createSim(save.sim), undefined, save), first.id)
  restarted.sim.advance(0)
  expect(restarted.sim.world.has(yard, Ghost)).toBe(true)

  // Здание снесли, и юниты игрока увидели пустое место, пока его самого не было: призрака больше нет.
  host.sim.world.destroy(yard as never)
  settle()
  const absent = join(host, first.id)
  absent.sim.advance(0)
  expect(absent.sim.world.has(yard, Ghost)).toBe(true)
  absent.peer.leave()
  shift(60)
  settle()
  shift(-60)
  settle()
  const back = join(host, first.id)
  back.sim.advance(0)
  expect(back.sim.world.alive(yard)).toBe(false)
})

test('вернувшийся игрок получает карту, разведанную раньше', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  host.advance(STEP)
  first.sim.advance(STEP)
  const [unit] = unitsOf(first.sim, 1)
  const { x, y } = first.sim.world.get(unit, Position)!
  first.peer.leave()

  const again = join(host, first.id)
  // Ещё ни одного мира от хоста, а разведанное уже есть.
  expect(again.sim.vision.explored(1, x, y)).toBe(true)
  expect(again.sim.vision.explored(1, x + 100, y)).toBe(false)
})

test('игроки видят ники друг друга и кто из них в сети', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host, undefined, '  Вася\u0007 ')
  const second = join(host)
  expect(first.sim.players).toEqual([
    { player: 1, name: 'Вася', online: true },
    { player: 2, name: 'Игрок 2', online: true },
  ])
  second.peer.leave()
  expect(first.sim.players[1].online).toBe(false)
  // Вернулся без ника — ник прежний.
  const back = join(host, second.id, 'Петя')
  expect(back.sim.players[1]).toEqual({ player: 2, name: 'Петя', online: true })
  expect(join(host, first.id).sim.players[0].name).toBe('Вася')
})

test('сохранённый сервер после перезапуска узнаёт игроков по id, помнит ники и не путает номера новых', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host, undefined, 'Вася')
  join(host, undefined, 'Петя').peer.leave()
  host.advance(STEP * 4)
  const units = unitsOf(first.sim, 1).length
  const save = throughJson(host.save()) as HostSave

  const restarted = createHost(createSim(save.sim), undefined, save)
  const back = join(restarted, first.id)
  expect(back.peer.player).toBe(1)
  expect(unitsOf(back.sim, 1).length).toBe(units)
  expect(back.sim.players).toEqual([
    { player: 1, name: 'Вася', online: true, safe: true },
    { player: 2, name: 'Петя', online: false, safe: true },
  ])
  expect(join(restarted).peer.player).toBe(3)
})

test('мир клиента, собранный из изменений, совпадает с тем, что игрок видит на хосте', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const first = join(host)
  const second = join(host)
  first.sim.advance(0)
  second.sim.advance(0)
  // Юниты обоих едут навстречу друг другу: появляются в обзоре, двигаются, уходят, стреляют.
  const target = host.sim.world.get(unitsOf(host.sim as never, 2)[0]! as never, Position)!
  const start = host.sim.world.get(unitsOf(host.sim as never, 1)[0]! as never, Position)!
  first.sim.send(1, { type: 'move', units: unitsOf(first.sim, 1), x: Math.floor(target.x), y: Math.floor(target.y) })
  second.sim.send(2, { type: 'move', units: unitsOf(second.sim, 2), x: Math.floor(start.x), y: Math.floor(start.y) })
  // Сравнивается то, что уходит в сеть: хост шлёт не все поля и округляет дробные, см. wire.ts.
  const wired = (world: Replica['world'], player: number, keep: (entity: Entity) => boolean) =>
    [...world.all]
      .filter(keep)
      .sort((a, b) => a - b)
      .map((entity) => {
        const { parts, motion } = wireOf(world, entity, player, 0)
        return [entity, { ...Object.fromEntries([...parts].map(([key, json]) => [key, JSON.parse(json)])), motion }]
      })
      .filter(([, data]) => Object.keys(data).length > 1 || (data as { motion?: unknown }).motion)
  const seen = (sim: Replica, player = 1) => wired(sim.world, player, (entity) => !sim.world.has(entity, Ghost))
  const truth = (player: number) => wired(host.sim.world as never, player, (entity) => shownTo(host.sim, player, entity as never))
  for (let tick = 1; tick <= 600; tick++) {
    host.advance(STEP)
    // Первый клиент применяет каждое изменение, второй — пачками по несколько.
    first.sim.advance(STEP)
    if (tick % 7 === 0) second.sim.advance(STEP)
    if (tick % 50 === 0) expect(seen(first.sim)).toEqual(truth(1))
  }
  second.sim.advance(STEP)
  expect(seen(second.sim, 2)).toEqual(truth(2))
})

test('сдавшийся на сервере начинает заново: новый стартовый набор, а до поражения — нельзя', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const { peer, sim } = join(host)
  host.advance(STEP)
  const before = unitsOf(host.sim as never, 1).length
  // Не проигравший заново не начинает.
  sim.respawn()
  host.advance(STEP)
  expect(unitsOf(host.sim as never, 1).length).toBe(before)
  sim.send(1, { type: 'surrender' })
  for (let i = 0; i < 8 / STEP; i++) host.advance(STEP)
  expect(unitsOf(host.sim as never, 1).length).toBe(0)
  const generation = sim.generation
  sim.respawn()
  host.advance(STEP)
  expect(unitsOf(host.sim as never, 1).length).toBe(before)
  expect(peer.player).toBe(1)
  sim.advance(STEP)
  // Клиент получил новое приветствие: туман и камера — как в новом мире, а юниты нового набора у него есть.
  expect(sim.generation).toBe(generation + 1)
  expect(unitsOf(sim, 1).length).toBe(before)
  expect(creditsOf(sim as never, 1)).toBe(STARTING_CREDITS)
})

test('склад, очередь производства и цель стрелка видны только хозяину', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256, fog: false }))
  const first = join(host)
  const second = join(host)
  host.advance(STEP)
  first.sim.advance(STEP)
  second.sim.advance(STEP)
  const mcv = unitsOf(host.sim as never, 1).find((entity) => host.sim.world.get(entity as never, Unit)!.type === 'mcv')!
  // Свой MCV — с очередью производства, чужой — без неё; сам юнит виден обоим.
  expect(first.sim.world.has(mcv as never, Producer)).toBe(true)
  expect(second.sim.world.has(mcv as never, Unit)).toBe(true)
  expect(second.sim.world.has(mcv as never, Producer)).toBe(false)

  // В кого целится стрелок, знает только хозяин.
  const soldier = unitsOf(host.sim as never, 1).find((entity) => host.sim.world.get(entity as never, Unit)!.type === 'infantry')!
  const enemy = unitsOf(host.sim as never, 2)[0]!
  host.sim.world.get(soldier as never, Armed)!.target = enemy
  const armedFor = (player: number) => JSON.parse(wireOf(host.sim.world as never, soldier as never, player, 0).parts.get(Armed.key)!)
  expect(armedFor(1).target).toBe(enemy)
  expect(armedFor(2)).not.toHaveProperty('target')
})

test('цель приказа выбирает сервер по точке: атаку по номеру сущности не берёт, а точка в тумане — движение', () => {
  // Без защиты новичка: под ней врага не атаковать.
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256, rules: { protection: 0 } }))
  const first = join(host)
  join(host)
  const { world } = host.sim
  const at = (entity: number) => world.get(entity as never, Position)!
  const [own] = unitsOf(host.sim as never, 1)
  const [far] = unitsOf(host.sim as never, 2)
  const gunner = spawnUnit(host.sim, 'infantry', 1, at(own).x, at(own).y)
  const armed = () => world.get(gunner, Armed)!
  const command = (value: object) => first.peer.receive(JSON.stringify({ type: 'command', command: value }))
  host.advance(STEP)
  expect(host.sim.vision.seesEntity(1, far as never)).toBe(false)
  // По номеру врага, которого игрок не видит, приказать нельзя.
  command({ type: 'attack', units: [gunner], target: far })
  host.advance(STEP)
  host.advance(STEP)
  expect(armed().ordered).toBe(false)
  expect(world.has(gunner, Path)).toBe(false)
  // Точка, где он стоит, в тумане: туда просто идут.
  command({ type: 'order', units: [gunner], x: at(far).x, y: at(far).y })
  host.advance(STEP)
  host.advance(STEP)
  expect(armed().ordered).toBe(false)
  expect(world.has(gunner, Path)).toBe(true)
  // Враг на виду: тот же приказ точкой — атака.
  const near = spawnUnit(host.sim, 'truck', 2, at(gunner).x + 3, at(gunner).y)
  host.advance(STEP)
  command({ type: 'order', units: [gunner], x: at(near).x, y: at(near).y })
  host.advance(STEP)
  host.advance(STEP)
  expect(armed()).toMatchObject({ target: near, ordered: true })
})

test('движение в кадре несёт срок: за сколько тиков оно набежало, не больше SPAN_MOST', () => {
  const bytes = encodeDelta(7, [[5, [256, 512, null], 1], [300, [0, 0, FACING], 9], [2, [1, 1, null], 500]], [], '{"set":[],"unset":[],"remove":[]}')
  const { tick, motion } = decodeDelta(bytes)
  expect(tick).toBe(7)
  expect(motion).toEqual([5, 1, 2, NaN, 1, 300, 0, 0, 1, 9, 2, 1 / POSITION, 1 / POSITION, NaN, SPAN_MOST])
})

test('выстрел идёт в кадре двоичной записью, а клиенту приходит как set — раньше остального о той же сущности', () => {
  const shot = { weapon: 'cannon' as WeaponType, player: 3, source: 70000, target: -1, fromX: -12.345, fromY: 300.5, toX: 0.001, toY: -511.999, prevX: 1, prevY: 2, age: 4, life: 130, blocked: true, air: false }
  const bytes = encodeDelta(900, [], [[41, shot, 896], [42, { ...shot, weapon: 'rifle', target: 5, blocked: false, air: true }, 900]], '{"set":[[41,{"Owner":{"player":3}}]],"unset":[],"remove":[9]}')
  const { set, remove } = decodeDelta(bytes)
  const { prevX: _x, prevY: _y, age: _age, ...sent } = shot
  expect(set).toEqual([
    [41, { Shot: { ...sent, born: 896 } }],
    [42, { Shot: { ...sent, weapon: 'rifle', target: 5, blocked: false, air: true, born: 900 } }],
    [41, { Owner: { player: 3 } }],
  ])
  expect(remove).toEqual([9])
  // Запись — около 30 байт вместо полутора сотен в JSON.
  expect(bytes.length).toBeLessThan(6 + 2 * 34 + 60)
})

test('тесный бюджет: новое приходит сразу, что на экране — чаще остального, а отложенное не застревает и догоняет', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256, fog: false, rules: { techTree: false } })
  // Байт на тик — на три десятка записей движения из четырёх сотен; время не ограничено, чтобы тест не зависел от машины.
  const host = createHost(sim, undefined, undefined, { budget: { ms: Infinity, bytes: 600 * 20 } })
  const updates = new Map<number, number>()
  let replica: Replica | undefined
  const peer = host.join((data) => {
    const message = decodeServer(data)
    if (message.type === 'welcome') replica = createReplica(message, () => {}, () => {})
    else replica!.receive(message)
    if (message.type === 'delta') for (let i = 0; i < message.motion.length; i += MOTION_FIELDS) updates.set(message.motion[i], (updates.get(message.motion[i]) ?? 0) + 1)
  })
  // Экран игрока стоит на первом отряде; второй — далеко за экраном.
  peer.receive(JSON.stringify({ type: 'view', left: -70, top: -20, right: -30, bottom: 20 }))
  const squad = (x: number) => Array.from({ length: 200 }, (_, i) => spawnUnit(sim, 'infantry', 1, x + (i % 20), -10 + Math.floor(i / 20)))
  const shown = squad(-60)
  const away = squad(40)
  const everyone = [...shown, ...away]
  host.advance(STEP)
  replica!.advance(STEP)
  // Появившееся — обязательное: все четыре сотни пришли в том же тике, бюджет им не указ.
  for (const unit of everyone) expect(replica!.world.has(unit, Unit)).toBe(true)

  updates.clear()
  let deferred = 0
  for (let tick = 0; tick < 200; tick++) {
    for (const unit of everyone) sim.world.get(unit, Position)!.x += 0.01
    host.advance(STEP)
    replica!.advance(STEP)
    deferred = Math.max(deferred, host.sending.deferred)
  }
  const count = (units: number[]) => units.reduce((sum, unit) => sum + (updates.get(unit) ?? 0), 0)
  expect(deferred).toBeGreaterThan(0)
  expect(count(shown)).toBeGreaterThan(count(away) * 4)
  // За экраном никто не забыт: возраст растёт, и очередь доходит до каждого.
  for (const unit of away) expect(updates.get(unit) ?? 0).toBeGreaterThan(0)

  // Юнит, появившийся посреди перегрузки, приходит в том же тике.
  const late = spawnUnit(sim, 'infantry', 1, 0, 30)
  host.advance(STEP)
  replica!.advance(STEP)
  expect(replica!.world.has(late, Unit)).toBe(true)

  // Юниты встали — отложенное доходит, и копия совпадает с хостом.
  const settled = everyone.map((unit) => ({ ...sim.world.get(unit, Position)! }))
  for (let tick = 0; tick < 200; tick++) {
    everyone.forEach((unit, i) => Object.assign(sim.world.get(unit, Position)!, settled[i]))
    host.advance(STEP)
    replica!.advance(STEP)
  }
  expect(host.sending.deferred).toBe(0)
  everyone.forEach((unit, i) => {
    expect(replica!.world.get(unit, Position)!.x).toBeCloseTo(settled[i].x, 2)
    expect(replica!.world.get(unit, Position)!.y).toBeCloseTo(settled[i].y, 2)
  })
})

test('юнит, которого хост придержал на несколько тиков, едет к новому месту столько же тиков, а не прыгает', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const { sim } = join(host)
  sim.advance(0)
  const id = 100_000
  let tick = sim.time.tick
  const frame = (motion: number[] = [], set: [number, Record<string, object>][] = []) => {
    sim.receive({ type: 'delta', tick: ++tick, set, unset: [], remove: [], motion })
    sim.advance(STEP)
    return sim.world.get(id as Entity, Position)!
  }
  const unit = () => sim.world.get(id as Entity, Unit)!
  expect(frame([id, 0, 0, 0, 1], [[id, { Unit: { type: 'tank' }, Owner: { player: 1 } }]])).toEqual({ x: 0, y: 0 })
  // Четыре тика о нём не слышно, потом место в четырёх тайлах и срок — четыре тика.
  expect(frame([id, 4, 0, 2, 4]).x).toBeCloseTo(1)
  expect(unit().prevX).toBeCloseTo(0)
  expect(unit().facing).toBeCloseTo(0.5)
  expect(frame().x).toBeCloseTo(2)
  expect(unit().prevX).toBeCloseTo(1)
  expect(frame().x).toBeCloseTo(3)
  expect(frame()).toEqual({ x: 4, y: 0 })
  expect(unit().facing).toBe(2)
  // Доехал — стоит.
  expect(frame()).toEqual({ x: 4, y: 0 })
  expect(unit().prevX).toBe(4)
  // Новое место посреди пути — едет к нему оттуда, где оказался; срок в один тик — сразу на место.
  expect(frame([id, 8, 0, 2, 2]).x).toBeCloseTo(6)
  expect(frame([id, 0, 0, 2, 1])).toEqual({ x: 0, y: 0 })
  expect(unit().prevX).toBeCloseTo(6)
})

test('притихшее сверяется реже, но изменение доходит за секунду, а тронувшийся с места юнит не плывёт к новому месту', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256 }))
  const motions: number[][] = []
  const peer = host.join((data) => {
    const message = decodeServer(data)
    if (message.type === 'delta') motions.push(message.motion)
  })
  const { sim, peer: second } = join(host)
  const ownUnit = (player: number) => {
    for (const [entity, , owner] of host.sim.world.query(Unit, Owner)) if (owner.player === player) return entity
    throw new Error(`у игрока ${player} нет юнитов`)
  }
  const near = host.sim.world.get(ownUnit(second.player), Position)!
  const building = placeBuilding(host.sim.world, 'metalYard', Math.floor(near.x) + 3, Math.floor(near.y) + 3, second.player)
  // Две секунды без дела: здание и стартовый набор притихли.
  for (let i = 0; i < 40; i++) {
    host.advance(STEP)
    sim.advance(STEP)
  }
  const health = host.sim.world.get(building, Health)!
  // Прочность меняется без попадания: здание не «в бою» и сверяется как притихшее.
  health.value = health.max / 2
  let ticks = 0
  while (sim.world.get(building, Health)?.value !== health.value && ticks < 40) {
    host.advance(STEP)
    sim.advance(STEP)
    ticks++
  }
  expect(sim.world.get(building, Health)?.value).toBe(health.value)
  expect(ticks).toBeGreaterThan(1)
  expect(ticks).toBeLessThanOrEqual(20)

  // Юнит первого клиента стоял, теперь сдвинулся на тайл: он тронулся только что, срок движения — один тик.
  const unit = ownUnit(peer.player)
  host.sim.world.get(unit, Position)!.x += 1
  motions.length = 0
  host.advance(STEP)
  const flat = motions.flat()
  const at = flat.findIndex((value, index) => index % MOTION_FIELDS === 0 && value === unit)
  expect(at).toBeGreaterThanOrEqual(0)
  expect(flat[at + 4]).toBe(1)
})

test('готовое покрытие: своё клиент знает сразу, чужое — увидев, и помнит его в тумане, пока не увидит снова', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 512 }))
  const first = join(host)
  const second = join(host)
  const tick = (count = 1) => {
    for (let i = 0; i < count; i++) {
      host.advance(STEP)
      first.sim.advance(STEP)
      second.sim.advance(STEP)
    }
  }
  tick(2)
  const positions = (player: number) => {
    const found: { x: number; y: number }[] = []
    for (const [, position, owner] of host.sim.world.query(Position, Owner)) if (owner.player === player) found.push(position)
    return found
  }
  // Дорога второго игрока в стороне от всех; войско первого — ещё дальше.
  const [base] = positions(second.peer.player)
  const road = { x: Math.floor(base.x) + 60, y: Math.floor(base.y) }
  const away = { x: road.x - 120, y: road.y }
  for (const position of positions(first.peer.player)) Object.assign(position, away)
  host.sim.paving.lay(road.x, road.y, paveCode('road', second.peer.player))
  tick(2)
  expect(second.sim.paving.laid(road.x, road.y)).toBe(paveCode('road', second.peer.player))
  expect(first.sim.paving.laid(road.x, road.y)).toBe(NO_PAVE)

  // Подошёл — увидел.
  for (const position of positions(first.peer.player)) Object.assign(position, { x: road.x + 2, y: road.y })
  tick(10)
  expect(first.sim.paving.laid(road.x, road.y)).toBe(paveCode('road', second.peer.player))
  // Ушёл — дорогу разобрали, а он помнит её такой, какой видел.
  for (const position of positions(first.peer.player)) Object.assign(position, away)
  tick(10)
  host.sim.paving.lift(road.x, road.y)
  tick(10)
  expect(first.sim.paving.laid(road.x, road.y)).toBe(paveCode('road', second.peer.player))
  expect(second.sim.paving.laid(road.x, road.y)).toBe(NO_PAVE)
  // Вернулся — узнал.
  for (const position of positions(first.peer.player)) Object.assign(position, { x: road.x + 2, y: road.y })
  tick(10)
  expect(first.sim.paving.laid(road.x, road.y)).toBe(NO_PAVE)
})

test('вспышки радара видит только хозяин: его копия открывает туман там же, где хост, а чужой про них не знает', () => {
  const host = createHost(createSim({ generator: DEFAULT_CONFIG, size: 256, rules: { techTree: false } }))
  const first = join(host)
  const second = join(host)
  const { world } = host.sim
  const at = world.get(unitsOf(host.sim as never, 1)[0]! as never, Position)!
  const x = Math.floor(at.x)
  const y = Math.floor(at.y)
  placeBuilding(world, 'command', x, y + 3, 1)
  placeBuilding(world, 'generator', x + 4, y + 3, 1)
  const radar = placeBuilding(world, 'radar', x + 8, y + 3, 1)
  connectAll(host.sim, 1)
  let flash = world.get(radar, Scan)
  for (let tick = 0; tick < 4000 && !((flash = world.get(radar, Scan)) && flash.tick >= 0 && Math.hypot(flash.x - x, flash.y - y) > 25); tick++) {
    host.advance(STEP)
    first.sim.advance(STEP)
    second.sim.advance(STEP)
  }
  expect(flash!.tick).toBeGreaterThanOrEqual(0)
  for (let tick = 0; tick < 6; tick++) {
    host.advance(STEP)
    first.sim.advance(STEP)
    second.sim.advance(STEP)
  }
  // Далеко за постоянным обзором радара: открыто вспышкой — и на хосте, и в копии хозяина.
  expect(host.sim.vision.sees(1, flash!.x, flash!.y)).toBe(true)
  expect(first.sim.world.get(radar, Scan)).toMatchObject({ x: flash!.x, y: flash!.y, tick: flash!.tick })
  expect(first.sim.vision.sees(1, flash!.x, flash!.y)).toBe(true)
  // Куда смотрит чужой радар, сосед не знает, даже видя сам радар.
  expect(wireOf(world as never, radar as never, 2, 0).parts.has(Scan.key)).toBe(false)
  expect(wireOf(world as never, radar as never, 1, 0).parts.has(Scan.key)).toBe(true)
})

test('новый игрок появляется там, где MCV сразу развернётся', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 1024 })
  const host = createHost(sim)
  for (let i = 0; i < 30; i++) host.join(() => {})
  let mcvs = 0
  for (const [entity, unit, owner] of sim.world.query(Unit, Owner)) {
    if (unit.type !== 'mcv') continue
    mcvs++
    expect(canDeploy(sim, owner.player, entity)).toBe(true)
  }
  expect(mcvs).toBe(30)
})
