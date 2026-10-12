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
 *
 * Снаряды — пулю, ракету, ядро, бомбу — купол останавливает на себе: чужой снаряд, прилетевший из-за купола или
 * сброшенный летающим, рвётся на его краю, и его урон берёт запас щита; внутрь не долетают ни он, ни осколки
 * (см. stopper и projectiles в combat.ts). Луч и разряд — не снаряды: их урон купол гасит у цели, см. absorb.
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
    if (!holds(sim, dome)) continue
    if (Math.hypot(x - dome.x, y - dome.y) > dome.radius) continue
    if (!fromAir && Math.hypot(fromX - dome.x, fromY - dome.y) <= dome.radius) continue
    if (dome.player !== player && !allied(sim, dome.player, player)) continue
    if (!best || dome.shield.charge > best.shield.charge) best = dome
  }
  if (!best) return damage
  return damage - drain(sim, best, damage)
}

/** Держит ли купол удар сейчас: с запасом и не погашен. */
const holds = (sim: Sim, dome: Dome) => dome.shield.charge > 0 && sim.time.tick >= dome.shield.down

/** Списывает урон с запаса купола; запас кончился — купол гаснет на SHIELD_DOWN. Возвращает, сколько списано. */
function drain(sim: Sim, dome: Dome, damage: number) {
  const taken = Math.min(dome.shield.charge, damage)
  dome.shield.charge -= taken
  dome.shield.hit = sim.time.tick
  if (dome.shield.charge <= 0) {
    dome.shield.charge = 0
    dome.shield.down = sim.time.tick + Math.round(SHIELD_DOWN / sim.time.step)
  }
  return taken
}

/**
 * Купол, который остановит снаряд игрока player, летящий из (x, y) в (toX, toY) за этот тик, и точка на его краю,
 * где снаряд рвётся; undefined — ни один. Останавливает купол чужого (не своего и не союзника), если снаряд входит
 * в него снаружи, а сброшенный летающим (fromAir) — и уже внутри: бомба падает сверху. Из нескольких — первый на пути.
 */
export function stopper(sim: Sim, list: readonly Dome[], player: number, x: number, y: number, toX: number, toY: number, fromAir: boolean) {
  let best: { dome: Dome; x: number; y: number; at: number } | undefined
  const dx = toX - x
  const dy = toY - y
  for (const dome of list) {
    if (!holds(sim, dome) || dome.player === player || allied(sim, dome.player, player)) continue
    const inside = Math.hypot(x - dome.x, y - dome.y) <= dome.radius
    if (inside) {
      if (fromAir && (!best || best.at > 0)) best = { dome, x, y, at: 0 }
      continue
    }
    // Где отрезок пути входит в круг: наименьший корень |p + t·d − c| = r на [0, 1].
    const fx = x - dome.x
    const fy = y - dome.y
    const a = dx * dx + dy * dy
    if (a === 0) continue
    const b = 2 * (fx * dx + fy * dy)
    const c = fx * fx + fy * fy - dome.radius * dome.radius
    const disc = b * b - 4 * a * c
    if (disc < 0) continue
    const at = (-b - Math.sqrt(disc)) / (2 * a)
    if (at < 0 || at > 1 || (best && best.at <= at)) continue
    best = { dome, x: x + dx * at, y: y + dy * at, at }
  }
  return best && { dome: best.dome, x: best.x, y: best.y }
}

/** Снаряд рвётся на куполе: его урон damage берёт запас щита, сколько его есть. */
export const stopShot = (sim: Sim, dome: Dome, damage: number) => void drain(sim, dome, damage)

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
