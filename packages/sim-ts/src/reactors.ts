import type { Entity } from '@kharos/ecs'
import { buildingSpec } from './buildings'
import { Building, Burst, Inventory, Off, Position, Reactor, Silo } from './components'
import { amountOf, take } from './inventory'
import type { Sim } from './sim'

/**
 * Реакторы (§9 п. 4 design.md). Включённый реактор жжёт стержень за стержнем со своего склада и, пока горит,
 * вырабатывает энергию; без стержней и выключенный — ничего. Стержень в работе догорает и у выключенного не тает:
 * выключатель только ставит работу на паузу. Разрушенный реактор с топливом взрывается.
 */

/** Горит ли в реакторе стержень: тогда он вырабатывает энергию. У других зданий — false. */
export const isBurning = (sim: Sim, entity: Entity) => (sim.world.get(entity, Reactor)?.left ?? 0) > 0 && !sim.world.has(entity, Off)

/** Раз в тик, до энергии зон: реакторы жгут стержни и берут следующий со склада, как только догорит нынешний. */
export function burnFuel(sim: Sim) {
  const { world, time } = sim
  for (const [entity, reactor, building, inventory] of world.query(Reactor, Building, Inventory)) {
    if (world.has(entity, Off)) continue
    if (reactor.left > 0) reactor.left--
    if (reactor.left > 0) continue
    const { good, seconds } = buildingSpec(building.type).burns!
    if (amountOf(inventory, good) < 1) continue
    take(inventory, good, 1)
    reactor.left = Math.round(seconds / time.step)
  }
}

/**
 * Авария: здание с топливом внутри (BuildingSpec.meltdown) оставляет на своём месте взрыв, который бьёт всех вокруг
 * в ближайшем бою, см. Burst. Топливо сгорает в нём и на землю не падает. Пустой реактор не взрывается; шахта —
 * только с готовой боеголовкой, и рвётся она как реактор, без ядерного взрыва.
 * Зовут те, кто разрушает здание: бой и перегруз.
 */
export function meltdown(sim: Sim, entity: Entity) {
  const { world } = sim
  const building = world.get(entity, Building)
  const position = world.get(entity, Position)
  const spec = building && buildingSpec(building.type)
  if (!spec?.meltdown || !position) return
  if (spec.burns) {
    const inventory = world.get(entity, Inventory)
    const rods = inventory ? amountOf(inventory, spec.burns.good) : 0
    if (rods <= 0 && (world.get(entity, Reactor)?.left ?? 0) <= 0) return
    if (inventory && rods > 0) take(inventory, spec.burns.good, rods)
  } else if (world.get(entity, Silo)?.ready !== true) return
  const { radius, damage } = spec.meltdown
  world.spawn(Position({ x: position.x + spec.width / 2, y: position.y + spec.height / 2 }), Burst({ radius, damage }))
}
