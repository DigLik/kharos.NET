// Проверка интерфейса на живом коде: та же графика панелей, значков и портретов, но без Preact и без игры.
// Запускается вместо игры: `KHAROS_SCRIPT=js/ui.bundle.js cargo run` (сборка — `bun run bundle:native`).
//
// Проверяются картинки, которые интерфейс рисует кодом: скин панелей (`installSkin`), значки грузов (`goodIcon`),
// портреты юнитов и зданий, значки покрытия и уровней (см. `packages/client/src/game/portraits.ts`). Живут они на двумерном холсте
// (см. `src/canvas`), пиксели которого лежат в Rust: PNG у холста несжатый, поэтому по нему видно и сторону картинки,
// и что она не пустая. Раскладка и рисование интерфейса проверяются отдельно — в самом интерфейсе окна (`js/hud.ts`).
//
// В конце картинки собираются мозаикой и уходят в окно (см. `js/show.ts`): числами видно, что холст нарисовал, а
// глазами — что нарисовано. Проверка окна идёт по кадрам, поэтому итог печатается уже из кадра.

import { GOOD_ICON, goodIcon } from '@kharos/client/game/goodIcons'
import { buildingPortrait, levelPortrait, pavePortrait, unitPortrait } from '@kharos/client/game/portraits'
import { BUILDING_TYPES, GOODS, UNIT_TYPES } from '@kharos/sim-ts'
import { installSkin } from '@kharos/client/ui/skin'
import { fromUrl, type Image, paintedBytes } from './png'
import { show, type Sheet } from './show'

/** Сколько сверок сошлось и сколько пикселей нарисовано — итог печатается один раз, в конце. */
let total = 0
let passed = 0
let painted = 0

/** Сверка с ожидаемым: печатает и полученное, и ожидаемое — по строке видно, где разошлось. */
const check = (name: string, got: unknown, want: unknown) => {
  const same = JSON.stringify(got) === JSON.stringify(want)
  total++
  if (same) passed++
  console.log(`${name}: ${JSON.stringify(got)} (ждём ${JSON.stringify(want)}) — ${same ? 'сходится' : 'НЕ сходится'}`)
}

/** Картинки, которые в конце уходят мозаикой на лист проверки. */
const tiles: Image[] = []

/** Сверяет картинку целиком: сторона в пикселях и то, что на ней что-то нарисовано. */
const checkPicture = (name: string, url: string, side: number) => {
  const image = fromUrl(url)
  const bytes = paintedBytes(image)
  painted += bytes
  check(name, [image.width, image.height, bytes > 0], [side, side, true])
  // Пустая картинка на лист не идёт: на нём ищут промахи, а не пустые места.
  if (image.width && image.height) tiles.push(image)
}

// 1. Скин интерфейса: `installSkin` кладёт картинки переменными в корень документа, откуда их берут стили панелей.
installSkin()
const style = document.documentElement.style
/** Рамки скина — 12×12 пикселей, монета — 8×8 (см. `packages/client/src/ui/skin.ts`). */
const SKINS: [string, number][] = [
  ['panel', 12],
  ['well', 12],
  ['button', 12],
  ['button-hover', 12],
  ['button-active', 12],
  ['button-disabled', 12],
  ['button-primary', 12],
  ['button-primary-hover', 12],
  ['gold', 12],
  ['danger', 12],
  ['coin', 8],
]
for (const [name, side] of SKINS) {
  const url = style.getPropertyValue(`--skin-${name}`)
  checkPicture(`скин «${name}»`, url.replace(/^url\(|\)$/g, ''), side)
}

// 2. Значки грузов: мини-иконки для складов, магазинов и перевозок (см. `packages/client/src/game/goodIcons.ts`).
for (const ware of GOODS) checkPicture(`значок груза «${ware}»`, goodIcon(ware), GOOD_ICON)

// 3. Портреты: юниты и здания, вписанные в квадрат панели выбранного (см. `packages/client/src/game/portraits.ts`). Тут рисование
// идёт тем же кодом, что на карте, — но без карты и без видеокарты: спрайт рисуется в пиксмап, а тот идёт на холст.
for (const type of UNIT_TYPES) checkPicture(`портрет юнита «${type}»`, unitPortrait(type, 48), 48)
for (const type of BUILDING_TYPES) checkPicture(`портрет здания «${type}»`, buildingPortrait(type, 64), 64)
// Без квадрата портрет идёт по содержимому: сторону задаёт сам спрайт, а не панель.
const natural = fromUrl(unitPortrait(UNIT_TYPES[0]))
check('портрет юнита без квадрата — по содержимому', natural.width > 0 && natural.height > 0 && paintedBytes(natural) > 0, true)
if (natural.width && natural.height) tiles.push(natural)

// 4. Значки покрытия и уровней: те же, что видно на карте.
for (const icon of ['foundation', 'road', 'bridge', 'remove', 'peak', 'cliff'] as const) {
  checkPicture(`значок покрытия «${icon}»`, pavePortrait(icon, 32), 32)
}
checkPicture('значок уровня', levelPortrait(3, 32), 32)

// 5. Лист проверки: картинки ложатся мозаикой и уходят в окно. Тут сходятся обе половины дела — холст, на котором
// картинки нарисованы, и окно, которое их показывает (см. `js/show.ts`). Проверка окна кадровая: сперва проход показа
// рисует лист, и только потом с экрана читаются точки — иначе читать было бы нечего.
/** Место картинки на листе: увеличение целое, поэтому пиксель холста остаётся ровным квадратом. */
type Place = { image: Image; x: number; y: number; zoom: number }

/** Сторона листа: в окно он ложится пиксель в пиксель, а буфер окна на экране с удвоенными пикселями вдвое плотнее. */
const WIDTH = 1600
/** Зазор между картинками: по нему видно, где кончается одна и начинается другая. */
const GAP = 8
/** Через сколько кадров читается окно: за это время проход показа успевает нарисовать лист. */
const CHECK_FRAME = 30

/** Увеличение картинки: мелкие скины и значки иначе не разглядеть, а портреты и так крупные. */
const zoomOf = (side: number) => (side <= 8 ? 8 : side <= 12 ? 6 : side <= 32 ? 3 : 2)

/**
 * Мозаика картинок на клетчатом фоне: клетка — это «здесь прозрачно», как в редакторах графики. Картинки ложатся на
 * фон смешением, поэтому прозрачных мест у листа нет: что в листе, то и в окне, — по этому и видно, что показ дошёл.
 */
function mosaic(images: Image[]): { sheet: Sheet; places: Place[] } {
  const places: Place[] = []
  let x = 0
  let y = 0
  let tall = 0
  for (const image of images) {
    const zoom = zoomOf(image.width)
    const across = image.width * zoom
    const down = image.height * zoom
    if (x && x + across > WIDTH) {
      y += tall + GAP
      x = 0
      tall = 0
    }
    places.push({ image, x, y, zoom })
    x += across + GAP
    tall = Math.max(tall, down)
  }
  const height = y + tall
  const pixels = new Uint8Array(WIDTH * height * 4)
  for (let row = 0; row < height; row++) {
    for (let column = 0; column < WIDTH; column++) {
      // Клетка 8×8: две тёмные краски в шахматку.
      const shade = ((column >> 3) + (row >> 3)) % 2 ? 0x1c : 0x24
      const at = (row * WIDTH + column) * 4
      pixels[at] = shade
      pixels[at + 1] = shade
      pixels[at + 2] = shade + 8
      pixels[at + 3] = 255
    }
  }
  for (const place of places) {
    const { image, zoom } = place
    for (let row = 0; row < image.height * zoom; row++) {
      const begin = ((row / zoom) | 0) * image.width * 4
      for (let column = 0; column < image.width * zoom; column++) {
        const from = begin + ((column / zoom) | 0) * 4
        const share = image.pixels[from + 3] / 255
        const at = ((place.y + row) * WIDTH + place.x + column) * 4
        for (let channel = 0; channel < 3; channel++) {
          pixels[at + channel] = Math.round(image.pixels[from + channel] * share + pixels[at + channel] * (1 - share))
        }
      }
    }
  }
  return { sheet: { width: WIDTH, height, pixels }, places }
}

/** Итог проверки печатается последним: к этому времени сошлись уже и сверки с экраном. */
function finish() {
  console.log(`проверка интерфейса: ${passed} из ${total} сходятся, ненулевых байт пикселей ${painted}`)
}

/** Читает окно по точкам, где лежат картинки, и сверяет их с листом: так видно, что до драйвера дошёл именно он. */
function checkWindow(gl: WebGL2RenderingContext, sheet: Sheet, places: Place[]) {
  let frames = 0
  const read = (milliseconds: number) => {
    // Первые кадры могут пройти до того, как окно отдало размер: показывать тогда нечего, и в счёт они не идут.
    if (!gl.drawingBufferWidth) {
      requestAnimationFrame(read)
      return
    }
    if (++frames < CHECK_FRAME) {
      requestAnimationFrame(read)
      return
    }
    const width = gl.drawingBufferWidth
    const height = gl.drawingBufferHeight
    console.log(`лист проверки: ${sheet.width}×${sheet.height} пикселей, картинок ${places.length}, окно ${width}×${height}`)
    console.log('в окне — лист проверки: скин панелей, значки грузов, портреты юнитов и зданий, значки покрытия и уровней')
    // Точки берутся по краям листа и посередине: промах всего листа виден и по одной, а так виден и перекос.
    for (const index of [0, places.length >> 1, places.length - 1]) {
      const place = places[index]
      const x = place.x + ((place.image.width * place.zoom) >> 1)
      const y = place.y + ((place.image.height * place.zoom) >> 1)
      const at = (y * sheet.width + x) * 4
      const pixel = new Uint8Array(4)
      // У чтения отсчёт снизу, у листа — сверху, и лист лежит от верхнего края окна: отсюда переворот по высоте окна.
      gl.readPixels(x, height - y - 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel)
      check(`окно: картинка ${index + 1} из ${places.length}`, [pixel[0], pixel[1], pixel[2]], [sheet.pixels[at], sheet.pixels[at + 1], sheet.pixels[at + 2]])
    }
    console.log(`лист в окне рисуется со скоростью ${Math.round(frames / (milliseconds / 1000))} кадров/с`)
    finish()
  }
  requestAnimationFrame(read)
}

const board = mosaic(tiles)
checkWindow(show(board.sheet), board.sheet, board.places)
