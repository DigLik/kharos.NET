import { MOTOR_SOUNDS } from './motorSynth'
import { LOOPS, SOUNDS } from './synth'
import { WORLD_LOOPS, WORLD_SOUNDS } from './worldSynth'

/** Все разовые звуки игры: бой и интерфейс (synth.ts), техника (motorSynth.ts), здания и прочий мир (worldSynth.ts). */
export const ALL_SOUNDS = { ...SOUNDS, ...MOTOR_SOUNDS, ...WORLD_SOUNDS } satisfies Record<string, (rate: number, variant: number) => Float32Array>
export type SoundName = keyof typeof ALL_SOUNDS
export const ALL_SOUND_NAMES = Object.keys(ALL_SOUNDS) as SoundName[]

/** Все фоновые петли: каждая звучит одним голосом, громкость задаёт тот, кто знает, сколько её источников. */
export const ALL_LOOPS = { ...LOOPS, ...WORLD_LOOPS } satisfies Record<string, (rate: number) => Float32Array>
export type LoopName = keyof typeof ALL_LOOPS
export const ALL_LOOP_NAMES = Object.keys(ALL_LOOPS) as LoopName[]

/** Звуки интерфейса: кнопки, приказы, уведомления. */
export const INTERFACE_SOUNDS = new Set<SoundName>([
  'click', 'hover', 'open', 'deny', 'select', 'selectBuilding', 'place', 'order', 'attackOrder', 'attacked', 'spotted', 'found', 'nuke',
  'unitReady', 'researched', 'upgraded', 'warheadReady', 'reward', 'countdown', 'brownout',
  'delivered', 'protectionOver', 'coins', 'gift', 'defeat', 'surrender', 'chat', 'chatSystem',
])
/** Звуки мира: работа техники и зданий. Остальные разовые звуки — бой. */
export const WORLD_SOUND_NAMES = new Set<SoundName>([
  ...(Object.keys(MOTOR_SOUNDS) as SoundName[]),
  'doorsLocked', 'liftoff', 'gate', 'built', 'deploy', 'rollout', 'crates', 'paved', 'radar', 'shieldDown', 'shieldUp',
  'step', 'stepSand', 'stepMud', 'stepHard', 'shipLaunch', 'shipLand', 'depleted', 'powerOn', 'powerOff', 'cargo',
])
