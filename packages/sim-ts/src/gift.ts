import type { Entity } from '@kharos/ecs'
import { allied } from './allies'
import { isOwn } from './common'
import { Attached, Builds, Doomed, Hauler, Inventory, Owner, Path, Unit } from './components'
import { stopAttack } from './combat'
import { releaseHauler } from './hauling'
import { amountOf } from './inventory'
import { clearOrders } from './orders'
import { GOODS } from './resources'
import type { Sim } from './sim'
import { clearTactics } from './tactics'
import { turretsOf } from './turrets'

/**
 * Передача своих юнитов союзнику: выбранные юниты переходят под его управление там же, где стоят, — это приказ,
 * а не груз, и корабль для него не нужен. Работу они бросают: стройка, маршрут, цель, патруль и путь снимаются.
 * Груз остаётся в кузове: что юнит вёз, то и довозит — уже новому хозяину,
 * в его хранилища и на его переработку. Турели уходят вместе с носителем: иначе на союзной технике остались бы
 * чужие башни. Принимает только союзник.
 */

/** Кого из названных юнитов можно отдать: свои, живые и не турели на носителе. */
export const giftable = (sim: Sim, player: number, units: readonly Entity[]) =>
  [...new Set(units)].filter(
    (entity) => sim.world.has(entity, Unit) && isOwn(sim, player, entity) && !sim.world.has(entity, Attached) && !sim.world.has(entity, Doomed),
  )

/** Отдаёт юнитов союзнику. Возвращает, сколько перешло; ноль — отдавать было нечего или он не союзник. */
export function handOver(sim: Sim, player: number, to: number, units: readonly Entity[]) {
  if (to === player || !Array.isArray(units) || !allied(sim, player, to)) return 0
  const taken = giftable(sim, player, units)
  if (!taken.length) return 0
  const { world } = sim
  for (const entity of taken) {
    world.remove(entity, Builds)
    releaseHauler(sim, entity)
    // Груз остаётся в кузове и едет с юнитом: дальше его сгружает уже перевозка нового хозяина.
    const cargo = world.get(entity, Inventory)
    const carried = cargo && GOODS.find((good) => amountOf(cargo, good) > 1e-9)
    const hauler = world.get(entity, Hauler)
    if (hauler && carried) Object.assign(hauler, { resource: carried, full: true })
    stopAttack(sim, entity)
    clearTactics(sim, entity)
    clearOrders(sim, [entity])
    world.remove(entity, Path)
    for (const turret of turretsOf(sim, entity)) world.set(turret, Owner, { player: to })
    world.set(entity, Owner, { player: to })
  }
  return taken.length
}
