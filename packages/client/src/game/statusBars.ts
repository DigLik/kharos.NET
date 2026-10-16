import type { Entity, World } from '@kharos/ecs'
import { Building, Ghost, Health, Shield, Site, buildingSpec } from '@kharos/sim-ts'

/** Высота полосы состояния над зданием и зазор между полосами — в пикселях экрана; зазор до крыши — в тайлах. */
export const STATUS_BAR = 3
export const STATUS_STACK = 1
export const STATUS_GAP = 0.1

/**
 * Полосы состояния над зданием, сверху вниз: прочность — у повреждённого, кроме строящегося (там она равна полосе
 * стройки), и под ней запас щита — у готового генератора щита, кроме призрака в тумане: что с его щитом сейчас,
 * не видно. Доли от 0 до 1.
 */
export function statusBarsOf(world: World, entity: Entity): { health?: number; shield?: number } {
  const bars: { health?: number; shield?: number } = {}
  const building = world.get(entity, Building)
  if (!building) return bars
  const site = world.get(entity, Site)
  const record = world.get(entity, Health)
  if (record && record.value < record.max && !(site && (!site.demolish || site.undo))) bars.health = Math.max(0, record.value / record.max)
  const dome = buildingSpec(building.type).shield
  const shield = !site && dome ? world.get(entity, Shield) : undefined
  if (shield && dome && !world.has(entity, Ghost)) bars.shield = Math.min(1, Math.max(0, shield.charge / dome.capacity))
  return bars
}

/** Сколько места над крышей, в тайлах, занимают полосы состояния здания: выше них встают прогресс и значки. */
export function statusLift(world: World, entity: Entity, zoom: number) {
  const { health, shield } = statusBarsOf(world, entity)
  const rows = (health === undefined ? 0 : 1) + (shield === undefined ? 0 : 1)
  return rows ? STATUS_GAP + (rows * (STATUS_BAR + STATUS_STACK)) / zoom : 0
}
