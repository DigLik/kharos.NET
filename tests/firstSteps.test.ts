import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { STEPS, currentStep, stepTarget, stepsDone } from '../src/game/firstSteps'
import { Building, Owner, Player, Position, Unit, createSim, depositAt, depositIn, spawnStartingUnits } from '../src/sim'
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

test('шаг шахты — на металлической руде; цель шага — месторождение металла, потом шахта, к которой ставить лабораторию', () => {
  const sim = createSim({ ...options, fog: false })
  let metal: { x: number; y: number } | undefined
  let other: { x: number; y: number } | undefined
  for (let cellY = -4; cellY < 4 && !(metal && other); cellY++) {
    for (let cellX = -4; cellX < 4 && !(metal && other); cellX++) {
      const spot = depositIn(sim, cellX, cellY)
      if (spot?.kind === 'metal' && !metal) metal = spot
      else if (spot && spot.kind !== 'metal' && !other) other = spot
    }
  }
  if (!metal || !other) throw new Error('Не нашлось месторождений')
  spawnStartingUnits(sim, 1, metal.x + 6, metal.y + 6)
  sim.world.spawn(Position({ x: metal.x + 8, y: metal.y + 8 }), Building({ type: 'command' }), Owner({ player: 1 }))
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { rewards: ['generator', 'matter'] })
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('mine')
  // Цель — месторождение металла, а не первое попавшееся.
  const target = stepTarget(sim, 1, 'mine')!
  expect(depositAt(sim, target.x, target.y)?.kind).toBe('metal')
  // Шахта на другой руде шаг не проходит: на ней плавку не изучить.
  sim.world.spawn(Position({ x: other.x, y: other.y }), Building({ type: 'mine' }), Owner({ player: 1 }))
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('mine')
  sim.world.spawn(Position({ x: metal.x, y: metal.y }), Building({ type: 'mine' }), Owner({ player: 1 }))
  // Руду с шахты в лабораторию возят грузовики: вплотную связаны только одинаковые здания, а трубы — после плавки.
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('truck')
  expect(stepTarget(sim, 1, 'truck')).toEqual({ x: metal.x + 8, y: metal.y + 8, width: 3, height: 3 })
  sim.world.spawn(Unit({ type: 'truck' }), Owner({ player: 1 }))
  expect(currentStep(stepsDone(sim, 1))?.key).toBe('lab')
  expect(stepTarget(sim, 1, 'lab')).toEqual({ x: metal.x, y: metal.y, width: 2, height: 2 })
})
