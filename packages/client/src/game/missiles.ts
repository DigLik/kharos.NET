import { BUILDINGS, missileLift } from '@kharos/sim-ts'

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
  return {
    x: missile.fromX + headingX * flown,
    y: missile.fromY + headingY * flown,
    lift: missileLift(age, missile.blind ? Infinity : reach - flown),
    headingX,
    headingY,
  }
}

/** Ракета-перехватчик, какой её знает клиент: см. Chaser в components.ts. */
interface Chase {
  fromX: number
  fromY: number
  toX: number
  toY: number
  lift: number
  born: number
  arrive: number
}

/**
 * Где ракета-перехватчик в момент clock: как missileAt. Со станка она уходит круто вверх и к встрече выравнивается
 * на высоте ракеты: так её след читается как запуск с земли, а не как выстрел вдоль неё.
 */
export function chaserAt(chaser: Chase, clock: number) {
  const share = Math.min(1, Math.max(0, (clock - chaser.born) / Math.max(1, chaser.arrive - chaser.born)))
  return {
    x: chaser.fromX + (chaser.toX - chaser.fromX) * share,
    y: chaser.fromY + (chaser.toY - chaser.fromY) * share,
    lift: chaser.lift * (1 - (1 - share) ** 2),
  }
}
