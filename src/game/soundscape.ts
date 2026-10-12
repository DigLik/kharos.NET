import type { Audio } from '../audio/audio'
import type { SoundName } from '../audio/sounds'
import type { Entity } from '../ecs'
import { Blast, Building, Position, Shot, Unit, buildingSpec, unitSpec, type WeaponType } from '../sim'
import { nearness, sideOf } from './hearing'
import type { Scene } from './scene'
import type { createShake } from './shake'

/** Какой звук у выстрела каждого оружия и насколько он громок. */
const SHOT_SOUNDS: Record<WeaponType, { name: SoundName; volume: number }> = {
  rifle: { name: 'rifle', volume: 0.35 },
  machinegun: { name: 'machinegun', volume: 0.25 },
  cannon: { name: 'cannon', volume: 0.8 },
  artillery: { name: 'artillery', volume: 1 },
  bomb: { name: 'bombFall', volume: 0.5 },
  launcher: { name: 'launcher', volume: 0.45 },
  laser: { name: 'laser', volume: 0.35 },
  arc: { name: 'arc', volume: 0.5 },
  flame: { name: 'flame', volume: 0.4 },
  flak: { name: 'flak', volume: 0.45 },
  coreGun: { name: 'laser', volume: 0.6 },
}
/**
 * Лучевое оружие бьёт сразу и без взрыва: попадание звучит само, в точке цели — лазер прожигает, огонь потрескивает,
 * разряд щёлкает.
 */
const HIT_SOUNDS: Partial<Record<WeaponType, SoundName>> = { laser: 'sizzle', coreGun: 'sizzle', flame: 'burn', arc: 'zap' }
const BEAM_HIT_VOLUME = 0.3
/** Взрывы: меньше SMALL_BLAST — попадание пули, оно щёлкает тихо; от BIG_BLAST — гибель техники или здания. */
const SMALL_BLAST = 0.3
const HIT_VOLUME = 0.12
/** Погибший — то, что пропало в кадр взрыва не дальше этого от него, в тайлах, или в его размер. */
const DEATH_REACH = 1
/** Громкость скрежета подбитой машины и обвала здания поверх взрыва. */
const WRECK_VOLUME = 0.6
const COLLAPSE_VOLUME = 0.8
const BIG_BLAST = 1.3
/** Громкость взрыва: base и сколько добавляет каждый тайл его размера. Взрывы громче выстрелов — выше единицы. */
const BLAST_VOLUME = 1
const BLAST_VOLUME_PER_SIZE = 0.6
/** Тряска от взрыва на тайл его размера и самая сильная от одного взрыва; выстрел пушки — как взрыв такого размера. */
const BLAST_SHAKE = 0.3
const MAX_BLAST_SHAKE = 0.8
const CANNON_SHAKE_SIZE = 0.3
/**
 * Докуда достаёт тряска от взрыва, в тайлах от середины экрана: SHAKE_REACH и ещё SHAKE_REACH_PER_SIZE на тайл
 * размера — большой взрыв чувствуется дальше. К краю досягаемости тряска слабеет как квадрат.
 */
const SHAKE_REACH = 6
const SHAKE_REACH_PER_SIZE = 8
/** Чаще этого, в секундах, один и тот же звук не повторяется: сотня винтовок звучит как одна очередь, а не как гул. */
const MIN_GAP: Partial<Record<SoundName, number>> = {
  rifle: 0.04, machinegun: 0.035, cannon: 0.08, launcher: 0.06, laser: 0.06, arc: 0.08, blast: 0.05, bigBlast: 0.1,
  flame: 0.12, flak: 0.06, artillery: 0.1, bombFall: 0.15, ricochet: 0.05, wreck: 0.1, collapse: 0.2, sizzle: 0.06, burn: 0.15, zap: 0.05,
}
/** За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана. */
const HEARING = 0.8
/**
 * Ветер полёта: с какой скорости камеры над картой, тайлов в секунду, он начинается и на какой свистит во всю силу.
 * Между ними — по логарифму: клавиши у земли едва шелестят, издалека и с Shift уже дуют, а бросок к группе свистит.
 */
const FLIGHT_START = 20
const FLIGHT_FULL = 500
/** Ветер погоды, тайлов в секунду, при котором фон дует во всю силу. */
const STRONG_WIND = 2.5

/**
 * Звуки мира. Раз в кадр смотрит, какие выстрелы и взрывы появились в мире, и даёт им голос: громкость — по тому,
 * насколько они далеко от середины экрана, сторона — по тому, левее они или правее. Взрывы и выстрелы пушек
 * ещё и трясут камеру: тем сильнее, чем взрыв больше и ближе к середине экрана, а на экране — чем крупнее зум.
 * Под боем — фон по погоде и высоте камеры, а когда камера несётся над картой, свистит ветер полёта.
 */
export function createSoundscape(scene: Scene, audio: Audio, shake: ReturnType<typeof createShake>) {
  let shots = new Map<Entity, number>()
  let blasts = new Map<Entity, number>()
  /** Где в прошлом кадре было то, что может погибнуть, и что это: здание, машина или пехотинец. */
  let bodies = new Map<Entity, { x: number; y: number; kind: 'building' | 'machine' | 'soldier' }>()
  const last = new Map<SoundName, number>()
  let clock = 0

  /** Толчок от взрыва размера size в (x, y): чем больше и ближе к середине экрана, тем сильнее. */
  const jolt = (size: number, x: number, y: number) => {
    const distance = Math.hypot(x - scene.camera.x, y - scene.camera.y)
    const near = Math.max(0, 1 - distance / (SHAKE_REACH + size * SHAKE_REACH_PER_SIZE))
    shake.add(Math.min(MAX_BLAST_SHAKE, size * BLAST_SHAKE) * near * near)
  }

  const play = (name: SoundName, volume: number, x: number, y: number) => {
    const level = volume * nearness(scene, x, y, HEARING)
    if (level <= 0.01 || clock - (last.get(name) ?? -Infinity) < (MIN_GAP[name] ?? 0)) return
    last.set(name, clock)
    audio.play(name, level, sideOf(scene, x) * 0.8)
  }

  return {
    /** speed — как быстро камера несётся над картой, тайлов в секунду. */
    update(seconds: number, speed: number) {
      clock += seconds
      const { world } = scene.sim
      // Новое — то, чего не было в прошлом кадре, или что моложе прежнего с тем же номером.
      const nextShots = new Map<Entity, number>()
      for (const [entity, shot] of world.query(Shot)) {
        const age = shots.get(entity)
        nextShots.set(entity, shot.age)
        // Перескоки разряда — те же выстрелы в тот же миг: их глушит MIN_GAP.
        if (age !== undefined && shot.age >= age) continue
        const sound = SHOT_SOUNDS[shot.weapon]
        play(sound.name, sound.volume, shot.fromX, shot.fromY)
        const hit = HIT_SOUNDS[shot.weapon]
        if (hit && !shot.blocked) play(hit, BEAM_HIT_VOLUME, shot.toX, shot.toY)
        if (shot.weapon === 'cannon') jolt(CANNON_SHAKE_SIZE, shot.fromX, shot.fromY)
      }
      shots = nextShots

      // Погибшее пропадает в тот же кадр, что появляется взрыв на его месте.
      const nextBodies = new Map<Entity, { x: number; y: number; kind: 'building' | 'machine' | 'soldier' }>()
      for (const [entity, unit, position] of world.query(Unit, Position)) {
        nextBodies.set(entity, { x: position.x, y: position.y, kind: unitSpec(unit.type).kind === 'infantry' ? 'soldier' : 'machine' })
      }
      for (const [entity, building, position] of world.query(Building, Position)) {
        const spec = buildingSpec(building.type)
        nextBodies.set(entity, { x: position.x + spec.width / 2, y: position.y + spec.height / 2, kind: 'building' })
      }
      const fallen = [...bodies].filter(([entity]) => !nextBodies.has(entity)).map(([, body]) => body)
      bodies = nextBodies

      const nextBlasts = new Map<Entity, number>()
      for (const [entity, blast, position] of world.query(Blast, Position)) {
        const age = blasts.get(entity)
        nextBlasts.set(entity, blast.age)
        if (age !== undefined && blast.age >= age) continue
        if (blast.size < SMALL_BLAST) {
          play('ricochet', HIT_VOLUME, position.x, position.y)
          continue
        }
        const big = blast.size >= BIG_BLAST
        play(big ? 'bigBlast' : 'blast', BLAST_VOLUME + blast.size * BLAST_VOLUME_PER_SIZE, position.x, position.y)
        jolt(blast.size, position.x, position.y)
        const reach = Math.max(DEATH_REACH, blast.size)
        const body = fallen.find((body) => Math.hypot(body.x - position.x, body.y - position.y) <= reach)
        if (body?.kind === 'building') play('collapse', COLLAPSE_VOLUME + blast.size * 0.2, position.x, position.y)
        else if (body?.kind === 'machine') play('wreck', WRECK_VOLUME, position.x, position.y)
      }
      blasts = nextBlasts

      audio.flight(seconds, Math.log(speed / FLIGHT_START) / Math.log(FLIGHT_FULL / FLIGHT_START))
      const { weather, camera } = scene
      audio.ambience(seconds, {
        wind: Math.hypot(weather.windX, weather.windY) / STRONG_WIND,
        precipitation: weather.precipitation,
        light: weather.light,
        altitude: camera.altitude,
      })
    },
  }
}
