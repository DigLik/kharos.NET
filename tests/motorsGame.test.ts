import { expect, test } from 'bun:test'
import type { Audio } from '../src/audio/audio'
import type { MotorSound } from '../src/audio/motorVoice'
import type { SoundName } from '../src/audio/sounds'
import { Camera } from '../src/game/camera'
import { createMotors } from '../src/game/motors'
import type { Scene } from '../src/game/scene'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Position, createSim } from '@kharos/sim-ts'
import { spawnUnit } from '@kharos/sim-ts/units'

/** Моторы над миром без браузера: камера в (0, 0), звук записывает, что ему велели. */
function setup() {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  const camera = new Camera()
  camera.width = 1280
  camera.height = 800
  camera.zoomTo(32)
  camera.centerOn(0, 0)
  const played: SoundName[] = []
  const voices: { sound: MotorSound | null; released: boolean }[] = []
  const audio = {
    play: (name: SoundName) => played.push(name),
    loop: () => {},
    motor: () => {
      const voice = { sound: null as MotorSound | null, released: false }
      voices.push(voice)
      return { set: (sound: MotorSound) => (voice.sound = sound), release: () => (voice.released = true) }
    },
  } as unknown as Audio
  const motors = createMotors({ sim, player: 1, camera } as unknown as Scene, audio)
  return { sim, played, voices, motors }
}

test('танк заводится, тронувшись, звучит своим голосом, встав — шипит тормозом и глохнет', () => {
  const { sim, played, voices, motors } = setup()
  const tank = spawnUnit(sim, 'tank', 1, 2, 2)
  const frame = 1 / 60
  const step = sim.time.step
  motors.update(frame)
  expect(played).toEqual([])
  expect(voices).toEqual([])
  // Едет три секунды на полной скорости: тик за тиком.
  const position = sim.world.get(tank, Position)!
  for (let t = 0; t < 3; t += frame) {
    if (t % step < frame) {
      ;(sim.time as { tick: number }).tick++
      position.x += 2.2 * step
    }
    motors.update(frame)
  }
  expect(played).toEqual(['ignitionDiesel'])
  expect(voices.length).toBe(1)
  expect(voices[0].sound!.level).toBeGreaterThan(0.5)
  expect(voices[0].sound!.running).toBeGreaterThan(0.5)
  // Встал: тормоз, потом холостые, потом заглох — и голос отпущен.
  for (let t = 0; t < 10; t += frame) {
    if (t % step < frame) (sim.time as { tick: number }).tick++
    motors.update(frame)
  }
  expect(played).toEqual(['ignitionDiesel', 'airBrake'])
  expect(voices[0].released).toBe(true)
})
