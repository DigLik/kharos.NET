// Показ картинки в окне: один квад с текстурой. Прежде рисовать в окно умела только игра — у неё проходы свои, а
// общего показа картинок не было: интерфейс в браузере рисует не видеокарта, а страница, и переносить это в оболочку
// ещё предстоит. Здесь показывается то, что уже нарисовано двумерным холстом, — по нему видно, что холст и окно
// сходятся, и проверку интерфейса видно глазами, а не только по числам в журнале (см. `js/ui.ts`).
//
// Картинка ложится в левый верхний угол, пиксель в пиксель: скины в проверке крошечные (12×12), и растяжение показало
// бы их сглаженными, а не такими, какими их нарисовал холст.
//
// Квад перерисовывается каждый кадр: после показа буфера драйвер его содержимое помнить не обязан — так же кадр
// рисует и игра (см. `js/game.ts`).

/** Картинка для показа: сторона в пикселях и пиксели RGBA сверху вниз — так их отдаёт холст. */
export type Sheet = { width: number; height: number; pixels: Uint8Array | Uint8ClampedArray }

/** Вершины квада: два треугольника по углам картинки. Место — в пикселях картинки, отсчёт сверху. */
const quad = (sheet: Sheet): Float32Array =>
  new Float32Array([0, 0, sheet.width, 0, 0, sheet.height, 0, sheet.height, sheet.width, 0, sheet.width, sheet.height])

/**
 * Вершинный шейдер: место в картинке становится местом на экране (`size` — сторона буфера окна). Переворота здесь
 * нет: полосы пикселей идут сверху вниз и ложатся в текстуру с её низа, поэтому место в текстуре — то же место в
 * картинке.
 */
const VERTEX = `#version 300 es
in vec2 at;
uniform vec2 size;
uniform vec2 sheet;
out vec2 spot;
void main() {
  spot = vec2(at.x / sheet.x, at.y / sheet.y);
  gl_Position = vec4(at.x / size.x * 2.0 - 1.0, 1.0 - at.y / size.y * 2.0, 0.0, 1.0);
}
`

/** Штриховой шейдер: цвет берётся из картинки как есть, без сглаживания (фильтр — `NEAREST`). */
const FRAGMENT = `#version 300 es
precision mediump float;
in vec2 spot;
uniform sampler2D image;
out vec4 color;
void main() {
  color = texture(image, spot);
}
`

/** Собирает шейдер: не собрался — в ошибке виден и исходник, и что сказал драйвер. */
function shader(gl: WebGL2RenderingContext, type: number, source: string, name: string): WebGLShader {
  const made = gl.createShader(type)
  if (!made) throw new Error(`${name}: драйвер не дал шейдер`)
  gl.shaderSource(made, source)
  gl.compileShader(made)
  if (!gl.getShaderParameter(made, gl.COMPILE_STATUS)) {
    throw new Error(`${name}: шейдер не собрался — ${gl.getShaderInfoLog(made)}`)
  }
  return made
}

/**
 * Показывает картинку в окне и возвращает контекст окна: им проверяют, что показанное дошло до драйвера (чтением
 * пикселей с экрана). Холст берётся тем же путём, что у игры: холст с контекстом WebGL 2 и есть холст окна.
 */
export function show(sheet: Sheet): WebGL2RenderingContext {
  const canvas = document.createElement('canvas')
  const gl = canvas.getContext('webgl2')
  if (!gl) throw new Error('у окна нет WebGL 2')

  const program = gl.createProgram()
  if (!program) throw new Error('драйвер не дал проход')
  gl.attachShader(program, shader(gl, gl.VERTEX_SHADER, VERTEX, 'проход показа (вершины)'))
  gl.attachShader(program, shader(gl, gl.FRAGMENT_SHADER, FRAGMENT, 'проход показа (штрихи)'))
  gl.linkProgram(program)
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    throw new Error(`проход показа не связался — ${gl.getProgramInfoLog(program)}`)
  }
  gl.useProgram(program)

  const vertices = gl.createBuffer()
  gl.bindBuffer(gl.ARRAY_BUFFER, vertices)
  gl.bufferData(gl.ARRAY_BUFFER, quad(sheet), gl.STATIC_DRAW)
  const array = gl.createVertexArray()
  gl.bindVertexArray(array)
  const corner = gl.getAttribLocation(program, 'at')
  gl.enableVertexAttribArray(corner)
  gl.vertexAttribPointer(corner, 2, gl.FLOAT, false, 0, 0)

  const image = gl.createTexture()
  gl.bindTexture(gl.TEXTURE_2D, image)
  // Полоса картинки идёт вплотную к следующей: у стороны, не кратной четырём, иначе сдвинулись бы ряды.
  gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1)
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, sheet.width, sheet.height, 0, gl.RGBA, gl.UNSIGNED_BYTE, sheet.pixels)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE)
  gl.uniform1i(gl.getUniformLocation(program, 'image'), 0)

  const size = gl.getUniformLocation(program, 'size')
  const place = gl.getUniformLocation(program, 'sheet')

  const draw = () => {
    const width = gl.drawingBufferWidth
    const height = gl.drawingBufferHeight
    // До первого показа окна буфер пуст: рисовать в него нечего, а после смены размера кадр придёт заново.
    if (width && height) {
      gl.viewport(0, 0, width, height)
      // Проход показа — последний в кадре: сцена игры рисует сама, а поверх ложится интерфейс. Прозрачные места
      // картинки пропускают то, что под ней: у листа проверки таких нет, а у панелей интерфейса будут.
      gl.disable(gl.DEPTH_TEST)
      gl.disable(gl.CULL_FACE)
      gl.enable(gl.BLEND)
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA)
      gl.clearColor(0.07, 0.08, 0.1, 1)
      gl.clear(gl.COLOR_BUFFER_BIT)
      gl.useProgram(program)
      gl.bindVertexArray(array)
      gl.activeTexture(gl.TEXTURE0)
      gl.bindTexture(gl.TEXTURE_2D, image)
      gl.uniform2fv(size, [width, height])
      gl.uniform2fv(place, [sheet.width, sheet.height])
      gl.drawArrays(gl.TRIANGLES, 0, 6)
    }
    requestAnimationFrame(draw)
  }
  requestAnimationFrame(draw)
  return gl
}
