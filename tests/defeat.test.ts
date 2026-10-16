import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Terrain, isCliffFoot, terrainAt } from '@kharos/shared/terrain'
import { Blast, Building, Health, Owner, Position, Unit, armyOf, createSim, creditsOf, isDefeated, spawnStartingUnits, type Sim } from '@kharos/sim-ts'
import { spawnUnit } from '@kharos/sim-ts/units'
import { placeBuilding } from '@kharos/sim-ts/buildings'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

function start() {
  const sim = createSim(options)
  for (let y = 0; y < 400; y++) {
    for (let x = 0; x < 400; x++) {
      let ok = true
      for (let ty = y; ty < y + 12 && ok; ty++) for (let tx = x; tx < x + 12 && ok; tx++) ok = terrainAt(sim.land, tx, ty) === Terrain.Rock && !isCliffFoot(sim.land, tx, ty)
      if (!ok) continue
      spawnStartingUnits(sim, 1, x + 2, y + 2)
      let mcv: Entity | undefined
      for (const [entity, unit] of sim.world.query(Unit)) if (unit.type === 'mcv') mcv = entity
      sim.send(1, { type: 'deploy', unit: mcv! })
      seconds(sim, 6)
      return sim
    }
  }
  throw new Error('Нет места')
}

const owned = (sim: Sim, player: number) => {
  let count = 0
  for (const [, owner] of sim.world.query(Owner)) if (owner.player === player) count++
  return count
}

test('сдавшийся взрывает всё своё волной и проигрывает', () => {
  const sim = start()
  expect(isDefeated(sim, 1)).toBe(false)
  const before = owned(sim, 1)
  sim.send(1, { type: 'surrender' })
  sim.advance(TICK)
  expect(creditsOf(sim, 1)).toBe(0)
  // Не всё сразу: волна идёт от главного здания.
  seconds(sim, 0.3)
  expect(owned(sim, 1)).toBe(before)
  let blasts = 0
  for (let i = 0; i < 6 / TICK; i++) {
    sim.advance(TICK)
    for (const _ of sim.world.query(Blast)) blasts++
  }
  expect(owned(sim, 1)).toBe(0)
  expect(blasts).toBeGreaterThan(0)
  expect(isDefeated(sim, 1)).toBe(true)
})

test('игрока, которого в мире нет, проигравшим не считают; с главным зданием — не проиграл', () => {
  const sim = start()
  expect(isDefeated(sim, 2)).toBe(false)
  expect(isDefeated(sim, 1)).toBe(false)
})

test('без главного здания, но с другим зданием — не проиграл; без зданий и MCV — проиграл', () => {
  const sim = start()
  // Массив строки обхода один на весь обход — читаем и возвращаем сразу.
  const core = (() => {
    for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
      if (building.type === 'command' && owner.player === 1) return entity
    }
    throw new Error('Главное здание игрока 1 не найдено')
  })()
  const spot = sim.world.get(core, Position)!
  sim.world.destroy(core)
  const generator = placeBuilding(sim.world, 'generator', spot.x, spot.y, 1)
  expect(isDefeated(sim, 1)).toBe(false)
  sim.world.destroy(generator)
  expect(isDefeated(sim, 1)).toBe(true)
})

test('игрок уничтожает своих юнитов: они гибнут на месте, никого не задев; кредиты не возвращаются, лимит свободен', () => {
  const sim = start()
  let core: { x: number; y: number } | undefined
  for (const [, building, position] of sim.world.query(Building, Position)) if (building.type === 'command') core = position
  const tank = spawnUnit(sim, 'tank', 1, core!.x + 5, core!.y + 1)
  const neighbour = spawnUnit(sim, 'tank', 1, core!.x + 6, core!.y + 1)
  // Чужой — далеко: рядом его расстреляли бы, а проверяется другое.
  const foe = spawnUnit(sim, 'truck', 2, core!.x + 60, core!.y + 2)
  sim.advance(TICK)
  const credits = creditsOf(sim, 1)
  const used = armyOf(sim, 1)
  // Чужого уничтожить нельзя, своего — можно.
  sim.send(1, { type: 'scuttle', units: [tank, foe] })
  seconds(sim, 0.2)
  expect(sim.world.has(tank, Unit)).toBe(false)
  expect(sim.world.has(foe, Unit)).toBe(true)
  expect(sim.world.get(neighbour, Health)!.value).toBe(1)
  expect(sim.world.get(foe, Health)!.value).toBe(1)
  expect(armyOf(sim, 1)).toBeLessThan(used)
  expect(creditsOf(sim, 1)).toBeLessThanOrEqual(credits + 1)
  expect(isDefeated(sim, 1)).toBe(false)
})
