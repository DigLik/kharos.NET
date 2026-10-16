import { MOTOR_NAMES, RUNNING, RUNNING_NAMES, motorLoop, type MotorName, type RunningName } from './motorSynth'
import { ALL_LOOPS, ALL_LOOP_NAMES, ALL_SOUNDS, ALL_SOUND_NAMES, type LoopName, type SoundName } from './sounds'
import { DISTANT_NAMES, DISTANT_SOUNDS, SOUND_VARIANTS, noiseLoop, reverbImpulse, type DistantName, type NoiseColor } from './synth'

/**
 * Частота, на которой синтезируются все звуки. Звук игры и музыка меню просят у браузера контекст с ней же: тогда
 * готовые записи подходят как есть, а к частоте устройства звук приводит сам браузер.
 */
export const SYNTH_RATE = 48000
/** Сколько секунд длится эхо фона игры и эхо музыки меню. */
const REVERB_SECONDS = 4
const MUSIC_REVERB_SECONDS = 5
/** Петли шума для ветра: какого цвета и сколько секунд. Короче — слышно, что петля повторяется. */
export const NOISE_SECONDS: [NoiseColor, number][] = [
  ['white', 3],
  ['pink', 8],
  ['brown', 8],
]

/** Имена записей в наборе звуков. */
export const soundKey = (name: SoundName, variant: number) => `sound:${name}:${variant}`
export const loopKey = (name: LoopName) => `loop:${name}`
export const runningKey = (name: RunningName) => `running:${name}`
export const motorKey = (name: MotorName, load: boolean) => `motor:${name}:${load ? 'load' : 'idle'}`
export const noiseKey = (color: NoiseColor) => `noise:${color}`
export const distantKey = (name: DistantName, variant: number) => `distant:${name}:${variant}`
export const REVERB_KEY = 'reverb'
export const MUSIC_REVERB_KEY = 'reverb:music'

/** Запись набора: каналы звука на частоте rate. */
type Make = (rate: number) => Float32Array[]

/** Все записи звука игры и музыки меню по именам. Работает без браузера: набор синтезирует воркер (см. loadSounds.ts). */
export function soundPieces(): Map<string, Make> {
  const pieces = new Map<string, Make>()
  for (const name of ALL_SOUND_NAMES) {
    for (let variant = 0; variant < SOUND_VARIANTS; variant++) pieces.set(soundKey(name, variant), (rate) => [ALL_SOUNDS[name](rate, variant)])
  }
  for (const name of ALL_LOOP_NAMES) pieces.set(loopKey(name), (rate) => [ALL_LOOPS[name](rate)])
  for (const name of RUNNING_NAMES) pieces.set(runningKey(name), (rate) => [RUNNING[name](rate)])
  for (const name of MOTOR_NAMES) {
    for (const load of [false, true]) pieces.set(motorKey(name, load), (rate) => [motorLoop(rate, name, load)])
  }
  // Петли шума — в стерео: у каждого уха своя, и ветер обступает со всех сторон.
  for (const [color, seconds] of NOISE_SECONDS) pieces.set(noiseKey(color), (rate) => [noiseLoop(rate, seconds, 1, color), noiseLoop(rate, seconds, 2, color)])
  pieces.set(REVERB_KEY, (rate) => reverbImpulse(rate, REVERB_SECONDS, 1))
  for (const name of DISTANT_NAMES) {
    for (let variant = 0; variant < SOUND_VARIANTS; variant++) pieces.set(distantKey(name, variant), (rate) => [DISTANT_SOUNDS[name](rate, variant)])
  }
  pieces.set(MUSIC_REVERB_KEY, (rate) => reverbImpulse(rate, MUSIC_REVERB_SECONDS, 3))
  return pieces
}
