import type { Component, Entity, World } from '../ecs'
import { Accumulator, Armed, Assembly, Attached, Blast, Builds, Burst, Chaser, Drop, Hauler, Health, Interceptor, Inventory, Mark, Missile, Owner, Path, Position, Producer, Reactor, Repair, Scan, Shot, Silo, SAVED, Tactics, Trade, Turret, Unit } from '../sim/components'
import { quantize, type Motion } from './codec'

/** До скольких знаков после запятой округляются дробные числа в сети: тысячная тайла глазу не видна. */
const PRECISION = 1000

/** Дробное — до PRECISION: короче в тексте, и стоящий юнит не уходит в сеть из-за дрожи в последних знаках. */
const round = (_key: string, value: unknown) =>
  typeof value === 'number' && !Number.isInteger(value) ? Math.round(value * PRECISION) / PRECISION : value

/**
 * Каким компонент уходит игроку viewer: только то, что нужно клиенту, или undefined — не уходит вовсе.
 * Прошлое место и поворот юнита и турели клиент помнит сам, место турели на носителе находит сам,
 * перезарядку считает по тику её конца, возраст выстрела и взрыва — по тику рождения, см. replica.ts. Всё, что меняется каждый тик без нужды, уходило бы каждый тик.
 */
function wire(component: Component<any>, data: any, tick: number): object | undefined {
  // Путь — свой у каждого игрока, см. pathOf. Авария живёт до ближайшего боя: клиент видит её взрывом (Blast).
  if (component === Path || component === Burst) return undefined
  // Место и поворот юнита идут движением, см. codec.ts.
  if (component === Unit) {
    const { prevX: _x, prevY: _y, prevFacing: _previous, facing: _facing, ...rest } = data
    return rest
  }
  if (component === Position) return undefined
  if (component === Turret) return { type: data.type, angle: data.angle }
  // Перезарядка тает каждый тик; тик, когда она кончится, — нет. Клиенту она нужна только для отдачи.
  if (component === Armed) return { target: data.target, until: data.cooldown > 0 ? tick + data.cooldown : 0 }
  if (component === Health) {
    const { hit: _hit, ...rest } = data
    return rest
  }
  // Возраст выстрела и взрыва растёт каждый тик, а тик рождения — нет; где снаряд был тик назад, клиент помнит сам.
  if (component === Shot) {
    const { prevX: _x, prevY: _y, age, ...rest } = data
    return { ...rest, born: tick - age }
  }
  if (component === Blast) return { size: data.size, life: data.life, lift: data.lift, born: tick - data.age }
  // Сколько горит стержень, тает каждый тик; клиенту хватает знать, горит ли он.
  if (component === Reactor) return { left: data.left > 0 ? 1 : 0 }
  // Где ракета сейчас, клиент считает сам по старту и тику: иначе она уходила бы каждый тик. Куда она летит, знает
  // только хозяин, см. seenBy; кому видна метка — только хост. Чья метка — не тайна: о пуске уже сказано в чате.
  if (component === Missile) return { fromX: data.fromX, fromY: data.fromY, toX: data.toX, toY: data.toY, born: data.born }
  if (component === Mark) return { kind: data.kind, until: data.until, player: data.player }
  // Счёт до следующей вспышки радара клиенту не нужен, а менялся бы дважды в секунду.
  if (component === Scan) return { x: data.x, y: data.y, tick: data.tick, dark: data.dark }
  return data
}

/** Сущность, какой она уходит игроку: JSON компонентов по имени и движение — место и поворот, см. codec.ts. */
export interface Wired {
  parts: Map<string, string>
  motion?: Motion
  /** Чей это: от чужих часть компонентов скрыта, см. PRIVATE. undefined — ничей. */
  owner?: number
  /** Склад ящика с грузом виден всем: это не тайна хозяина. */
  drop?: boolean
  /** Та же сущность глазами чужого игрока: собирается один раз, когда понадобится. */
  foreign?: Wired
}

/**
 * Что о своём знает только хозяин: склад, очередь производства, работа грузовиков, копателей и строителей, сборка,
 * торговля, тактика, куда смотрит радар, заряжен ли перехватчик. Чужому это незачем рисовать, а знать — нечестно.
 */
const PRIVATE = new Set([Inventory, Producer, Hauler, Assembly, Trade, Tactics, Builds, Repair, Scan, Accumulator, Reactor, Interceptor].map((component) => component.key))

/**
 * Сущность глазами игрока viewer: своё и ничьё — целиком, чужое — без PRIVATE и без цели стрелка; у чужой шахты —
 * только пуск, у чужой ракеты — курс без точки удара.
 */
export function seenBy(wired: Wired, viewer: number): Wired {
  if (wired.owner === undefined || wired.owner === 0 || wired.owner === viewer) return wired
  if (!wired.foreign) {
    const parts = new Map(wired.parts)
    for (const key of PRIVATE) if (!(key === Inventory.key && wired.drop)) parts.delete(key)
    // В кого целится стрелок, знает только хозяин: чужой узнал бы это раньше выстрела.
    const armed = parts.get(Armed.key)
    if (armed) {
      const { target: _target, ...rest } = JSON.parse(armed)
      parts.set(Armed.key, JSON.stringify(rest))
    }
    // Чужая шахта: виден только пуск — створки открываются у всех на глазах; готова ли боеголовка, знает хозяин.
    const silo = parts.get(Silo.key)
    if (silo) parts.set(Silo.key, JSON.stringify({ start: JSON.parse(silo).start }))
    // Чужая ракета: куда она летит, видно по курсу, но не где упадёт.
    const missile = parts.get(Missile.key)
    if (missile) {
      const { fromX, fromY, toX, toY, born } = JSON.parse(missile)
      const reach = Math.hypot(toX - fromX, toY - fromY) || 1
      parts.set(Missile.key, JSON.stringify({ fromX, fromY, toX: fromX + (toX - fromX) / reach, toY: fromY + (toY - fromY) / reach, born, blind: true }, round))
    }
    wired.foreign = { parts, motion: wired.motion }
  }
  return wired.foreign
}

/**
 * Сущность, какой она уходит игрокам, кроме пути: хозяину целиком, чужим — через seenBy; пусто — сущности в сети нечего показать. Не зависит от того,
 * кто смотрит: хост собирает её раз за тик и раздаёт всем, кто её видит. Турель на носителе клиент ставит на место
 * сам: её место меняется с каждым шагом носителя.
 */
export function sharedWireOf(world: World, entity: Entity, tick: number): Wired {
  const parts = new Map<string, string>()
  for (const component of SAVED) {
    const data = world.get(entity, component)
    if (data === undefined) continue
    const shown = wire(component, data, tick)
    if (shown !== undefined) parts.set(component.key, JSON.stringify(shown, round))
  }
  const owner = world.get(entity, Owner)?.player ?? world.get(entity, Missile)?.player ?? world.get(entity, Chaser)?.player
  return { parts, owner, drop: world.has(entity, Drop), motion: motionOf(world, entity) }
}

/**
 * Запомненное здание (JSON его компонентов, см. Remembered.parts в buildMemory.ts), каким его собрал бы из сети клиент игрока viewer:
 * чужое — без того, что знает только хозяин, см. seenBy; союзное — целиком. Место идёт тут же, а не движением.
 */
export function rememberedWire(remembered: string, viewer: number, friends: readonly number[], tick: number): Record<string, object> {
  const data = JSON.parse(remembered) as Record<string, object>
  const parts = new Map<string, string>()
  for (const component of SAVED) {
    const value = data[component.key]
    if (value === undefined) continue
    const shown = component === Position ? value : wire(component, value, tick)
    if (shown !== undefined) parts.set(component.key, JSON.stringify(shown, round))
  }
  const owner = (data[Owner.key] as { player: number } | undefined)?.player
  const wired: Wired = { parts, owner }
  const seen = owner !== undefined && friends.includes(owner) ? wired : seenBy(wired, viewer)
  const record: Record<string, object> = {}
  for (const [key, json] of seen.parts) record[key] = JSON.parse(json)
  return record
}

/** Движение сущности, каким оно уходит в сеть; undefined — места у неё нет или клиент находит его сам (турель на носителе). */
export function motionOf(world: World, entity: Entity): Motion | undefined {
  if (world.has(entity, Attached) && world.has(entity, Turret)) return undefined
  const position = world.get(entity, Position)
  if (!position) return undefined
  return quantize(position.x, position.y, world.get(entity, Unit)?.facing)
}

/**
 * JSON пути сущности для игрока viewer, его союзника или undefined. Путь — только своим: чужой клиенту не нужен и
 * выдал бы, куда идёт враг; из пути — только точки, по ним рисуется дорожка выбранного. friends — союзники viewer,
 * см. allies.ts.
 */
export function pathOf(world: World, entity: Entity, viewer: number, friends: readonly number[] = []) {
  const path = world.get(entity, Path)
  if (!path) return undefined
  const owner = world.get(entity, Owner)?.player
  if (owner !== viewer && !(owner !== undefined && friends.includes(owner))) return undefined
  return JSON.stringify({ points: path.points }, round)
}

/** Сущность, какой она уходит игроку viewer: общее для всех и его путь (свой или союзника, см. allies.ts). */
export function wireOf(world: World, entity: Entity, viewer: number, tick: number, friends: readonly number[] = []): Wired {
  const shared = seenBy(sharedWireOf(world, entity, tick), viewer)
  const path = pathOf(world, entity, viewer, friends)
  if (path === undefined) return shared
  return { parts: new Map(shared.parts).set(Path.key, path), motion: shared.motion }
}
