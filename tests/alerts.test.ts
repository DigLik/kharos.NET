import { expect, test } from 'bun:test'
import { createAlerts } from '../src/game/alerts'
import type { Scene } from '../src/game/scene'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { tileKey } from '@kharos/shared/terrain'
import { DEPOSIT_TYPES, Health, Mark, Missile, Position, Shot, WARNING, createSim } from '@kharos/sim-ts'
import { loadDeposits } from '@kharos/sim-ts/deposits'
import { spawnUnit } from '@kharos/sim-ts/units'

/** Уведомления над миром без отрисовки: им нужны только мир и номер игрока. */
function setup() {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  const alerts = createAlerts({ sim, player: 1 } as Scene, null)
  const kinds = () => alerts.current().map(({ kind }) => kind)
  return { sim, alerts, kinds }
}

test('о замеченном противнике предупреждают не чаще раза в 12 секунд', () => {
  const { sim, alerts, kinds } = setup()
  alerts.update(0.1)
  spawnUnit(sim, 'infantry', 2, 5, 5)
  alerts.update(0.1)
  expect(kinds()).toEqual(['spotted'])
  // Ещё один враг через 5 секунд — тихо.
  alerts.update(5)
  spawnUnit(sim, 'infantry', 2, 6, 5)
  alerts.update(0.1)
  expect(alerts.current().filter(({ kind }) => kind === 'spotted')).toHaveLength(1)
  // Через 12 — снова.
  alerts.update(8)
  spawnUnit(sim, 'infantry', 2, 7, 5)
  alerts.update(0.1)
  expect(kinds().at(-1)).toBe('spotted')
  expect(alerts.current().at(-1)!.age).toBeLessThan(1)
})

test('об атаке предупреждают раз, а снова — когда враги пропали из виду и напали опять', () => {
  const { sim, alerts, kinds } = setup()
  const own = spawnUnit(sim, 'tank', 1, 0, 0)
  const foe = spawnUnit(sim, 'infantry', 2, 5, 5)
  alerts.update(0.1)
  const hit = () => {
    // Выстрел врага в танк и урон от него.
    sim.world.spawn(Shot({ player: 2, fromX: 5, fromY: 5, toX: 0, toY: 0 }))
    sim.world.get(own, Health)!.value -= 1
    alerts.update(0.1)
  }
  hit()
  expect(kinds()).toEqual(['spotted', 'attacked'])
  hit()
  hit()
  expect(kinds().filter((kind) => kind === 'attacked')).toHaveLength(1)
  // Враг пропал из виду: флаг сброшен. Вернулся и снова бьёт — снова тревога.
  sim.world.destroy(foe)
  alerts.update(0.1)
  spawnUnit(sim, 'infantry', 2, 5, 5)
  alerts.update(0.1)
  hit()
  expect(kinds().filter((kind) => kind === 'attacked')).toHaveLength(2)
})

test('урон без чужого выстрела рядом — не атака: прочность падает и от нехватки энергии', () => {
  const { sim, alerts, kinds } = setup()
  const own = spawnUnit(sim, 'tank', 1, 0, 0)
  spawnUnit(sim, 'infantry', 2, 30, 30)
  // Выстрел врага далеко от танка.
  sim.world.spawn(Shot({ player: 2, fromX: 30, fromY: 30, toX: 25, toY: 25 }))
  alerts.update(0.1)
  sim.world.get(own, Health)!.value -= 1
  alerts.update(0.1)
  expect(kinds()).not.toContain('attacked')
})

test('о найденном месторождении сообщают, когда игрок узнал новое; знакомые при входе в мир — не новость', () => {
  const { sim, alerts, kinds } = setup()
  // Как у клиента: слой месторождений присылает хост — только то, что игрок разведал.
  sim.deposits.complete = true
  const learn = (x: number, y: number) => {
    sim.deposits.cells.set(tileKey(Math.floor(x / 40), Math.floor(y / 40)), [{ x, y, kind: 'kharite', reserve: 300 }])
    sim.deposits.revision++
  }
  learn(10, 10)
  alerts.update(0.1)
  expect(kinds()).toEqual([])
  learn(60, 10)
  alerts.update(0.1)
  expect(alerts.current()).toMatchObject([{ kind: 'found', text: 'oreFound', x: 61, y: 11 }])
  // Следом ещё одно — молча: армия в походе находит их пачками.
  learn(100, 10)
  alerts.update(1)
  expect(kinds()).toEqual(['found'])
  alerts.update(6)
  learn(140, 10)
  alerts.update(0.1)
  expect(kinds()).toEqual(['found', 'found'])
})

test('слой в пути не выдаёт знакомое месторождение за находку: иначе пиликает каждый перезаход', () => {
  const { sim, alerts, kinds } = setup()
  // Мир принят (replica.meet): слой этого мира ещё шлётся, а в нём пусто — и пусто не по незнанию игрока.
  sim.deposits.complete = true
  sim.deposits.pending = true
  alerts.update(0.1)
  expect(kinds()).toEqual([])
  // Слой дошёл: месторождение игрок разведал ещё в прошлый заход — оно знакомо, находкой не объявляется.
  const kind = DEPOSIT_TYPES.indexOf('kharite')
  loadDeposits(sim.deposits, { cells: [], spots: [60, 10, kind, 300] })
  expect(sim.deposits.pending).toBe(false)
  alerts.update(0.1)
  expect(kinds()).toEqual([])
  // А разведанное уже в этом заходе — по-прежнему находка, и именно оно, а не знакомое.
  loadDeposits(sim.deposits, { cells: [], spots: [60, 10, kind, 300, 140, 10, kind, 300] })
  alerts.update(0.1)
  expect(alerts.current()).toMatchObject([{ kind: 'found', text: 'oreFound', x: 141, y: 11 }])
})

test('чужая ядерная ракета — сирена всем; точка удара — первый сигнал, а за WARNING секунд до удара — второй', () => {
  const { sim, alerts } = setup()
  const texts = () => alerts.current().map(({ text }) => text)
  alerts.update(0.1)
  // Чужая ракета приходит без точки удара, только с курсом.
  sim.world.spawn(Missile({ fromX: 50, fromY: 50, toX: 51, toY: 50, born: sim.time.tick, blind: true }))
  alerts.update(0.1)
  alerts.update(0.1)
  expect(texts()).toEqual(['nukeLaunched'])
  expect(alerts.current()[0]).toMatchObject({ x: 50, y: 50 })

  // Наше под ударом: метка точки удара, до удара ещё далеко.
  const step = sim.time.step
  const strike = sim.world.spawn(Position({ x: 5, y: 5 }), Mark({ kind: 'strike', until: sim.time.tick + Math.round((WARNING + 10) / step), player: 2 }))
  alerts.update(0.1)
  alerts.update(0.1)
  expect(texts()).toEqual(['nukeLaunched', 'nukeIncoming'])
  // Осталось меньше WARNING — второй сигнал, один раз.
  sim.world.get(strike, Mark)!.until = sim.time.tick + Math.round((WARNING - 1) / step)
  alerts.update(0.1)
  alerts.update(0.1)
  expect(texts()).toEqual(['nukeLaunched', 'nukeIncoming', 'nukeImminent'])

  // Узнали о точке удара уже под конец — только второй сигнал. Свою метку и место пуска сиреной не встречают.
  sim.world.spawn(Position({ x: 9, y: 9 }), Mark({ kind: 'strike', until: sim.time.tick + Math.round(5 / step), player: 3 }))
  sim.world.spawn(Position({ x: 1, y: 1 }), Mark({ kind: 'strike', until: sim.time.tick + Math.round(5 / step), player: 1 }))
  sim.world.spawn(Position({ x: 2, y: 2 }), Mark({ kind: 'launch', until: sim.time.tick + 100, player: 2 }))
  alerts.update(0.1)
  expect(texts()).toEqual(['nukeLaunched', 'nukeIncoming', 'nukeImminent', 'nukeImminent'])
})
