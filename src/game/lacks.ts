import type { Entity } from '../ecs'
import {
  Assembly, Building, Converting, GOODS, Interceptor, Inventory, Producer, Research, Site, amountOf, awaitsMaterials, buildingSpec, hasRoom, sitesShortOfCredits, interceptorLack, isOwn, missingFor,
  recipeOf, shutDown, type Amounts, type Good, type Sim,
} from '../sim'
import { networkHas } from '../sim/upgrades'

const EPSILON = 1e-6

/** Чего не хватает: груза или кредитов — стройку оплачивают по ходу, и без них она стоит. */
export type Lack = Good | 'credits'

/**
 * Чего ждут свои здания, которые из-за этого стоят: каждому — первый недостающий груз. Стройка, дошедшая до
 * привезённого, а у которой материалы есть, но на остаток цены кредитов не хватит (см. sitesShortOfCredits), — кредитов; производитель, чей заказ не из чего начать; включённый цех без сырья на сборку; лаборатория без
 * сырья темы; перехватчик, которому не из чего собрать ракету. Чего не хватает зданию, которое и не должно
 * работать — выключено, нет заказа, — не считается: это не тревога. О чужих судить не по чему: их склады знает
 * только хозяин. По этому над зданием рисуется значок, см. powerPass.ts.
 */
export function lacksOf(sim: Sim, player: number): Map<Entity, Lack> {
  const { world } = sim
  const lacks = new Map<Entity, Lack>()
  const firstOf = (amounts: Amounts) => Object.keys(amounts)[0] as Good | undefined
  const note = (entity: Entity, good: Good | undefined) => void (good && lacks.set(entity, good))
  const stands = (entity: Entity) => isOwn(sim, player, entity) && !shutDown(sim, player, entity)

  const broke = sitesShortOfCredits(sim, player)
  for (const [entity, site] of world.query(Site)) {
    if (!isOwn(sim, player, entity) || site.demolish) continue
    if (awaitsMaterials(sim, entity)) note(entity, firstOf(missingFor(sim, entity)))
    else if (broke.has(entity)) lacks.set(entity, 'credits')
  }
  for (const [entity, producer] of world.query(Producer)) {
    if (!producer.queue.length || producer.progress > 0 || world.has(entity, Site) || world.has(entity, Converting) || !stands(entity)) continue
    note(entity, firstOf(missingFor(sim, entity)))
  }
  for (const [entity, assembly, inventory] of world.query(Assembly, Inventory)) {
    // Готовому некуда лечь — это не нехватка сырья.
    if (!assembly.on || assembly.progress > 0 || world.has(entity, Site) || !isOwn(sim, player, entity) || !hasRoom(sim, entity)) continue
    note(entity, recipeOf(assembly.recipe).find(([good, amount]) => amountOf(inventory, good) < amount - EPSILON)?.[0])
  }
  for (const [entity, research] of world.query(Research)) {
    if (stands(entity) && (GOODS as string[]).includes(research.lack)) note(entity, research.lack as Good)
  }
  for (const [entity, , building] of world.query(Interceptor, Building)) {
    if (world.has(entity, Site) || !stands(entity) || interceptorLack(sim, entity) !== 'materials') continue
    const materials = buildingSpec(building.type).intercept?.materials ?? {}
    note(entity, (Object.keys(materials) as Good[]).find((good) => !networkHas(sim, entity, { [good]: materials[good] })))
  }
  return lacks
}
