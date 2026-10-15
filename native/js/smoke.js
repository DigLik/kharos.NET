// Проверка фундамента нативной игры — запускается вместо собранной игры:
// `KHAROS_SCRIPT=js/smoke.js cargo run`. Тут тот же путь, что у игры, — холст, контекст WebGL 2, программа из GLSL
// ES, набор квадратов с данными на каждый квадрат и кадры. Пока рисуется один квадрат: на нём проверяются перевод
// версии GLSL (игра пишет шейдеры для браузера), буферы, места атрибутов, юниформы, очередь кадров и чтение кадра
// с драйвера — цвет в середине квадрата видно в выводе.

const canvas = document.createElement('canvas')
const gl = canvas.getContext('webgl2')

// Исходники — как в игре: `#version 300 es` и `precision`. Python-перевод версии делает Rust (см. gl/data.rs).
const VERTEX = `#version 300 es
in vec2 aCorner;
in vec4 aRect;
in vec4 aTint;
out vec4 vTint;
void main() {
  vTint = aTint;
  vec2 place = aRect.xy + aCorner * aRect.zw;
  gl_Position = vec4(place.x * 2.0 - 1.0, 1.0 - place.y * 2.0, 0.0, 1.0);
}
`

const FRAGMENT = `#version 300 es
precision mediump float;
in vec4 vTint;
uniform float uTime;
uniform vec3 uColor;
out vec4 outColor;
void main() {
  // Цвет идёт из юниформа-массива: обычным массивом игра задаёт цвета огней и пелены погоды, и если обёртка
  // не переведёт его в числа, свет станет чёрным — то есть невидимым.
  float wave = 0.75 + 0.25 * (0.5 + 0.5 * sin(uTime * 3.0));
  outColor = vec4(vTint.rgb * uColor * wave, vTint.a);
}
`

/** Цвет, заданный обычным массивом — так же, как игровые `LIGHT_COLOR` в проходе освещения. */
const COLOR = [0.5, 0.25, 1]

/** Собирает программу так же, как игра: юниформы находит сама и запоминает их номера. */
function program(vertex, fragment) {
  const compile = (type, source) => {
    const shader = gl.createShader(type)
    gl.shaderSource(shader, source)
    gl.compileShader(shader)
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader))
    return shader
  }
  const handle = gl.createProgram()
  const shaders = [compile(gl.VERTEX_SHADER, vertex), compile(gl.FRAGMENT_SHADER, fragment)]
  for (const shader of shaders) gl.attachShader(handle, shader)
  gl.bindAttribLocation(handle, 0, 'aCorner')
  gl.linkProgram(handle)
  if (!gl.getProgramParameter(handle, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(handle))
  for (const [name, size, type] of uniforms(handle)) console.log(`юниформ ${name}: размер ${size}, вид 0x${type.toString(16)}`)
  return handle
}

/** Обходит юниформы программы: имена, размеры и виды — как это делает `src/gl/program.ts`. */
function* uniforms(handle) {
  const count = gl.getProgramParameter(handle, gl.ACTIVE_UNIFORMS)
  for (let index = 0; index < count; index++) {
    const info = gl.getActiveUniform(handle, index)
    yield [info.name, info.size, info.type]
  }
}

const handle = program(VERTEX, FRAGMENT)
const uTime = gl.getUniformLocation(handle, 'uTime')
const uColor = gl.getUniformLocation(handle, 'uColor')
const attributes = { aRect: 4, aTint: 4 }

// Угол квадрата — общий на всю программу: рисуем по четыре вершины на квадрат.
const corners = gl.createBuffer()
gl.bindBuffer(gl.ARRAY_BUFFER, corners)
gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW)

const stride = attributes.aRect + attributes.aTint
const data = new Float32Array(stride * 4)
const squares = gl.createBuffer()

const vao = gl.createVertexArray()
gl.bindVertexArray(vao)
gl.bindBuffer(gl.ARRAY_BUFFER, corners)
gl.enableVertexAttribArray(0)
gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0)
gl.bindBuffer(gl.ARRAY_BUFFER, squares)
let offset = 0
for (const [name, size] of Object.entries(attributes)) {
  const location = gl.getAttribLocation(handle, name)
  if (location < 0) continue
  gl.enableVertexAttribArray(location)
  gl.vertexAttribPointer(location, size, gl.FLOAT, false, stride * 4, offset * 4)
  gl.vertexAttribDivisor(location, 1)
  offset += size
}
gl.bindVertexArray(null)
console.log(`место aRect: ${gl.getAttribLocation(handle, 'aRect')}`)

const clears = [
  [0.05, 0.08, 0.13, 1],
  [0.11, 0.05, 0.13, 1],
  [0.05, 0.13, 0.09, 1],
]
let clears_ = 0

gl.enable(gl.BLEND)
gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA)

/** Кадр игры: игра просит кадры у обёртки так же, как у браузера. */
function draw(milliseconds) {
  const seconds = milliseconds / 1000
  const width = gl.drawingBufferWidth
  const height = gl.drawingBufferHeight
  if (width > 0 && height > 0) {
    gl.viewport(0, 0, width, height)
    const [red, green, blue, alpha] = clears[Math.floor(seconds) % clears.length]
    gl.clearColor(red, green, blue, alpha)
    gl.clear(gl.COLOR_BUFFER_BIT)

    const size = Math.min(width, height) * 0.4
    data[0] = 0.5 - size / 2 / width
    data[1] = 0.5 - size / 2 / height
    data[2] = size / width
    data[3] = size / height
    data[4] = 0.6
    data[5] = 0.85
    data[6] = 0.35
    data[7] = 1
    gl.useProgram(handle)
    gl.uniform1f(uTime, seconds)
    // Юниформ-массив задаётся обычным массивом чисел — так игра задаёт цвета огней и пелены погоды.
    gl.uniform3fv(uColor, COLOR)
    gl.bindVertexArray(vao)
    gl.bindBuffer(gl.ARRAY_BUFFER, squares)
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW)
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, 1)
    gl.bindVertexArray(null)
    // Проверка, что кадр дошёл до драйвера: цвет читается с экрана в середине квадрата и в углу кадра. Угол — это
    // заливка, и по ней видно, что кадр нарисован и читается верно, а не пуст. Цвет квадрата считают проходы шейдера из
    // данных квадрата и юниформов, поэтому он сверяется с ожидаемым — так видно, что прошли и буферы, и места
    // атрибутов, и юниформы, включая юниформ-массив, заданный обычным массивом чисел.
    if (clears_ === 0) {
      const [red, green, blue] = gl.readPixels(width >> 1, height >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE)
      const [fillRed, fillGreen, fillBlue] = gl.readPixels(4, 4, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE)
      const wave = 0.75 + 0.25 * (0.5 + 0.5 * Math.sin(seconds * 3))
      const expected = COLOR.map((color, i) => Math.round(Math.min(1, data[4 + i] * color * wave) * 255))
      const fill = clears[0].slice(0, 3).map((color) => Math.round(color * 255))
      const close = (value, want) => value.every((part, i) => Math.abs(part - want[i]) <= 3)
      console.log(`центр кадра: ${red}, ${green}, ${blue} (ждём ${expected.join(', ')}) — ${close([red, green, blue], expected) ? 'сходится' : 'НЕ сходится'}`)
      console.log(`угол кадра: ${fillRed}, ${fillGreen}, ${fillBlue} (ждём ${fill.join(', ')}) — ${close([fillRed, fillGreen, fillBlue], fill) ? 'сходится' : 'НЕ сходится'}`)
    }
    clears_++
  }
  console.log(`кадр ${clears_}: ${width}×${height} строк за ${seconds.toFixed(2)} с`)
  requestAnimationFrame(draw)
}

requestAnimationFrame(draw)
