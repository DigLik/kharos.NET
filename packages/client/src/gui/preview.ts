// Схема будущей карты для окна новой игры на холсте — как мини-карта в игре, только без гор: они мельче пикселя схемы.
// Считается порциями, по BUDGET мс за кадр: карта проявляется сверху вниз, а меню не подвисает. Пока ползунок тянут,
// заново не считается — только после паузы SETTLE.

import { PALETTE } from '../game/minimap'
import { Terrain, sampleTerrain, type GeneratorConfig } from '@kharos/shared/terrain'

/** Сторона предпросмотра в пикселях холста. */
const PREVIEW_SIZE = 256
const BUDGET = 8
const SETTLE = 120
/** Цвет превью вне карты, RGBA. */
const OUTSIDE = [8, 17, 28, 255]

/** Какая доля карты под скалами (на них строят и на них руда) и под болотами. */
export interface Shares {
  rock: number
  swamp: number
}

export interface Preview {
  readonly canvas: HTMLCanvasElement
  /** Карта досчитана: схема яркая. */
  readonly ready: boolean
  readonly shares: Shares | null
  /** Показать карту с такой местностью и размером: другая — считается заново. */
  show(config: GeneratorConfig, width: number, height?: number): void
  destroy(): void
}

export function createPreview(): Preview {
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = PREVIEW_SIZE
  const context = canvas.getContext('2d')!
  let key = ''
  let ready = false
  let shares: Shares | null = null
  let timer: ReturnType<typeof setTimeout> | undefined
  let frame = 0

  const draw = (config: GeneratorConfig, size: number, height: number) => {
    const image = context.getImageData(0, 0, PREVIEW_SIZE, PREVIEW_SIZE)
    // Карта вписана в квадрат превью по большей стороне; что вне её — тёмное поле.
    const scale = Math.max(size, height) / PREVIEW_SIZE
    const counts = [0, 0, 0, 0]
    let inside = 0
    let row = 0
    const step = () => {
      const start = performance.now()
      while (row < PREVIEW_SIZE && performance.now() - start < BUDGET) {
        const y = (row + 0.5 - PREVIEW_SIZE / 2) * scale
        for (let column = 0; column < PREVIEW_SIZE; column++) {
          const x = (column + 0.5 - PREVIEW_SIZE / 2) * scale
          const index = (row * PREVIEW_SIZE + column) * 4
          if (Math.abs(x) > size / 2 || Math.abs(y) > height / 2) {
            image.data.set(OUTSIDE, index)
            continue
          }
          const { terrain, biome } = sampleTerrain(config, x, y)
          counts[terrain]++
          inside++
          const [r, g, b] = PALETTE[biome][terrain]
          image.data[index] = r
          image.data[index + 1] = g
          image.data[index + 2] = b
          image.data[index + 3] = 255
        }
        row++
      }
      context.putImageData(image, 0, 0)
      if (row < PREVIEW_SIZE) {
        frame = requestAnimationFrame(step)
        return
      }
      const total = Math.max(1, inside)
      shares = { rock: counts[Terrain.Rock] / total, swamp: counts[Terrain.Swamp] / total }
      ready = true
    }
    frame = requestAnimationFrame(step)
  }

  return {
    canvas,
    get ready() {
      return ready
    },
    get shares() {
      return shares
    },
    show(config, width, height = width) {
      const next = `${JSON.stringify(config)}|${width}|${height}`
      if (next === key) return
      key = next
      ready = false
      clearTimeout(timer)
      cancelAnimationFrame(frame)
      timer = setTimeout(() => draw(config, width, height), SETTLE)
    },
    destroy() {
      clearTimeout(timer)
      cancelAnimationFrame(frame)
    },
  }
}
