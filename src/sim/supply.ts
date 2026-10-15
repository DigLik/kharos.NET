import type { Entity } from '../ecs'
import { buildingSpec } from './buildings'
import { Building, Inventory, Magazine, Owner, Path, Position, Tactics, Unit } from './components'
import { take } from './inventory'
import { flowsOf, givenBy, offerIndex } from './logistics'
import type { Sim } from './sim'
import { leaveHome, stanceOf } from './tactics'
import { TURRETS } from './turrets'
import { orderMove, unitSpec, type UnitType } from './units'
import { WEAPONS, type WeaponSpec, type WeaponType } from './weapons'
import { inCircles, zonesOf, type Zone } from './zones'

/**
 * Снабжение войск: у юнита магазин (Magazine), выстрел тратит из него свою долю, пустой юнит молчит. В своей сети —
 * внутри её кругов — магазин сам пополняется из её хранилищ, как турель. Пустой юнит вне сети по стойке: держащий
 * позицию стоит и ждёт, остальные сами идут к ближайшей сети с боеприпасами, а наполнив магазин, возвращаются на место,
 * откуда ушли (см. Tactics.away). Пехота ходит в той же сети: её круги и есть «у любого своего здания».
 */

/** На сколько секунд непрерывного огня всеми стволами хватает полного магазина. */
export const MAGAZINE_SECONDS = 50
/** То же у пехоты: её запас больше, чтобы ответом на снабжение не стала толпа пехоты. */
export const INFANTRY_MAGAZINE_SECONDS = 120
/** Сколько боеприпасов в секунду юнит набирает в своей сети. */
export const REFILL_RATE = 10
/** Раз во сколько тиков пополняются магазины. */
const SUPPLY_TICKS = 10
/** Раз во сколько проходов пустой юнит ищет снабжение: поиск пути дорог, а снабжения может и не быть. */
const SEEK_PASSES = 4
/** То же для идущего за снабжением, который не дошёл до сети: путь оборвался или хранилище опустело. */
const RESEEK_PASSES = 10
/** На сколько тайлов от края хранилища достаточно подойти: пополняет вся сеть, а не само хранилище. */
const SEEK_NEAR = 2
const EPSILON = 1e-9

/** Сколько стволов у юнита: его оружие и вооружённые турели. */
function gunsOf(type: UnitType) {
  const spec = unitSpec(type)
  let guns = spec.weapon ? 1 : 0
  for (const mount of spec.mounts ?? []) if ((TURRETS[mount.turret] as { weapon?: WeaponType }).weapon) guns++
  return guns
}

/** Сколько боеприпасов в полном магазине юнита; ноль — стреляет бесплатно. */
export const magazineOf = (type: UnitType) => unitSpec(type).ammo ?? 0

const costs = new Map<string, number>()
/** Сколько боеприпасов тратит выстрел юнита этим оружием: магазин делится на секунды огня всех стволов. */
export function shotCost(type: UnitType, weapon: WeaponSpec) {
  const key = `${type}:${weapon.reload}`
  let cost = costs.get(key)
  if (cost === undefined) {
    const capacity = magazineOf(type)
    const seconds = unitSpec(type).kind === 'infantry' ? INFANTRY_MAGAZINE_SECONDS : MAGAZINE_SECONDS
    cost = capacity ? (capacity * weapon.reload) / (seconds * Math.max(1, gunsOf(type))) : 0
    costs.set(key, cost)
  }
  return cost
}

/** Самый дешёвый выстрел юнита: меньше этого в магазине — юнит пуст. */
const cheapest = new Map<UnitType, number>()
function cheapestShot(type: UnitType) {
  let cost = cheapest.get(type)
  if (cost === undefined) {
    const spec = unitSpec(type)
    const weapons: WeaponType[] = []
    if (spec.weapon) weapons.push(spec.weapon)
    for (const mount of spec.mounts ?? []) {
      const weapon = (TURRETS[mount.turret] as { weapon?: WeaponType }).weapon
      if (weapon) weapons.push(weapon)
    }
    cost = weapons.length ? Math.min(...weapons.map((weapon) => shotCost(type, WEAPONS[weapon]))) : 0
    cheapest.set(type, cost)
  }
  return cost
}

/** Сколько боеприпасов в магазине юнита. Без компонента — полный: так у юнитов из старых сохранений. */
export function ammoOf(sim: Sim, entity: Entity) {
  const magazine = sim.world.get(entity, Magazine)
  if (magazine) return magazine.ammo
  const type = sim.world.get(entity, Unit)?.type
  return type ? magazineOf(type) : 0
}

/** Пуст ли магазин юнита: не хватает на самый дешёвый его выстрел. Без магазина и с выключенным правилом — не пуст. */
export function isEmpty(sim: Sim, entity: Entity) {
  const type = sim.world.get(entity, Unit)?.type
  if (!type || !sim.rules.unitAmmo || !magazineOf(type)) return false
  return ammoOf(sim, entity) < cheapestShot(type) - EPSILON
}

/** Хватит ли юниту боеприпасов на выстрел этим оружием. */
export function unitHasAmmo(sim: Sim, entity: Entity, type: UnitType, weapon: WeaponSpec) {
  if (!sim.rules.unitAmmo) return true
  const cost = shotCost(type, weapon)
  return !cost || ammoOf(sim, entity) >= cost - EPSILON
}

/** Списывает выстрел из магазина юнита. Проверять запас — unitHasAmmo. */
export function spendAmmo(sim: Sim, entity: Entity, type: UnitType, weapon: WeaponSpec) {
  if (!sim.rules.unitAmmo) return
  const cost = shotCost(type, weapon)
  if (!cost) return
  const magazine = sim.world.get(entity, Magazine)
  if (magazine) magazine.ammo = Math.max(0, magazine.ammo - cost)
  else sim.world.add(entity, Magazine({ ammo: Math.max(0, magazineOf(type) - cost) }))
}

/** Идёт ли юнит за боеприпасами: тогда в погоню он не уходит, см. combat.ts. */
export const resupplying = (sim: Sim, entity: Entity) => sim.world.get(entity, Tactics)?.resupply ?? false

/** Центр здания: Position — его левый верхний тайл. */
function centerOf(sim: Sim, entity: Entity) {
  const { x, y } = sim.world.get(entity, Position)!
  const type = sim.world.get(entity, Building)?.type
  const spec = type ? buildingSpec(type) : undefined
  return { x: x + (spec?.width ?? 1) / 2, y: y + (spec?.height ?? 1) / 2, size: Math.max(spec?.width ?? 1, spec?.height ?? 1) }
}

/**
 * Раз в SUPPLY_TICKS: магазины юнитов в своих сетях пополняются из хранилищ сети, пустые вне сетей идут к ближайшей
 * сети с боеприпасами, наполнившие магазин возвращаются. Груз берётся сверх резерва хранилища и сверх обещанного
 * грузовикам — как его берут заявки, см. givenBy.
 */
export function supply(sim: Sim) {
  const { world, time } = sim
  if (!sim.rules.unitAmmo || time.tick % SUPPLY_TICKS) return
  const seconds = SUPPLY_TICKS * time.step
  // Проход — раз в SUPPLY_TICKS, поэтому очередь юнитов считается проходами, а не тиками, как в onTurn.
  const pass = time.tick / SUPPLY_TICKS
  const onPass = (entity: Entity, passes: number) => (pass + entity) % passes === 0
  let flows: ReturnType<typeof flowsOf> | undefined
  const offers = offerIndex(sim)
  /** Сколько ещё можно взять с хранилища в этом проходе. */
  const left = new Map<Entity, number>()
  const leftIn = (source: Entity) => {
    let amount = left.get(source)
    if (amount === undefined) {
      flows ??= flowsOf(sim)
      amount = Math.max(0, givenBy(sim, source, 'ammo') - (flows.outgoing.get(source)?.ammo ?? 0))
      left.set(source, amount)
    }
    return amount
  }
  const stocked = (zone: Zone) => offers(zone, 'ammo').some((source) => leftIn(source) > EPSILON)

  const seekers: Entity[] = []
  for (const [entity, unit, position, owner] of world.query(Unit, Position, Owner)) {
    const capacity = magazineOf(unit.type)
    if (!capacity) continue
    const magazine = world.get(entity, Magazine)
    if (!magazine) {
      world.add(entity, Magazine({ ammo: capacity }))
      continue
    }
    const tactics = world.get(entity, Tactics)
    const zone = magazine.ammo < capacity - EPSILON ? zonesOf(sim, owner.player).find((one) => inCircles(one.circles, position.x, position.y)) : undefined
    if (zone) {
      let wanted = Math.min(capacity - magazine.ammo, REFILL_RATE * seconds)
      for (const source of offers(zone, 'ammo')) {
        if (wanted <= EPSILON) break
        const amount = Math.min(wanted, leftIn(source))
        if (amount <= EPSILON) continue
        const taken = take(world.get(source, Inventory)!, 'ammo', amount)
        left.set(source, leftIn(source) - taken)
        magazine.ammo += taken
        wanted -= taken
      }
    }
    if (tactics?.resupply) {
      // Наполнил — встаёт и возвращается, куда помнит (patrol), и снова воюет по стойке. Путь у идущего за снабжением —
      // всегда снабжения: приказ игрока снимает resupply, см. clearTactics.
      if (magazine.ammo >= capacity - EPSILON) {
        tactics.resupply = false
        world.remove(entity, Path)
      } else if (!zone && !world.has(entity, Path) && onPass(entity, RESEEK_PASSES)) seekers.push(entity)
      continue
    }
    if (zone || magazine.ammo >= cheapestShot(unit.type) - EPSILON) continue
    // Идущий по приказу идёт, куда велели: за боеприпасами он пойдёт, когда встанет.
    if (world.has(entity, Path) || stanceOf(sim, entity) === 'hold' || !onPass(entity, SEEK_PASSES)) continue
    seekers.push(entity)
  }

  // Пустые — к ближайшему хранилищу своей сети, где есть боеприпасы. Нет такого — ждут, где стоят.
  for (const entity of seekers) {
    const player = world.get(entity, Owner)!.player
    const { x, y } = world.get(entity, Position)!
    let best: { x: number; y: number; size: number } | undefined
    let bestDistance = Infinity
    for (const zone of zonesOf(sim, player)) {
      if (!stocked(zone)) continue
      for (const source of offers(zone, 'ammo')) {
        if (leftIn(source) <= EPSILON) continue
        const center = centerOf(sim, source)
        const distance = Math.hypot(center.x - x, center.y - y)
        if (distance < bestDistance) {
          bestDistance = distance
          best = center
        }
      }
    }
    if (!best) continue
    leaveHome(sim, entity)
    orderMove(sim, entity, Math.floor(best.x), Math.floor(best.y), undefined, 0, best.size / 2 + SEEK_NEAR)
    if (!world.has(entity, Path)) continue
    world.get(entity, Tactics)!.resupply = true
  }
}
