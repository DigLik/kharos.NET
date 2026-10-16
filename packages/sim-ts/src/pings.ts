import { Mark, Position } from './components'
import type { Sim } from './sim'
import { inBounds } from './units'

/** Сколько секунд сигнал виден на карте. */
export const PING_SECONDS = 8
/** Не больше стольких сигналов от игрока за PING_WINDOW секунд: сигналом не засыпают экран союзника. */
export const PING_LIMIT = 3
export const PING_WINDOW = 5

/**
 * Сигнал союзникам: метка в точке карты, которую PING_SECONDS видят сам игрок и его союзники — где бы ни была их
 * камера и что бы ни скрывал туман (метка, а не обзор: см. Mark и правило LISTED в vision.ts). Гасит её тот же
 * обход, что и метки ядерных ударов, см. silos.ts. Возвращает, поставлена ли метка.
 */
export function ping(sim: Sim, player: number, x: number, y: number) {
  if (!Number.isFinite(x) || !Number.isFinite(y) || !inBounds(sim, Math.floor(x), Math.floor(y))) return false
  const { world, time } = sim
  const life = Math.round(PING_SECONDS / time.step)
  let recent = 0
  for (const [, mark] of world.query(Mark)) {
    if (mark.kind === 'ping' && mark.player === player && (time.tick - (mark.until - life)) * time.step < PING_WINDOW) recent++
  }
  if (recent >= PING_LIMIT) return false
  world.spawn(Position({ x, y }), Mark({ kind: 'ping', until: time.tick + life, player, viewers: [] }))
  return true
}
