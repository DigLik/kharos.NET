import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Health, Inventory, Position, TOWER_RANGE, TRAINING_PLAYER, Unit, WEAPONS, createSim } from '@kharos/sim-ts'
import { placeBuilding } from '@kharos/sim-ts/buildings'
import { spawnUnit } from '@kharos/sim-ts/units'

const newSim = () => createSim({ generator: DEFAULT_SETTINGS.generator, size: DEFAULT_SETTINGS.world.size, rules: DEFAULT_SETTINGS.rules })

test('ракетная турель достаёт штурмовика дальше его собственного выстрела', () => {
  const sim = newSim()
  const tower = placeBuilding(sim.world, 'rocketTurret', 0, 0, 1)
  sim.world.get(tower, Inventory)!.items.ammo = 100
  // Дальше дальности ракетницы штурмовика, но в пределах надбавки турели.
  const away = WEAPONS.launcher.range + TOWER_RANGE / 2
  spawnUnit(sim, 'gunship', TRAINING_PLAYER, Math.floor(0.5 + away), 0)
  for (let i = 0; i < 20 * 10; i++) sim.advance(0.05)
  for (const [, , unit, health] of sim.world.query(Position, Unit, Health)) {
    if (unit.type === 'gunship') expect(health.value).toBeLessThan(1)
  }
})

test('главное здание бьёт по всей своей зоне, и по воздуху тоже, а дальше — нет', () => {
  // Центр главного здания — (1.5, 1.5); его зона — 12 тайлов. Каждую цель — в своём мире: турель бьёт по одной.
  const hurt = (type: 'buggy' | 'drone', x: number, y: number) => {
    const sim = newSim()
    placeBuilding(sim.world, 'command', 0, 0, 1)
    const unit = spawnUnit(sim, type, TRAINING_PLAYER, x, y)
    for (let i = 0; i < 20 * 10; i++) sim.advance(0.05)
    return !sim.world.alive(unit) || sim.world.get(unit, Health)!.value < 1
  }
  expect(hurt('buggy', 12, 1)).toBe(true)
  expect(hurt('drone', 1, 12)).toBe(true)
  expect(hurt('buggy', -14, 1)).toBe(false)
})

test('лазерная турель бьёт танк без всяких патронов, на дальности лазера с надбавкой здания, — но только на энергии зоны', () => {
  const hurt = (powered: boolean) => {
    const sim = newSim()
    // Главное здание даёт зону, но стоит так, чтобы его собственная пушка до танка не доставала.
    placeBuilding(sim.world, 'command', 0, 10, 1)
    if (powered) placeBuilding(sim.world, 'generator', 4, 10, 1)
    const tower = placeBuilding(sim.world, 'laserTurret', 0, 0, 1)
    expect(sim.world.has(tower, Inventory)).toBe(false)
    const tank = spawnUnit(sim, 'tank', TRAINING_PLAYER, 9, 0)
    for (let i = 0; i < 20 * 5; i++) sim.advance(0.05)
    return !sim.world.alive(tank) || sim.world.get(tank, Health)!.value < 1
  }
  expect(hurt(true)).toBe(true)
  // Без электростанции лазеру стрелять нечем.
  expect(hurt(false)).toBe(false)
})

test('разрядная турель бьёт и землю, и воздух, и тоже только на энергии зоны', () => {
  const hurt = (type: 'buggy' | 'drone', powered: boolean) => {
    const sim = newSim()
    placeBuilding(sim.world, 'command', 0, 10, 1)
    if (powered) placeBuilding(sim.world, 'generator', 4, 10, 1)
    const tower = placeBuilding(sim.world, 'arcTurret', 0, 0, 1)
    expect(sim.world.has(tower, Inventory)).toBe(false)
    // Цель — по другую сторону от главного здания: его пушка туда не достаёт.
    const unit = spawnUnit(sim, type, TRAINING_PLAYER, 0, -Math.floor(WEAPONS.arc.range + TOWER_RANGE / 2))
    sim.send(TRAINING_PLAYER, { type: 'stance', units: [unit], stance: 'passive' })
    for (let i = 0; i < 20 * 5; i++) sim.advance(0.05)
    return !sim.world.alive(unit) || sim.world.get(unit, Health)!.value < 1
  }
  expect(hurt('buggy', true)).toBe(true)
  expect(hurt('drone', true)).toBe(true)
  expect(hurt('buggy', false)).toBe(false)
})
