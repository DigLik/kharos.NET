import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { isCliffFoot } from '@kharos/shared/terrain'
import {
  Building, CORE, Converting, Deploys, Health, Owner, Path, Pave, Player, Position, Producer, Site, UNITS, Unit,
  canDeploy, canDeployAt, canPlace, createSim, creditsOf, dropAt, producibleBy, spawnStartingUnits, type Sim,
} from '@kharos/sim-ts'
import { STARTING_CREDITS } from '@kharos/sim-ts/economy'
import { placeBuilding } from '@kharos/sim-ts/buildings'
import { DEPLOY_SECONDS } from '@kharos/sim-ts/conversion'
import { dropItems } from '@kharos/sim-ts/drops'
import { spawnUnit } from '@kharos/sim-ts/units'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

// Строка запроса одна на весь обход, поэтому нужное из неё вынимается сразу, в цикле.
function unitsOf(sim: Sim, type: string) {
  const found: Entity[] = []
  for (const [entity, unit] of sim.world.query(Unit)) if (unit.type === type) found.push(entity)
  return found
}

/** Симуляция, где MCV игрока 1 стоит на скале и может развернуться, а рядом — пехотинец. */
/** Нет подножий обрыва в поле вокруг площадки главного здания. */
const clearOfCliffs = (sim: Sim, x: number, y: number) => {
  for (let tileY = y - 6; tileY < y + 9; tileY++) for (let tileX = x - 6; tileX < x + 9; tileX++) if (isCliffFoot(sim.land, tileX, tileY)) return false
  return true
}

function start() {
  const sim = createSim(options)
  for (let y = 0; y < 400; y++) {
    for (let x = 0; x < 400; x++) {
      // Подальше от обрывов: юниты вокруг площадки должны свободно расходиться.
      if (!canPlace(sim, CORE, x, y) || !clearOfCliffs(sim, x, y)) continue
      spawnStartingUnits(sim, 1, x + 1, y + 1)
      return { sim, mcv: unitsOf(sim, 'mcv')[0], site: { x, y } }
    }
  }
  throw new Error('В мире не нашлось скалы')
}

const units = (sim: Sim, type: string) => unitsOf(sim, type).length
function core(sim: Sim) {
  for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
    if (building.type === CORE && owner.player === 1) return entity
  }
  return undefined
}

test('игрок появляется с кредитами', () => {
  const { sim } = start()
  expect(creditsOf(sim, 1)).toBe(STARTING_CREDITS)
  expect(creditsOf(sim, 2)).toBe(0)
})

test('MCV разворачивается в главное здание и сворачивается обратно', () => {
  const { sim, mcv, site } = start()
  expect(canDeploy(sim, 1, mcv)).toBe(true)
  sim.send(1, { type: 'deploy', unit: mcv })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Converting)).toBe(true)
  // Пока разворачивается, с места не трогается и второй раз развернуть нельзя.
  sim.send(1, { type: 'move', units: [mcv], x: site.x + 10, y: site.y })
  seconds(sim, 1)
  expect(Math.floor(sim.world.get(mcv, Position)!.x)).toBe(site.x + 1)
  expect(canDeploy(sim, 1, mcv)).toBe(false)

  seconds(sim, 2.1)
  expect(sim.world.alive(mcv)).toBe(false)
  const building = core(sim)!
  expect(sim.world.get(building, Position)).toEqual({ x: site.x, y: site.y })
  expect(units(sim, 'mcv')).toBe(0)

  // Юниты, стоявшие на месте здания, вышли из него.
  seconds(sim, 3)
  for (const [, position] of sim.world.query(Position, Unit)) {
    expect(sim.occupancy.at(Math.floor(position.x), Math.floor(position.y))).toBeUndefined()
  }

  sim.send(2, { type: 'pack', building })
  sim.send(1, { type: 'pack', building })
  seconds(sim, 9)
  expect(sim.world.alive(building)).toBe(true)
  seconds(sim, 1.2)
  expect(sim.world.alive(building)).toBe(false)
  expect(units(sim, 'mcv')).toBe(1)
  expect(sim.occupancy.at(site.x, site.y)).toBeUndefined()
})

test('развернуться можно только на свободной скале и только своим MCV', () => {
  const { sim, mcv } = start()
  expect(canDeploy(sim, 2, mcv)).toBe(false)
  const [infantry] = unitsOf(sim, 'infantry')
  expect(canDeploy(sim, 1, infantry)).toBe(false)
  expect(canDeploy(sim, 1, 999999 as Entity)).toBe(false)

  // MCV на песке.
  for (let x = 0; x < 400; x++) {
    const sand = spawnUnit(sim, 'mcv', 1, x, 0)
    if (!canDeploy(sim, 1, sand)) return
    sim.world.destroy(sand)
  }
  throw new Error('Не нашлось места, где развернуться нельзя')
})

/** Стартовый набор с развёрнутой базой: производит главное здание, свёрнутая база не производит. */
function deployed() {
  const { sim, mcv, site } = start()
  sim.send(1, { type: 'deploy', unit: mcv })
  seconds(sim, 3.1)
  return { sim, base: core(sim)!, site }
}

/** Кредиты меняются ещё и доходом главного здания, 0,2 в секунду: сверяем с допуском в кредит. */
const near = (actual: number, expected: number) => expect(Math.abs(actual - expected)).toBeLessThanOrEqual(1)

test('производство списывает кредиты, строит по очереди и выпускает юнитов рядом', () => {
  const { sim, base } = deployed()
  const credits = creditsOf(sim, 1)
  const before = { builders: units(sim, 'builder'), truck: units(sim, 'truck') }
  sim.send(1, { type: 'produce', producer: base, unit: 'truck' })
  sim.send(1, { type: 'produce', producer: base, unit: 'builder' })
  sim.advance(TICK)
  near(creditsOf(sim, 1), credits - UNITS.truck.cost - UNITS.builder.cost)
  expect(sim.world.get(base, Producer)!.queue).toEqual(['truck', 'builder'])

  seconds(sim, UNITS.truck.buildTime + 0.1)
  expect(units(sim, 'truck')).toBe(before.truck + 1)
  expect(units(sim, 'builder')).toBe(before.builders)
  seconds(sim, UNITS.builder.buildTime + 0.1)
  expect(units(sim, 'builder')).toBe(before.builders + 1)
  expect(sim.world.get(base, Producer)!.queue).toEqual([])

  // Каждый вышел на свой тайл, а не встал на голову соседу.
  const tiles: string[] = []
  for (const [, position] of sim.world.query(Position, Unit)) tiles.push(`${Math.floor(position.x)},${Math.floor(position.y)}`)
  expect(new Set(tiles).size).toBe(tiles.length)
})

test('в свёрнутой базе заказать нельзя: MCV не производит', () => {
  const { sim, mcv } = start()
  expect(producibleBy(sim, mcv)).toEqual([])
  sim.send(1, { type: 'produce', producer: mcv, unit: 'builder' })
  sim.advance(TICK)
  expect(sim.world.get(mcv, Producer)!.queue).toEqual([])
  expect(creditsOf(sim, 1)).toBe(STARTING_CREDITS)
})

test('заказ отклоняется без кредитов, у чужого производителя, сверх очереди и для непроизводимого юнита', () => {
  const { sim, base } = deployed()
  const credits = creditsOf(sim, 1)
  sim.send(2, { type: 'produce', producer: base, unit: 'truck' })
  sim.send(1, { type: 'produce', producer: base, unit: 'mcv' })
  sim.send(1, { type: 'produce', producer: base, unit: 'dragon' as never })
  sim.advance(TICK)
  expect(sim.world.get(base, Producer)!.queue).toEqual([])
  near(creditsOf(sim, 1), credits)

  // Очередь — не больше пяти заказов.
  for (let i = 0; i < 8; i++) sim.send(1, { type: 'produce', producer: base, unit: 'builder' })
  sim.advance(TICK)
  expect(sim.world.get(base, Producer)!.queue.length).toBe(5)
  near(creditsOf(sim, 1), credits - 5 * UNITS.builder.cost)

  // Готовый юнит освободил в очереди только одно место.
  seconds(sim, UNITS.builder.buildTime + 0.1)
  sim.send(1, { type: 'produce', producer: base, unit: 'builder' })
  sim.send(1, { type: 'produce', producer: base, unit: 'builder' })
  sim.advance(TICK)
  expect(sim.world.get(base, Producer)!.queue.length).toBe(5)

  // Без кредитов заказ не принимается.
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { credits: 0, earned: 0 })
  sim.send(1, { type: 'cancelProduction', producer: base })
  sim.advance(TICK)
  sim.send(1, { type: 'produce', producer: base, unit: 'truck' })
  sim.advance(TICK)
  expect(sim.world.get(base, Producer)!.queue.length).toBe(4)
})

test('отмена возвращает кредиты за последний заказ', () => {
  const { sim, base } = deployed()
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'produce', producer: base, unit: 'builder' })
  sim.send(1, { type: 'produce', producer: base, unit: 'truck' })
  sim.send(1, { type: 'cancelProduction', producer: base })
  sim.advance(TICK)
  expect(sim.world.get(base, Producer)!.queue).toEqual(['builder'])
  near(creditsOf(sim, 1), credits - UNITS.builder.cost)

  seconds(sim, 1)
  sim.send(1, { type: 'cancelProduction', producer: base })
  sim.send(1, { type: 'cancelProduction', producer: base })
  sim.advance(TICK)
  near(creditsOf(sim, 1), credits)
  expect(sim.world.get(base, Producer)!.progress).toBe(0)
})

test('очередь главного здания едет в свёрнутой базе и стоит, а после разворота продолжается', () => {
  const { sim, base } = deployed()
  sim.send(1, { type: 'produce', producer: base, unit: 'builder' })
  sim.send(1, { type: 'produce', producer: base, unit: 'truck' })
  sim.send(1, { type: 'pack', building: base })
  sim.advance(TICK)
  const builders = units(sim, 'builder')
  seconds(sim, 10.1)
  const [mcv] = unitsOf(sim, 'mcv')
  expect(sim.world.get(mcv, Producer)!.queue).toEqual(['builder', 'truck'])
  // Свёрнутая — не производит, сколько ни жди; отменить заказ можно.
  seconds(sim, UNITS.builder.buildTime + 1)
  expect(units(sim, 'builder')).toBe(builders)
  sim.send(1, { type: 'cancelProduction', producer: mcv })
  sim.advance(TICK)
  expect(sim.world.get(mcv, Producer)!.queue).toEqual(['builder'])

  sim.send(1, { type: 'deploy', unit: mcv })
  seconds(sim, 3.1)
  expect(sim.world.get(core(sim)!, Producer)!.queue).toEqual(['builder'])
  seconds(sim, UNITS.builder.buildTime + 0.1)
  expect(units(sim, 'builder')).toBe(builders + 1)
})

test('сохранение посреди превращения и производства продолжается так же', () => {
  const { sim, base } = deployed()
  sim.send(1, { type: 'produce', producer: base, unit: 'truck' })
  sim.send(1, { type: 'pack', building: base })
  seconds(sim, 1)
  const copy = createSim(throughJson(sim.save()))
  seconds(sim, 10)
  seconds(copy, 10)
  expect(copy.save()).toEqual(sim.save())
})

test('MCV не разворачивается, пока под будущим зданием чужой юнит, а своих выгоняет', () => {
  const { sim, mcv, site } = start()
  const stranger = spawnUnit(sim, 'infantry', 0, site.x, site.y)
  const own = spawnUnit(sim, 'builder', 1, site.x + 2, site.y)
  sim.send(1, { type: 'deploy', unit: mcv })
  seconds(sim, 8)
  // Свой строитель ушёл, чужой пехотинец стоит, MCV ждёт.
  const position = sim.world.get(own, Position)!
  expect(position.x >= site.x && position.x < site.x + 3 && position.y >= site.y && position.y < site.y + 3).toBe(false)
  expect(Math.floor(sim.world.get(stranger, Position)!.x)).toBe(site.x)
  expect(core(sim)).toBeUndefined()
  expect(sim.world.has(mcv, Converting)).toBe(true)

  sim.send(0, { type: 'move', units: [stranger], x: site.x - 3, y: site.y })
  seconds(sim, 5)
  expect(core(sim)).toBeDefined()
})

/** Место в нескольких тайлах от MCV, где он сможет развернуться: левый верхний тайл основания. */
function siteAway(sim: Sim, mcv: Entity, site: { x: number; y: number }) {
  for (let ring = 6; ring < 60; ring++) {
    for (let dy = -ring; dy <= ring; dy++) {
      for (let dx = -ring; dx <= ring; dx++) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) !== ring) continue
        const x = site.x + dx
        const y = site.y + dy
        if (canDeployAt(sim, 1, mcv, x, y) && clearOfCliffs(sim, x, y)) return { x, y }
      }
    }
  }
  throw new Error('Рядом нет второго места под главное здание')
}

test('MCV с местом едет туда и разворачивается там', () => {
  const { sim, mcv, site } = start()
  const target = siteAway(sim, mcv, site)
  sim.send(2, { type: 'deploy', unit: mcv, x: target.x, y: target.y })
  sim.send(1, { type: 'deploy', unit: mcv, x: target.x, y: target.y })
  sim.advance(TICK)
  expect(sim.world.get(mcv, Deploys)).toMatchObject(target)
  expect(sim.world.has(mcv, Converting)).toBe(false)
  for (let i = 0; i < 600 && sim.world.alive(mcv); i++) sim.advance(TICK)
  expect(sim.world.alive(mcv)).toBe(false)
  expect(sim.world.get(core(sim)!, Position)).toEqual(target)
})

test('на своём месте MCV разворачивается сразу, а негодное место не принимает', () => {
  const { sim, mcv, site } = start()
  // Вне карты — не принимается вовсе.
  sim.send(1, { type: 'deploy', unit: mcv, x: -100000, y: 0 })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Deploys)).toBe(false)
  // Место под ним, но сдвинутое на тайл, — тоже на месте: ехать не нужно.
  sim.send(1, { type: 'deploy', unit: mcv, x: site.x, y: site.y })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Converting)).toBe(true)
  seconds(sim, DEPLOY_SECONDS + 0.2)
  expect(sim.world.get(core(sim)!, Position)).toEqual(site)
})

test('поездку на разворачивание снимают отмена и приказ идти', () => {
  const { sim, mcv, site } = start()
  const target = siteAway(sim, mcv, site)
  sim.send(1, { type: 'deploy', unit: mcv, x: target.x, y: target.y })
  sim.advance(TICK)
  sim.send(1, { type: 'cancelDeploy', unit: mcv })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Deploys)).toBe(false)
  expect(sim.world.has(mcv, Path)).toBe(false)

  sim.send(1, { type: 'deploy', unit: mcv, x: target.x, y: target.y })
  sim.advance(TICK)
  sim.send(1, { type: 'move', units: [mcv], x: site.x - 4, y: site.y })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Deploys)).toBe(false)
  seconds(sim, 30)
  expect(core(sim)).toBeUndefined()
})

test('место заняли, пока MCV ехал: он остаётся машиной', () => {
  const { sim, mcv, site } = start()
  const target = siteAway(sim, mcv, site)
  sim.send(1, { type: 'deploy', unit: mcv, x: target.x, y: target.y })
  sim.advance(TICK)
  placeBuilding(sim.world, 'generator', target.x, target.y, 2)
  seconds(sim, 30)
  expect(sim.world.alive(mcv)).toBe(true)
  expect(sim.world.has(mcv, Deploys)).toBe(false)
  expect(core(sim)).toBeUndefined()
})

test('разворачивание можно отменить: MCV снова едет', () => {
  const { sim, mcv, site } = start()
  spawnUnit(sim, 'infantry', 0, site.x, site.y)
  sim.send(1, { type: 'deploy', unit: mcv })
  seconds(sim, 5)
  sim.send(2, { type: 'cancelDeploy', unit: mcv })
  sim.send(1, { type: 'cancelDeploy', unit: mcv })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Converting)).toBe(false)
  expect(core(sim)).toBeUndefined()
  const before = { ...sim.world.get(mcv, Position)! }
  sim.send(1, { type: 'move', units: [mcv], x: site.x + 6, y: site.y + 1 })
  seconds(sim, 2)
  expect(sim.world.get(mcv, Position)!.x).not.toBe(before.x)
})

test('MCV, которому не доехать до места, пробует снова всё реже и после пяти промахов бросает поездку', () => {
  const { sim, mcv, site } = start()
  const target = siteAway(sim, mcv, site)
  // Второй MCV заперт стенами на своём тайле: места ему не достать.
  const trapped = spawnUnit(sim, 'mcv', 1, target.x + 1, target.y + 1)
  for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) if (dx || dy) placeBuilding(sim.world, 'wall', target.x + 1 + dx, target.y + 1 + dy, 1)
  sim.send(1, { type: 'deploy', unit: trapped, x: site.x, y: site.y })
  seconds(sim, 10)
  expect(sim.world.get(trapped, Deploys)!.misses).toBeGreaterThan(1)
  seconds(sim, 60)
  expect(sim.world.has(trapped, Deploys)).toBe(false)
})

test('место стало негодным, пока MCV ехал: чужая зона, площадка стройки, дорога — он остаётся машиной', () => {
  const spoilers: ((sim: Sim, target: { x: number; y: number }) => void)[] = [
    (sim, target) => placeBuilding(sim.world, 'command', target.x + 5, target.y, 2),
    (sim, target) => sim.world.spawn(Position({ x: target.x + 1, y: target.y }), Site({ type: 'wall' }), Owner({ player: 1 })),
    (sim, target) => sim.world.spawn(Position({ x: target.x + 2, y: target.y + 2 }), Pave({ kind: 'road' })),
  ]
  for (const spoil of spoilers) {
    const { sim, mcv, site } = start()
    const target = siteAway(sim, mcv, site)
    sim.send(1, { type: 'deploy', unit: mcv, x: target.x, y: target.y })
    sim.advance(TICK)
    spoil(sim, target)
    seconds(sim, 30)
    expect(sim.world.alive(mcv)).toBe(true)
    expect(sim.world.has(mcv, Deploys)).toBe(false)
    expect(core(sim)).toBeUndefined()
  }
})

test('место заняли, пока MCV разворачивался: он остаётся машиной', () => {
  const { sim, mcv, site } = start()
  sim.send(1, { type: 'deploy', unit: mcv })
  sim.advance(TICK)
  expect(sim.world.has(mcv, Converting)).toBe(true)
  placeBuilding(sim.world, 'wall', site.x + 2, site.y, 1)
  seconds(sim, DEPLOY_SECONDS + 0.2)
  expect(sim.world.alive(mcv)).toBe(true)
  expect(sim.world.has(mcv, Converting)).toBe(false)
  expect(core(sim)).toBeUndefined()
})

test('развёрнутая база уничтожает дропы под собой, но не рядом, и получает фазу по счёту зданий', () => {
  const { sim, mcv, site } = start()
  placeBuilding(sim.world, 'wall', site.x - 8, site.y - 8, 1)
  dropItems(sim, site.x + 2, site.y + 2, { metal: 10 })
  dropItems(sim, site.x + 3, site.y + 2, { metal: 10 })
  sim.send(1, { type: 'deploy', unit: mcv })
  seconds(sim, DEPLOY_SECONDS + 0.2)
  expect(sim.world.get(core(sim)!, Building)!.phase).toBe(5)
  expect(dropAt(sim, site.x + 2, site.y + 2)).toBeUndefined()
  expect(dropAt(sim, site.x + 3, site.y + 2)).toBeDefined()
})

test('свёрнутая база уносит прочность здания в MCV', () => {
  const { sim, base } = deployed()
  sim.world.get(base, Health)!.value = 0.5
  sim.send(1, { type: 'pack', building: base })
  seconds(sim, 10.2)
  const [mcv] = unitsOf(sim, 'mcv')
  expect(sim.world.get(mcv, Health)!.value).toBe(0.5)
})
