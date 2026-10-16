// Интерфейс в нативном окне: панель собрана деревом (`dom.ts`), разложена (`layout.ts`) и нарисована (`draw.ts`) —
// тот же путь, каким пойдёт и настоящий HUD, но пока одним компонентом. Компонент — панель сведений о выделенном
// (см. `packages/client/src/ui/Hud.tsx`): портрет в утопленном окне, значки грузов, кнопки и монета кредитов. Ни подписей, ни
// событий здесь ещё нет: шрифта и ввода у оболочки нет (см. README), и это следующие шаги.
//
// Запускается вместо собранной игры: `KHAROS_SCRIPT=js/hud.bundle.js cargo run` (сборка — `bun run bundle:native`).
// Проверка идёт по числам: сверяются места узлов (раскладка), цвета на холсте (рисование) и то же самое в окне —
// значит панель не только нарисована, но и показана.

import { goodIcon } from '@kharos/client/game/goodIcons'
import { unitPortrait } from '@kharos/client/game/portraits'
import { GOODS, UNIT_TYPES } from '@kharos/sim-ts'
import { installSkin } from '@kharos/client/ui/skin'
import { box, createDocument, Element, type Document, type Node, picture } from './dom'
import { draw } from './draw'
import { frame, layout } from './layout'
import { address, fromUrl } from './png'
import { show } from './show'

/** Сторона портрета и кнопок — те же, что у браузерной панели (см. `PORTRAIT` в `packages/client/src/ui/Hud.tsx`). */
const PORTRAIT = 96
const BUTTON = 24
/** Ячейка значка: у страницы это квадрат 40 со своим фоном, а сам значок груза — 10 пикселей (см. `.bar__units`). */
const CELL = 40
/** Сколько значков грузов ставится в ряд: у браузерной панели их столько же в строке сведений. */
const ICONS = 4
/** Через сколько кадров читается окно: к этому времени кадр показан, а окно отдало свой размер. */
const CHECK_FRAME = 30

/** Сколько сверок сошлось — итог печатается один раз, в конце. */
let total = 0
let passed = 0

/** Сверка с ожидаемым: печатает и полученное, и ожидаемое — по строке видно, где разошлось. */
function check(name: string, got: unknown, want: unknown): void {
  const same = JSON.stringify(got) === JSON.stringify(want)
  total++
  if (same) passed++
  console.log(`${name}: ${JSON.stringify(got)} (ждём ${JSON.stringify(want)}) — ${same ? 'сходится' : 'НЕ сходится'}`)
}

/** Ячейка значка: у страницы это квадрат сорок со своим фоном, а сам значок стоит в нём по центру. */
const cell = (icon: Element): Element =>
  box({ width: CELL, height: CELL, skin: 'well', display: 'flex', justifyContent: 'center', alignItems: 'center' }, icon)

/**
 * Панель сведений о выделенном: утопленное окно с портретом, значки грузов, кнопки команд и монета кредитов.
 * Разметка — кодом, стили — при узле: у оболочки нет ни таблицы стилей, ни разметки (см. `docs/design.md`, 10.4).
 */
function bar(document: Document): Parts {
  // Монета — картинка скина целиком, а не рамка: у страницы она лежит фоном 16×16 (см. `.hud__coin` в `packages/client/src/style.css`).
  const coin = address(document.documentElement.style.getPropertyValue('--skin-coin'))
  const portrait = picture(unitPortrait(UNIT_TYPES[0], PORTRAIT))
  const well = box({ width: 104, height: 104, padding: 4, skin: 'well' }, portrait)
  const icons = box({ display: 'flex', gap: 4 }, ...GOODS.slice(0, ICONS).map((good) => cell(picture(goodIcon(good)))))
  const buttons = box({ display: 'flex', gap: 4 }, ...Array.from({ length: 3 }, () => box({ width: BUTTON, height: BUTTON, skin: 'button' })))
  const column = box({ display: 'flex', flexDirection: 'column', gap: 6, flexGrow: 1 }, icons, buttons)
  const money = picture(coin, { width: 16, height: 16 })
  // Полоса запаса: заливка цветом — так у страницы нарисованы полоски прочности и хода работ.
  const strip = box({ width: 96, height: 6, background: '#5a3d0c' })
  const stock = box({ display: 'flex', flexDirection: 'column', alignItems: 'end', gap: 6 }, money, strip)
  const panel = box(
    { position: 'absolute', left: 16, right: 16, bottom: 16, display: 'flex', alignItems: 'center', padding: 8, gap: 10, skin: 'well' },
    well,
    column,
    stock,
  )
  return { panel, well, portrait, icons, buttons, column, stock, coin: money, strip }
}


/** Узлы панели по именам: проверкам нужны места и рисунки каждого. */
type Parts = {
  panel: Element
  well: Element
  portrait: Element
  icons: Element
  buttons: Element
  column: Element
  stock: Element
  coin: Element
  strip: Element
}

/** Цвет пикселя картинки интерфейса: сверка рисунка идёт с самой картинкой, а не с выписанным числом. */
function sample(url: string, share = 0.5): number[] {
  const image = fromUrl(url)
  const x = Math.floor(image.width * share)
  const y = Math.floor(image.height * share)
  const at = (y * image.width + x) * 4
  return [...image.pixels.slice(at, at + 4).slice(0, 3)]
}

/** Цвет пикселя на холсте: так сверяются нарисованное, показанное и прочитанное с экрана. */
const pixel = (context: CanvasRenderingContext2D, x: number, y: number) => [...context.getImageData(x, y, 1, 1).data].slice(0, 3)

/** Середина узла: точки проверок берутся там, где рисунок точно есть. */
const middle = (node: Element): [number, number] => {
  const rect = frame(node)
  return [rect.x + (rect.width >> 1), rect.y + (rect.height >> 1)]
}

/** Сколько узлов в дереве: по числу видно, что собралось дерево, а не один узел. */
function count(node: Node): number {
  return node.childNodes.reduce((sum, child) => sum + count(child), 1)
}

/** Узлы-дети, а не подписи: проверки ходят по картинкам и кнопкам. */
const elements = (node: Element) => node.childNodes.filter((child): child is Element => child instanceof Element)

/** Сверки раскладки: места узлов — то, чем панель отличается от кучи нарисованных картинок. */
function checkLayout(parts: Parts, width: number, height: number): void {
  const panel = frame(parts.panel)
  const well = frame(parts.well)
  const stamps = elements(parts.icons)
  const marks = elements(stamps[0])
  const coin = frame(parts.coin)
  const stock = frame(parts.stock)
  check('панель прижата к низу окна с отступом', [panel.x, height - (panel.y + panel.height)], [16, 16])
  check('панель растянута между краями', panel.width, width - 32)
  check('высота панели — по содержимому', panel.height, 8 * 2 + 104)
  check('утопленное окно встало у левого края', [well.x - panel.x, well.width, well.height], [8, 104, 104])
  check('портрет вписан в своё окно', [frame(parts.portrait).x - well.x, frame(parts.portrait).width], [4, 96])
  check('ячейки значков — ряд с промежутком', [frame(stamps[1]).x - frame(stamps[0]).x, frame(stamps[0]).width], [CELL + 4, CELL])
  check('значков четыре', [stamps.length, frame(stamps[3]).x - frame(stamps[0]).x], [ICONS, 3 * (CELL + 4)])
  check('значок стоит в ячейке по центру', [frame(marks[0]).width, frame(marks[0]).x - frame(stamps[0]).x], [10, (CELL - 10) / 2])
  check('кнопок три в ряд', parts.buttons.childNodes.length, 3)
  check('монета прижата к правому краю колонки', [coin.width, stock.x + stock.width - (coin.x + coin.width)], [16, 0])
  check('дерево собралось целиком', [elements(parts.panel).length, count(parts.panel)], [3, 20])
}

/** Сверки рисования: цвета берутся с холста панели — по ним видно, что прошли и скины, и картинки, и заливки. */
function checkPainted(parts: Parts, context: CanvasRenderingContext2D): void {
  const panel = frame(parts.panel)
  const well = frame(parts.well)
  check('рамка панели — утопленное окно', pixel(context, panel.x + 1, panel.y + (panel.height >> 1)), [28, 43, 62])
  // Промежуток между окном портрета и колонкой сведений: там видна заливка скина — сам фон панели.
  check('фон панели между детьми', pixel(context, well.x + well.width + 5, panel.y + (panel.height >> 1)), [7, 13, 21])
  check('значок груза нарисован', pixel(context, ...middle(elements(parts.icons)[0])), sample(goodIcon(GOODS[0])))
  check('портрет нарисован', pixel(context, ...middle(parts.portrait)), sample(unitPortrait(UNIT_TYPES[0], PORTRAIT)))
  check('монета золотая', pixel(context, ...middle(parts.coin)), [240, 201, 90])
  check('полоса запаса залита', pixel(context, ...middle(parts.strip)), [90, 61, 12])
}

/**
 * Читает окно по точкам панели и сверяет его с холстом: так видно, что до драйвера дошёл именно он. У чтения
 * отсчёт снизу, а у холста — сверху, отсюда переворот по высоте окна: рисунок лежит от верхнего края.
 */
function checkWindow(gl: WebGL2RenderingContext, context: CanvasRenderingContext2D, parts: Parts, height: number): void {
  const spots: [string, number, number][] = [
    ['значок груза', ...middle(elements(parts.icons)[0])],
    ['портрет', ...middle(parts.portrait)],
    ['монета', ...middle(parts.coin)],
  ]
  let frames = 0
  const read = (milliseconds: number) => {
    if (!gl.drawingBufferWidth || ++frames < CHECK_FRAME) {
      requestAnimationFrame(read)
      return
    }
    for (const [name, x, y] of spots) {
      const taken = new Uint8Array(4)
      gl.readPixels(x, height - y - 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, taken)
      check(`окно: ${name}`, [taken[0], taken[1], taken[2]], pixel(context, x, y))
    }
    console.log(`панель рисуется со скоростью ${Math.round(frames / (milliseconds / 1000))} кадров/с`)
    console.log(`интерфейс в окне: ${passed} из ${total} сходятся`)
  }
  requestAnimationFrame(read)
}

/** Собирает панель по размеру окна, рисует её на холст и показывает в окне. */
function build(width: number, height: number): void {
  // Скины кладутся переменными корня (`--skin-*`): рисование берёт их оттуда — так же, как это делает `border-image`.
  installSkin()
  const document = createDocument()
  const parts = bar(document)
  document.body.appendChild(parts.panel)
  layout(document.body, { width, height })
  const surface = document.createElement('canvas') as unknown as HTMLCanvasElement
  surface.width = width
  surface.height = height
  const context = surface.getContext('2d')!
  draw(document, context)
  console.log(`панель интерфейса: ${width}×${height} пикселей окна, узлов в дереве ${count(document.body)}`)
  console.log(`в панели: портрет в утопленном окне, ${ICONS} значка грузов, кнопки команд и монета кредитов`)
  checkLayout(parts, width, height)
  checkPainted(parts, context)
  checkWindow(show({ width, height, pixels: context.getImageData(0, 0, width, height).data }), context, parts, height)
}

/** Начало: панель собирается в кадре — до него окно ещё не сказало, какого оно размера. */
function main(): void {
  const canvas = document.getElementById('game') as unknown as HTMLCanvasElement
  const ask = () => {
    const gl = canvas.getContext('webgl2')
    if (!gl || !gl.drawingBufferWidth || !gl.drawingBufferHeight) {
      requestAnimationFrame(ask)
      return
    }
    build(gl.drawingBufferWidth, gl.drawingBufferHeight)
  }
  ask()
}

main()
