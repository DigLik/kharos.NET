import { BUILDINGS } from '../sim'

/** Высота полёта ядерной ракеты над землёй, в тайлах, и за сколько секунд она её набирает после старта и теряет перед ударом. */
const CRUISE = 2.5
const CLIMB = 3

/** Ракета, какой её знает клиент: см. Missile в components.ts. */
interface Flight {
  fromX: number
  fromY: number
  toX: number
  toY: number
  born: number
  blind: boolean
}

/**
 * Где ракета в момент clock (тик с долей), в тайлах: над какой точкой земли (x, y), как высоко (lift) и куда она летит
 * (headingX, headingY — единичный вектор). Хост её места не шлёт: она летит по прямой с известной скоростью, и клиент
 * ведёт её сам. Своя ракета останавливается в точке удара; у чужой (blind) точки удара нет — она летит, пока хост её
 * не уберёт.
 */
export function missileAt(missile: Flight, clock: number, step: number) {
  const { speed } = BUILDINGS.silo.warhead
  const reach = Math.hypot(missile.toX - missile.fromX, missile.toY - missile.fromY) || 1
  const headingX = (missile.toX - missile.fromX) / reach
  const headingY = (missile.toY - missile.fromY) / reach
  const age = Math.max(0, (clock - missile.born) * step)
  const flown = missile.blind ? speed * age : Math.min(reach, speed * age)
  const climb = Math.min(1, age / CLIMB)
  const descent = missile.blind ? 1 : Math.min(1, (reach - flown) / (speed * CLIMB))
  return {
    x: missile.fromX + headingX * flown,
    y: missile.fromY + headingY * flown,
    lift: CRUISE * Math.min(climb, descent),
    headingX,
    headingY,
  }
}
