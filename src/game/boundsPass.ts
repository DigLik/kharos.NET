import { setBlend } from '../gl'
import type { Pass } from '../render/renderer'
import { createSpriteProgram, createSprites, createWhiteTexture } from '../render/sprites'
import { FOG_ALPHA } from './fogPass'
import type { Scene } from './scene'

/**
 * Край мира — темнота, как у неразведанного тумана, только она не рассеивается: за границей карты ничего не видно.
 * Тон тот же, что у тумана, поэтому неразведанное у края сливается с ней. Рисуется поверх тумана войны: границу мира
 * видно и там, где ещё не бывали. Это не больше четырёх прямоугольников вокруг карты, обрезанных по экрану.
 */
export function createBoundsPass(gl: WebGL2RenderingContext, scene: Scene): Pass {
  const program = createSpriteProgram(gl)
  const sprites = createSprites(gl, program)
  const white = createWhiteTexture(gl)
  const alpha = FOG_ALPHA[0]

  return {
    draw({ camera, width, height, view }) {
      const { bounds } = scene.sim
      const halfWidth = width / 2 / camera.zoom + 1
      const halfHeight = height / 2 / camera.zoom + 1
      const left = camera.x - halfWidth
      const right = camera.x + halfWidth
      const top = camera.y - halfHeight
      const bottom = camera.y + halfHeight
      sprites.clear()
      const dark = (x0: number, y0: number, x1: number, y1: number) => {
        if (x1 > x0 && y1 > y0) sprites.push(x0 - camera.x, y0 - camera.y, x1 - x0, y1 - y0, 0, 0, 1, 1, 0, 0, 0, alpha)
      }
      // Слева и справа — во всю высоту экрана, сверху и снизу — между ними.
      dark(left, top, Math.min(right, bounds.left), bottom)
      dark(Math.max(left, bounds.right), top, right, bottom)
      const innerLeft = Math.max(left, bounds.left)
      const innerRight = Math.min(right, bounds.right)
      dark(innerLeft, top, innerRight, Math.min(bottom, bounds.top))
      dark(innerLeft, Math.max(top, bounds.bottom), innerRight, bottom)
      if (!sprites.count) return

      setBlend(gl, 'alpha')
      program.use(view, { uTexture: white })
      sprites.draw()
    },
    destroy() {
      sprites.destroy()
      program.destroy()
      white.destroy()
    },
  }
}
