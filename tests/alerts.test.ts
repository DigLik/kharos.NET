import { expect, test } from 'bun:test'
import { createAlerts } from '../src/game/alerts'
import type { Scene } from '../src/game/scene'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { tileKey } from '../src/map/terrain'
import { Health, Shot, createSim } from '../src/sim'
import { spawnUnit } from '../src/sim/units'

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
