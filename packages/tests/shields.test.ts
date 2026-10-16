import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Health, Position, SHIELD_DOWN, Shield, Shot, WEAPONS, createSim, domes, isWalkable, offerAlliance, type Sim } from '@kharos/sim-ts'
import { BUILDINGS, placeBuilding } from '@kharos/sim-ts/buildings'
import { addCredits } from '@kharos/sim-ts/economy'
import { absorb } from '@kharos/sim-ts/shields'
import { spawnUnit } from '@kharos/sim-ts/units'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { protection: 0 } }
const TICK = 1 / 20
const { radius, capacity, regen } = BUILDINGS.shieldGenerator.shield

const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Симуляция и левый верхний тайл ровного проходимого поля. */
function field(width = 36, height = 20) {
  const sim = createSim(options)
  for (let y = -60; y < 60; y++) {
    search: for (let x = -60; x < 60; x++) {
      for (let dy = 0; dy < height; dy++) {
        for (let dx = 0; dx < width; dx++) if (!isWalkable(sim, x + dx, y + dy)) continue search
      }
      return { sim, x, y }
    }
  }
  throw new Error('В мире не нашлось ровного поля')
}

/** Генератор щита 2×2 в (x + 10, y + 9), электростанция и радар рядом; щит заряжен целиком. */
function base(charge = capacity) {
  const { sim, x, y } = field()
  for (const player of [1, 2, 3]) addCredits(sim, player, 5000)
  const shield = placeBuilding(sim.world, 'shieldGenerator', x + 10, y + 9, 1)
  const generator = placeBuilding(sim.world, 'generator', x + 12, y + 9, 1)
  const radar = placeBuilding(sim.world, 'radar', x + 8, y + 9, 1)
  sim.world.get(shield, Shield)!.charge = charge
  return { sim, x, y, shield, generator, radar, cx: x + 11, cy: y + 10 }
}

const health = (sim: Sim, entity: Entity) => sim.world.get(entity, Health)!.value
const charge = (sim: Sim, shield: Entity) => sim.world.get(shield, Shield)!.charge

test('артиллерия из-за купола бьёт в щит, а не в здания под ним', () => {
  const { sim, x, y, shield, generator, radar, cx } = base()
  // Артиллерия снаружи купола, но на выстрел до радара.
  const gun = spawnUnit(sim, 'artillery', 2, Math.floor(cx - radius - 3), y + 10)
  expect(Math.hypot(x + 9 - (cx - radius - 3), 0)).toBeLessThan(11)
  sim.send(2, { type: 'attack', units: [gun], target: radar })
  seconds(sim, 12)
  expect(charge(sim, shield)).toBeLessThan(capacity)
  for (const entity of [shield, generator, radar]) expect(health(sim, entity)).toBe(1)
})

test('кто зашёл под купол, бьёт мимо щита', () => {
  const { sim, y, shield, radar, cx } = base()
  const soldier = spawnUnit(sim, 'infantry', 2, Math.floor(cx - 5), y + 13)
  sim.send(2, { type: 'attack', units: [soldier], target: radar })
  seconds(sim, 4)
  expect(health(sim, radar)).toBeLessThan(1)
  expect(charge(sim, shield)).toBe(capacity)
})

test('урон гасит купол своего и союзника, летающему купол не помеха; чужое под куполом не укрыто', () => {
  const { sim, shield, cx, cy } = base()
  const outside = { x: cx + radius + 2, y: cy }
  const hit = (player: number, fromX: number, fromY: number, fromAir = false) => absorb(sim, domes(sim), player, cx + 2, cy, 50, fromX, fromY, fromAir)
  expect(hit(1, outside.x, outside.y)).toBe(0)
  expect(hit(1, cx - 1, cy)).toBe(50)
  expect(hit(1, cx - 1, cy, true)).toBe(0)
  expect(hit(3, outside.x, outside.y)).toBe(50)
  offerAlliance(sim, 1, 3)
  offerAlliance(sim, 3, 1)
  expect(hit(3, outside.x, outside.y)).toBe(0)
  // Цель вне купола щит не укрывает.
  expect(absorb(sim, domes(sim), 1, cx + radius + 1, cy, 50, outside.x + 5, outside.y, false)).toBe(50)
  expect(charge(sim, shield)).toBe(capacity - 150)
})

test('пробитый купол гаснет на время и потом растёт с нуля; под огнём не растёт', () => {
  const { sim, shield, cx, cy } = base(30)
  const outside = cx + radius + 2
  expect(absorb(sim, domes(sim), 1, cx, cy, 50, outside, cy, false)).toBe(20)
  expect(charge(sim, shield)).toBe(0)
  expect(domes(sim)).toEqual([])
  seconds(sim, SHIELD_DOWN - 0.5)
  expect(charge(sim, shield)).toBe(0)
  seconds(sim, 1.5)
  expect(charge(sim, shield)).toBeGreaterThan(0)

  // Погашенное попадание останавливает рост на паузу ремонта.
  sim.world.get(shield, Shield)!.charge = 1000
  absorb(sim, domes(sim), 1, cx, cy, 10, outside, cy, false)
  seconds(sim, sim.rules.repairPause - 0.5)
  expect(charge(sim, shield)).toBe(990)
  seconds(sim, 1.5)
  expect(charge(sim, shield)).toBeGreaterThan(990)
  expect(charge(sim, shield)).toBeLessThanOrEqual(990 + regen * 1.5 + 1e-6)
})

test('без энергии и выключенный купол гаснет', () => {
  const { sim, shield, generator } = base()
  sim.send(1, { type: 'work', building: shield, on: false })
  seconds(sim, TICK * 2)
  expect(charge(sim, shield)).toBe(0)
  expect(domes(sim)).toEqual([])
  sim.send(1, { type: 'work', building: shield, on: true })
  seconds(sim, 1)
  expect(charge(sim, shield)).toBeCloseTo(regen, 0)

  sim.world.destroy(generator)
  seconds(sim, 0.5)
  expect(charge(sim, shield)).toBe(0)
})

test('щит переживает сохранение', () => {
  const { sim, shield } = base(1234)
  const loaded = createSim(throughJson(sim.save()))
  expect(loaded.world.get(shield, Shield)!.charge).toBe(1234)
})

test('купол останавливает чужие снаряды на своём краю: внутрь не долетает ни снаряд, ни осколки; свои летят насквозь', () => {
  const { sim, y, shield, radar, cx, cy } = base()
  // Ракетчики снаружи бьют по радару под куполом.
  const shooters = [0, 2].map((dy) => spawnUnit(sim, 'rocketeer', 2, Math.floor(cx - radius - 3), y + 9 + dy))
  sim.send(2, { type: 'attack', units: shooters, target: radar })
  let deepest = Infinity
  let stopped = 0
  for (let i = 0; i < Math.round(8 / TICK); i++) {
    sim.advance(TICK)
    for (const [, shot, position] of sim.world.query(Shot, Position)) {
      if (shot.player !== 2 || !WEAPONS[shot.weapon].speed) continue
      deepest = Math.min(deepest, Math.hypot(position.x - cx, position.y - cy))
    }
    if (charge(sim, shield) < capacity) stopped++
  }
  expect(stopped).toBeGreaterThan(0)
  // Глубже края снаряд не заходит.
  expect(deepest).toBeGreaterThan(radius - 0.01)
  expect(health(sim, radar)).toBe(1)

  // Свой снаряд изнутри купола наружу купол не держит: стрелок под куполом бьёт врага снаружи.
  const guard = spawnUnit(sim, 'rocketeer', 1, Math.floor(cx), cy)
  const foe = spawnUnit(sim, 'infantry', 3, Math.floor(cx + radius + 2), cy)
  sim.send(1, { type: 'attack', units: [guard], target: foe })
  seconds(sim, 6)
  expect(sim.world.get(foe, Health)?.value ?? 0).toBeLessThan(1)
})
