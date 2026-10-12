import { MOTOR_SOUNDS } from './motorSynth'
import { SOUNDS } from './synth'

/** Все разовые звуки игры: бой и интерфейс (synth.ts) и техника (motorSynth.ts). */
export const ALL_SOUNDS = { ...SOUNDS, ...MOTOR_SOUNDS } satisfies Record<string, (rate: number, variant: number) => Float32Array>
export type SoundName = keyof typeof ALL_SOUNDS
export const ALL_SOUND_NAMES = Object.keys(ALL_SOUNDS) as SoundName[]

/** Звуки интерфейса: кнопки, приказы, уведомления. */
export const INTERFACE_SOUNDS = new Set<SoundName>(['click', 'hover', 'open', 'deny', 'select', 'selectBuilding', 'place', 'order', 'attackOrder', 'attacked', 'spotted', 'found', 'nuke'])
/** Звуки мира: работа техники и зданий. Остальные разовые звуки — бой. */
export const WORLD_SOUNDS = new Set<SoundName>(Object.keys(MOTOR_SOUNDS) as SoundName[])
