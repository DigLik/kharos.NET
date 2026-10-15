import type { Entity } from '../ecs'
import { buildingSpec, isReady } from './buildings'
import { Building, Inventory, Magazine, Owner, Path, Position, Tactics, Unit } from './components'
import { amountOf, put, reaches, roomFor, take, transfer } from './inventory'
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
 *
 * Вне сети магазины пополняет машина снабжения (UnitSpec.supplies): всем своим в её радиусе — со своего склада. Пустые
 * идут и к ней, если она ближе хранилища. Склад машины наполняют грузовики по её заявке (см. requestsOf), хранилища
 * сети, в кругах которой она стоит, и станция поставки (BuildingSpec.depot) — своим лучом, вчетверо быстрее хранилищ.
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
/** Насколько ближе края раздачи встаёт пустой, идущий к машине снабжения: она тоже может отъехать. */
const SUPPLIER_MARGIN = 1.5
/** Сколько боеприпасов в секунду машина снабжения грузит из хранилищ своей сети. Станция поставки грузит своим лучом. */
export const SUPPLIER_LOAD = 10
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

/** Машина снабжения в мире: где стоит, чья, на какой радиус раздаёт и её склад. */
interface Supplier {
  entity: Entity
  player: number
  x: number
  y: number
  radius: number
  rate: number
  stock: Parameters<typeof amountOf>[0]
}

/** Машины снабжения всех игроков. */
function suppliersOf(sim: Sim) {
  const found: Supplier[] = []
  for (const [entity, unit, stock, owner, position] of sim.world.query(Unit, Inventory, Owner, Position)) {
    const supplies = unitSpec(unit.type).supplies
    if (supplies) found.push({ entity, player: owner.player, x: position.x, y: position.y, radius: supplies.radius, rate: supplies.rate, stock })
  }
  return found
}

/**
 * Каждый тик: станции поставки грузят машины снабжения, до которых дотягивается их луч. Каждый тик, а не проходами,
 * потому что луч рисуется по связям нынешнего тика, см. resetBeams. Станции ищутся по тайлам вокруг машины.
 */
function loadAtDepots(sim: Sim, suppliers: readonly Supplier[]) {
  const { world, occupancy } = sim
  for (const supplier of suppliers) {
    if (roomFor(supplier.stock, 'ammo') <= EPSILON) continue
    const reach = Math.ceil(supplier.radius + DEPOT_REACH)
    const seen = new Set<Entity>()
    for (let y = Math.floor(supplier.y) - reach; y <= Math.floor(supplier.y) + reach; y++) {
      for (let x = Math.floor(supplier.x) - reach; x <= Math.floor(supplier.x) + reach; x++) {
        const depot = occupancy.at(x, y)
        if (depot === undefined || seen.has(depot)) continue
        seen.add(depot)
        const type = world.get(depot, Building)!.type
        if (!buildingSpec(type).depot || !isReady(sim, supplier.player, depot) || !reaches(sim, depot, supplier.entity)) continue
        transfer(sim, depot, supplier.entity, 'ammo')
      }
    }
  }
}
/** Дальше скольких тайлов от машины снабжения станцию не ищут: луч станции короче. */
const DEPOT_REACH = 4

/** Ближайшая к точке машина снабжения игрока с боеприпасами, в радиусе раздачи которой точка лежит; except — не она. */
function supplierAt(suppliers: readonly Supplier[], player: number, x: number, y: number, except?: Entity) {
  let best: Supplier | undefined
  let bestDistance = Infinity
  for (const supplier of suppliers) {
    if (supplier.player !== player || supplier.entity === except || amountOf(supplier.stock, 'ammo') <= EPSILON) continue
    const distance = Math.hypot(supplier.x - x, supplier.y - y)
    if (distance <= supplier.radius && distance < bestDistance) {
      best = supplier
      bestDistance = distance
    }
  }
  return best
}

/**
 * Раз в SUPPLY_TICKS: магазины юнитов в своих сетях пополняются из хранилищ сети, вне сетей — у машин снабжения, пустые
 * идут к ближайшему снабжению, наполнившие магазин возвращаются. Груз берётся сверх резерва хранилища и сверх
 * обещанного грузовикам — как его берут заявки, см. givenBy. Станции поставки грузят машины снабжения каждый тик.
 */
export function supply(sim: Sim) {
  const { world, time } = sim
  if (!sim.rules.unitAmmo) return
  const suppliers = suppliersOf(sim)
  if (suppliers.length) loadAtDepots(sim, suppliers)
  if (time.tick % SUPPLY_TICKS) return
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
  /** Берёт из хранилищ сети до amount боеприпасов; возвращает, сколько взято. */
  const takeFrom = (zone: Zone, amount: number) => {
    let taken = 0
    for (const source of offers(zone, 'ammo')) {
      if (amount - taken <= EPSILON) break
      const part = take(world.get(source, Inventory)!, 'ammo', Math.min(amount - taken, leftIn(source)))
      left.set(source, leftIn(source) - part)
      taken += part
    }
    return taken
  }

  // Машины снабжения в кругах своей сети грузятся из её хранилищ.
  for (const supplier of suppliers) {
    const room = roomFor(supplier.stock, 'ammo')
    if (room <= EPSILON) continue
    const zone = zonesOf(sim, supplier.player).find((one) => inCircles(one.circles, supplier.x, supplier.y))
    if (zone) put(supplier.stock, 'ammo', takeFrom(zone, Math.min(room, SUPPLIER_LOAD * seconds)))
  }

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
    const lacking = magazine.ammo < capacity - EPSILON
    const zone = lacking ? zonesOf(sim, owner.player).find((one) => inCircles(one.circles, position.x, position.y)) : undefined
    // Вне сети магазин пополняет машина снабжения, в радиусе которой юнит стоит.
    const supplier = lacking && !zone ? supplierAt(suppliers, owner.player, position.x, position.y) : undefined
    if (zone) magazine.ammo += takeFrom(zone, Math.min(capacity - magazine.ammo, REFILL_RATE * seconds))
    else if (supplier) magazine.ammo += take(supplier.stock, 'ammo', Math.min(capacity - magazine.ammo, supplier.rate * seconds))
    if (tactics?.resupply) {
      // Наполнил — встаёт и возвращается, куда помнит (patrol), и снова воюет по стойке. Путь у идущего за снабжением —
      // всегда снабжения: приказ игрока снимает resupply, см. clearTactics.
      if (magazine.ammo >= capacity - EPSILON) {
        tactics.resupply = false
        world.remove(entity, Path)
      } else if (!zone && !supplier && !world.has(entity, Path) && onPass(entity, RESEEK_PASSES)) seekers.push(entity)
      continue
    }
    if (zone || supplier || magazine.ammo >= cheapestShot(unit.type) - EPSILON) continue
    // Идущий по приказу идёт, куда велели: за боеприпасами он пойдёт, когда встанет.
    if (world.has(entity, Path) || stanceOf(sim, entity) === 'hold' || !onPass(entity, SEEK_PASSES)) continue
    seekers.push(entity)
  }

  // Пустые — к ближайшему хранилищу своей сети, где есть боеприпасы, или к машине снабжения с ними. Нет таких — ждут,
  // где стоят.
  for (const entity of seekers) {
    const player = world.get(entity, Owner)!.player
    const { x, y } = world.get(entity, Position)!
    let best: { x: number; y: number; size: number } | undefined
    let near = 0
    let bestDistance = Infinity
    for (const supplier of suppliers) {
      if (supplier.player !== player || supplier.entity === entity || amountOf(supplier.stock, 'ammo') <= EPSILON) continue
      const distance = Math.hypot(supplier.x - x, supplier.y - y)
      if (distance >= bestDistance) continue
      best = { x: supplier.x, y: supplier.y, size: 0 }
      near = Math.max(0, supplier.radius - SUPPLIER_MARGIN)
      bestDistance = distance
    }
    for (const zone of zonesOf(sim, player)) {
      if (!stocked(zone)) continue
      for (const source of offers(zone, 'ammo')) {
        if (leftIn(source) <= EPSILON) continue
        const center = centerOf(sim, source)
        const distance = Math.hypot(center.x - x, center.y - y)
        if (distance < bestDistance) {
          bestDistance = distance
          best = center
          near = center.size / 2 + SEEK_NEAR
        }
      }
    }
    if (!best) continue
    leaveHome(sim, entity)
    orderMove(sim, entity, Math.floor(best.x), Math.floor(best.y), undefined, 0, near)
    if (!world.has(entity, Path)) continue
    world.get(entity, Tactics)!.resupply = true
  }
}
