import type { Entity } from '../ecs'
import { allied } from './allies'
import { BUILDINGS, buildingSpec } from './buildings'
import { Building, Owner, Position, Shield, Site } from './components'
import { powerSupply } from './income'
import type { Sim } from './sim'

/**
 * Щиты. Генератор щита (BuildingSpec.shield) держит купол: урон по своим и союзным зданиям и юнитам под ним
 * от стрелков снаружи купола и от летающих гасит запас щита, а не прочность. Кто зашёл под купол, бьёт мимо щита:
 * ответ на щит — штурм вплотную, а не осада издалека. Урон болот и перегруза щит не трогает.
 *
 * Энергию генератор берёт постоянно, как любой потребитель: нехватка замедляет восстановление, без энергии или
 * выключенный купол гаснет и запас пропадает. Растёт запас не раньше rules.repairPause секунд после последнего
 * погашенного попадания — то же правило, что у ремонта, иначе щит с электростанцией за спиной держал бы малый отряд
 * вечно. Пробитый до нуля купол гаснет на SHIELD_DOWN секунд и потом растёт с нуля.
 */
export const SHIELD_DOWN = 10

/** Купол, который сейчас гасит урон. */
export interface Dome {
  entity: Entity
  player: number
  x: number
  y: number
  radius: number
  shield: { charge: number; hit: number; down: number }
}

/** Середина здания: из неё купол. */
function centerOf(sim: Sim, entity: Entity) {
  const position = sim.world.get(entity, Position)!
  const { width, height } = BUILDINGS[sim.world.get(entity, Building)!.type]
  return { x: position.x + width / 2, y: position.y + height / 2 }
}

/** Купола, которые держатся сейчас: готовый генератор, с энергией и с запасом. */
export function domes(sim: Sim): Dome[] {
  const { world, time } = sim
  const result: Dome[] = []
  let supply: Map<Entity, number> | undefined
  for (const [entity, shield, building, owner] of world.query(Shield, Building, Owner)) {
    if (shield.charge <= 0 || time.tick < shield.down || world.has(entity, Site)) continue
    if (!((supply ??= powerSupply(sim)).get(entity) ?? 0)) continue
    const { x, y } = centerOf(sim, entity)
    result.push({ entity, player: owner.player, x, y, radius: buildingSpec(building.type).shield!.radius, shield })
  }
  return result
}

/**
 * Гасит урон damage (в единицах прочности) по цели игрока player в точке (x, y), пришедший из точки (fromX, fromY);
 * fromAir — стрелял летающий. Урон берёт тот из подходящих куполов, у кого запас больше. Возвращает, сколько урона
 * прошло сквозь щит.
 */
export function absorb(sim: Sim, list: readonly Dome[], player: number, x: number, y: number, damage: number, fromX: number, fromY: number, fromAir: boolean) {
  let best: Dome | undefined
  for (const dome of list) {
    if (dome.shield.charge <= 0 || sim.time.tick < dome.shield.down) continue
    if (Math.hypot(x - dome.x, y - dome.y) > dome.radius) continue
    if (!fromAir && Math.hypot(fromX - dome.x, fromY - dome.y) <= dome.radius) continue
    if (dome.player !== player && !allied(sim, dome.player, player)) continue
    if (!best || dome.shield.charge > best.shield.charge) best = dome
  }
  if (!best) return damage
  const taken = Math.min(best.shield.charge, damage)
  best.shield.charge -= taken
  best.shield.hit = sim.time.tick
  if (best.shield.charge <= 0) {
    best.shield.charge = 0
    best.shield.down = sim.time.tick + Math.round(SHIELD_DOWN / sim.time.step)
  }
  return damage - taken
}

/** Раз в тик: щиты восстанавливаются на долю энергии своей зоны; без энергии купол гаснет. */
export function chargeShields(sim: Sim) {
  const { world, time } = sim
  let supply: Map<Entity, number> | undefined
  const pause = sim.rules.repairPause / time.step
  for (const [entity, shield, building] of world.query(Shield, Building)) {
    if (world.has(entity, Site)) continue
    const share = (supply ??= powerSupply(sim)).get(entity) ?? 0
    if (share <= 0) {
      shield.charge = 0
      continue
    }
    if (time.tick < shield.down || (shield.hit >= 0 && time.tick - shield.hit < pause)) continue
    const { capacity, regen } = buildingSpec(building.type).shield!
    if (shield.charge < capacity) shield.charge = Math.min(capacity, shield.charge + regen * share * time.step)
  }
}
