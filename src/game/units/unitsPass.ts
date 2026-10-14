import { setBlend } from '../../gl'
import { uploadAtlas, type AtlasFrame, type PackedAtlas, type Sprite } from '../../render/atlas'
import type { Pixmap } from '../../render/pixmap'
import type { Pass } from '../../render/renderer'
import { createSpriteProgram, createSprites } from '../../render/sprites'
import type { Entity } from '../../ecs'
import {
  Armed, Attached, Building, Converting, Ghost, Owner, Position, TURN, TURRET_TYPES, Turret, UNIT_TYPES, Unit, WEAPONS, alliesOf, flies, weaponOf, wrap, type ShotKind, type TurretType,
  type UnitType,
} from '../../sim'
import type { Scene } from '../scene'
import { GAIT_PHASES, GAIT_STEP, MASK_TEAM, TEAM_NAMES, TEAM_PALETTE, TURRET_ART, TURRET_FRAME, UNIT_ART, UNIT_DIRECTIONS, UNIT_FRAME, UNIT_GAITS, UNIT_LIGHTS, type Team } from './unitArt'

/** Сторона спрайта юнита в тайлах. */
const SPRITE_TILES = UNIT_FRAME / 16
const TURRET_TILES = TURRET_FRAME / 16
const SHADOW_ALPHA = 0.4
/** Непрозрачность призрака юнита в редакторе. */
const GHOST_ALPHA = 0.55
/** Тень — силуэт юнита, сдвинутый вправо вниз на столько тайлов. */
const SHADOW_SHIFT = 2 / 16
/** Летающий высоко: его тень бледнее и лежит дальше. */
const AIR_SHADOW_ALPHA = 0.25
const AIR_SHADOW_SHIFT = 10 / 16
/** На сколько тайлов за край экрана юнит ещё рисуется: его самого не видно, но луч фар дотягивается. */
const LIGHT_REACH = 7
/** На сколько пикселей спрайта выстрел отбрасывает стрелка или турель назад, смотря чем он стреляет. */
const RECOIL: Record<ShotKind, number> = { bullet: 0.7, rocket: 1.2, shell: 2.5, laser: 1, arc: 0, flame: 0 }
/** За сколько тиков после выстрела ствол возвращается на место; у скорострельных — до следующего выстрела. */
const RECOIL_TICKS = 4
/** Счётчики пути юнитов, не попадавших на экран столько кадров, забываются. */
const FORGET_FRAMES = 120

/** Сколько кадров анимации хода у юнита этого вида. */
const phasesOf = (type: UnitType) => (UNIT_GAITS[type] === 'air' ? 1 : GAIT_PHASES)

/** Где рисовать юнит в этом кадре: между местом тик назад и нынешним, по доле тика alpha. */
export function drawnPosition(position: { x: number; y: number }, unit: { prevX: number; prevY: number }, alpha: number) {
  return {
    x: unit.prevX + (position.x - unit.prevX) * alpha,
    y: unit.prevY + (position.y - unit.prevY) * alpha,
  }
}

/** Куда юнит смотрит в этом кадре: между прошлым и нынешним углом по короткой дуге. */
export function drawnFacing(unit: { facing: number; prevFacing: number }, alpha: number) {
  return unit.prevFacing + wrap(unit.facing - unit.prevFacing) * alpha
}

/**
 * Картинки юнитов и турелей: атлас и листы в нём по порядку — у каждого листа по кадру на направление. Листы общие
 * для всех сторон: цвет стороны — маска (MASK_TEAM), его подставляет шейдер.
 */
export interface UnitsArt {
  atlas: PackedAtlas
  sheets: string[]
}

/** Листы каждого вида и кадра анимации — все направления подряд; turret — у турелей их по одному кадру. */
export function unitsSprites(): { sprites: Sprite[]; sheets: string[] } {
  const sprites: Sprite[] = []
  const sheets: string[] = []
  const sheet = (key: string, frame: number, draw: (image: Pixmap, angle: number) => void) => {
    for (let direction = 0; direction < UNIT_DIRECTIONS; direction++) {
      sprites.push({
        width: frame,
        height: frame,
        draw(image) {
          image.originX = image.originY = frame / 2
          draw(image, (direction / UNIT_DIRECTIONS) * TURN)
        },
      })
    }
    sheets.push(key)
  }
  for (const type of UNIT_TYPES) {
    for (let phase = 0; phase < phasesOf(type); phase++) {
      sheet(`${type}:${phase}`, UNIT_FRAME, (image, angle) => UNIT_ART[type](image, angle, MASK_TEAM, phase))
    }
  }
  for (const type of TURRET_TYPES) {
    sheet(`turret:${type}`, TURRET_FRAME, (image, angle) => TURRET_ART[type](image, angle, MASK_TEAM, 0))
  }
  return { sprites, sheets }
}

/**
 * Проходы юнитов и турелей: наземные юниты рисуются под зданиями, установленные на зданиях турели — поверх
 * своих оснований, а летающие юниты — выше них. Картинки у проходов общие.
 */
export function createUnitsPasses(gl: WebGL2RenderingContext, scene: Scene, art: UnitsArt): { ground: Pass; emplacements: Pass; air: Pass } {
  const atlas = uploadAtlas(gl, art.atlas)
  const frames = new Map<string, AtlasFrame[]>()
  art.sheets.forEach((key, i) => frames.set(key, atlas.frames.slice(i * UNIT_DIRECTIONS, (i + 1) * UNIT_DIRECTIONS)))
  const framesOf = (type: UnitType, phase: number) => frames.get(`${type}:${phase}`)!
  const turretFramesOf = (type: TurretType) => frames.get(`turret:${type}`)!
  /** Номер кадра поворота для угла. */
  const directionOf = (facing: number) => ((Math.round((facing / TURN) * UNIT_DIRECTIONS) % UNIT_DIRECTIONS) + UNIT_DIRECTIONS) % UNIT_DIRECTIONS

  const program = createSpriteProgram(gl, false, TEAM_PALETTE.length / 3)
  /** Ничьё (игрок 0) — серым. */
  const greyProgram = createSpriteProgram(gl, true, TEAM_PALETTE.length / 3)
  const uniforms = { uTexture: atlas.texture, uPalette: TEAM_PALETTE }

  /**
   * Сколько юнит прошёл, в тайлах: от этого крутятся колёса, бегут гусеницы и шагают ноги. Считается по тому,
   * где юнит нарисован, и только пока он на экране. moving — сдвинулся ли он с прошлого кадра.
   */
  const walked = new Map<Entity, { x: number; y: number; distance: number; moving: boolean; seen: number }>()
  let frame = 0
  /** Кадр анимации хода юнита, нарисованного в (x, y). */
  const phaseOf = (entity: Entity, type: UnitType, x: number, y: number) => {
    const gait = UNIT_GAITS[type]
    if (gait === 'air') return 0
    let track = walked.get(entity)
    if (!track) walked.set(entity, (track = { x, y, distance: 0, moving: false, seen: frame }))
    const step = Math.hypot(x - track.x, y - track.y)
    // Скачок больше тайла за кадр — не ход: юнит вернулся на экран издалека или номер достался новому.
    if (step < 1) track.distance += step
    track.moving = step > 1e-6
    track.x = x
    track.y = y
    track.seen = frame
    // Стоящий пехотинец стоит прямо, а колёса и гусеницы замирают, где остановились.
    if (gait === 'legs' && !track.moving) return 0
    return Math.floor(track.distance / GAIT_STEP[gait]) % GAIT_PHASES
  }

  /** На сколько тайлов выстрел отбросил стрелка назад в этом кадре: рывок сразу после выстрела и возврат за RECOIL_TICKS. */
  const recoilOf = (entity: Entity) => {
    const { world, time } = scene.sim
    const armed = world.get(entity, Armed)
    const type = armed && armed.cooldown > 0 ? weaponOf(scene.sim, entity) : undefined
    if (!armed || !type) return 0
    const weapon = WEAPONS[type]
    const reload = Math.max(1, Math.round(weapon.reload / time.step))
    const since = reload - armed.cooldown + time.alpha
    const back = Math.min(RECOIL_TICKS, reload)
    return since < back ? (RECOIL[weapon.shot] * (1 - since / back)) / 16 : 0
  }

  /** Цвет спрайта: призрак (юнит, который редактор поставит по щелчку) полупрозрачный, а где ему не встать — красный. */
  const tint = (ghost: { blocked: boolean } | undefined): [number, number, number, number] =>
    !ghost ? [1, 1, 1, 1] : ghost.blocked ? [GHOST_ALPHA, GHOST_ALPHA * 0.3, GHOST_ALPHA * 0.3, GHOST_ALPHA] : [GHOST_ALPHA, GHOST_ALPHA, GHOST_ALPHA, GHOST_ALPHA]

  const layer = (air: boolean, emplacements = false): Pass => {
    // Тени лежат под всеми юнитами, иначе тень соседа ляжет на кабину.
    const shadows = createSprites(gl, program, true)
    const sprites = createSprites(gl, program, true)
    const neutral = createSprites(gl, greyProgram, true)
    const shadowShift = air ? AIR_SHADOW_SHIFT : SHADOW_SHIFT
    const shadowAlpha = air ? AIR_SHADOW_ALPHA : SHADOW_ALPHA

    return {
      draw({ camera, width, height, view, lights }) {
        const { world, time } = scene.sim
        // Союзники — зелёные, см. allies.ts: своих от союзников на карте видно с одного взгляда.
        const friends = alliesOf(scene.sim, scene.player)
        const halfWidth = width / 2 / camera.zoom + LIGHT_REACH
        const halfHeight = height / 2 / camera.zoom + LIGHT_REACH

        shadows.clear()
        sprites.clear()
        neutral.clear()
        if (!air && !emplacements) frame++
        if (!emplacements) {
          for (const [entity, position, unit, owner] of world.query(Position, Unit, Owner)) {
            if (flies(unit.type) !== air) continue
            const { x, y } = drawnPosition(position, unit, time.alpha)
            if (Math.abs(x - camera.x) > halfWidth || Math.abs(y - camera.y) > halfHeight) continue

            const facing = drawnFacing(unit, time.alpha)
            const forwardX = Math.cos(facing) / 16
            const forwardY = Math.sin(facing) / 16
            // Выстрел отбрасывает стрелка назад.
            const kick = recoilOf(entity) * 16
            const left = x - forwardX * kick - camera.x - SPRITE_TILES / 2
            const top = y - forwardY * kick - camera.y - SPRITE_TILES / 2
            // Свет встаёт на узел сетки пиксель-арта, иначе его пиксели не совпадут с пикселями земли.
            const snap = (value: number) => Math.round(value * 16) / 16
            const ghost = world.get(entity, Ghost)
            const { lamps, beam } = UNIT_LIGHTS[unit.type]
            if (!ghost) for (const lamp of lamps) {
              const lampX = snap(x + lamp.along * forwardX - lamp.across * forwardY)
              const lampY = snap(y + lamp.along * forwardY + lamp.across * forwardX)
              lights.add(lampX, lampY, lamp.glow * 2, lamp.glow, 1)
            }
            if (!ghost) lights.beam(snap(x + beam.along * forwardX), snap(y + beam.along * forwardY), facing, beam.length, beam.near, beam.spread, beam.level)

            const direction = directionOf(facing)
            const team: Team = !owner.player || owner.player === scene.player ? 'own' : friends.includes(owner.player) ? 'ally' : 'foe'
            const batch = owner.player ? sprites : neutral
            const phase = phaseOf(entity, unit.type, x, y)
            const { u, v, width: frameWidth, height: frameHeight } = framesOf(unit.type, phase)[direction]
            // Разворачивающийся MCV тает, пока на его месте растёт главное здание, см. buildingsPass.
            const converting = world.get(entity, Converting)
            const fade = converting ? Math.max(0, Math.min(1, (converting.left - time.alpha) / converting.total)) : 1
            const [r, g, b, a] = tint(ghost)
            shadows.push(left + shadowShift, top + shadowShift, SPRITE_TILES, SPRITE_TILES, u, v, frameWidth, frameHeight, 0, 0, 0, shadowAlpha * fade, 0)
            batch.push(left, top, SPRITE_TILES, SPRITE_TILES, u, v, frameWidth, frameHeight, r * fade, g * fade, b * fade, a * fade, TEAM_NAMES.indexOf(team))
          }
        }
        // Турели — поверх всех юнитов слоя: на своём носителе они должны лежать сверху.
        for (const [entity, position, turret, owner, attached] of world.query(Position, Turret, Owner, Attached)) {
          const carrier = world.get(attached.parent as never, Unit)
          // Турель здания рисуется в верхнем слое: её основание уже нарисовано проходом зданий.
          const mountedOnBuilding = !carrier && world.has(attached.parent as never, Building)
          if (mountedOnBuilding !== emplacements) continue
          if (!mountedOnBuilding && (!carrier || flies(carrier.type) !== air)) continue
          const { x, y } = drawnPosition(position, turret, time.alpha)
          if (Math.abs(x - camera.x) > halfWidth || Math.abs(y - camera.y) > halfHeight) continue
          const team: Team = !owner.player || owner.player === scene.player ? 'own' : friends.includes(owner.player) ? 'ally' : 'foe'
          // Поворот турели — относительно носителя: оба сглаживаются по отдельности, как в симуляции.
          const facing = (carrier ? drawnFacing(carrier, time.alpha) : 0) + turret.prevAngle + wrap(turret.angle - turret.prevAngle) * time.alpha
          const { u, v, width: frameWidth, height: frameHeight } = turretFramesOf(turret.type)[directionOf(facing)]
          const kick = recoilOf(entity)
          const left = x - Math.cos(facing) * kick - camera.x - TURRET_TILES / 2
          const top = y - Math.sin(facing) * kick - camera.y - TURRET_TILES / 2
          const turretShadowShift = mountedOnBuilding ? SHADOW_SHIFT : shadowShift
          const turretShadowAlpha = mountedOnBuilding ? SHADOW_ALPHA : shadowAlpha
          shadows.push(left + turretShadowShift / 2, top + turretShadowShift / 2, TURRET_TILES, TURRET_TILES, u, v, frameWidth, frameHeight, 0, 0, 0, turretShadowAlpha, 0)
          ;(owner.player ? sprites : neutral).push(left, top, TURRET_TILES, TURRET_TILES, u, v, frameWidth, frameHeight, ...tint(world.get(attached.parent as never, Ghost)), TEAM_NAMES.indexOf(team))
        }
        if (!air && !emplacements && frame % FORGET_FRAMES === 0) {
          for (const [entity, track] of walked) if (frame - track.seen > FORGET_FRAMES) walked.delete(entity)
        }
        if (!sprites.count && !neutral.count) return

        setBlend(gl, 'alpha')
        program.use(view, uniforms)
        shadows.draw()
        sprites.draw()
        if (neutral.count) {
          greyProgram.use(view, uniforms)
          neutral.draw()
        }
      },
      // Летающие лучам фар не мешают: они выше.
      drawOccluders: air
        ? undefined
        : (view) => {
            setBlend(gl, 'alpha')
            program.use(view, uniforms)
            sprites.draw()
            neutral.draw()
          },
      destroy() {
        shadows.destroy()
        sprites.destroy()
        neutral.destroy()
        // Общие картинки и программу освобождает наземный проход.
        if (air || emplacements) return
        program.destroy()
        greyProgram.destroy()
        atlas.texture.destroy()
      },
    }
  }

  return { ground: layer(false), emplacements: layer(false, true), air: layer(true) }
}
