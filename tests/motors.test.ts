import { expect, test } from 'bun:test'
import { MOTORS, MOTOR_NAMES, RUNNING, RUNNING_NAMES, motorLoop } from '../src/audio/motorSynth'
import { CRANK, HANDLING, IDLE_HOLD, createDrive, driveSound, stepDrive, type DriveEvent } from '../src/game/drive'

const RATE = 44100

test('петли моторов: конечные, не тишина, холостая и нагруженная одной длины', () => {
  for (const name of MOTOR_NAMES) {
    const idle = motorLoop(RATE, name, false)
    const load = motorLoop(RATE, name, true)
    expect(idle.length).toBe(load.length)
    for (const data of [idle, load]) {
      let energy = 0
      for (const value of data) energy += value * value
      expect(Number.isFinite(energy)).toBe(true)
      expect(Math.sqrt(energy / data.length)).toBeGreaterThan(0.05)
    }
    expect(idle).not.toEqual(load)
  }
  for (const name of RUNNING_NAMES) expect(RUNNING[name](RATE).length).toBeGreaterThan(RATE)
})

/** Едет по плану: [секунд, доля скорости]; возвращает события с их временем и езду в конце. */
const ride = (name: 'petrol' | 'diesel' | 'heavy', plan: [number, number][]) => {
  const drive = createDrive(false)
  const events: [DriveEvent, number][] = []
  const step = 1 / 60
  let time = 0
  const samples: { time: number; rpm: number; speed: number; gear: number; throttle: number }[] = []
  for (const [seconds, target] of plan) {
    for (let t = 0; t < seconds; t += step) {
      const event = stepDrive(drive, MOTORS[name], HANDLING[name], target, step)
      if (event) events.push([event, time])
      samples.push({ time, rpm: drive.rpm, speed: drive.speed, gear: drive.gear, throttle: drive.throttle })
      time += step
    }
  }
  return { drive, events, samples }
}

test('мотор заводится стартером, разгоняется по передачам, тормозит и глохнет, постояв', () => {
  const { drive, events, samples } = ride('diesel', [[1, 0], [6, 1], [IDLE_HOLD + 2, 0]])
  expect(events.map(([event]) => event)).toEqual(['start', 'brake'])
  // Пока крутит стартер, машина для звука стоит.
  const crank = samples.filter((sample) => sample.time > 1 && sample.time < 1 + CRANK.ignitionDiesel - 0.05)
  expect(crank.every((sample) => sample.speed === 0)).toBe(true)
  // Передачи только вверх, пока разгоняется, и на каждой обороты сначала падают, потом растут.
  const gears = samples.filter((sample) => sample.time < 7).map((sample) => sample.gear)
  expect(Math.max(...gears)).toBe(HANDLING.diesel.gears.length - 1)
  for (let i = 1; i < gears.length; i++) expect(gears[i]).toBeGreaterThanOrEqual(gears[i - 1])
  // На ровном ходу газ не в пол.
  const cruise = samples.find((sample) => Math.abs(sample.time - 6.5) < 0.01)!
  expect(cruise.speed).toBe(1)
  expect(cruise.throttle).toBeLessThan(0.7)
  expect(drive.phase).toBe('off')
  expect(driveSound(drive, MOTORS.diesel, 0).level).toBe(0)
})

test('бензиновый не шипит тормозом, а короткая заминка на ходу — не остановка', () => {
  const { events } = ride('petrol', [[3, 1], [0.1, 0], [3, 1], [2, 0]])
  expect(events.map(([event]) => event)).toEqual(['start'])
})

test('гусеничный скрипит, вертясь на месте, и не скрипит на ходу', () => {
  const standing = ride('heavy', [[3, 1], [2, 0]]).drive
  expect(driveSound(standing, MOTORS.heavy, 1).squeal).toBeGreaterThan(0.9)
  const moving = ride('heavy', [[5, 1]]).drive
  expect(driveSound(moving, MOTORS.heavy, 1).squeal).toBe(0)
})

test('летающий не глохнет и раскручивает винты со взлёта', () => {
  const drive = createDrive(true)
  for (let t = 0; t < 30; t += 1 / 60) expect(stepDrive(drive, MOTORS.rotor, HANDLING.air, 0, 1 / 60)).toBeNull()
  expect(drive.phase).toBe('run')
  expect(drive.spool).toBe(1)
  expect(driveSound(drive, MOTORS.rotor, 0).level).toBeGreaterThan(0.9)
})
