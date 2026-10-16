// Картинка интерфейса из её адреса. Интерфейс получает свои картинки адресом (`url(data:image/png;base64,...)`):
// скины панелей уходят в переменные корня (см. `packages/client/src/ui/skin.ts`), значки и портреты — в `<img>` (см.
// `packages/client/src/game/portraits.ts`, `packages/client/src/game/goodIcons.ts`). В браузере адрес разбирает движок, у голого V8 такого нет —
// зато пиксели у нас свои: холст лежит в Rust (см. `src/canvas`), и PNG у него несжатый.
//
// Отсюда и разбор без распаковки: PNG холста выходит несжатым — поток zlib ложится полосами «как есть», а перед
// каждой полосой стоит её фильтр, нулевой, то есть без преобразования (см. `src/canvas/paint.rs`). Сжатия в потоке
// нет: есть только заголовки полос, и по ним пиксели достаются одним чтением. Появится у холста сжатие — здесь
// понадобится распаковка, а её у голого V8 нет, и место ей в Rust.

/** Картинка интерфейса: сторона и пиксели RGBA сверху вниз — те же, что лежат на холсте. */
export type Image = { width: number; height: number; pixels: Uint8Array }

/** Знаки base64: по ним собираются байты картинки — `atob` у голого V8 нет. */
const signs = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'

/** Байты из base64. */
export function fromBase64(text: string): Uint8Array {
  const bytes: number[] = []
  let bits = 0
  let value = 0
  for (const sign of text.replace(/=+$/, '')) {
    value = (value << 6) | signs.indexOf(sign)
    bits += 6
    if (bits >= 8) {
      bits -= 8
      bytes.push((value >> bits) & 0xff)
    }
  }
  return Uint8Array.from(bytes)
}

/** Картинка без стороны и пикселей: адрес оказался чужим, и рисовать нечего. */
const nothing: Image = { width: 0, height: 0, pixels: new Uint8Array(0) }

/**
 * Картинка по адресу. Чужой адрес (не наш `data:image/png;base64,`) даёт пустую картинку — так же ведёт себя и
 * `<img>` с несуществующей ссылкой, а размеры читаются из полей `IHDR`: сторона нужна раскладке.
 */
export function fromUrl(url: string): Image {
  const sign = 'data:image/png;base64,'
  if (!url.startsWith(sign)) return nothing
  const bytes = fromBase64(url.slice(sign.length))
  if (bytes.length < 41 + 2) return nothing
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const width = view.getUint32(16)
  const height = view.getUint32(20)
  const raw = new Uint8Array(height * (width * 4 + 1))
  // Поток zlib: два байта заголовка, полосы со своими длинами (пятибайтные заголовки), четыре байта суммы.
  let read = 41 + 2
  let at = 0
  while (read < 41 + view.getUint32(33) - 4 && at < raw.length) {
    const last = bytes[read]
    const length = bytes[read + 1] | (bytes[read + 2] << 8)
    read += 5
    raw.set(bytes.subarray(read, read + length), at)
    at += length
    read += length
    if (last & 1) break
  }
  // Полосы идут со своими фильтрами: нулевой отдаёт пиксели как есть, другой пришлось бы разворачивать.
  const pixels = new Uint8Array(width * height * 4)
  for (let row = 0; row < height; row++) {
    const begin = row * (width * 4 + 1)
    if (raw[begin] !== 0) return nothing
    pixels.set(raw.subarray(begin + 1, begin + 1 + width * 4), row * width * 4)
  }
  return { width, height, pixels }
}

/** Сколько байт пикселей в картинке не нули: так видно, что картинка не пустая (см. `js/ui.ts`). */
export function paintedBytes(image: Image): number {
  let count = 0
  for (const byte of image.pixels) if (byte) count++
  return count
}

/**
 * Адрес из значения переменной: интерфейс отдаёт картинки в обёртке (`url(data:...)`, см. `packages/client/src/ui/skin.ts`), а холсту
 * нужен сам адрес — обёртку снимает эта функция, как это делает `border-image` у страницы.
 */
export function address(value: string): string {
  return value.startsWith('url(') ? value.slice(4, -1).trim() : value
}

/**
 * Картинка на своём холсте: рисовать умеет холст, а источником `drawImage` берёт тоже холст — у номера холста в
 * Rust (`src/canvas`) есть и пиксели, и сторона, а у картинки из JS только они одни.
 */
export function toCanvas(image: Image): HTMLCanvasElement {
  const canvas = document.createElement('canvas')
  canvas.width = image.width
  canvas.height = image.height
  const context = canvas.getContext('2d')!
  const data = context.createImageData(image.width, image.height)
  data.data.set(image.pixels)
  context.putImageData(data, 0, 0)
  return canvas
}
