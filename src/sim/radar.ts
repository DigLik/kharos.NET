import type { Entity, Time } from '../ecs'
import { hash } from '../map/terrain'
import { BUILDINGS, buildingSpec, isReady, levelFactor, type BuildingType } from './buildings'
import { Building, Off, Owner, Position, Scan } from './components'
import { powerSupply } from './income'
import type { Sim } from './sim'

/**
 * Разведка радара. Постоянно радар видит недалеко, как обычное здание повыше, а дальше — разведывает вспышками: раз
 * в SCAN_EVERY секунд открывает круг в SCAN_SIZE тайлов где-то в своём радиусе на SCAN_TICKS тиков (см. vision.ts). Что во
 * вспышке — видно, как в обзоре юнита: враги, здания, следы; после неё место остаётся разведанным, и месторождения
 * на нём игрок знает. Место вспышки радар берёт там, где ещё не разведано, а когда разведано всё — где придётся: так
 * он сначала открывает округу, а потом сторожит её, заглядывая то туда, то сюда. Радиус — BuildingSpec.scan,
 * с уровнем растёт (levelFactor), и потребление с ним, см. powerAt. При нехватке энергии вспышки реже, а если её
 * меньше RADAR_POWER от нужного — радар и постоянно видит не дальше обычного здания (Scan.dark); без энергии вовсе
 * и выключенный радар не разведывает.
 */
export const SCAN_EVERY = 10
/** С какой доли нужной энергии радар видит дальше обычного здания. */
export const RADAR_POWER = 0.5
/** Раз во сколько тиков радары смотрят, не пора ли: счёт до вспышки идёт этими шагами. */
const CHECK_TICKS = 10
/** Сколько мест радар пробует, ища неразведанное. */
const ATTEMPTS = 8
const SALT = 40487

/** На сколько тайлов от своего центра разведывает радар этого вида и уровня; ноль — не разведывает. */
export const scanRadius = (type: BuildingType, level = 1) => (buildingSpec(type).scan ?? 0) * levelFactor(level)

/** Раз в тик: радары ведут счёт до вспышки и вспыхивают. */
export function scan(sim: Sim, time: Time) {
  if (time.tick % CHECK_TICKS) return
  const { world, vision, bounds } = sim
  const due: { entity: Entity; player: number; x: number; y: number; radius: number }[] = []
  let supply: ReadonlyMap<Entity, number> | undefined
  for (const [entity, building, owner, position] of world.query(Building, Owner, Position)) {
    const radius = scanRadius(building.type, building.level)
    if (!radius || !isReady(sim, owner.player, entity) || world.has(entity, Off)) continue
    const state = world.get(entity, Scan)
    if (!state) {
      // Первая вспышка — через полный срок: радары, поставленные разом, всё равно расходятся — места у них разные.
      world.add(entity, Scan({ wait: SCAN_EVERY / time.step }))
      continue
    }
    // Энергии не хватает — счёт идёт медленнее. Считается недёшево, поэтому только когда есть радары.
    supply ??= powerSupply(sim)
    const share = supply.get(entity) ?? 0
    state.dark = share < RADAR_POWER
    state.wait -= CHECK_TICKS * share
    if (state.wait > 0) continue
    const { width, height } = BUILDINGS[building.type]
    due.push({ entity, player: owner.player, x: position.x + width / 2, y: position.y + height / 2, radius })
  }
  for (const { entity, player, x, y, radius } of due) {
    let spot: { x: number; y: number } | undefined
    for (let attempt = 0; attempt < ATTEMPTS; attempt++) {
      const turn = hash(entity, time.tick * ATTEMPTS + attempt, SALT) * Math.PI * 2
      // Корень — чтобы места ложились по кругу ровно, а не кучей у радара.
      const reach = Math.sqrt(hash(entity, time.tick * ATTEMPTS + attempt, SALT + 1)) * radius
      const tx = x + Math.cos(turn) * reach
      const ty = y + Math.sin(turn) * reach
      if (tx < bounds.left || tx >= bounds.right || ty < bounds.top || ty >= bounds.bottom) continue
      spot = { x: Math.round(tx * 10) / 10, y: Math.round(ty * 10) / 10 }
      if (!vision.explored(player, tx, ty)) break
    }
    const state = world.get(entity, Scan)!
    state.wait = SCAN_EVERY / time.step
    if (!spot) continue
    state.x = spot.x
    state.y = spot.y
    state.tick = time.tick
  }
}
