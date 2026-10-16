import { biomeAt, terrainAt, type Land } from '@kharos/shared/terrain'
import { BUILDINGS, Building, DEPOSIT_CELL, Mark, Missile, Owner, Position, Site, Unit, alliesOf, buildingSpec, depositsIn, type Sim } from '@kharos/sim-ts'
import { missileAt } from './missiles'
import { ALERT_SECONDS } from './alerts'
import { cssColor } from './resourceColors'
import type { Scene } from './scene'

/**
 * Сторона холста, под которую подобраны размеры значков: на холсте крупнее они растут вместе с ним. Сам холст —
 * какой дали: сторону задаёт настройка игрока и плотность экрана, см. MinimapView в Hud.tsx.
 */
const BASE_SIZE = 132
/** Сколько миллисекунд за кадр можно тратить на подготовку местности мини-карты. */
const BUDGET = 4

/**
 * Цвета местности по биомам, [песок, скала, болото, горы] — средние тона палитр terrain.frag.
 * Мини-карта — схема: детали шейдера ей не нужны.
 */
export const PALETTE: [number, number, number][][] = [
  [[194, 153, 69], [99, 69, 15], [54, 51, 26], [140, 104, 40]],
  [[209, 201, 179], [94, 92, 84], [77, 133, 133], [130, 128, 120]],
  [[179, 92, 41], [84, 36, 20], [69, 33, 18], [128, 64, 36]],
  [[133, 122, 56], [69, 64, 33], [26, 54, 33], [100, 95, 52]],
]

const OWN = '#5aa9ff'
const FOE = '#ff6b5a'
const ALLY = '#4ec973'
const NEUTRAL = '#c8c8c8'

/**
 * Мини-карта: местность всей карты, месторождения, здания и юниты точками и рамка того, что видно на экране.
 * Местность считается по кусочку за кадр, чтобы не подвесить игру: карта проявляется сверху вниз.
 */
/** Насколько тёмен туман на мини-карте, 0–255: не разведано, разведано, видно. */
const FOG_ALPHA = [235, 120, 0]

export function createMinimap(scene: Scene) {
  /** Сторона холста в его пикселях. Карта квадратная: один пиксель — несколько тайлов. */
  let size = BASE_SIZE
  let fog = new ImageData(size, size)
  let fogCanvas = new OffscreenCanvas(size, size)
  let fogChanged = -1
  let fogCells: Uint8Array | null = null
  let sim: Sim | null = null
  let terrain: ImageData | null = null
  let canvas: OffscreenCanvas | null = null
  let row = 0
  /** С какой картой и правкой рисуется местность: клиенту настоящая карта приходит после приветствия, а правки — по ходу игры. */
  let land: Land | null = null
  let revision = 0
  let deposits: { x: number; y: number; color: string }[] = []

  /**
   * Как карта ложится на квадрат мини-карты: вписана по большей стороне, по центру. scale — пикселей на тайл,
   * (left, top) — тайл в левом верхнем углу мини-карты, span — сколько тайлов в её стороне.
   */
  const fit = ({ left, top, right, bottom }: Sim['bounds']) => {
    const span = Math.max(right - left, bottom - top)
    return { span, scale: size / span, left: left - (span - (right - left)) / 2, top: top - (span - (bottom - top)) / 2 }
  }

  /** Мир сменился — местность считается заново. */
  const reset = () => {
    sim = scene.sim
    terrain = new ImageData(size, size)
    canvas = new OffscreenCanvas(size, size)
    row = 0
    const { left, top, right, bottom } = sim.bounds
    deposits = []
    for (let cellY = Math.floor(top / DEPOSIT_CELL); cellY * DEPOSIT_CELL < bottom; cellY++) {
      for (let cellX = Math.floor(left / DEPOSIT_CELL); cellX * DEPOSIT_CELL < right; cellX++) {
        for (const spot of depositsIn(sim, cellX, cellY)) deposits.push({ x: spot.x + 1, y: spot.y + 1, color: cssColor(spot.kind) })
      }
    }
  }

  /** Досчитывает строки местности, пока не выйдет время кадра. */
  const prepare = () => {
    if (!sim || !terrain || row >= size) return
    const { bounds } = sim
    const view = fit(bounds)
    const start = performance.now()
    while (row < size && performance.now() - start < BUDGET) {
      const y = view.top + (row + 0.5) / view.scale
      for (let column = 0; column < size; column++) {
        const x = view.left + (column + 0.5) / view.scale
        const index = (row * size + column) * 4
        // Вне карты — прозрачно: там фон мини-карты.
        if (x < bounds.left || y < bounds.top || x >= bounds.right || y >= bounds.bottom) {
          terrain.data[index + 3] = 0
          continue
        }
        const [r, g, b] = PALETTE[biomeAt(sim.land, x, y)][terrainAt(sim.land, x, y)]
        terrain.data[index] = r
        terrain.data[index + 1] = g
        terrain.data[index + 2] = b
        terrain.data[index + 3] = 255
      }
      row++
    }
    canvas!.getContext('2d')!.putImageData(terrain, 0, 0)
  }

  return {
    /** Рисует мини-карту во весь холст; холст квадратный. */
    draw(context: CanvasRenderingContext2D) {
      // Холст сменил размер — местность и туман считаются заново под него.
      if (context.canvas.width !== size) {
        size = context.canvas.width
        fog = new ImageData(size, size)
        fogCanvas = new OffscreenCanvas(size, size)
        fogCells = null
        sim = null
      }
      if (sim !== scene.sim) reset()
      if (land !== scene.sim.land || revision !== scene.sim.land.revision) {
        land = scene.sim.land
        revision = land.revision
        row = 0
      }
      prepare()
      const { world, bounds } = scene.sim
      const view = fit(bounds)
      const { scale } = view
      const toX = (x: number) => (x - view.left) * scale
      const toY = (y: number) => (y - view.top) * scale
      /** Во сколько раз значки крупнее, чем на холсте BASE_SIZE; точка — не меньше двух пикселей. */
      const k = size / BASE_SIZE
      const dot = Math.max(2, Math.round(2 * k))

      context.fillStyle = '#05090f'
      context.fillRect(0, 0, size, size)
      context.drawImage(canvas!, 0, 0)

      for (const { x, y, color } of deposits) {
        if (!scene.sim.vision.explored(scene.player, x, y)) continue
        context.fillStyle = color
        context.fillRect(toX(x) - dot / 2, toY(y) - dot / 2, dot, dot)
      }
      // Туман: как на карте, только пиксель — несколько тайлов.
      const { cells, changed } = scene.sim.vision.cells(scene.player)
      if (changed !== fogChanged || cells !== fogCells) {
        fogChanged = changed
        fogCells = cells
        const width = bounds.right - bounds.left
        const height = bounds.bottom - bounds.top
        for (let y = 0; y < size; y++) {
          const tileY = Math.floor(view.top + y / scale) - bounds.top
          for (let x = 0; x < size; x++) {
            const tileX = Math.floor(view.left + x / scale) - bounds.left
            const inside = tileX >= 0 && tileY >= 0 && tileX < width && tileY < height
            fog.data[(y * size + x) * 4 + 3] = inside ? FOG_ALPHA[cells[tileY * width + tileX]] : 0
          }
        }
        fogCanvas.getContext('2d')!.putImageData(fog, 0, 0)
      }
      context.drawImage(fogCanvas, 0, 0)

      // Свои — синие, союзники — зелёные, чужие — красные, ничьи — серые, см. allies.ts.
      const friends = alliesOf(scene.sim, scene.player)
      const colorOf = (player: number) => (player === scene.player ? OWN : player === 0 ? NEUTRAL : friends.includes(player) ? ALLY : FOE)
      for (const [entity, position, owner] of world.query(Position, Owner)) {
        const type = world.get(entity, Building)?.type ?? world.get(entity, Site)?.type
        if (type !== undefined) {
          const spec = buildingSpec(type)
          context.fillStyle = colorOf(owner.player)
          context.fillRect(toX(position.x), toY(position.y), Math.max(dot, spec.width * scale), Math.max(dot, spec.height * scale))
        } else if (world.has(entity, Unit)) {
          context.fillStyle = colorOf(owner.player)
          context.fillRect(toX(position.x) - dot / 2, toY(position.y) - dot / 2, dot, dot)
        }
      }

      // Ядерный удар: место пуска — янтарный крест, точка удара — красный круг взрыва, ракета — белая точка.
      for (const [, mark, position] of world.query(Mark, Position)) {
        // Сигнал союзника рисует кольцо уведомления, см. ниже.
        if (mark.kind === 'ping') continue
        const x = toX(position.x)
        const y = toY(position.y)
        context.lineWidth = 2 * k
        if (mark.kind === 'launch') {
          const arm = 4 * k
          context.strokeStyle = '#ffb347'
          context.beginPath()
          context.moveTo(x - arm, y - arm)
          context.lineTo(x + arm, y + arm)
          context.moveTo(x + arm, y - arm)
          context.lineTo(x - arm, y + arm)
          context.stroke()
        } else {
          context.strokeStyle = FOE
          context.beginPath()
          context.arc(x, y, Math.max(4 * k, BUILDINGS.silo.warhead!.blast.radius * scale), 0, Math.PI * 2)
          context.stroke()
        }
      }
      for (const [, missile] of world.query(Missile)) {
        const at = missileAt(missile, scene.sim.time.tick, scene.sim.time.step)
        context.fillStyle = '#ffffff'
        context.fillRect(toX(at.x) - 1.5 * k, toY(at.y) - 1.5 * k, 3 * k, 3 * k)
      }

      // Уведомления: мигающее кольцо там, где напали или заметили врага.
      for (const alert of scene.alerts?.current() ?? []) {
        if (alert.age > ALERT_SECONDS) continue
        const pulse = (alert.age * 2) % 1
        context.strokeStyle = alert.kind === 'attacked' || alert.kind === 'nuke' ? FOE : alert.kind === 'ping' ? ALLY : alert.kind === 'found' || alert.kind === 'intercepted' ? '#8fe0a8' : '#ffd27a'
        context.globalAlpha = 1 - pulse
        context.lineWidth = 2 * k
        context.beginPath()
        context.arc(toX(alert.x), toY(alert.y), (3 + pulse * 12) * k, 0, Math.PI * 2)
        context.stroke()
        context.globalAlpha = 1
      }

      // Рамка экрана.
      const { camera } = scene
      // Только видимая часть: то, что закрыто панелями, на экране не видно.
      const { from, to } = camera.visible
      const line = Math.max(1, Math.round(k))
      context.strokeStyle = '#e4f4ff'
      context.lineWidth = line
      context.strokeRect(
        Math.round(toX(from.x)) + line / 2,
        Math.round(toY(from.y)) + line / 2,
        Math.round((to.x - from.x) * scale),
        Math.round((to.y - from.y) * scale),
      )
    },
    /** Тайл под точкой мини-карты; координаты — доли стороны от 0 до 1. */
    tileAt(u: number, v: number) {
      const view = fit(scene.sim.bounds)
      return { x: view.left + u * view.span, y: view.top + v * view.span }
    },
  }
}

export type Minimap = ReturnType<typeof createMinimap>
