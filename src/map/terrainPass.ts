import { SCREEN_VERTEX, bindScreen, createProgram, createTarget, drawQuad, setBlend } from '../gl'
import type { Scene } from '../game/scene'
import type { Pass } from '../render/renderer'
import { TEXELS_PER_TILE, blitTexelGrid, createBlitProgram, createTexelGrid, fitTexelGrid } from '../render/texelGrid'
import { WINDOW, wrap, type LandWindow } from './landWindow'
import fragment from './terrain.frag?raw'

/** Сколько раз в секунду шагает анимация болот. Должно совпадать с ней в terrain.frag. */
const SWAMP_FPS = 5

// Сетка тайлов поверх земли рисуется при переносе на экран: её линии тоньше текселя.
const GRID = `
  vec2 edge = abs(fract(uCamera + tile) - 0.5);
  float line = smoothstep(0.5 - 1.5 / uZoom, 0.5, max(edge.x, edge.y));
  finalColor.rgb *= 1.0 - 0.3 * line * uGrid * smoothstep(10.0, 24.0, uZoom);
`

/**
 * Проход местности: полноэкранный шейдер сам считает, какая точка мира попала в пиксель. Земля одинакова в пределах
 * текселя пиксель-арта, поэтому вблизи она рисуется в карту в сетке местности (см. texelGrid.ts) — в разы меньше
 * пикселей, — а на экран переносится без сглаживания. Нарисованное держится, пока камера, окно местности и
 * шаг анимации болот те же: стоящая камера почти ничего не стоит.
 */
export function createTerrainPass(gl: WebGL2RenderingContext, scene: Scene, landWindow: LandWindow): Pass {
  const program = createProgram(gl, SCREEN_VERTEX, fragment)
  const blit = createBlitProgram(gl, GRID, 'uniform vec2 uCamera; uniform float uZoom; uniform float uGrid;')
  const target = createTarget(gl)
  const grid = createTexelGrid()
  const uniforms = { uDunes: new Float32Array(3), uCamera: new Float32Array(2), uScreenSize: new Float32Array(2), uZoom: 1, uTime: 0, uPixel: 1 }
  const blitUniforms = { uGrid: 0, uZoom: 1, uCamera: landWindow.uniforms.uCamera }
  /** С чем нарисована карта: если всё то же, рисовать заново незачем. */
  const drawn = new Float64Array(12).fill(NaN)
  const next = new Float64Array(12)

  return {
    draw(frame) {
      // Параметры отрисовки читаются каждый кадр, поэтому их смена не пересоздаёт карту.
      const { ergDunes, redDunes, duneMargin } = scene.settings.render
      // Частота барханов переводится в порог шума: чем чаще, тем ниже порог.
      uniforms.uDunes[0] = 0.9 - ergDunes * 0.6
      uniforms.uDunes[1] = 0.9 - redDunes * 0.6
      uniforms.uDunes[2] = duneMargin

      // Тексель мельче двух пикселей экрана шейдер усредняет по нескольким точкам пикселя — там карта в пикселях экрана.
      fitTexelGrid(grid, frame, 2)
      uniforms.uCamera[0] = wrap(grid.centerX, WINDOW)
      uniforms.uCamera[1] = wrap(grid.centerY, WINDOW)
      uniforms.uScreenSize[0] = grid.close ? grid.width : frame.width
      uniforms.uScreenSize[1] = grid.close ? grid.height : frame.height
      uniforms.uZoom = grid.close ? TEXELS_PER_TILE : frame.camera.zoom
      uniforms.uPixel = grid.pixel
      uniforms.uTime = Math.floor(frame.time * SWAMP_FPS) / SWAMP_FPS

      next.set([grid.width, grid.height, uniforms.uCamera[0], uniforms.uCamera[1], uniforms.uZoom, uniforms.uPixel, uniforms.uTime, landWindow.revision])
      next.set(uniforms.uDunes, 8)
      setBlend(gl, 'none')
      if (next.some((value, i) => value !== drawn[i])) {
        drawn.set(next)
        target.bind(grid.width, grid.height)
        program.use(landWindow.uniforms, uniforms)
        drawQuad(gl)
        bindScreen(gl)
      }

      blitUniforms.uGrid = scene.grid ? 1 : 0
      blitUniforms.uZoom = frame.camera.zoom
      blitTexelGrid(gl, blit, grid, frame, target.texture, blitUniforms)
    },
    destroy() {
      program.destroy()
      blit.destroy()
      target.destroy()
    },
  }
}
