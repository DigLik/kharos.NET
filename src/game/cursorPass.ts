import { setBlend } from '../gl'
import type { Pass } from '../render/renderer'
import { createSpriteProgram, createSprites, createWhiteTexture } from '../render/sprites'
import type { Entity } from '../ecs'
import { BUILDINGS, Building, CONTROL_RADIUS, EXPAND_RADIUS, Silo, Hauler, Owner, Position, Tactics, allZones, buildingSpec, gridOf, wellPartners, type BuildingSpec, type BuildingType, type Zone } from '../sim'
import { paveStrokeOf, placementOf } from './placing'
import type { Scene } from './scene'
import { currentStep, stepTarget, stepsDone, type StepTarget } from './firstSteps'
import { prefs } from './prefs'

/** Толщина рамки в тайлах: один пиксель пиксель-арта. */
const BORDER = 1 / 16
const FILL_ALPHA = 0.18
const BORDER_ALPHA = 0.85

type Color = readonly [number, number, number]
const ALLOWED: Color = [0.35, 1, 0.45]
const FORBIDDEN: Color = [1, 0.3, 0.25]
const CONTROL: Color = [0.3, 0.6, 1]
/** Снимаемое покрытие. */
const REMOVE: Color = [1, 0.65, 0.2]
/** Покрытие, на которое не хватает кредитов. */
const SHORT: Color = [1, 0.85, 0.15]
/** Маршрут грузовиков: цвет, размер точки пунктира в пикселях экрана и шаг пунктира в тайлах. */
const ROUTE: Color = [1, 0.85, 0.3]
/** Здания, которые обслуживают выбранные грузовики: только рамки, без пунктира. */
const SERVE: Color = [0.45, 0.85, 1]
const ROUTE_DOT = 3
/** Подземный отрезок колодца, с которым свяжется ставящийся. */
const LINK: Color = [0.4, 0.75, 1]
/** Шаг пунктира в пикселях экрана: при любом масштабе одна густота. */
const ROUTE_GAP = 14
/** Пунктир маршрута бледнее рамок: он на виду, пока выбран грузовик, и не должен резать глаз. */
const ROUTE_ALPHA = 0.55
/** Круг, который защищает перехватчик: пунктир шагом GUARD_STEP тайлов. */
const GUARD: Color = [0.55, 1, 0.85]
const GUARD_STEP = 1.5
/** Цель текущего первого шага: золотые уголки вокруг, пульсируют; пересчитывается раз в STEP_EVERY секунд. */
const STEP: Color = [1, 0.82, 0.3]
const STEP_EVERY = 0.5
/** Длина стороны уголка и отступ рамки от цели, в тайлах. */
const STEP_CORNER = 0.9
const STEP_MARGIN = 0.3

/** Маршрут на карте: остановки прямоугольниками основания; closed — замкнут по кругу, а не ещё набирается. */
interface DrawnRoute {
  stops: { x: number; y: number; width: number; height: number }[]
  closed: boolean
  /** Только рамки зданий, без пунктира между ними: назначение грузовиков, а не маршрут. */
  marks?: boolean
}

/** Маршруты для показа: набираемый игроком и маршруты выбранных грузовиков, каждый по разу. */
function routesOf(scene: Scene): DrawnRoute[] {
  const { world } = scene.sim
  const stopOf = (entity: number) => {
    const position = world.get(entity as Entity, Position)
    const type = world.get(entity as Entity, Building)?.type
    return position && type !== undefined ? { x: position.x, y: position.y, ...BUILDINGS[type] } : null
  }
  const routes: DrawnRoute[] = []
  const seen = new Set<string>()
  const add = (entities: readonly number[], closed: boolean) => {
    const key = `${closed}:${entities.join()}`
    if (!entities.length || seen.has(key)) return
    seen.add(key)
    routes.push({ stops: entities.map(stopOf).filter((stop) => stop !== null), closed })
  }
  if (scene.routing) add(scene.routing, false)
  // Назначение: отмечаемые сейчас здания и назначенные выбранным грузовикам.
  const marks = (entities: readonly number[]) => {
    const key = `serve:${entities.join()}`
    if (!entities.length || seen.has(key)) return
    seen.add(key)
    routes.push({ stops: entities.map(stopOf).filter((stop) => stop !== null), closed: false, marks: true })
  }
  if (scene.serving) marks(scene.serving)
  for (const entity of scene.selection) {
    const serve = world.get(entity, Hauler)?.serve
    if (serve?.length) marks(serve)
  }
  for (const entity of scene.selection) {
    const route = world.get(entity, Hauler)?.route
    if (route?.length) add(route, true)
  }
  // Патруль: точки — тайлы. Набираемый — от выбранного бойца через набранные точки.
  const tiles = (points: readonly number[], closed: boolean) => {
    const key = `patrol:${closed}:${points.join()}`
    if (points.length < 2 || seen.has(key)) return
    seen.add(key)
    const stops = []
    for (let i = 0; i + 1 < points.length; i += 2) stops.push({ x: points[i], y: points[i + 1], width: 1, height: 1 })
    routes.push({ stops, closed })
  }
  for (const entity of scene.selection) {
    const patrol = world.get(entity, Tactics)?.patrol
    if (patrol?.length) tiles(patrol, true)
  }
  return routes
}
/** Граница радиуса контроля — пунктир: столько штрихов на круг, каждый такой длины и толщины в пикселях экрана. */
const CONTROL_DASHES = 120
const CONTROL_DASH = 3
/** Насколько бледнее границы зон рисуются энергосеть выбранного здания и круг, который добавит ставящееся. */
const FAINT_ALPHA = 0.5

/**
 * Точки внешней границы зоны, x и y подряд. zone — круги: x, y и радиус подряд. Точка внутри соседнего круга —
 * не граница. Соседей точка ищет по сетке, а не среди всех кругов: у большой базы кругов сотни, и перебор
 * каждого с каждым — десятки миллионов проверок.
 */
export function outlineOf(zone: readonly number[]) {
  let cell = 1
  for (let i = 2; i < zone.length; i += 3) cell = Math.max(cell, zone[i])
  // Сторона ячейки — самый большой радиус: круг, накрывающий точку, лежит центром в её ячейке или в соседней.
  const keyOf = (x: number, y: number) => (Math.floor(x / cell) + 32768) * 65536 + Math.floor(y / cell) + 32768
  const cells = new Map<number, number[]>()
  for (let i = 0; i < zone.length; i += 3) {
    const key = keyOf(zone[i], zone[i + 1])
    const list = cells.get(key)
    if (list) list.push(i)
    else cells.set(key, [i])
  }
  const dots: number[] = []
  for (let i = 0; i < zone.length; i += 3) {
    const radius = zone[i + 2]
    // Точки идут с одним шагом на любом круге, поэтому на малом их меньше.
    const dashes = Math.round((CONTROL_DASHES * radius) / CONTROL_RADIUS)
    dashes: for (let dash = 0; dash < dashes; dash++) {
      const angle = (dash / dashes) * Math.PI * 2
      const x = zone[i] + Math.cos(angle) * radius
      const y = zone[i + 1] + Math.sin(angle) * radius
      for (let dy = -cell; dy <= cell; dy += cell) {
        for (let dx = -cell; dx <= cell; dx += cell) {
          const list = cells.get(keyOf(x + dx, y + dy))
          if (!list) continue
          for (const other of list) {
            if (other !== i && Math.hypot(x - zone[other], y - zone[other + 1]) < zone[other + 2] - 0.01) continue dashes
          }
        }
      }
      dots.push(x, y)
    }
  }
  return dots
}

/**
 * Подсветка под указателем мыши, пока игрок выбирает место под здание: его основание (зелёное, если строить можно,
 * красное, если нельзя), круг, которым оно расширит зону, и границы зон строительства. Так же — тайлы покрытия,
 * которое игрок тянет мышью. Границы зон видны и без выбора места: пока зажат Alt или курсор стоит на кнопке
 * здания в меню стройки; у выбранного своего здания — граница его энергосети.
 * В остальное время ничего не рисует.
 * Ставить выше освещения, чтобы ночью не темнела.
 */
/**
 * Круги перехватчиков, которые показать: центры и радиусы в тайлах. Выбранные — всегда; пока ставится перехватчик —
 * его круг под указателем и круги всех своих.
 */
function guardsOf(scene: Scene, placement: { type: BuildingType; x: number; y: number } | null) {
  const { world } = scene.sim
  const guards: { x: number; y: number; radius: number }[] = []
  const add = (type: BuildingType, x: number, y: number) => {
    const { width, height, intercept } = buildingSpec(type)
    if (intercept) guards.push({ x: x + width / 2, y: y + height / 2, radius: intercept.radius })
  }
  const placing = placement !== null && !!buildingSpec(placement.type).intercept
  if (placing) add(placement.type, placement.x, placement.y)
  for (const [entity, building, position] of world.query(Building, Position)) {
    if (!buildingSpec(building.type).intercept) continue
    if (scene.selection.has(entity) || (placing && world.get(entity, Owner)?.player === scene.player)) add(building.type, position.x, position.y)
  }
  return guards
}

export function createCursorPass(gl: WebGL2RenderingContext, scene: Scene): Pass {
  const program = createSpriteProgram(gl)
  const rects = createSprites(gl, program)
  const white = createWhiteTexture(gl)
  /**
   * Границы зон: свои — где строить можно, чужие — где нельзя. Считаются заново, только когда сменились сами зоны
   * (allZones отдаёт новый набор) или игрок, а не каждый кадр.
   */
  let borders: { zones: unknown; player: number; own: number[]; foreign: number[]; circles: number[] } | undefined
  const bordersOf = () => {
    const zones = allZones(scene.sim)
    if (borders?.zones === zones && borders.player === scene.player) return borders
    borders = { zones, player: scene.player, own: [], foreign: [], circles: [] }
    for (const [player, list] of zones) {
      if (player === scene.player) borders.own = outlineOf((borders.circles = list.flatMap((zone) => zone.circles)))
      else for (const zone of list) for (const value of outlineOf(zone.circles)) borders.foreign.push(value)
    }
    return borders
  }
  /** Границы энергосетей: считаются раз на сеть — с любой стройкой или сносом сети собираются заново. */
  const gridBorders = new WeakMap<Zone, number[]>()
  /** Границы энергосетей выбранных своих зданий, каждая по разу. */
  const selectedGrids = () => {
    const { sim } = scene
    const seen = new Set<Zone>()
    const list: number[][] = []
    for (const entity of scene.selection) {
      if (!sim.world.has(entity, Building) || sim.world.get(entity, Owner)?.player !== scene.player) continue
      const grid = gridOf(sim, entity)
      if (!grid || seen.has(grid)) continue
      seen.add(grid)
      let dots = gridBorders.get(grid)
      if (!dots) gridBorders.set(grid, (dots = outlineOf(grid.circles)))
      list.push(dots)
    }
    return list
  }

  /** Цель текущего первого шага, пока список шагов не скрыт; null — показывать нечего. */
  let step: { target: StepTarget | null; at: number; sim: unknown } = { target: null, at: -Infinity, sim: null }
  const stepOf = (time: number) => {
    if (step.sim !== scene.sim || Math.abs(time - step.at) >= STEP_EVERY) {
      const current = prefs().steps === 'off' ? undefined : currentStep(stepsDone(scene.sim, scene.player))
      step = { target: (current && stepTarget(scene.sim, scene.player, current.key)) ?? null, at: time, sim: scene.sim }
    }
    return step.target
  }

  return {
    draw({ camera, view, width, height, time }) {
      const placement = scene.camera.pointerTile ? placementOf(scene) : null
      const stroke = scene.camera.pointerTile ? paveStrokeOf(scene) : null
      const routes = routesOf(scene)
      // Наводка ядерного удара: под указателем — круг взрыва и его ядра. Наводить можно только готовую боеголовку.
      const aim = scene.aiming !== null && scene.sim.world.get(scene.aiming, Silo)?.ready ? scene.camera.pointerTile : null
      const guards = guardsOf(scene, placement)
      // Все зоны — пока ставят, тянут, держат Alt или смотрят кнопку здания; иначе — только сеть выбранного.
      const zones = !!placement || !!stroke || scene.zonesHeld || scene.zonesHover
      const grids = zones ? [] : selectedGrids()
      const target = stepOf(time)
      if (!zones && !grids.length && !routes.length && !aim && !guards.length && !target) return

      /** Прямоугольник в тайлах от камеры. */
      const rect = (x: number, y: number, width: number, height: number, [r, g, b]: Color, alpha: number) =>
        rects.push(x, y, width, height, 0, 0, 1, 1, r * alpha, g * alpha, b * alpha, alpha)

      /** Рамка прямоугольника. */
      const frame = (left: number, top: number, width: number, height: number, color: Color) => {
        const x = left - camera.x
        const y = top - camera.y
        rect(x, y, width, BORDER, color, BORDER_ALPHA)
        rect(x, y + height - BORDER, width, BORDER, color, BORDER_ALPHA)
        rect(x, y + BORDER, BORDER, height - BORDER * 2, color, BORDER_ALPHA)
        rect(x + width - BORDER, y + BORDER, BORDER, height - BORDER * 2, color, BORDER_ALPHA)
      }
      /** Залитый прямоугольник с рамкой. */
      const area = (left: number, top: number, width: number, height: number, color: Color) => {
        const x = left - camera.x
        const y = top - camera.y
        rect(x, y, width, height, color, FILL_ALPHA)
        rect(x, y, width, BORDER, color, BORDER_ALPHA)
        rect(x, y + height - BORDER, width, BORDER, color, BORDER_ALPHA)
        rect(x, y + BORDER, BORDER, height - BORDER * 2, color, BORDER_ALPHA)
        rect(x + width - BORDER, y + BORDER, BORDER, height - BORDER * 2, color, BORDER_ALPHA)
      }

      rects.clear()
      const gap = ROUTE_GAP / camera.zoom
      // Маршруты грузовиков: остановки в рамке, между ними — пунктир по кругу.
      for (const { stops, closed, marks } of routes) {
        const size = ROUTE_DOT / camera.zoom
        const centers = stops.map(({ x, y, width, height }) => ({ x: x + width / 2, y: y + height / 2 }))
        for (const { x, y, width, height } of stops) area(x, y, width, height, marks ? SERVE : ROUTE)
        const legs = marks ? 0 : closed ? centers.length : centers.length - 1
        for (let i = 0; i < legs; i++) {
          const from = centers[i]
          const to = centers[(i + 1) % centers.length]
          const length = Math.hypot(to.x - from.x, to.y - from.y)
          for (let along = 0; along < length; along += gap) {
            const t = along / length
            rect(from.x + (to.x - from.x) * t - camera.x - size / 2, from.y + (to.y - from.y) * t - camera.y - size / 2, size, size, ROUTE, ROUTE_ALPHA)
          }
        }
      }
      const size = CONTROL_DASH / camera.zoom
      const reachX = width / 2 / camera.zoom + size
      const reachY = height / 2 / camera.zoom + size
      /** Внешняя граница зоны пунктиром: те её точки, что попали в кадр. dots — x и y подряд. */
      const outline = (dots: readonly number[], color: Color, alpha = BORDER_ALPHA) => {
        for (let i = 0; i < dots.length; i += 2) {
          const x = dots[i] - camera.x
          const y = dots[i + 1] - camera.y
          if (Math.abs(x) > reachX || Math.abs(y) > reachY) continue
          rect(x - size / 2, y - size / 2, size, size, color, alpha)
        }
      }
      if (zones) {
        const { own, foreign } = bordersOf()
        outline(own, CONTROL)
        outline(foreign, FORBIDDEN)
      }
      for (const dots of grids) outline(dots, CONTROL, FAINT_ALPHA)
      if (placement || stroke) {
        /**
         * С чем свяжется ставящееся: колодец — пунктир до парных колодцев, без пары — красная метка.
         */
        const connections = (type: BuildingType, x: number, y: number) => {
          const dot = ROUTE_DOT / camera.zoom
          const cross = (cx: number, cy: number) => {
            for (let t = -0.35; t <= 0.35; t += 0.07) {
              rect(cx + t - camera.x - dot / 2, cy + t - camera.y - dot / 2, dot, dot, FORBIDDEN, BORDER_ALPHA)
              rect(cx + t - camera.x - dot / 2, cy - t - camera.y - dot / 2, dot, dot, FORBIDDEN, BORDER_ALPHA)
            }
          }
          if (type === 'well') {
            const partners = wellPartners(scene.sim, scene.player, x, y)
            for (const partner of partners) {
              frame(partner.x, partner.y, 1, 1, LINK)
              const length = Math.hypot(partner.x - x, partner.y - y)
              for (let along = 0.75; along < length - 0.5; along += gap) {
                const t = along / length
                rect(x + 0.5 + (partner.x - x) * t - camera.x - dot / 2, y + 0.5 + (partner.y - y) * t - camera.y - dot / 2, dot, dot, LINK, partner.ready ? BORDER_ALPHA : FILL_ALPHA * 2)
              }
            }
            if (!partners.length) cross(x + 0.5, y + 0.5)
            return
          }
        }
        if (placement) {
          const { width, height } = BUILDINGS[placement.type]
          // Само здание рисует призраком проход зданий: здесь — только обводка основания.
          frame(placement.x, placement.y, width, height, placement.allowed ? ALLOWED : FORBIDDEN)
          // Круг, которым готовое здание расширит зону: та его часть, что выходит за нынешние зоны.
          const { zone, expand, isolated }: BuildingSpec = BUILDINGS[placement.type]
          const radius = isolated ? 0 : (zone ?? expand ?? EXPAND_RADIUS)
          if (placement.allowed && radius > 0) {
            const cx = placement.x + width / 2
            const cy = placement.y + height / 2
            const { circles } = bordersOf()
            // Нынешние круги, до которых новый дотягивается: только с ними сверяется каждая его точка.
            const near: number[] = []
            for (let i = 0; i < circles.length; i += 3) {
              if (Math.hypot(circles[i] - cx, circles[i + 1] - cy) < circles[i + 2] + radius) near.push(circles[i], circles[i + 1], circles[i + 2])
            }
            const dashes = Math.round((CONTROL_DASHES * radius) / CONTROL_RADIUS)
            const added: number[] = []
            dashes: for (let dash = 0; dash < dashes; dash++) {
              const angle = (dash / dashes) * Math.PI * 2
              const x = cx + Math.cos(angle) * radius
              const y = cy + Math.sin(angle) * radius
              for (let i = 0; i < near.length; i += 3) if (Math.hypot(x - near[i], y - near[i + 1]) < near[i + 2] - 0.01) continue dashes
              added.push(x, y)
            }
            outline(added, CONTROL, FAINT_ALPHA)
          }
          if (!scene.edit) connections(placement.type, placement.x, placement.y)
        }
        if (stroke) {
          const good = stroke.tool === 'remove' ? REMOVE : ALLOWED
          // Один колодец — с кем он свяжется; пара — пунктир подземного отрезка между концами.
          if (stroke.tool === 'well' && stroke.tiles.length === 2) connections('well', stroke.tiles[0], stroke.tiles[1])
          if (stroke.tool === 'well' && stroke.tiles.length === 4) {
            const [ax, ay, bx, by] = stroke.tiles
            const dot = ROUTE_DOT / camera.zoom
            const length = Math.hypot(bx - ax, by - ay)
            const color = stroke.allowed[0] && stroke.allowed[1] ? LINK : FORBIDDEN
            for (let along = 0.75; along < length - 0.5; along += gap) {
              const t = along / length
              rect(ax + 0.5 + (bx - ax) * t - camera.x - dot / 2, ay + 0.5 + (by - ay) * t - camera.y - dot / 2, dot, dot, color, BORDER_ALPHA)
            }
          }
          for (let i = 0; i < stroke.tiles.length; i += 2) {
            const tile = i >> 1
            area(stroke.tiles[i], stroke.tiles[i + 1], 1, 1, stroke.allowed[tile] ? good : stroke.short[tile] ? SHORT : FORBIDDEN)
          }
        }
      }

      // Круги перехватчиков: выбранных и, пока ставится новый, всех своих — видно, где защиты ещё нет.
      for (const { x, y, radius } of guards) {
        const size = ROUTE_DOT / camera.zoom
        const dots = Math.ceil((Math.PI * 2 * radius) / GUARD_STEP)
        for (let i = 0; i < dots; i++) {
          const a = (i / dots) * Math.PI * 2
          rect(x + Math.cos(a) * radius - camera.x - size / 2, y + Math.sin(a) * radius - camera.y - size / 2, size, size, GUARD, BORDER_ALPHA)
        }
      }

      if (aim) {
        const size = ROUTE_DOT / camera.zoom
        const cx = Math.floor(aim.x) + 0.5
        const cy = Math.floor(aim.y) + 0.5
        const { radius, core } = BUILDINGS.silo.warhead.blast
        for (const r of [radius, core]) {
          const dots = Math.ceil((Math.PI * 2 * r) / gap)
          for (let i = 0; i < dots; i++) {
            const a = (i / dots) * Math.PI * 2
            rect(cx + Math.cos(a) * r - camera.x - size / 2, cy + Math.sin(a) * r - camera.y - size / 2, size, size, FORBIDDEN, BORDER_ALPHA)
          }
        }
        area(cx - 0.5, cy - 0.5, 1, 1, FORBIDDEN)
      }

      if (target) {
        // Уголки рамки вокруг цели: дышат, чтобы цель находилась глазом, но не мешала смотреть, что под ней.
        const alpha = 0.75 + 0.25 * Math.sin(time * 4)
        const left = target.x - STEP_MARGIN - camera.x
        const top = target.y - STEP_MARGIN - camera.y
        const right = target.x + target.width + STEP_MARGIN - camera.x
        const bottom = target.y + target.height + STEP_MARGIN - camera.y
        const thick = Math.max(BORDER * 3, 3 / camera.zoom)
        for (const [x, y, dx, dy] of [[left, top, 1, 1], [right, top, -1, 1], [left, bottom, 1, -1], [right, bottom, -1, -1]] as const) {
          rect(dx > 0 ? x : x - STEP_CORNER, dy > 0 ? y : y - thick, STEP_CORNER, thick, STEP, alpha)
          rect(dx > 0 ? x : x - thick, dy > 0 ? y : y - STEP_CORNER, thick, STEP_CORNER, STEP, alpha)
        }
      }

      setBlend(gl, 'alpha')
      program.use(view, { uTexture: white })
      rects.draw()
    },
    destroy() {
      rects.destroy()
      program.destroy()
      white.destroy()
    },
  }
}
