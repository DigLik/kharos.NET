import type { Entity } from '@kharos/ecs'
import { cycleSeconds, hasRoom, recipeOf } from './assembly'
import { BUILDINGS, type BuildingSpec } from './buildings'
import { NONE } from './common'
import { Armed, Assembly, Building, Busy, Converting, Interceptor, Inventory, Producer, Repair, Research, Shield, Silo, Site, Trade, Transfer } from './components'
import { alwaysBusy, switchedOff } from './income'
import { amountOf, roomFor } from './inventory'
import { missingFor } from './logistics'
import { resourceOf } from './resources'
import type { Sim } from './sim'
import { turretsOf } from './turrets'

/**
 * Работающие потребители энергии. Здание, которое делает своё дело, просит у зоны всю свою энергию
 * (BuildingSpec.power), а которое стоит без дела — меньше, см. idlePower и economyOfZone в income.ts. Работает ли
 * оно, решает хост раз в BUSY_TICKS и ставит метку Busy: хозяйство клиент считает сам по своей копии мира, а чем
 * занято здание, по ней узнать можно не всегда. «Работает» — значит, есть над чем: от энергии это не зависит, иначе
 * в зоне, где её не хватает, метка мигала бы — встал, запросил меньше, получил энергию, заработал, запросил больше.
 * Кончив дело, здание ещё HOLD секунд значится работающим: завод между двумя сборками нагрузку не дёргает.
 */
export const BUSY_TICKS = 10
const HOLD = 2

const EPSILON = 1e-6

/** Когда каждое здание последний раз видели за работой, в тиках. Не сохраняется: после загрузки набирается заново. */
const seen = new WeakMap<Sim, Map<Entity, number>>()

/** Есть ли у готового включённого потребителя дело. Вид, о котором тут ничего не сказано, занят всегда. */
function working(sim: Sim, entity: Entity, spec: BuildingSpec) {
  const { world } = sim
  // Переработка: есть руда и место под готовое.
  if (spec.refines) {
    const inventory = world.get(entity, Inventory)
    return !!inventory && amountOf(inventory, spec.refines) > EPSILON && roomFor(inventory, resourceOf(spec.refines)) > EPSILON
  }
  // Цех: сборка идёт или её есть из чего начать; готовое, которому некуда лечь, — простой.
  const assembly = world.get(entity, Assembly)
  if (assembly) {
    if (assembly.progress > 0) return assembly.progress < cycleSeconds(assembly.recipe) - EPSILON || hasRoom(sim, entity)
    const inventory = world.get(entity, Inventory)
    return !!inventory && hasRoom(sim, entity) && recipeOf(assembly.recipe).every(([good, amount]) => amountOf(inventory, good) >= amount - EPSILON)
  }
  // Производство: заказ начат или его материалы на месте.
  if (spec.produces) {
    const producer = world.get(entity, Producer)
    if (!producer?.queue.length || world.has(entity, Converting)) return false
    return producer.progress > 0 || !Object.keys(missingFor(sim, entity)).length
  }
  // Лаборатория: ведёт тему или вела бы, будь энергия.
  if (spec.researches) {
    const research = world.get(entity, Research)
    return !!research && (research.speed > 0 || research.lack === 'power')
  }
  if (spec.repair) return (world.get(entity, Repair)?.target ?? NONE) !== NONE
  if (spec.warhead) {
    const silo = world.get(entity, Silo)
    return !!silo && ((silo.ticks > 0 && !silo.ready) || silo.start >= 0)
  }
  if (spec.intercept) return (world.get(entity, Interceptor)?.ticks ?? 0) > 0
  // Космопорт: идёт рейс или подарок союзнику.
  if (spec.trades) return world.has(entity, Trade) || world.has(entity, Transfer)
  // Щит: купол не полон — заряжается.
  if (spec.shield) return (world.get(entity, Shield)?.charge ?? 0) < spec.shield.capacity - EPSILON
  // Турель на энергии: есть цель или оружие ещё перезаряжается.
  if (spec.mounts) {
    return turretsOf(sim, entity).some((turret) => {
      const armed = world.get(turret, Armed)
      return !!armed && (armed.target !== NONE || armed.cooldown > 0)
    })
  }
  return true
}

/** Раз в тик, до энергии зон: потребители, у которых есть дело, получают метку Busy, оставшиеся без него — теряют. */
export function markBusy(sim: Sim) {
  const { world, time } = sim
  if (time.tick % BUSY_TICKS) return
  let last = seen.get(sim)
  if (!last) seen.set(sim, (last = new Map()))
  const hold = HOLD / time.step
  const changed: Entity[] = []
  const alive = new Set<Entity>()
  for (const [entity, building] of world.query(Building)) {
    const spec: BuildingSpec = BUILDINGS[building.type]
    // Кто работает всегда, тому метка не нужна: хозяйство знает это по виду здания.
    if ((spec.power ?? 0) >= 0 || alwaysBusy(spec)) continue
    alive.add(entity)
    const off = world.has(entity, Site) || switchedOff(sim, entity)
    if (!off && working(sim, entity, spec)) last.set(entity, time.tick)
    // Выключенному и недостроенному держать метку незачем.
    const since = off ? undefined : last.get(entity)
    const busy = since !== undefined && time.tick - since < hold
    if (busy !== world.has(entity, Busy)) changed.push(entity)
  }
  for (const entity of changed) {
    if (world.has(entity, Busy)) world.remove(entity, Busy)
    else world.add(entity, Busy())
  }
  for (const entity of last.keys()) if (!alive.has(entity)) last.delete(entity)
}
