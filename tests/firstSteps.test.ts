import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { STEPS, currentStep, stepsDone } from '../src/game/firstSteps'
import { Building, Owner, Player, Position, createSim, spawnStartingUnits } from '../src/sim'
import { grantResearch } from '../src/sim/research'
import { spawnUnit } from '../src/sim/units'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }

test('первые шаги: отмечаются по миру, пройденный дальше отмечает и прежние', () => {
  const sim = createSim(options)
  spawnStartingUnits(sim, 1, 0, 0)
  // Старт: MCV не развёрнут — текущий шаг первый.
  expect(stepsDone(sim, 1).every((done) => !done)).toBe(true)
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('deploy')

  // Строитель есть, а MCV всё ещё не развёрнут: строитель мог приехать и так — шаг MCV не пройден, а строителя — да.
  spawnUnit(sim, 'builder', 1, 2, 2)
  expect(stepsDone(sim, 1).slice(0, 2)).toEqual([true, true])
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('generator')

  // Награда за электростанцию остаётся, даже если станцию снесли.
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { rewards: ['generator'] })
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('matter')

  // Открытая плавка — значит, лаборатория и шахта уже были.
  grantResearch(sim, 1, ['science', 'metalOre'])
  sim.world.spawn(Position({ x: 20, y: 20 }), Building({ type: 'matter' }), Owner({ player: 1 }))
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('smelter')

  // Первая продажа — пройдено всё.
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { rewards: ['generator', 'sale'] })
  expect(stepsDone(sim, 1)).toEqual(STEPS.map(() => true))
  expect(currentStep(stepsDone(sim, 1))).toBeUndefined()
  // Чужие шаги — свои.
  expect(stepsDone(sim, 2).some(Boolean)).toBe(false)
})
