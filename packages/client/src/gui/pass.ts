import { SCREEN_VERTEX, createProgram, createTexture, drawQuad, setBlend } from '../gl'
import type { Pass } from '../render/renderer'

/** Холст интерфейса поверх кадра: пиксель в пиксель, цвет уже умножен на альфу (так его заливает проход). */
const FRAGMENT = `#version 300 es
precision mediump float;
uniform sampler2D uImage;
in vec2 vUV;
out vec4 outColor;

void main() {
  outColor = texture(uImage, vUV);
}
`

/** Чем проходу знать о холсте интерфейса: сам холст и поменялся ли он с прошлой заливки. */
export interface Overlay {
  readonly surface: HTMLCanvasElement
  /** Холст поменялся — его надо залить в текстуру заново. Чтение сбрасывает признак. */
  takeChanged(): boolean
}

/**
 * Проход интерфейса — последний в кадре: кладёт холст интерфейса (gui.ts) поверх мира, курсора и выделения. Заливает
 * холст в текстуру, только когда тот поменялся: интерфейс стоит почти всегда, а заливка во весь экран — не даром.
 * Так интерфейс одинаково ложится в кадр и на странице, и в нативной оболочке, и попадает в записанные ролики.
 */
export function createGuiPass(gl: WebGL2RenderingContext, overlay: Overlay): Pass {
  const program = createProgram(gl, SCREEN_VERTEX, FRAGMENT)
  const texture = createTexture(gl, { width: 1, height: 1 })
  let filled = false

  return {
    draw() {
      const { surface } = overlay
      if (overlay.takeChanged() || !filled) {
        gl.bindTexture(gl.TEXTURE_2D, texture.handle)
        // Умножение на альфу — при заливке: наложение прохода рассчитано на такие цвета (setBlend 'alpha').
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true)
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, surface)
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false)
        filled = true
      }
      setBlend(gl, 'alpha')
      program.use({ uImage: texture })
      drawQuad(gl)
    },
    destroy() {
      texture.destroy()
    },
  }
}
