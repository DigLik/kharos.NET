import { START_CRANK, type Motor } from '../audio/motorSynth'
import type { MotorSound } from '../audio/motorVoice'

/**
 * Езда машины для звука. Хост двигает юнит сразу на полной скорости и останавливает разом, а мотор так не умеет:
 * здесь у машины своя скорость, которая догоняет настоящую, — с ней и звучат разгон по передачам, ровный ход,
 * сброс газа и остановка. Постояв на холостых, мотор глохнет; тронувшись снова, заводится стартером.
 * Летающие не глохнут: винты крутятся, пока юнит в воздухе.
 */

/** Как машина разгоняется и тормозит: доля полной скорости в секунду. */
export interface Handling {
  accel: number
  brake: number
  /** Где кончается каждая передача, в долях полной скорости. Последняя — с запасом на дорогу, где едут быстрее. */
  gears: number[]
}

// Разгон короткий: машина на карте трогается сразу на полной скорости, и долгий звук разгона от неё отставал бы.
// Лёгкая набирает ход за полсекунды, грузовик и гусеничные — меньше чем за секунду.
export const HANDLING = {
  petrol: { accel: 2.2, brake: 2.5, gears: [0.22, 0.45, 0.72, 1.6] },
  diesel: { accel: 1.4, brake: 1.8, gears: [0.3, 0.6, 1.6] },
  heavy: { accel: 1.2, brake: 1.6, gears: [0.35, 0.7, 1.4] },
  air: { accel: 0.6, brake: 0.8, gears: [] },
} satisfies Record<string, Handling>

/** Что случилось за шаг: голос играет разовый звук. */
export type DriveEvent = 'start' | 'brake'

export interface Drive {
  /** off — заглушен, crank — крутит стартер, run — работает. */
  phase: 'off' | 'crank' | 'run'
  /** Скорость машины для звука, доля полной. */
  speed: number
  /** Обороты: 0 — холостые, 1 — предел. */
  rpm: number
  /** Газ: 0 — отпущен, 1 — в пол. */
  throttle: number
  gear: number
  /** Сколько ещё секунд выжато сцепление: идёт переключение. */
  shift: number
  /** Сколько секунд в этой фазе: крутит стартер, стоит на холостых, заглушен. */
  timer: number
  /** Раскрутка винтов летающего, от 0 до 1. */
  spool: number
}

/** Столько секунд мотор стоит на холостых, а потом глохнет. */
export const IDLE_HOLD = 6
/** Сколько длится переключение передачи, в секундах. */
const SHIFT = 0.1
/** После переключения вверх обороты падают до этой доли. */
const SHIFT_DROP = 0.35
/** Медленнее этого, в долях полной скорости, машина считается стоящей: толчки соседей — не езда. */
const STILL = 0.06
/** Тормозя до остановки с такой скорости, машина шипит тормозом — когда проходит её. */
const BRAKE_FROM = 0.35
/** За сколько секунд винты летающего раскручиваются со взлёта. */
const SPOOL = 2.5
/** Шины визжат, когда машина поворачивает быстрее этой доли своего предела и едет быстрее этой доли скорости. */
const SCREECH_TURN = 0.5
const SCREECH_SPEED = 0.5
/** Как быстро обороты и газ догоняют нужные, в секундах. */
const RPM_LAG = 0.12
const THROTTLE_LAG = 0.08

/** Новая машина: наземная заглушена давно, летающая только взлетает. */
export const createDrive = (air: boolean): Drive => ({ phase: air ? 'run' : 'off', speed: 0, rpm: 0, throttle: 0, gear: 0, shift: 0, timer: air ? 0 : Infinity, spool: 0 })

const follow = (value: number, target: number, seconds: number, lag: number) => value + (target - value) * (1 - Math.exp(-seconds / lag))

/**
 * Шаг езды на seconds: target — с какой долей полной скорости юнит едет на самом деле. Возвращает разовое событие,
 * если оно случилось.
 */
export function stepDrive(drive: Drive, motor: Motor, handling: Handling, target: number, seconds: number): DriveEvent | null {
  let event: DriveEvent | null = null
  const air = !motor.start
  const moving = target > STILL
  drive.timer += seconds
  if (air) drive.spool = Math.min(1, drive.spool + seconds / SPOOL)

  if (drive.phase === 'off') {
    if (!moving) return null
    drive.phase = 'crank'
    drive.timer = 0
    event = 'start'
  }
  if (drive.phase === 'crank') {
    // Пока крутит стартер, машина для звука ещё стоит: тронется, когда мотор схватит, — с перегазовкой.
    if (drive.timer < START_CRANK[motor.start!]) return event
    drive.phase = 'run'
    drive.timer = 0
    // Схватил — перегазовка: обороты взлетают и садятся на холостые или идут в разгон.
    drive.rpm = 0.6
    drive.throttle = 1
  }

  const before = drive.speed
  drive.shift = Math.max(0, drive.shift - seconds)
  const wanted = moving ? target : 0
  if (wanted > drive.speed) {
    // Пока выжато сцепление, машина не разгоняется.
    if (!drive.shift) drive.speed = Math.min(wanted, drive.speed + handling.accel * seconds)
  } else drive.speed = Math.max(wanted, drive.speed - handling.brake * seconds)
  if (motor.brake && !moving && before >= BRAKE_FROM && drive.speed < BRAKE_FROM) event = 'brake'

  // Газ: разгон — в пол, ровный ход — чуть, сброс — отпущен. На переключении газ сброшен.
  const accelerating = wanted > drive.speed + 0.02
  const cruising = moving && !accelerating && wanted >= drive.speed - 0.02
  let throttle = accelerating ? 1 : cruising ? 0.35 + 0.25 * Math.min(1, drive.speed) : 0
  if (drive.shift) throttle = 0.1

  let rpm: number
  if (air) rpm = Math.min(1, drive.speed * 0.75 + throttle * 0.25)
  else {
    // Передача — по скорости: вверх на пределе своей, вниз с запасом, чтобы не дёргалась туда-сюда.
    const { gears } = handling
    while (drive.gear < gears.length - 1 && drive.speed >= gears[drive.gear]) {
      drive.gear++
      if (accelerating) {
        drive.shift = SHIFT
        drive.rpm = Math.min(drive.rpm, SHIFT_DROP + 0.1)
        throttle = 0.1
      }
    }
    while (drive.gear > 0 && drive.speed < gears[drive.gear - 1] * 0.8) drive.gear--
    const low = drive.gear ? gears[drive.gear - 1] : 0
    const share = Math.max(0, Math.min(1, (drive.speed - low) / (gears[drive.gear] - low)))
    rpm = drive.gear ? SHIFT_DROP + (1 - SHIFT_DROP) * share : share
    // Стоя, мотор держит холостые; на газу с места — чуть подвывает.
    if (drive.speed < STILL) rpm = accelerating ? 0.15 : 0
  }
  drive.rpm = follow(drive.rpm, rpm, seconds, drive.shift ? RPM_LAG / 2 : RPM_LAG)
  drive.throttle = follow(drive.throttle, throttle, seconds, THROTTLE_LAG)

  if (!air) {
    if (moving || drive.speed > STILL) drive.timer = 0
    else if (drive.timer >= IDLE_HOLD) {
      drive.phase = 'off'
      drive.gear = 0
      drive.timer = 0
    }
  }
  return event
}

/** Как звучит мотор при этой езде; volume, pan и clarity ставит тот, кто знает, где машина. turn — доля поворота. */
export function driveSound(drive: Drive, motor: Motor, turn: number): Omit<MotorSound, 'volume' | 'pan' | 'clarity'> {
  const air = !motor.start
  const running = drive.phase === 'run'
  // Заглохший мотор, сбегая с оборотов, опускается ниже холостых.
  const rate = running ? 1 + (motor.revs - 1) * drive.rpm : 0.6
  return {
    rate: air ? rate * (0.6 + 0.4 * drive.spool) : rate,
    load: running ? drive.throttle : 0,
    level: running ? (air ? 0.4 + 0.6 * drive.spool : 0.55 + 0.45 * drive.throttle) : 0,
    running: Math.min(1, drive.speed * 1.2),
    runningRate: drive.speed,
    // Гусеницы скрипят, когда машина вертится почти на месте; шины визжат в крутом повороте на ходу.
    squeal: !running ? 0
      : motor.running === 'clatter' ? Math.min(1, turn) * Math.max(0, 1 - drive.speed * 3)
      : motor.running === 'roll' ? Math.max(0, Math.min(1, (turn - SCREECH_TURN) / (1 - SCREECH_TURN))) * Math.max(0, drive.speed - SCREECH_SPEED) / (1 - SCREECH_SPEED)
      : 0,
  }
}
