import { SCREEN_VERTEX, bindScreen, createProgram, createTarget, drawQuad, setBlend } from '../gl'
import type { Scene } from '../game/scene'
import { WINDOW, wrap, type LandWindow } from '../map/landWindow'
import type { Pass } from '../render/renderer'
import { TEXELS_PER_TILE, blitTexelGrid, createBlitProgram, createTexelGrid, fitTexelGrid } from '../render/texelGrid'
import fragment from './weather.frag?raw'

/**
 * Проход осадков по биомам: пыль в пустынях, снег на солончаках, дождь в топях. Биомы берёт из того же окна местности, что и шейдер карты.
 * Ставить выше игровых слоёв, но ниже освещения: ночью осадки темнеют вместе со всем остальным.
 * Осадки, как и земля, одинаковы в пределах текселя пиксель-арта: вблизи они рисуются в карту в сетке местности
 * и переносятся на экран, см. texelGrid.ts.
 */
export function createPrecipitationPass(gl: WebGL2RenderingContext, scene: Scene, landWindow: LandWindow): Pass {
  const program = createProgram(gl, SCREEN_VERTEX, fragment)
  const blit = createBlitProgram(gl)
  const target = createTarget(gl)
  const grid = createTexelGrid()
  const uniforms = { uWind: new Float32Array(2), uDrift: new Float32Array(2), uPrecipitation: 0, uPixel: 1 }
  const gridUniforms = { uCamera: new Float32Array(2), uScreenSize: new Float32Array(2), uZoom: TEXELS_PER_TILE }

  return {
    draw(frame) {
      const { view, delta } = frame
      const weather = scene.weather
      uniforms.uWind[0] = weather.windX
      uniforms.uWind[1] = weather.windY
      // Снос копится здесь, а не считается в шейдере как ветер × время: иначе смена ветра дёргала бы всю пелену.
      uniforms.uDrift[0] = wrap(uniforms.uDrift[0] + weather.windX * delta, WINDOW)
      uniforms.uDrift[1] = wrap(uniforms.uDrift[1] + weather.windY * delta, WINDOW)
      uniforms.uPrecipitation = weather.precipitation
      if (weather.precipitation <= 0) return

      fitTexelGrid(grid, frame)
      uniforms.uPixel = grid.pixel
      if (!grid.close) {
        setBlend(gl, 'alpha')
        program.use(view, landWindow.uniforms, uniforms)
        drawQuad(gl)
        return
      }
      gridUniforms.uCamera[0] = wrap(grid.centerX, WINDOW)
      gridUniforms.uCamera[1] = wrap(grid.centerY, WINDOW)
      gridUniforms.uScreenSize[0] = grid.width
      gridUniforms.uScreenSize[1] = grid.height
      setBlend(gl, 'none')
      target.bind(grid.width, grid.height)
      program.use(view, landWindow.uniforms, uniforms, gridUniforms)
      drawQuad(gl)
      bindScreen(gl)
      setBlend(gl, 'alpha')
      blitTexelGrid(gl, blit, grid, frame, target.texture)
    },
    destroy() {
      program.destroy()
      blit.destroy()
      target.destroy()
    },
  }
}
