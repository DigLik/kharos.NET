import type { Entity } from '../ecs'
import { NONE, isOwn } from './common'
import { Converting, Drop, Hauler, Inventory, Path, Site, Unit } from './components'
import { amountOf, beamFor, reaches, roomFor, transfer } from './inventory'
import { releaseHauler, seekBeam, seekDue } from './hauling'
import { spareOf } from './logistics'
import { GOODS, type Good } from './resources'
import type { Sim } from './sim'

/**
 * Ручной приказ грузовикам: перевезти amount груза resource со склада from на склад to — «взять здесь, отвезти туда».
 * Грузовики возят рейсами, пока не перевезут заданное (на всех поровну), потом снова работают сами. Возят по воле
 * игрока: берут всё, кроме товара открытой продажи, и кладут в любой его склад, где груз помещается, а не только туда,
 * где его заказали. from = -1 — груз уже в кузове: отвезти его в to.
 *
 * Приказ кончается сам, когда перевезено всё, источник опустел, а кузов пуст, получателю некуда класть или здание
 * пропало. Груз в кузове тогда, как после любой отмены, грузовик отвезёт в хранилище сам. Чужой груз в кузове приказ
 * не возит: с ним приказ сразу кончается.
 */

const EPSILON = 1e-9

/** Приказ грузовика: что, откуда, куда, сколько ещё перевезти; full — набрал и едет к получателю. */
export interface CarryOrder {
  from: number
  to: number
  resource: Good
  left: number
  full: boolean
}

/** Годится ли склад отдать по приказу: своё здание или стройка со складом, или дроп. */
export const isCarrySource = (sim: Sim, player: number, entity: Entity) =>
  sim.world.has(entity, Inventory) && (sim.world.has(entity, Drop) || (isOwn(sim, player, entity) && !sim.world.has(entity, Unit)))

/** Годится ли склад принять груз по приказу: своё здание, стройка или юнит со складом, где этот груз поместится. */
export function isCarryTarget(sim: Sim, player: number, entity: Entity, resource: Good) {
  const inventory = sim.world.get(entity, Inventory)
  if (!inventory || !isOwn(sim, player, entity) || sim.world.has(entity, Hauler)) return false
  // Стройке — только её материалы: прочее ей некуда деть.
  if (sim.world.has(entity, Site) && !inventory.accepts.includes(resource)) return false
  return roomFor(inventory, resource) > EPSILON
}

/**
 * Даёт грузовикам игрока приказ перевезти amount груза resource из from в to (from = -1 — из кузова). Прежние приказы
 * снимаются. Не грузовики и чужие из списка выбрасываются. false — приказ не годится.
 */
export function setCarry(sim: Sim, player: number, units: Entity[], from: Entity, to: Entity, resource: Good, amount: number) {
  const { world } = sim
  if (!GOODS.includes(resource) || !Number.isFinite(amount) || amount <= 0 || from === to) return false
  if (from !== NONE && !isCarrySource(sim, player, from)) return false
  if (!isCarryTarget(sim, player, to, resource)) return false
  const trucks = [...new Set(units)].filter((entity) => world.has(entity, Hauler) && world.has(entity, Unit) && isOwn(sim, player, entity))
  if (!trucks.length) return false
  const share = amount / trucks.length
  for (const truck of trucks) {
    releaseHauler(sim, truck)
    const carried = amountOf(world.get(truck, Inventory)!, resource)
    const order: CarryOrder = { from, to, resource, left: from === NONE ? Math.min(share, carried) : share, full: from === NONE }
    world.get(truck, Hauler)!.order = order
    world.remove(truck, Path)
  }
  return true
}

/** Чужой груз в кузове: первый, которого там хоть сколько-то и который не приказной. */
const foreignCargo = (sim: Sim, truck: Entity, resource: Good) => {
  const cargo = sim.world.get(truck, Inventory)
  return cargo && GOODS.find((good) => good !== resource && amountOf(cargo, good) > EPSILON)
}

/** Раз в тик, после маршрутов: грузовики с ручным приказом возят по нему. */
export function runCarry(sim: Sim) {
  const { world } = sim
  const seeking: { truck: Entity; building: Entity }[] = []
  for (const [truck, hauler, cargo] of world.query(Hauler, Inventory)) {
    const order = hauler.order
    if (!order || world.has(truck, Path) || world.has(truck, Converting)) continue
    const end = () => {
      hauler.order = null
      hauler.waiting = false
      hauler.loading = false
    }
    const to = order.to as Entity
    const from = order.from as Entity
    if (!world.alive(to) || !world.has(to, Inventory) || foreignCargo(sim, truck, order.resource) !== undefined) {
      end()
      continue
    }
    const carried = amountOf(cargo, order.resource)
    if (!order.full) {
      const want = Math.min(order.left, carried + roomFor(cargo, order.resource)) - carried
      const spare = from !== NONE && world.alive(from) && world.has(from, Inventory) ? spareOf(sim, from, order.resource) : 0
      if (want > EPSILON && spare > EPSILON) {
        if (!reaches(sim, from, truck) && !reaches(sim, truck, from)) {
          if (seekDue(sim, truck, hauler)) seeking.push({ truck, building: from })
          hauler.waiting = true
          continue
        }
        hauler.waiting = false
        const moved = transfer(sim, from, truck, order.resource, Math.min(want, spare))
        hauler.loading = moved > EPSILON
        if (moved > EPSILON) continue
      }
      // Набрал, сколько нужно и влезает, или брать больше нечего: везёт, что есть, а пустой — приказ кончен.
      if (carried <= EPSILON) {
        end()
        continue
      }
      order.full = true
    }
    if (carried <= EPSILON) {
      if (order.left <= EPSILON || order.from === NONE) end()
      else order.full = false
      continue
    }
    if (!reaches(sim, truck, to) && !reaches(sim, to, truck)) {
      if (seekDue(sim, truck, hauler)) seeking.push({ truck, building: to })
      hauler.waiting = true
      continue
    }
    hauler.waiting = false
    const moved = transfer(sim, truck, to, order.resource, Math.min(carried, order.left))
    hauler.loading = moved > EPSILON
    order.left -= moved
    // Получателю некуда класть — приказ кончен: остаток грузовик отвезёт в хранилище сам.
    if (moved <= EPSILON) end()
  }
  for (const { truck, building } of seeking) seekBeam(sim, truck, building, beamFor(sim, truck, building) ?? beamFor(sim, building, truck))
}
