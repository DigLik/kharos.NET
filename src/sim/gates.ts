import type { Entity, Time } from '@kharos/ecs'
import { tileKey } from '../map/terrain'
import { buildingSpec } from './buildings'
import { Building, Gate, Owner, Position, Site, Unit } from './components'
import type { Sim } from './sim'
import { flies } from './units'
import { alliances } from './allies'

/**
 * Ворота — плиты, которые выезжают из земли. Каждая занимает тайл; их тянут линией, как стену, и секции, стоящие
 * вплотную, — одни ворота любой ширины. Обычно плиты опущены: через них ходят и ездят все, выстрелы летят поверху.
 * Когда к воротам подходит чужой наземный юнит — не союзник хозяина, см. allies.ts (ближе GATE_CLOSE тайлов до любой секции), плиты поднимаются
 * и становятся стеной — и для своих тоже. Опускаются, когда чужих не осталось ближе GATE_OPEN: запас между порогами
 * не даёт им хлопать. Секция, на которой кто-то стоит, не поднимается, пока он не съедет: врага, который успел
 * въехать, она уже не остановит, а своего не подбросит; остальные секции тех же ворот поднимаются. Летающим ворота
 * безразличны. Недостроенные ворота — просто площадка.
 */
export const GATE_CLOSE = 6
export const GATE_OPEN = 7.5
/** Раз во сколько тиков ворота смотрят по сторонам. */
const CHECK_TICKS = 4

/** Раз в тик: ворота закрываются перед чужими и открываются за ними. */
export function gates(sim: Sim, time: Time) {
  if (time.tick % CHECK_TICKS) return
  const { world } = sim
  const list: { entity: Entity; player: number; x: number; y: number; foe: number; held: boolean }[] = []
  for (const [entity, building, owner, position] of world.query(Building, Owner, Position)) {
    if (!buildingSpec(building.type).gate || world.has(entity, Site)) continue
    if (!world.has(entity, Gate)) world.add(entity, Gate({ open: true }))
    list.push({ entity, player: owner.player, x: position.x + 0.5, y: position.y + 0.5, foe: Infinity, held: false })
  }
  if (!list.length) return
  // Ворота разложены по клеткам со стороной в порог открытия: юнит смотрит только свою клетку и соседние.
  const cells = new Map<number, typeof list>()
  const cellOf = (value: number) => Math.floor(value / GATE_OPEN)
  for (const gate of list) {
    const key = tileKey(cellOf(gate.x), cellOf(gate.y))
    const cell = cells.get(key)
    if (cell) cell.push(gate)
    else cells.set(key, [gate])
  }
  const friends = alliances(sim)
  for (const [, unit, owner, position] of world.query(Unit, Owner, Position)) {
    if (flies(unit.type)) continue
    const cx = cellOf(position.x)
    const cy = cellOf(position.y)
    for (let dy = -1; dy <= 1; dy++) {
      for (let dx = -1; dx <= 1; dx++) {
        const cell = cells.get(tileKey(cx + dx, cy + dy))
        if (!cell) continue
        for (const gate of cell) {
          const distance = Math.hypot(position.x - gate.x, position.y - gate.y)
          // В проёме — тот, чей центр в тайле ворот.
          if (Math.abs(position.x - gate.x) < 0.5 && Math.abs(position.y - gate.y) < 0.5) gate.held = true
          // Чужой — не хозяин, не ничей и не союзник хозяина.
          if (owner.player !== gate.player && owner.player !== 0 && distance < gate.foe && !friends.get(gate.player)?.includes(owner.player)) gate.foe = distance
        }
      }
    }
  }
  // Секции вплотную — одни ворота: чужой у одной поднимает все.
  const byTile = new Map(list.map((gate) => [tileKey(Math.floor(gate.x), Math.floor(gate.y)), gate]))
  const grouped = new Set<(typeof list)[number]>()
  for (const first of list) {
    if (grouped.has(first)) continue
    const group = [first]
    grouped.add(first)
    for (let at = 0; at < group.length; at++) {
      const { x, y, player } = group[at]
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const next = byTile.get(tileKey(Math.floor(x) + dx, Math.floor(y) + dy))
        if (!next || next.player !== player || grouped.has(next)) continue
        grouped.add(next)
        group.push(next)
      }
    }
    const foe = Math.min(...group.map((gate) => gate.foe))
    for (const gate of group) {
      const state = world.get(gate.entity, Gate)
      if (!state) continue
      if (state.open && foe < GATE_CLOSE && !gate.held) state.open = false
      else if (!state.open && foe >= GATE_OPEN) state.open = true
    }
  }
}
