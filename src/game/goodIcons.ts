import { isOre, resourceOf, type Good } from '@kharos/sim-ts'
import { GOOD_COLORS } from './resourceColors'

/**
 * Мини-иконки грузов для интерфейса: пиксельные маски 10×10, раскрашенные цветом груза. Знаки маски:
 * o — контур (самый тёмный тон, чтобы иконка читалась на тёмной панели), l — светлый тон, b — основной, d — тёмный, x — вкрапление цвета ресурса (у руды), точка — пусто.
 * Руда — серый ком с вкраплениями цвета своего ресурса: сразу видно, что это сырьё и для чего.
 */
const MASKS: Record<string, string[]> = {
  // Слиток.
  metal: [
    '..........',
    '..........',
    '..oooooo..',
    '..ollllo..',
    '.olbbbbdo.',
    '.obbbbbdo.',
    'obbbbbbddo',
    'oooooooooo',
    '..........',
    '..........',
  ],
  // Кристалл.
  silicon: [
    '....oo....',
    '...olbo...',
    '..olbbdo..',
    '..olbbdo..',
    '..olbbdo..',
    '..olbbdo..',
    '..olbbdo..',
    '...obdo...',
    '....oo....',
    '..........',
  ],
  // Канистра.
  fuel: [
    '..........',
    '...ooo....',
    '..oo.oooo.',
    '.olllllbo.',
    '.olbbbbdo.',
    '.olbddbdo.',
    '.olbbbbdo.',
    '.olbbbbdo.',
    '.oddddddo.',
    '.oooooooo.',
  ],
  // Гранёный камень.
  kharite: [
    '..........',
    '..oooooo..',
    '.olllbbbo.',
    'oolllbbddo',
    '.olbbbbdo.',
    '..olbbdo..',
    '...obdo...',
    '....oo....',
    '..........',
    '..........',
  ],
  // Столбик лепёшек.
  lithium: [
    '..........',
    '...oooo...',
    '..olllbo..',
    '..oddddo..',
    '..olllbo..',
    '..oddddo..',
    '..olllbo..',
    '..obbbdo..',
    '..oddddo..',
    '...oooo...',
  ],
  // Таблетка со знаком радиации.
  uranium: [
    '...oooo...',
    '..olddlo..',
    '.ollddllo.',
    'obllddllbo',
    'obbbllbbbo',
    'oddbllbddo',
    'odddbbdddo',
    '.oddbbddo.',
    '..obbbbo..',
    '...oooo...',
  ],
  // Три стержня связкой, торцы светятся.
  rods: [
    '.oo.oo.oo.',
    '.ol.ol.ol.',
    '.ob.ob.ob.',
    '.ob.ob.ob.',
    '.ob.ob.ob.',
    '.ob.ob.ob.',
    'oooooooooo',
    'odddddddd.',
    '.ob.ob.ob.',
    '.od.od.od.',
  ],
  // Кладка.
  blocks: [
    '..........',
    '..........',
    'oooooooooo',
    'olbbdolbbd',
    'oddddodddd',
    'oooooooooo',
    'olbdolbbdo',
    'oddoddddoo',
    'oooooooooo',
    '..........',
  ],
  // Два патрона.
  ammo: [
    '..o....o..',
    '.olo..olo.',
    '.olo..olo.',
    '.ooo..ooo.',
    '.olo..olo.',
    '.obo..obo.',
    '.obo..obo.',
    '.odo..odo.',
    '.ooo..ooo.',
    '..........',
  ],
  // Микросхема.
  parts: [
    '..........',
    '..o.o.o...',
    '.ooooooo..',
    'oolllllboo',
    '.olbbbbdo.',
    'oolbbbbdoo',
    '.oddddddo.',
    '.ooooooo..',
    '..o.o.o...',
    '..........',
  ],
  // Ком руды.
  ore: [
    '..........',
    '...oooo...',
    '..odbbdo..',
    '.odbxbbdo.',
    '.obbbbxbo.',
    'odbxbbbbdo',
    'obbbbxbbdo',
    'oddbbbddo.',
    '.oooooooo.',
    '..........',
  ],
}

const SIZE = 10
/** Камень руды: тона серо-бурого кома. */
const ROCK = 0x7a6f62

/** Цвет, сдвинутый к белому (amount > 0) или к чёрному (amount < 0). */
function shade(color: number, amount: number) {
  const channel = (shift: number) => {
    const value = (color >> shift) & 255
    return Math.round(amount > 0 ? value + (255 - value) * amount : value * (1 + amount))
  }
  return [channel(16), channel(8), channel(0)] as const
}

/** Сторона мини-иконки груза в пикселях. */
export const GOOD_ICON = SIZE

/** Рисует мини-иконку груза: put получает место и цвет каждого закрашенного пикселя. Работает и без браузера. */
export function paintGood(good: Good, put: (x: number, y: number, color: readonly [number, number, number]) => void) {
  const ore = isOre(good)
  const base = ore ? ROCK : GOOD_COLORS[good]
  const tones: Record<string, readonly [number, number, number]> = {
    o: shade(base, -0.7),
    l: shade(base, 0.4),
    b: shade(base, 0),
    d: shade(base, -0.4),
    x: shade(ore ? GOOD_COLORS[resourceOf(good)] : base, 0.1),
  }
  MASKS[ore ? 'ore' : good].forEach((row, y) => {
    for (let x = 0; x < SIZE; x++) {
      const tone = tones[row[x]]
      if (tone) put(x, y, tone)
    }
  })
}

const canvases = new Map<Good, HTMLCanvasElement>()
const cache = new Map<Good, string>()

/** Мини-иконка груза холстом: её рисует интерфейс на холсте (src/gui). Рисуется один раз. */
export function goodCanvas(good: Good) {
  let canvas = canvases.get(good)
  if (canvas) return canvas
  const image = new ImageData(SIZE, SIZE)
  paintGood(good, (x, y, tone) => image.data.set([...tone, 255], (y * SIZE + x) * 4))
  canvas = document.createElement('canvas')
  canvas.width = canvas.height = SIZE
  canvas.getContext('2d')!.putImageData(image, 0, 0)
  canvases.set(good, canvas)
  return canvas
}

/** Адрес картинки мини-иконки груза. Рисуется один раз. */
export function goodIcon(good: Good) {
  let url = cache.get(good)
  if (!url) cache.set(good, (url = goodCanvas(good).toDataURL()))
  return url
}
