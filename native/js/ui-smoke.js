// Проверка двумерного холста — запускается вместо собранной игры: `KHAROS_SCRIPT=js/ui-smoke.js cargo run`.
// Тут тот же путь, что у интерфейса: холст игры (его заводит `stage.ts`), холсты-вычислители (скины, значки,
// портреты, мини-карта), заливка, путь, перенос картинки и картинка строкой. Пиксели сверяются с ожидаемыми:
// рисование ушло в Rust (см. `src/canvas`), и по цвету в середине фигуры видно, что прошли и цвет, и путь, и мост.

/** Сколько проверок сошлось — итог печатается один раз, в конце. */
let total = 0
let good = 0

/** Один пиксель картинки: четыре байта, как их читает игра. */
const at = (image, x, y) => {
  const index = (y * image.width + x) * 4
  return [...image.data.slice(index, index + 4)]
}

/** Сверка с ожидаемым: печатает и полученное, и ожидаемое — по строке видно, где разошлось. */
const check = (name, got, want) => {
  const same = JSON.stringify(got) === JSON.stringify(want)
  total++
  if (same) good++
  console.log(`${name}: ${JSON.stringify(got)} (ждём ${JSON.stringify(want)}) — ${same ? 'сходится' : 'НЕ сходится'}`)
}

/** Холст-вычислитель с готовым контекстом: так их заводят скины, значки и портреты. */
const sheet = (width, height = width) => {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  return { canvas, context: canvas.getContext('2d') }
}

// Холст игры заводится тем же способом, что у игры (см. `stage.ts`): тем, у кого просят WebGL 2, он и становится.
const game = document.createElement('canvas')
check('холст игры получил WebGL 2', !!game.getContext('webgl2'), true)
check('холст игры не подменяет холст окна', game !== document.getElementById(), true)

// 1. Заливка: так нарисованы и скины интерфейса, и местность под мини-картой.
const flat = sheet(8)
flat.context.fillStyle = '#ff0000'
flat.context.fillRect(2, 2, 4, 4)
const painted = flat.context.getImageData(0, 0, 8, 8)
check('заливка: середина', at(painted, 3, 3), [255, 0, 0, 255])
check('заливка: за краем пусто', at(painted, 0, 0), [0, 0, 0, 0])

// 2. Картинка туда и обратно: так мини-карта кладёт местность (см. minimap.ts), и так же её читает меню (MapPreview).
const pattern = new ImageData(2, 2)
pattern.data.set([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255])
flat.context.putImageData(pattern, 3, 1)
const round = flat.context.getImageData(3, 1, 2, 2)
check('пиксели туда и обратно', [...round.data], [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255])

// 3. Перенос с увеличением без сглаживания: так портреты вписываются в квадрат (см. portraits.ts).
const tiny = sheet(2)
const tinyPattern = new ImageData(2, 2)
tinyPattern.data.set([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255])
tiny.context.putImageData(tinyPattern, 0, 0)
const big = sheet(4)
big.context.imageSmoothingEnabled = false
big.context.drawImage(tiny.canvas, 0, 0, 4, 4)
const scaled = big.context.getImageData(0, 0, 4, 4)
check('увеличение: левый верхний угол', at(scaled, 0, 0), [255, 0, 0, 255])
check('увеличение: правый нижний угол', at(scaled, 3, 3), [255, 255, 0, 255])

// 4. Перенос один к одному поверх готового: так на местность ложится туман — прозрачное не затирает нижнее.
const layer = sheet(2)
const sparse = new ImageData(2, 2)
sparse.data.set([0, 0, 255, 255])
layer.context.putImageData(sparse, 0, 0)
const background = sheet(2)
background.context.fillStyle = '#05090f'
background.context.fillRect(0, 0, 2, 2)
background.context.drawImage(layer.canvas, 0, 0)
const over = background.context.getImageData(0, 0, 2, 2)
check('прозрачное не затирает фон', at(over, 1, 1), [5, 9, 15, 255])
check('непрозрачное ложится поверх', at(over, 0, 0), [0, 0, 255, 255])

// 5. Путь: крестик и кольцо — так мини-карта помечает сигналы и ядерный удар (см. minimap.ts).
const marks = sheet(11)
marks.context.strokeStyle = '#ffb347'
marks.context.lineWidth = 2
marks.context.beginPath()
marks.context.moveTo(2, 2)
marks.context.lineTo(8, 8)
marks.context.moveTo(8, 2)
marks.context.lineTo(2, 8)
marks.context.stroke()
const cross = marks.context.getImageData(0, 0, 11, 11)
check('крестик: середина', at(cross, 5, 5), [255, 179, 71, 255])
check('крестик: выше него пусто', at(cross, 5, 0), [0, 0, 0, 0])

const ring = sheet(21)
ring.context.strokeStyle = '#ff6b5a'
ring.context.lineWidth = 2
ring.context.beginPath()
ring.context.arc(10, 10, 6, 0, Math.PI * 2)
ring.context.stroke()
const circle = ring.context.getImageData(0, 0, 21, 21)
check('кольцо: точка на окружности', at(circle, 16, 10), [255, 107, 90, 255])
check('кольцо: середина пуста', at(circle, 10, 10), [0, 0, 0, 0])

// 6. Заливка контура: так залит круг, которым помечают место удара.
const disk = sheet(21)
disk.context.fillStyle = '#4ec973'
disk.context.beginPath()
disk.context.arc(10, 10, 5, 0, Math.PI * 2)
disk.context.fill()
const filled = disk.context.getImageData(0, 0, 21, 21)
check('залитый круг: середина', at(filled, 10, 10), [78, 201, 115, 255])
check('залитый круг: за краем пусто', at(filled, 18, 10), [0, 0, 0, 0])

// 7. Прозрачность и короткая запись цвета: ими гасят значки и пишут тона скина (см. skin.ts).
const blended = sheet(2)
blended.context.fillStyle = '#000'
blended.context.fillRect(0, 0, 2, 2)
blended.context.globalAlpha = 0.5
blended.context.fillStyle = 'rgb(255, 255, 255)'
blended.context.fillRect(0, 0, 2, 2)
check('половина прозрачности поверх чёрного', at(blended.context.getImageData(0, 0, 2, 2), 0, 0), [128, 128, 128, 255])
blended.context.globalAlpha = 1
blended.context.fillStyle = '#0f0'
blended.context.fillRect(1, 1, 1, 1)
check('короткая запись цвета', at(blended.context.getImageData(0, 0, 2, 2), 1, 1), [0, 255, 0, 255])

// 8. Картинка строкой: её берут стили скина (см. skin.ts) и портреты (см. portraits.ts). Читается она разбором
// base64: по сигнатуре видно, что это PNG, а по полям IHDR — что размеры холста дошли до картинки.
const skin = sheet(12)
const paint = (x, y, width, height, color) => {
  skin.context.fillStyle = color
  skin.context.fillRect(x, y, width, height)
}
paint(1, 0, 10, 1, '#0b111b')
paint(0, 1, 1, 10, '#0b111b')
paint(1, 1, 10, 10, '#0e1825')
const url = skin.canvas.toDataURL()
check('скин отдан строкой', url.startsWith('data:image/png;base64,'), true)

/** Разбор base64: своим разбором видно и сигнатуру PNG, и поля IHDR — подстрокой столько не проверить. */
const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
const fromBase64 = (text) => {
  const bytes = []
  let bits = 0
  let value = 0
  for (const sign of text.slice(text.indexOf(',') + 1).replace(/=+$/, '')) {
    value = (value << 6) | alphabet.indexOf(sign)
    bits += 6
    if (bits >= 8) {
      bits -= 8
      bytes.push((value >> bits) & 0xff)
    }
  }
  return bytes
}
const png = fromBase64(url)
check('сигнатура PNG', png.slice(0, 8), [137, 80, 78, 71, 13, 10, 26, 10])
check('размеры картинки в IHDR', png.slice(16, 24), [0, 0, 0, 12, 0, 0, 0, 12])

// 9. Вычислитель: им мини-карта ведёт туман (см. `new OffscreenCanvas` в minimap.ts).
const offscreen = new OffscreenCanvas(3, 3)
offscreen.context = offscreen.getContext('2d')
offscreen.context.fillStyle = '#c8c8c8'
offscreen.context.fillRect(0, 0, 3, 3)
check('вычислитель рисует и читается', at(offscreen.context.getImageData(0, 0, 3, 3), 2, 2), [200, 200, 200, 255])
check('у вычислителя видны размеры', [offscreen.width, offscreen.height], [3, 3])

console.log(`проверка холста: ${good} из ${total} сходятся`)
