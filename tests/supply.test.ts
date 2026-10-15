import { expect, test } from 'bun:test'
import type { Entity } from '../src/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Health, Inventory, Position, amountOf, canPlace, createSim, type Rules, type Sim } from '../src/sim'
import { placeBuilding } from '../src/sim/buildings'
import { Magazine, Tactics } from '../src/sim/components'
import { connectAll } from '../src/sim/piping'
import { INFANTRY_MAGAZINE_SECONDS, MAGAZINE_SECONDS, isEmpty, magazineOf, shotCost } from '../src/sim/supply'
import { freeTilesNear, isWalkable, spawnUnit, type UnitType } from '../src/sim/units'
import { WEAPONS } from '../src/sim/weapons'

const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}
function until(sim: Sim, done: () => boolean, limit = 60) {
  for (let i = 0; i < limit / TICK; i++) {
    if (done()) return
    sim.advance(TICK)
  }
  throw new Error('Не дождались')
}

/** Главное здание и бункер боеприпасов в одной сети на скале, с запасом ammo; рядом — место для юнитов. */
function base(ammo = 200, rules: Partial<Rules> = {}) {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { protection: 0, ...rules } })
  for (let y = -100; y < 100; y++) {
    for (let x = -100; x < 100; x++) {
      if (!canPlace(sim, 'command', x, y) || !canPlace(sim, 'ammoBunker', x + 5, y)) continue
      // Справа от базы — проходимая полоса: туда отводят юнитов из сети.
      let open = true
      for (let dx = 8; dx <= 40 && open; dx++) open = isWalkable(sim, x + dx, y + 4)
      if (!open) continue
      placeBuilding(sim.world, 'command', x, y, 1)
      const bunker = placeBuilding(sim.world, 'ammoBunker', x + 5, y, 1)
      connectAll(sim, 1)
      sim.world.get(bunker, Inventory)!.items.ammo = ammo
      return { sim, bunker, x, y }
    }
  }
  throw new Error('Не нашлось места')
}

const ammo = (sim: Sim, entity: Entity) => sim.world.get(entity, Magazine)!.ammo
const stock = (sim: Sim, bunker: Entity) => amountOf(sim.world.get(bunker, Inventory)!, 'ammo')
const spawnAt = (sim: Sim, type: UnitType, player: number, x: number, y: number) => {
  const [tx, ty] = freeTilesNear(sim, x, y, 1)
  return spawnUnit(sim, type, player, tx, ty)
}

test('магазин: технике хватает на MAGAZINE_SECONDS огня, пехоте — дольше; лазер стреляет бесплатно', () => {
  const tank = shotCost('tank', WEAPONS.cannon)
  expect((magazineOf('tank') / tank) * WEAPONS.cannon.reload).toBeCloseTo(MAGAZINE_SECONDS)
  const rifle = shotCost('infantry', WEAPONS.rifle)
  expect((magazineOf('infantry') / rifle) * WEAPONS.rifle.reload).toBeCloseTo(INFANTRY_MAGAZINE_SECONDS)
  // Три ракетные турели носителя делят один магазин.
  expect((magazineOf('carrier') / shotCost('carrier', WEAPONS.launcher)) * WEAPONS.launcher.reload).toBeCloseTo(MAGAZINE_SECONDS * 3)
  expect(magazineOf('lancer')).toBe(0)
  expect(shotCost('lancer', WEAPONS.laser)).toBe(0)
})

test('выстрел тратит магазин, пустой юнит молчит', () => {
  const { sim, x, y } = base()
  const tank = spawnAt(sim, 'tank', 1, x + 30, y + 4)
  sim.send(1, { type: 'stance', units: [tank], stance: 'hold' })
  const { x: tx, y: ty } = sim.world.get(tank, Position)!
  const target = placeBuilding(sim.world, 'metalYard', Math.floor(tx) + 3, Math.floor(ty) - 4, 2)
  // На два выстрела.
  sim.world.get(tank, Magazine)!.ammo = shotCost('tank', WEAPONS.cannon) * 2
  until(sim, () => isEmpty(sim, tank))
  seconds(sim, 2)
  const health = sim.world.get(target, Health)!.value
  expect(health).toBeLessThan(1)
  seconds(sim, 10)
  expect(sim.world.get(target, Health)!.value).toBe(health)
})

test('в своей сети магазин пополняется из хранилища', () => {
  const { sim, bunker, x, y } = base()
  const tank = spawnAt(sim, 'tank', 1, x + 4, y + 4)
  sim.world.get(tank, Magazine)!.ammo = 0
  seconds(sim, 10)
  expect(ammo(sim, tank)).toBeCloseTo(magazineOf('tank'))
  expect(stock(sim, bunker)).toBeCloseTo(200 - magazineOf('tank'))
})

test('пустой вне сети сам идёт за боеприпасами и возвращается на место', () => {
  const { sim, bunker, x, y } = base()
  const tank = spawnAt(sim, 'tank', 1, x + 30, y + 4)
  const home = { ...sim.world.get(tank, Position)! }
  sim.world.get(tank, Magazine)!.ammo = 0
  // Пустой ищет снабжение не сразу, а в свой черёд.
  seconds(sim, 3)
  expect(sim.world.get(tank, Tactics)!.resupply).toBe(true)
  until(sim, () => ammo(sim, tank) >= magazineOf('tank') - 1e-6)
  expect(stock(sim, bunker)).toBeCloseTo(200 - magazineOf('tank'))
  until(sim, () => Math.hypot(sim.world.get(tank, Position)!.x - home.x, sim.world.get(tank, Position)!.y - home.y) < 2)
  expect(sim.world.get(tank, Tactics)!.resupply).toBe(false)
})

test('держащий позицию пустой стоит, а без боеприпасов в сетях никто не уходит', () => {
  const { sim, x, y } = base()
  const held = spawnAt(sim, 'tank', 1, x + 30, y + 4)
  sim.send(1, { type: 'stance', units: [held], stance: 'hold' })
  sim.world.get(held, Magazine)!.ammo = 0
  const at = { ...sim.world.get(held, Position)! }
  seconds(sim, 5)
  expect(sim.world.get(held, Position)).toEqual(at)

  const dry = base(0)
  const tank = spawnAt(dry.sim, 'tank', 1, dry.x + 30, dry.y + 4)
  dry.sim.world.get(tank, Magazine)!.ammo = 0
  const from = { ...dry.sim.world.get(tank, Position)! }
  seconds(dry.sim, 5)
  expect(dry.sim.world.get(tank, Position)).toEqual(from)
  expect(dry.sim.world.get(tank, Tactics)?.resupply ?? false).toBe(false)
})

test('приказ идти снимает поход за боеприпасами', () => {
  const { sim, x, y } = base()
  const tank = spawnAt(sim, 'tank', 1, x + 30, y + 4)
  sim.world.get(tank, Magazine)!.ammo = 0
  // Пустой ищет снабжение не сразу, а в свой черёд.
  seconds(sim, 3)
  expect(sim.world.get(tank, Tactics)!.resupply).toBe(true)
  const { x: tx, y: ty } = sim.world.get(tank, Position)!
  sim.send(1, { type: 'move', units: [tank], x: Math.floor(tx) + 5, y: Math.floor(ty) })
  sim.advance(TICK)
  expect(sim.world.get(tank, Tactics)!.resupply).toBe(false)
})

test('с выключенным правилом юниты стреляют бесплатно', () => {
  const { sim, x, y } = base(200, { unitAmmo: false })
  const tank = spawnAt(sim, 'tank', 1, x + 30, y + 4)
  const { x: tx, y: ty } = sim.world.get(tank, Position)!
  placeBuilding(sim.world, 'metalYard', Math.floor(tx) + 3, Math.floor(ty) - 4, 2)
  seconds(sim, 10)
  expect(ammo(sim, tank)).toBe(magazineOf('tank'))
})
