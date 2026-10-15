// Текст интерфейса на холсте. Шрифты — те же, что у страницы (src/fonts.css): Ark Pixel и GNU Unifont запасным.
//
// Строка рисуется один раз в свою картинку и дальше переносится готовой: так текст не собирается заново каждый кадр.
// Заодно картинка проходит порог по альфе: холст браузера сглаживает края даже у пиксельного шрифта (ореол с альфой
// 28–104 вокруг букв), а после порога буквы те же, что в DOM, — пиксель в пиксель. У нативной оболочки ореола нет
// (см. native/src/canvas/text.rs), и порог там ничего не меняет.
//
// Шрифт страницы приходит кусками по unicode-range, и холст сам их не качает: строку с буквами, кусок которых ещё не
// пришёл, ui сперва меряет тем, что есть, а когда кусок пришёл, — заново (fontsLoaded).

/** Шрифты интерфейса: запись для холста и высота строки в пикселях CSS (верх и низ шрифта). */
export const FONTS = {
  body: { font: '12px "Ark Pixel 12", "Kharos Unifont 12"', line: 16, ascent: 13 },
  small: { font: '10px "Ark Pixel 10", "Kharos Unifont 10"', line: 14, ascent: 11 },
  title: { font: '20px "Ark Pixel 10", "Kharos Unifont 10"', line: 28, ascent: 22 },
} as const

export type FontName = keyof typeof FONTS

/** Сколько строк помнить: интерфейс показывает сотни строк, а числа в нём меняются. */
const LIMIT = 2000

const widths = new Map<string, number>()
const images = new Map<string, HTMLCanvasElement | null>()
let measurer: CanvasRenderingContext2D | null = null
/** Сколько раз приходили новые куски шрифта: интерфейс по нему видит, что строки пора перемерить. */
let generation = 0
const loading = new Set<string>()

const context = () => (measurer ??= document.createElement('canvas').getContext('2d')!)

/** Помнит не больше LIMIT записей: старые уходят первыми. */
function remember<T>(map: Map<string, T>, key: string, value: T) {
  if (map.size >= LIMIT) map.delete(map.keys().next().value!)
  map.set(key, value)
}

/**
 * Кусок шрифта для строки ещё не пришёл — просит его. Когда придёт, меры и картинки строк сбрасываются, а поколение
 * растёт. У оболочки шрифты целиком с диска, и `document.fonts` там нет — там всё уже есть.
 */
function ensure(font: string, text: string) {
  const fonts = (document as { fonts?: FontFaceSet }).fonts
  if (!fonts || fonts.check(font, text)) return
  const key = `${font}|${text}`
  if (loading.has(key)) return
  loading.add(key)
  void fonts.load(font, text).then(() => {
    loading.delete(key)
    widths.clear()
    images.clear()
    generation++
  })
}

/** Сколько раз пришли новые куски шрифта — чтобы интерфейс перерисовался с ними. */
export const fontsLoaded = () => generation

const tones = new Map<string, Uint8ClampedArray>()

/** Цвет строкой — в байты: холст разбирает любую запись цвета, которую понимает страница. */
function tone(color: string) {
  let bytes = tones.get(color)
  if (!bytes) {
    const draw = context()
    draw.clearRect(0, 0, 1, 1)
    draw.fillStyle = color
    draw.fillRect(0, 0, 1, 1)
    bytes = draw.getImageData(0, 0, 1, 1).data
    tones.set(color, bytes)
  }
  return bytes
}

/** Ширина строки в пикселях CSS. */
export function textWidth(name: FontName, text: string): number {
  const { font } = FONTS[name]
  const key = `${name}|${text}`
  let width = widths.get(key)
  if (width === undefined) {
    ensure(font, text)
    const measure = context()
    measure.font = font
    width = Math.ceil(measure.measureText(text).width)
    remember(widths, key, width)
  }
  return width
}

/** Строка, разбитая по словам на строки не шире width; слово длиннее строки идёт отдельной строкой как есть. */
export function wrapText(name: FontName, text: string, width: number): string[] {
  const lines: string[] = []
  let line = ''
  for (const word of text.split(' ')) {
    const next = line ? `${line} ${word}` : word
    if (line && textWidth(name, next) > width) {
      lines.push(line)
      line = word
    } else line = next
  }
  lines.push(line)
  return lines
}

/**
 * Картинка строки цветом color для холста плотностью density (пикселей холста на пиксель CSS): высотой в строку
 * шрифта, буквы — по порогу альфы. Пустая строка — null.
 */
export function textImage(name: FontName, color: string, text: string, density: number): HTMLCanvasElement | null {
  const key = `${name}|${color}|${density}|${text}`
  const cached = images.get(key)
  if (cached !== undefined) return cached
  const { font, line, ascent } = FONTS[name]
  const width = Math.ceil(textWidth(name, text) * density)
  const height = Math.ceil(line * density)
  if (!text || !width) {
    remember(images, key, null)
    return null
  }
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const draw = canvas.getContext('2d')!
  draw.setTransform(density, 0, 0, density, 0, 0)
  draw.font = font
  draw.fillStyle = color
  draw.textBaseline = 'alphabetic'
  draw.fillText(text, 0, ascent)
  const pixels = draw.getImageData(0, 0, width, height)
  const { data } = pixels
  const [r, g, b] = tone(color)
  let soft = false
  for (let i = 0; i < data.length; i += 4) {
    const alpha = data[i + 3]
    if (alpha === 0 || alpha === 255) continue
    soft = true
    // Оставшийся пиксель края — точным цветом строки: у полупрозрачного он после разделения на альфу плывёт.
    if (alpha >= 128) data.set([r, g, b, 255], i)
    else data[i + 3] = 0
  }
  // Полутона были — холст сгладил края: после порога они уходят, а цвет буквы остаётся чистым.
  if (soft) draw.putImageData(pixels, 0, 0)
  remember(images, key, canvas)
  return canvas
}
