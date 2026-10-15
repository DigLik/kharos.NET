import type { Entity } from '../ecs'
import { setBlend } from '../gl'
import type { Pass } from '../render/renderer'
import { createLineProgram, createLines } from '../render/lines'
import { BUILDINGS, Batch, Building, Owner, Position, Site, allZones, isPipe, joints, zonesOf } from '../sim'
import { GOOD_COLORS } from './resourceColors'
import type { Scene } from './scene'

/** Сторона метки пачки в тайлах и толщина её тёмной обводки. */
const MARK = 0.3
const OUTLINE = 0.08
/** Стык зданий вплотную: длина перемычки поперёк общей стороны и её толщина, в тайлах — в пикселях спрайтов, 1/16 тайла. */
const JOINT_LENGTH = 12 / 16
const JOINT_WIDTH = 6 / 16
const JOINT_EDGE = 1 / 16
/** Цвета перемычки: обводка, тело и блик — тона стали чертежей зданий (см. buildingArt.ts). */
const rgb = (color: number) => [((color >> 16) & 255) / 255, ((color >> 8) & 255) / 255, (color & 255) / 255] as const
const JOINT_INK = rgb(0x0b111b)
const JOINT_BODY = rgb(0x41617f)
const JOINT_LIGHT = rgb(0x9bb9d1)

/**
 * Проход потока: что идёт по трубам — бегущие метки цвета груза. Пачка известна клиенту целиком с отправки
 * (путь узлами, тики отправки и прихода, см. Batch), так что метка едет сама, без вестей с хоста. На подземном
 * отрезке между колодцами и над зданиями метки не видно — только на трубе. Хост шлёт пачки только хозяину труб.
 */
export function createFlowPass(gl: WebGL2RenderingContext, scene: Scene): Pass {
  const program = createLineProgram(gl)
  const lines = createLines(gl, program)

  /** Середина узла пути: здание — центр основания, труба — центр тайла. undefined — узла клиент не знает. */
  const centerOf = (entity: Entity) => {
    const { world } = scene.sim
    const position = world.get(entity, Position)
    const type = world.get(entity, Building)?.type ?? world.get(entity, Site)?.type
    if (!position || type === undefined) return undefined
    return { x: position.x + BUILDINGS[type].width / 2, y: position.y + BUILDINGS[type].height / 2, well: type === 'well' }
  }

  /**
   * Подземные отрезки своих колодцев: пунктир от колодца до парного, по графу сети (см. zones.ts). У колодца
   * без пары — красная метка: под землёй он ни с чем не связан.
   */
  const drawWells = (camera: { x: number; y: number }) => {
    const { world } = scene.sim
    const paired = new Set<number>()
    for (const zone of zonesOf(scene.sim, scene.player)) {
      for (let i = 0; i < zone.edges.length; i += 3) {
        const a = zone.edges[i] as Entity
        const b = zone.edges[i + 1] as Entity
        if (world.get(a, Building)?.type !== 'well' || world.get(b, Building)?.type !== 'well') continue
        paired.add(a).add(b)
        const one = centerOf(a)!
        const two = centerOf(b)!
        const length = Math.hypot(two.x - one.x, two.y - one.y)
        // Штрих на каждом тайле отрезка, кроме самих колодцев.
        for (let step = 0.75; step < length - 0.5; step += 1) {
          const from = step / length
          const to = Math.min(step + 0.5, length - 0.5) / length
          lines.push(one.x + (two.x - one.x) * from - camera.x, one.y + (two.y - one.y) * from - camera.y, one.x + (two.x - one.x) * to - camera.x, one.y + (two.y - one.y) * to - camera.y, 0.12, 0.25, 0.39, 0.56, 0.7)
        }
      }
    }
    for (const [entity, building, position, owner] of world.query(Building, Position, Owner)) {
      if (building.type !== 'well' || owner.player !== scene.player || paired.has(entity) || world.has(entity, Site)) continue
      const x = position.x + 0.5 - camera.x
      const y = position.y + 0.5 - camera.y
      lines.push(x - 0.3, y - 0.3, x + 0.3, y + 0.3, 0.12, 0.81, 0.18, 0.14, 0.9)
      lines.push(x - 0.3, y + 0.3, x + 0.3, y - 0.3, 0.12, 0.81, 0.18, 0.14, 0.9)
    }
  }

  /**
   * Стыки зданий вплотную (см. joints в zones.ts): перемычка поперёк середины общей стороны — так видно, что здания
   * связаны в сеть и без трубы. Рисуются у всех, кого клиент знает: стык — такая же постройка, как труба.
   */
  const drawJoints = (camera: { x: number; y: number }, halfWidth: number, halfHeight: number) => {
    const { world } = scene.sim
    const base = (entity: Entity) => {
      const position = world.get(entity, Position)!
      const { width, height } = BUILDINGS[world.get(entity, Building)!.type]
      return { x: position.x, y: position.y, width, height }
    }
    for (const zones of allZones(scene.sim).values()) {
      for (const zone of zones) {
        const pairs = joints(scene.sim, zone)
        for (let i = 0; i < pairs.length; i += 2) {
          const a = base(pairs[i] as Entity)
          const b = base(pairs[i + 1] as Entity)
          // Общая сторона: вертикальная, если основания сходятся по x, иначе горизонтальная.
          const vertical = a.x + a.width === b.x || b.x + b.width === a.x
          const x = vertical ? (a.x + a.width === b.x ? b.x : a.x) : (Math.max(a.x, b.x) + Math.min(a.x + a.width, b.x + b.width)) / 2
          const y = vertical ? (Math.max(a.y, b.y) + Math.min(a.y + a.height, b.y + b.height)) / 2 : a.y + a.height === b.y ? b.y : a.y
          const cx = x - camera.x
          const cy = y - camera.y
          if (Math.abs(cx) > halfWidth || Math.abs(cy) > halfHeight) continue
          const dx = vertical ? JOINT_LENGTH / 2 : 0
          const dy = vertical ? 0 : JOINT_LENGTH / 2
          const edge = JOINT_EDGE * 2
          lines.push(cx - dx - (vertical ? JOINT_EDGE : 0), cy - dy - (vertical ? 0 : JOINT_EDGE), cx + dx + (vertical ? JOINT_EDGE : 0), cy + dy + (vertical ? 0 : JOINT_EDGE), JOINT_WIDTH + edge, ...JOINT_INK, 1)
          lines.push(cx - dx, cy - dy, cx + dx, cy + dy, JOINT_WIDTH, ...JOINT_BODY, 1)
          // Блик по верхней или левой кромке и хомут посередине — тёмной полосой поперёк.
          const shift = (JOINT_WIDTH - JOINT_EDGE) / 2
          lines.push(cx - dx - (vertical ? 0 : shift), cy - dy - (vertical ? shift : 0), cx + dx - (vertical ? 0 : shift), cy + dy - (vertical ? shift : 0), JOINT_EDGE, ...JOINT_LIGHT, 1)
          const collar = (JOINT_WIDTH + edge) / 2
          lines.push(cx - (vertical ? 0 : collar), cy - (vertical ? collar : 0), cx + (vertical ? 0 : collar), cy + (vertical ? collar : 0), edge, ...JOINT_INK, 1)
        }
      }
    }
  }

  return {
    draw({ camera, width, height, view }) {
      const { world, time } = scene.sim
      const now = time.tick + time.alpha
      const halfWidth = width / 2 / camera.zoom + 1
      const halfHeight = height / 2 / camera.zoom + 1
      lines.clear()
      drawJoints(camera, halfWidth, halfHeight)
      for (const [, batch] of world.query(Batch)) {
        const share = (now - batch.sent) / Math.max(1, batch.arrive - batch.sent)
        if (share < 0 || share > 1) continue
        const points = batch.path.map((node) => centerOf(node as Entity))
        if (points.some((point) => point === undefined)) continue
        // Длина пути по отрезкам: метка едет с одной скоростью по всей ломаной.
        let total = 0
        for (let i = 1; i < points.length; i++) total += Math.hypot(points[i]!.x - points[i - 1]!.x, points[i]!.y - points[i - 1]!.y)
        let left = share * total
        for (let i = 1; i < points.length; i++) {
          const from = points[i - 1]!
          const to = points[i]!
          const length = Math.hypot(to.x - from.x, to.y - from.y)
          if (left > length && i < points.length - 1) {
            left -= length
            continue
          }
          // Под землёй — между двумя колодцами, не соседними, — пачки не видно.
          if (from.well && to.well && length > 1) break
          const part = length ? Math.min(1, left / length) : 1
          const atX = from.x + (to.x - from.x) * part
          const atY = from.y + (to.y - from.y) * part
          // Груз видно только на трубе: над зданием метку не рисуют.
          const under = scene.sim.occupancy.at(Math.floor(atX), Math.floor(atY))
          const type = under === undefined ? undefined : world.get(under, Building)?.type
          if (type === undefined || !isPipe(type)) break
          const x = atX - camera.x
          const y = atY - camera.y
          if (Math.abs(x) > halfWidth || Math.abs(y) > halfHeight) break
          const color = GOOD_COLORS[batch.resource]
          const r = ((color >> 16) & 255) / 255
          const g = ((color >> 8) & 255) / 255
          const b = (color & 255) / 255
          const outer = MARK + OUTLINE * 2
          lines.push(x - outer / 2, y, x + outer / 2, y, outer, 0.04, 0.05, 0.08, 1)
          lines.push(x - MARK / 2, y, x + MARK / 2, y, MARK, r, g, b, 1)
          break
        }
      }
      drawWells(camera)
      setBlend(gl, 'alpha')
      program.use(view)
      lines.draw()
    },
    destroy() {
      lines.destroy()
      program.destroy()
    },
  }
}
