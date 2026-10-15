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

// 10. Текст — им интерфейс пишет всё (пункт 10.4 плана). Ark Pixel в родном кегле ложится на сетку: края без
// полутонов, а шаг букв — целые пиксели. Ширину «Kharos Игрок» страница меряет в 72 пикселя.
const FONT = '12px "Ark Pixel 12", "Kharos Unifont 12"'
const words = sheet(120, 20)
words.context.font = FONT
words.context.fillStyle = '#ffffff'
words.context.fillText('Kharos Игрок', 2, 14)
const letters = words.context.getImageData(0, 0, 120, 20)
const alphas = new Set()
let inked = 0
for (let i = 3; i < letters.data.length; i += 4) {
  alphas.add(letters.data[i])
  if (letters.data[i]) inked++
}
check('текст: края без полутонов', [...alphas].sort((a, b) => a - b), [0, 255])
check('текст: буквы нарисованы', inked > 150, true)
check('текст: ширина как у страницы', words.context.measureText('Kharos Игрок').width, 72)
check('текст: верх и низ строки', [words.context.measureText('x').fontBoundingBoxAscent, words.context.measureText('x').fontBoundingBoxDescent], [13, 3])

// Чего нет в Ark Pixel (сербская Ђ, молния), рисует Unifont — в своих родных 16 точках, тоже без полутонов.
const spare = sheet(40, 24)
spare.context.font = FONT
spare.context.fillStyle = '#ffffff'
spare.context.fillText('Ђ⚡', 2, 18)
const spared = spare.context.getImageData(0, 0, 40, 24)
let spareInk = 0
const spareAlphas = new Set()
for (let i = 3; i < spared.data.length; i += 4) {
  spareAlphas.add(spared.data[i])
  if (spared.data[i]) spareInk++
}
check('запасной шрифт: глифы есть', spareInk > 30, true)
check('запасной шрифт: без полутонов', [...spareAlphas].sort((a, b) => a - b), [0, 255])
check('запасной шрифт: шаг — 16 точек Unifont', spare.context.measureText('Ђ').width, 8)

// Выравнивание: по середине строка встаёт вокруг точки письма, по верху — опускается на высоту над базовой линией.
const centered = sheet(100, 30)
centered.context.font = FONT
centered.context.fillStyle = '#ffffff'
centered.context.textAlign = 'center'
centered.context.textBaseline = 'top'
centered.context.fillText('ИИ', 50, 0)
const middleRow = centered.context.getImageData(0, 0, 100, 30)
let left = 100
let right = -1
let top = 30
for (let y = 0; y < 30; y++) {
  for (let x = 0; x < 100; x++) {
    if (!at(middleRow, x, y)[3]) continue
    left = Math.min(left, x)
    right = Math.max(right, x)
    top = Math.min(top, y)
  }
}
check('текст по середине: поровну слева и справа', Math.abs(50 - left - (right + 1 - 50)) <= 1, true)
check('текст по верху: буквы ниже точки', top >= 1 && top <= 6, true)

// 11. Преобразование и стек: сдвиг с увеличением двигает заливку, а restore возвращает прежнее.
const moved = sheet(16)
moved.context.save()
moved.context.translate(4, 4)
moved.context.scale(2, 2)
moved.context.fillStyle = '#00ff00'
moved.context.fillRect(0, 0, 2, 2)
moved.context.restore()
moved.context.fillStyle = '#0000ff'
moved.context.fillRect(0, 0, 2, 2)
const movedPixels = moved.context.getImageData(0, 0, 16, 16)
check('преобразование: заливка сдвинута и увеличена', [at(movedPixels, 4, 4), at(movedPixels, 7, 7), at(movedPixels, 8, 8)], [[0, 255, 0, 255], [0, 255, 0, 255], [0, 0, 0, 0]])
check('restore вернул прежнее преобразование', at(movedPixels, 1, 1), [0, 0, 255, 255])

// 12. Обрезка: заливка всего холста видна только внутри прямоугольника обрезки — так панели обрезают списки.
const clipped = sheet(10)
clipped.context.save()
clipped.context.beginPath()
clipped.context.rect(2, 2, 3, 3)
clipped.context.clip()
clipped.context.fillStyle = '#ffffff'
clipped.context.fillRect(0, 0, 10, 10)
clipped.context.restore()
clipped.context.fillStyle = '#ff0000'
clipped.context.fillRect(8, 8, 2, 2)
const clippedPixels = clipped.context.getImageData(0, 0, 10, 10)
check('обрезка: внутри залито, снаружи пусто', [at(clippedPixels, 3, 3)[3], at(clippedPixels, 6, 6)[3]], [255, 0])
check('после restore обрезки нет', at(clippedPixels, 9, 9), [255, 0, 0, 255])

// 13. Наложение: «destination-out» вырезает — так стирается туман и гаснут подсказки.
const cut = sheet(4)
cut.context.fillStyle = '#ffffff'
cut.context.fillRect(0, 0, 4, 4)
cut.context.globalCompositeOperation = 'destination-out'
cut.context.fillRect(0, 0, 2, 4)
const cutPixels = cut.context.getImageData(0, 0, 4, 4)
check('вырезание: слева пусто, справа осталось', [at(cutPixels, 0, 0)[3], at(cutPixels, 3, 0)[3]], [0, 255])

// 14. Скруглённая рамка и дуга касания: углы срезаны, середина стороны на месте.
const rounded = sheet(20)
rounded.context.fillStyle = '#ffffff'
rounded.context.beginPath()
rounded.context.roundRect(0, 0, 20, 20, 6)
rounded.context.fill()
const roundedPixels = rounded.context.getImageData(0, 0, 20, 20)
check('скругление: угол пуст, середина стороны залита', [at(roundedPixels, 0, 0)[3], at(roundedPixels, 10, 0)[3], at(roundedPixels, 10, 10)[3]], [0, 255, 255])

console.log(`проверка холста: ${good} из ${total} сходятся`)
