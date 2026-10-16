import type { Entity } from '@kharos/ecs'
import {
  Assembly, Building, Converting, GOODS, Interceptor, Inventory, Producer, Research, Site, amountOf, awaitsMaterials, buildingSpec, hasRoom, sitesShortOfCredits, interceptorLack, isOwn, missingFor,
  recipeOf, shutDown, type Amounts, type BuildingType, type Good, type Sim,
} from '../sim'
import { networkHas } from '../sim/upgrades'
import { feedOf } from '../sim/buildings'
import { hasAmmo } from '../sim/combat'
import { roomFor } from '../sim/inventory'
import { AMMO_REORDER } from '../sim/logistics'
import { TURRETS } from '../sim/turrets'
import { WEAPONS, type WeaponType } from '../sim/weapons'

const EPSILON = 1e-6

/** Чего не хватает: груза или кредитов — стройку оплачивают по ходу, и без них она стоит. */
export type Lack = Good | 'credits'

/**
 * Чего ждут свои здания, которые из-за этого стоят: каждому — первый недостающий груз. Стройка, дошедшая до
 * привезённого, а у которой материалы есть, но на остаток цены кредитов не хватит (см. sitesShortOfCredits), — кредитов; производитель, чей заказ не из чего начать; включённый цех без сырья на сборку; лаборатория без
 * сырья темы; перехватчик, которому не из чего собрать ракету; турель, которой нечем выстрелить, и реактор без
 * стержней. Чего не хватает зданию, которое и не должно
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
  for (const [entity, building] of world.query(Building, Inventory)) {
    const feed = feedOf(buildingSpec(building.type))
    if (feed && !world.has(entity, Site) && stands(entity) && !fed(sim, entity, building.type)) note(entity, feed)
  }
  return lacks
}

/** Стержень реактор сжигает целиком: меньше одного — не горит, см. reactors.ts. */
const ROD = 1

/** Есть ли у турели чем выстрелить хоть одним стволом, у реактора — стержень. */
function fed(sim: Sim, entity: Entity, type: BuildingType) {
  const spec = buildingSpec(type)
  const inventory = sim.world.get(entity, Inventory)!
  if (!spec.ammo) return !spec.burns || amountOf(inventory, spec.burns.good) >= ROD - EPSILON
  return (spec.mounts ?? []).some(({ turret }) => {
    const weapon = (TURRETS[turret] as { weapon?: WeaponType }).weapon
    return !weapon || hasAmmo(sim, entity, WEAPONS[weapon])
  })
}

/**
 * Свои турели и реакторы, у которых запаса меньше AMMO_REORDER, но он ещё не кончился: тот же порог, с которого они
 * сами заказывают подвоз. Над ними значок груза бледный — тревога заранее; кончится — станет ярким, см. lacksOf.
 */
export function runningLow(sim: Sim, player: number): Map<Entity, Good> {
  const { world } = sim
  const low = new Map<Entity, Good>()
  for (const [entity, building, inventory] of world.query(Building, Inventory)) {
    const spec = buildingSpec(building.type)
    const feed = feedOf(spec)
    if (!feed || world.has(entity, Site) || !isOwn(sim, player, entity) || shutDown(sim, player, entity)) continue
    const have = amountOf(inventory, feed)
    const share = have / (have + roomFor(inventory, feed))
    if (share < AMMO_REORDER && fed(sim, entity, building.type)) low.set(entity, feed)
  }
  return low
}
