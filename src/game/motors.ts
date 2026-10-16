import type { Audio } from '../audio/audio'
import { MOTORS, MOTOR_NAMES, type Motor, type MotorName } from '../audio/motorSynth'
import type { MotorSound, MotorVoice } from '../audio/motorVoice'
import type { SoundName } from '../audio/sounds'
import type { Entity } from '@kharos/ecs'
import { Position, Unit, unitSpec, wrap, type UnitType } from '../sim'
import { HANDLING, createDrive, driveSound, stepDrive, type Drive, type Handling } from './drive'
import { nearness, sideOf } from './hearing'
import type { Scene } from './scene'

/** Какой мотор у юнита; у пехоты мотора нет — она шагает. */
const UNIT_MOTORS: Record<UnitType, MotorName | null> = {
  infantry: null, rocketeer: null, flamer: null,
  builder: 'petrol', buggy: 'petrol', flak: 'petrol', lancer: 'petrol',
  truck: 'diesel', artillery: 'diesel', carrier: 'diesel', supplier: 'diesel',
  mcv: 'heavy', tank: 'heavy', tesla: 'heavy',
  drone: 'props', airTruck: 'rotor', gunship: 'rotor', bomber: 'jet',
}
/** Тон машины против её мотора: мелкие выше, громоздкие ниже. */
const PITCH: Partial<Record<UnitType, number>> = { builder: 1.12, buggy: 1.18, lancer: 0.95, carrier: 0.9, supplier: 0.85, mcv: 0.85, tesla: 1.06, gunship: 0.92 }
/** Громкость мотора вблизи: тяжёлые громче. */
const LOUDNESS: Record<MotorName, number> = { petrol: 0.3, diesel: 0.38, heavy: 0.48, rotor: 0.4, props: 0.2, jet: 0.38 }
const HANDLINGS: Record<MotorName, Handling> = { petrol: HANDLING.petrol, diesel: HANDLING.diesel, heavy: HANDLING.heavy, rotor: HANDLING.air, props: HANDLING.air, jet: HANDLING.air }
/** Сколько машин слышно каждую своим голосом; остальных — общим голосом толпы их мотора. */
const VOICES = 6
/** Звучащая машина держит голос, пока её не перекричат во столько раз: голоса не перескакивают туда-сюда. */
const KEEP = 1.4
/** За краем экрана мотор стихает на этом расстоянии, в долях полуширины экрана. */
const HEARING = 0.6
/**
 * Толпа: сколько вблизи едущих набирают полную громкость — десять танков громче одного, но не вдесятеро.
 * Громкость — 1 − e^(−сумма/SATURATE). Толпа звучит глуше и тише своих голосов.
 */
const SATURATE = 3
const CROWD_LEVEL = 0.6
const CROWD_CLARITY = 0.4
/** Громкость стартера и тормоза против мотора. */
const START_VOLUME = 0.5
const BRAKE_VOLUME = 0.7
/** Чаще этого, в секундах, стартер и тормоз не звучат: колонна по одному приказу заводится не хором. */
const EVENT_GAP = 0.25
/** Медленнее этого юнит стоит: тики, где его не сдвинули, — ещё не остановка, если они не дольше STOP_HOLD секунд. */
const STILL = 0.06
const STOP_HOLD = 0.3
/** Быстрее этого, в долях своей скорости, юнит не ездит: такой скачок — перенос, а не ход. */
const JUMP = 2.2
/** Шаги пехоты — общая петля: громкость при «полном» звучании. */
const STEPS_LEVEL = 0.16

interface Car {
  type: UnitType
  motor: MotorName
  drive: Drive
  /** Где и когда юнит был на прошлом тике. */
  x: number
  y: number
  facing: number
  tick: number
  /** С какой долей полной скорости он едет и поворачивает на самом деле. */
  target: number
  turn: number
  /** Сколько секунд он не сдвигается. */
  still: number
  /** Своя высота тона: две одинаковые машины рядом звучат по-разному. */
  pitch: number
  voice: MotorVoice | null
}

/** Средние по толпе одного мотора: из них звучит её голос. */
interface Crowd {
  voice: MotorVoice | null
  sum: number
  side: number
  rate: number
  load: number
  running: number
}

/**
 * Моторы техники. Каждая машина — своя езда (drive.ts): заводится стартером, разгоняется по передачам, едет, сбрасывает
 * газ, тормозит, стоит на холостых и глохнет. Ближайшие к середине экрана VOICES машин слышны каждая своим голосом,
 * со своей стороны и со своими оборотами; остальные — общим голосом толпы своего мотора. Пехота шагает общей петлёй.
 */
export function createMotors(scene: Scene, audio: Audio) {
  const cars = new Map<Entity, Car>()
  const crowds = new Map<MotorName, Crowd>()
  let steps = 0
  let stepsSide = 0
  let clock = 0
  const last = new Map<'start' | 'brake', number>()
  const event = (kind: 'start' | 'brake', name: SoundName, volume: number, side: number) => {
    if (volume <= 0.01 || clock - (last.get(kind) ?? -Infinity) < EVENT_GAP) return
    last.set(kind, clock)
    audio.play(name, volume, side * 0.8)
  }

  const release = (car: Car) => {
    car.voice?.release()
    car.voice = null
  }

  /** Новый замер хода: юнит сдвинулся на тике. Скорость — доля его полной, поворот — доля его наибыстрейшего. */
  const measure = (car: Car, x: number, y: number, facing: number, tick: number, step: number, seconds: number) => {
    if (tick === car.tick) return
    const elapsed = (tick - car.tick) * step
    const spec = unitSpec(car.type)
    const speed = Math.hypot(x - car.x, y - car.y) / elapsed / spec.speed
    const turn = Math.abs(wrap(facing - car.facing)) / elapsed / spec.turn
    car.x = x
    car.y = y
    car.facing = facing
    car.tick = tick
    car.turn = turn
    if (speed > JUMP) return
    if (speed > STILL) {
      car.still = 0
      car.target = speed
    } else {
      car.still += Math.max(elapsed, seconds)
      if (car.still >= STOP_HOLD) car.target = 0
    }
  }

  return {
    update(seconds: number) {
      clock += seconds
      const { world, time } = scene.sim
      const seen = new Set<Entity>()
      const heard: { car: Car; score: number; near: number; side: number; sound: Omit<MotorSound, 'volume' | 'pan' | 'clarity'> }[] = []
      steps = 0
      stepsSide = 0

      for (const [entity, unit, position] of world.query(Unit, Position)) {
        const near = nearness(scene, position.x, position.y, HEARING)
        const side = sideOf(scene, position.x)
        const name = UNIT_MOTORS[unit.type]
        if (!name) {
          if (near > 0 && Math.abs(position.x - unit.prevX) + Math.abs(position.y - unit.prevY) >= 1e-3) {
            steps += near
            stepsSide += near * side
          }
          continue
        }
        seen.add(entity)
        let car = cars.get(entity)
        if (!car) {
          car = {
            type: unit.type, motor: name, drive: createDrive(!MOTORS[name].start), x: position.x, y: position.y, facing: unit.facing, tick: time.tick,
            target: 0, turn: 0, still: 0, pitch: (PITCH[unit.type] ?? 1) * (0.96 + ((entity * 0.618034) % 1) * 0.08), voice: null,
          }
          cars.set(entity, car)
        }
        measure(car, position.x, position.y, unit.facing, time.tick, time.step, seconds)
        const motor: Motor = MOTORS[name]
        const happened = stepDrive(car.drive, motor, HANDLINGS[name], car.target, seconds)
        const loudness = LOUDNESS[name] * near
        if (happened === 'start' && motor.start) event('start', motor.start, loudness * START_VOLUME, side)
        if (happened === 'brake') event('brake', 'airBrake', loudness * BRAKE_VOLUME, side)
        // Заглохший слышен ещё секунду: мотор сбегает с оборотов.
        const { drive } = car
        if (near <= 0 || (drive.phase === 'off' && drive.timer > 1)) {
          release(car)
          continue
        }
        const sound = driveSound(drive, motor, Math.min(1, car.turn))
        sound.rate *= car.pitch
        heard.push({ car, near, side, sound, score: loudness * (0.6 + 0.4 * drive.throttle) * (car.voice ? KEEP : 1) })
      }

      for (const [entity, car] of cars) {
        if (seen.has(entity)) continue
        release(car)
        cars.delete(entity)
      }

      // Громче всех слышные — своими голосами, остальные — толпой.
      heard.sort((a, b) => b.score - a.score)
      for (const crowd of crowds.values()) {
        crowd.sum = crowd.side = crowd.rate = crowd.load = crowd.running = 0
      }
      heard.forEach(({ car, near, side, sound }, index) => {
        if (index < VOICES) car.voice ??= audio.motor(car.motor, MOTORS[car.motor].running, !!MOTORS[car.motor].running)
        else release(car)
        if (car.voice) {
          car.voice.set({ ...sound, volume: LOUDNESS[car.motor] * near, pan: side * 0.8, clarity: near })
          return
        }
        if (sound.level <= 0) return
        let crowd = crowds.get(car.motor)
        if (!crowd) crowds.set(car.motor, (crowd = { voice: null, sum: 0, side: 0, rate: 0, load: 0, running: 0 }))
        crowd.sum += near
        crowd.side += near * side
        crowd.rate += near * sound.rate
        crowd.load += near * sound.load
        crowd.running += near * sound.running
      })
      for (const name of MOTOR_NAMES) {
        const crowd = crowds.get(name)
        if (!crowd) continue
        if (!crowd.sum) {
          crowd.voice?.release()
          crowd.voice = null
          continue
        }
        crowd.voice ??= audio.motor(name, MOTORS[name].running, false)
        crowd.voice?.set({
          rate: crowd.rate / crowd.sum,
          load: crowd.load / crowd.sum,
          level: 1,
          running: crowd.running / crowd.sum,
          runningRate: crowd.running / crowd.sum,
          squeal: 0,
          volume: LOUDNESS[name] * CROWD_LEVEL * (1 - Math.exp(-crowd.sum / SATURATE)),
          pan: (crowd.side / crowd.sum) * 0.8,
          clarity: CROWD_CLARITY,
        })
      }
      audio.loop('steps', STEPS_LEVEL * (1 - Math.exp(-steps / SATURATE)), steps ? stepsSide / steps : 0)
    },
    destroy() {
      for (const car of cars.values()) release(car)
      for (const crowd of crowds.values()) crowd.voice?.release()
    },
  }
}
