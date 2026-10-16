import { setBlend } from '../../gl'
import { tileKey } from '@kharos/shared/terrain'
import { uploadAtlas, type AtlasFrame, type PackedAtlas, type Sprite } from '../../render/atlas'
import type { Pass } from '../../render/renderer'
import { createSpriteProgram, createSprites } from '../../render/sprites'
import type { Entity } from '@kharos/ecs'
import { Assembly, BUILDINGS, BUILDING_TYPES, Building, CORE, Converting, Deploys, Interceptor, Unit, deploySite, Inventory, Owner, Position, Reactor, Research, Silo, Site, amountOf, buildingSpec, depositAt, loadOf, ORES, ORE_OF, isBurning, isOpenGate, isPipe, loadedPlants, shutDown, siteTicks, type BuildingType } from '@kharos/sim-ts'
import { placementOf } from '../placing'
import type { Scene } from '../scene'
import { ART_FRAMES, ART_TILE, BUILDING_ART, GATE_OPEN_VARIANT, SILO_DOOR_STEPS, SILO_RISE_STEPS, WALL_CONNECTION, type BuildingArt } from './buildingArt'

/** Поля вокруг основания в пикселях спрайта: место для высоких частей. */
const PAD = ART_TILE
const FRAMES_PER_SECOND = 8
const SHADOW_ALPHA = 0.4
/** Тень — силуэт здания, сдвинутый вправо вниз на столько пикселей спрайта. */
const SHADOW_SHIFT = 3
/** Во сколько раз пятно света на земле и ореол шире самой лампы. */
const LIGHT_REACH = 16
const BLOOM_REACH = 3
/** Запас в тайлах вокруг экрана: здание за краем ещё может дотянуться до него светом. */
const VISIBLE_MARGIN = 5
/**
 * Сколько секунд здание ещё считается работающим после того, как работа последний раз сдвинулась: склад меняется
 * по тикам симуляции, а не каждый кадр, и без запаса анимация мигала бы.
 */
const WORK_HOLD = 0.6
/** Раз во сколько секунд игры пересчитывается, какие электростанции под нагрузкой. */
const PLANTS_EVERY = 0.5
/** Пуск ракетной шахты: какую долю срока расходятся створки и с какой доли поднимается ракета. */
const DOORS_SHARE = 0.35
const RISE_FROM = 0.6
/** Цвет недостроенного: чертёж здания, сквозь который видно землю. Альфа меньше половины — тени от лучей он не даёт. */
const BLUEPRINT = [0.3, 0.6, 1, 0.4] as const
/** Призрак здания, которому выбирают место: полупрозрачное здание, красное — если туда нельзя. Теней он не даёт. */
/** Своё выключенное здание: спрайт темнее, огни не горят. */
const SWITCHED_OFF = [0.55, 0.57, 0.62, 1] as const
const GHOST = [0.7, 0.7, 0.7, 0.7] as const
const GHOST_FORBIDDEN = [0.7, 0.14, 0.1, 0.7] as const
/** Место, куда едет разворачиваться свой MCV: призрак бледнее выбираемого, чтобы не путать их. */
const PLANNED = [0.4, 0.4, 0.4, 0.4] as const

/** Огонь чертежа: место в пикселях спрайта и яркость в каждом кадре. */
interface ArtLight {
  x: number
  y: number
  size: number
  levels: number[]
}

interface Sheet {
  art: BuildingArt
  /** Места кадров в атласе. */
  frames: AtlasFrame[]
  lights: ArtLight[]
}

/** Сколько кадров в листе: у здания с видом работы — сначала кадры простоя, за ними кадры работы. */
const framesOf = (art: BuildingArt) => ART_FRAMES * (art.working ? 2 : 1)

/** Картинки зданий: атлас и огни каждого варианта чертежа; кадры вариантов лежат в атласе подряд, по типам в порядке BUILDING_TYPES. */
export interface BuildingsArt {
  atlas: PackedAtlas
  lights: Record<BuildingType, ArtLight[][]>
}

/** Кадры анимации всех вариантов всех чертежей. Огни чертежа собираются в lights по ходу рисования. */
export function buildingsSprites(): { sprites: Sprite[]; lights: Record<BuildingType, ArtLight[][]> } {
  const sprites: Sprite[] = []
  const lights = {} as Record<BuildingType, ArtLight[][]>
  for (const type of BUILDING_TYPES) {
    const art = BUILDING_ART[type]
    lights[type] = Array.from({ length: art.variants ?? 1 }, (_, variant) => {
      const sheet: ArtLight[] = []
      for (let i = 0; i < framesOf(art); i++) {
        sprites.push({
          width: art.width * ART_TILE + PAD * 2,
          height: art.height * ART_TILE + PAD * 2,
          draw(image) {
            image.originX = image.originY = PAD
            let slot = 0
            art.draw(image, (i % ART_FRAMES) / ART_FRAMES, (x, y, size, level) => {
              const light = (sheet[slot++] ??= { x, y, size, levels: [] })
              light.levels[i] = level
            }, variant, i >= ART_FRAMES)
          },
        })
      }
      return sheet
    })
  }
  return { sprites, lights }
}

interface Visible {
  x: number
  y: number
  sheet: Sheet
  frame: number
  /** Нижний край основания: по нему здания перекрывают друг друга. */
  bottom: number
  /** Готовность от 0 до 1: у достроенного — 1, у размеченной площадки — 0. */
  built: number
  /** Ничьё (игрок 0): рисуется серым. */
  neutral: boolean
  /** Своё выключенное: темнее, без огней, анимация стоит. */
  off: boolean
}

/**
 * Проход зданий: тени, сами здания и их огни. Здания — сущности с Position и Building.
 * Стройка (Site) рисуется чертежом, поверх которого снизу вверх растёт настоящее здание.
 */
export function createBuildingsPass(gl: WebGL2RenderingContext, scene: Scene, art: BuildingsArt): Pass {
  const atlas = uploadAtlas(gl, art.atlas)
  let first = 0
  const sheets = new Map<BuildingType, Sheet[]>(BUILDING_TYPES.map((type): [BuildingType, Sheet[]] => {
    const drawing = BUILDING_ART[type]
    return [type, art.lights[type].map((lights) => {
      const frames = atlas.frames.slice(first, (first += framesOf(drawing)))
      return { art: drawing, frames, lights }
    })]
  }))

  /**
   * Работает ли здание: переработка — пока убывает её руда, шахта — пока прибывает добытое, цех — пока движется сборка.
   * Для каждого здания помнится прошлое значение и до какой секунды экранных часов оно считается работающим.
   */
  const activity = new Map<Entity, { value: number; until: number }>()
  const workOf = (entity: Entity, type: BuildingType) => {
    const { world } = scene.sim
    const spec = buildingSpec(type)
    if (spec.extract) {
      // Склад чужой шахты хост не шлёт: работает ли она, не видно.
      const inventory = world.get(entity, Inventory)
      return inventory ? loadOf(inventory) : undefined
    }
    const ore = spec.refines
    if (ore) {
      // Склад чужого здания хост не шлёт: работает ли оно, не видно.
      const inventory = world.get(entity, Inventory)
      return inventory ? amountOf(inventory, ore) : undefined
    }
    const assembly = world.get(entity, Assembly)
    return assembly ? assembly.progress : undefined
  }

  /**
   * Электростанции под нагрузкой, см. loadedPlants: пересчитываются раз в PLANTS_EVERY секунд игры и когда мир
   * сменился, — это обход всех зон.
   */
  let plants = new Set<Entity>()
  let plantsAt = -Infinity
  let plantsOf: unknown

  const program = createSpriteProgram(gl)
  /** Ничьё (игрок 0) — серым. */
  const greyProgram = createSpriteProgram(gl, true)
  // Тени лежат под всеми зданиями, иначе тень соседа ляжет на стену.
  const shadows = createSprites(gl, program)
  const sprites = createSprites(gl, program)
  const neutral = createSprites(gl, greyProgram)
  const ghosts = createSprites(gl, program)
  const visible: Visible[] = []

  return {
    draw({ camera, width, height, time, view, lights }) {
      const halfWidth = width / 2 / camera.zoom + VISIBLE_MARGIN
      const halfHeight = height / 2 / camera.zoom + VISIBLE_MARGIN
      const step = Math.floor(time * FRAMES_PER_SECOND)
      const pad = PAD / ART_TILE

      const { world, time: simTime } = scene.sim
      /** Какая доля превращения ещё впереди, плавно между тиками. */
      const remainingOf = (converting: { left: number; total: number }) => Math.max(0, Math.min(1, (converting.left - simTime.alpha) / converting.total))
      if (plantsOf !== scene.sim || Math.abs(time - plantsAt) >= PLANTS_EVERY) {
        plants = loadedPlants(scene.sim)
        plantsAt = time
        plantsOf = scene.sim
      }
      // И готовая стена, и ещё не начатая соседняя площадка участвуют в соединении: чертёж сразу показывает итог.
      const walls = new Set<number>()
      for (const [, position, building] of world.query(Position, Building)) {
        if (building.type === 'wall' || building.type === 'gate') walls.add(tileKey(position.x, position.y))
      }
      for (const [, position, site] of world.query(Position, Site)) {
        if (site.type === 'wall' || site.type === 'gate') walls.add(tileKey(position.x, position.y))
      }
      // Труба соединяется со всем, что у неё сбоку и входит в сеть: с трубами и зданиями, кроме стен, и с площадками труб.
      const pipes = new Set<number>()
      for (const [, position, site] of world.query(Position, Site)) if (isPipe(site.type)) pipes.add(tileKey(position.x, position.y))
      const joins = (x: number, y: number) => {
        if (pipes.has(tileKey(x, y))) return true
        const other = scene.sim.occupancy.at(x, y)
        const type = other === undefined ? undefined : world.get(other, Building)?.type
        return type !== undefined && !buildingSpec(type).isolated
      }
      const wallVariant = (position: { x: number; y: number }, type: BuildingType, entity?: Entity) => {
        if (type === 'silo') return entity === undefined ? 0 : siloVariant(entity)
        // Шахта — цвета руды под ней: номер руды в ORES плюс один.
        if (buildingSpec(type).extract) {
          const kind = depositAt(scene.sim, position.x, position.y)?.kind
          return kind ? ORES.indexOf(ORE_OF[kind]) + 1 : 0
        }
        // Перехватчик: сколько труб пакета заряжено. Чужой — всегда пустым: это знает хозяин.
        if (type === 'interceptor') return entity === undefined ? 0 : (world.get(entity, Interceptor)?.missiles ?? 0)

        if (isPipe(type)) {
          let variant = 0
          if (joins(position.x, position.y - 1)) variant |= WALL_CONNECTION.north
          if (joins(position.x + 1, position.y)) variant |= WALL_CONNECTION.east
          if (joins(position.x, position.y + 1)) variant |= WALL_CONNECTION.south
          if (joins(position.x - 1, position.y)) variant |= WALL_CONNECTION.west
          return variant
        }
        if (type !== 'wall' && type !== 'gate') return 0
        // Опущенная плита ворот — свой вид; соседи у ворот те же, что у стены.
        let variant = type === 'gate' && entity !== undefined && isOpenGate(scene.sim, entity) ? GATE_OPEN_VARIANT : 0
        if (walls.has(tileKey(position.x, position.y - 1))) variant |= WALL_CONNECTION.north
        if (walls.has(tileKey(position.x + 1, position.y))) variant |= WALL_CONNECTION.east
        if (walls.has(tileKey(position.x, position.y + 1))) variant |= WALL_CONNECTION.south
        if (walls.has(tileKey(position.x - 1, position.y))) variant |= WALL_CONNECTION.west
        return variant
      }
      /**
       * Ступень пуска шахты: створки расходятся в первую DOORS_SHARE срока пуска, ракета поднимается с RISE_FROM до
       * старта. Вне пуска — закрыта.
       */
      const siloVariant = (entity: Entity) => {
        const start = world.get(entity, Silo)?.start ?? -1
        if (start < 0) return 0
        const share = (simTime.tick + simTime.alpha - start) / Math.round(BUILDINGS.silo.warhead!.launch / simTime.step)
        const doors = Math.min(SILO_DOOR_STEPS - 1, Math.floor((share / DOORS_SHARE) * (SILO_DOOR_STEPS - 1)))
        if (share < RISE_FROM) return doors
        return SILO_DOOR_STEPS - 1 + Math.min(SILO_RISE_STEPS, Math.ceil(((share - RISE_FROM) / (1 - RISE_FROM)) * SILO_RISE_STEPS))
      }
      const see = (position: { x: number; y: number }, type: BuildingType, phase: number, built: number, working = false, neutral = false, entity?: Entity, off = false) => {
        const sheet = sheets.get(type)![wallVariant(position, type, entity)]
        if (position.x + sheet.art.width < camera.x - halfWidth || position.x > camera.x + halfWidth) return
        if (position.y + sheet.art.height < camera.y - halfHeight || position.y > camera.y + halfHeight) return
        const frame = (((off ? 0 : step) + phase) % ART_FRAMES) + (working && sheet.art.working ? ART_FRAMES : 0)
        visible.push({ x: position.x, y: position.y, sheet, frame, bottom: position.y + sheet.art.height, built, neutral, off })
      }

      visible.length = 0
      for (const [entity, position, building] of world.query(Position, Building)) {
        const site = world.get(entity, Site)
        // Реактор работает, пока горит стержень: это видно сразу, без догадок по складу. Электростанция — пока на неё
        // приходится нагрузка зоны: лишние стоят в запасе. Шахта — пока в ней готовая
        // боеголовка, перехватчик — пока в нём есть ракета (у чужого не видно), лаборатория — пока ведёт тему: это они
        // сообщают сами.
        let working = !site && ((world.has(entity, Reactor) && isBurning(scene.sim, entity)) || plants.has(entity) || world.get(entity, Silo)?.ready === true || (world.get(entity, Interceptor)?.missiles ?? 0) > 0 || (world.get(entity, Research)?.speed ?? 0) > 0)
        const value = !site && !working && BUILDING_ART[building.type].working && building.type !== 'interceptor' ? workOf(entity, building.type) : undefined
        if (value !== undefined) {
          const state = activity.get(entity) ?? { value, until: 0 }
          if (Math.abs(value - state.value) > 1e-9) state.until = time + WORK_HOLD
          state.value = value
          activity.set(entity, state)
          working = time < state.until
        }
        const off = shutDown(scene.sim, scene.player, entity)
        // Сворачивающееся главное здание уходит сверху вниз, как стройка наоборот: в конце от него остаётся MCV.
        const packing = world.get(entity, Converting)
        const built = site ? site.progress / siteTicks(site.type, simTime.step) : packing ? remainingOf(packing) : 1
        see(position, building.type, building.phase, built, working && !off && !packing, !world.get(entity, Owner)?.player, entity, off)
      }
      // Площадки, к которым строитель ещё не приступил: здания на них пока нет.
      for (const [entity, position, site] of world.query(Position, Site)) {
        if (!world.has(entity, Building)) see(position, site.type, 0, 0)
      }
      // Разворачивающийся MCV: главное здание растёт на своём месте снизу вверх, как стройка, а сам MCV тает.
      for (const [entity, converting] of world.query(Converting, Unit)) see(deploySite(scene.sim, entity), CORE, 0, 1 - remainingOf(converting))
      if (step % 64 === 0) for (const entity of activity.keys()) if (!world.has(entity, Building)) activity.delete(entity)
      const placement = placementOf(scene)
      // Куда едут разворачиваться свои MCV: место видно призраком, пока они в пути.
      const planned: { x: number; y: number }[] = []
      for (const [entity, deploys, owner] of world.query(Deploys, Owner)) {
        if (owner.player === scene.player && !world.has(entity, Converting)) planned.push(deploys)
      }
      if (!visible.length && !placement && !planned.length) return
      // Нижние здания рисуются позже и перекрывают верхние.
      visible.sort((a, b) => a.bottom - b.bottom)

      shadows.clear()
      sprites.clear()
      neutral.clear()
      const shift = SHADOW_SHIFT / ART_TILE
      for (const { x, y, sheet, frame, built, neutral: grey, off } of visible) {
        const batch = grey ? neutral : sprites
        const { u, v, width: frameWidth, height: frameHeight } = sheet.frames[frame]
        const left = x - pad - camera.x
        const top = y - pad - camera.y
        const spriteWidth = sheet.art.width + pad * 2
        const spriteHeight = sheet.art.height + pad * 2
        if (built < 1) {
          batch.push(left, top, spriteWidth, spriteHeight, u, v, frameWidth, frameHeight, ...BLUEPRINT)
          // Готовая часть — нижние строки спрайта, целое число пикселей.
          const rows = Math.round(spriteHeight * ART_TILE)
          const hidden = (rows - Math.floor(rows * built)) / rows
          batch.push(
            left, top + spriteHeight * hidden, spriteWidth, spriteHeight * (1 - hidden),
            u, v + frameHeight * hidden, frameWidth, frameHeight * (1 - hidden),
            1, 1, 1, 1,
          )
          continue
        }
        shadows.push(left + shift, top + shift, spriteWidth, spriteHeight, u, v, frameWidth, frameHeight, 0, 0, 0, SHADOW_ALPHA)
        if (off) {
          batch.push(left, top, spriteWidth, spriteHeight, u, v, frameWidth, frameHeight, ...SWITCHED_OFF)
          continue
        }
        batch.push(left, top, spriteWidth, spriteHeight, u, v, frameWidth, frameHeight, 1, 1, 1, 1)
        for (const light of sheet.lights) {
          lights.add(
            x + light.x / ART_TILE,
            y + light.y / ART_TILE,
            light.size * LIGHT_REACH,
            light.size * BLOOM_REACH + 2,
            light.levels[frame],
          )
        }
      }

      // Призрак выбираемого места — поверх всех зданий, своим слоем: тенью от лучей он не служит.
      ghosts.clear()
      for (const { x, y } of planned) {
        const sheet = sheets.get(CORE)![0]
        const { u, v, width: frameWidth, height: frameHeight } = sheet.frames[step % ART_FRAMES]
        ghosts.push(x - pad - camera.x, y - pad - camera.y, sheet.art.width + pad * 2, sheet.art.height + pad * 2, u, v, frameWidth, frameHeight, ...PLANNED)
      }
      if (placement) {
        const sheet = sheets.get(placement.type)![0]
        const { u, v, width: frameWidth, height: frameHeight } = sheet.frames[step % ART_FRAMES]
        ghosts.push(
          placement.x - pad - camera.x, placement.y - pad - camera.y, sheet.art.width + pad * 2, sheet.art.height + pad * 2,
          u, v, frameWidth, frameHeight, ...(placement.allowed ? GHOST : GHOST_FORBIDDEN),
        )
      }

      setBlend(gl, 'alpha')
      program.use(view, { uTexture: atlas.texture })
      shadows.draw()
      sprites.draw()
      ghosts.draw()
      if (neutral.count) {
        greyProgram.use(view, { uTexture: atlas.texture })
        neutral.draw()
      }
    },
    drawOccluders(view) {
      setBlend(gl, 'alpha')
      program.use(view, { uTexture: atlas.texture })
      sprites.draw()
      neutral.draw()
    },
    destroy() {
      shadows.destroy()
      ghosts.destroy()
      sprites.destroy()
      neutral.destroy()
      program.destroy()
      greyProgram.destroy()
      atlas.texture.destroy()
    },
  }
}
