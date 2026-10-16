// Рисование интерфейса: что легло на холст по местам раскладки (см. `layout.ts`). В браузере интерфейс рисует сам
// движок — рамками скинов (`border-image`, см. `packages/client/src/style.css`) и картинками значков; здесь то же самое делает
// этот файл: рамка ложится девятью частями, картинка — пиксель в пиксель, заливка — цветом.
//
// Рисуется то, у чего есть сторона: пустой узел (строка текста, полоса-заполнитель) ничего не занимает, и рисовать
// ему нечего. Порядок — как у страницы: сперва сам узел (заливка, рамка, картинка), потом его дети.

import { type Document, Element, Node, type Style } from './dom'
import { frame, type Rect } from './layout'
import { address, fromUrl, toCanvas } from './png'

/** Пиксель скина — два пикселя экрана: так скин задан в стилях (`border-image ... / 8px`, см. `packages/client/src/style.css`). */
const SCALE = 2
/** Срез скина: столько пикселей с каждой стороны отрезает `border-image-slice`. */
const SLICE = 4
/** Толщина рамки в пикселях экрана: срез скина, растянутый масштабом. */
const BORDER = SLICE * SCALE

/**
 * Картинки по адресу: `drawImage` берёт источником холст, а разбор адреса — дело не бесплатное, и оно разовое
 * (см. `png.ts`). Чужой адрес даёт пустоту: рисовать такую картинку нечем.
 */
const canvases = new Map<string, HTMLCanvasElement | null>()

/** Холст с картинкой по адресу: им рисуется и значок, и рамка скина. */
function pictureOf(url: string): HTMLCanvasElement | null {
  if (!canvases.has(url)) {
    const image = fromUrl(url)
    canvases.set(url, image.width && image.height ? toCanvas(image) : null)
  }
  return canvases.get(url) ?? null
}

/**
 * Рамка скина по имени его переменной: адрес берётся из переменной корня, куда его кладёт `installSkin`, и разбирается
 * в холст — рисовать умеет только холст.
 */
function skinOf(document: Document, name: string): HTMLCanvasElement | null {
  const value = document.documentElement.style.getPropertyValue(`--skin-${name}`)
  return value ? pictureOf(address(value)) : null
}

/**
 * Рамка скина: девять частей картинки. Углы кладутся срезом, растянутым масштабом, полосы тянутся по сторонам, а
 * центр разливается на остаток — ровно так же раскладывает картинку `border-image` у страницы.
 */
function paintFrame(context: CanvasRenderingContext2D, image: HTMLCanvasElement, rect: Rect): void {
  const cuts = [0, SLICE, SLICE * 2]
  for (const [row, sy] of cuts.entries()) {
    for (const [column, sx] of cuts.entries()) {
      // Средняя часть тянется: у полосы сторона — остаток, у угла — толщина рамки.
      const width = column === 1 ? Math.max(0, rect.width - BORDER * 2) : BORDER
      const height = row === 1 ? Math.max(0, rect.height - BORDER * 2) : BORDER
      // Место части: углы по краям, полоса — между ними; у узла уже самой рамки части сходятся внахлёст.
      const x = column === 0 ? rect.x : column === 1 ? rect.x + BORDER : rect.x + Math.max(BORDER, rect.width - BORDER)
      const y = row === 0 ? rect.y : row === 1 ? rect.y + BORDER : rect.y + Math.max(BORDER, rect.height - BORDER)
      context.drawImage(image, sx, sy, SLICE, SLICE, x, y, width, height)
    }
  }
}

/** Рисует узел: заливка, рамка скина, картинка — в том же порядке, в каком их кладёт страница. */
function paint(document: Document, context: CanvasRenderingContext2D, node: Element): void {
  const style: Style = node.style
  const rect = frame(node)
  if (style.display === 'none' || rect.width <= 0 || rect.height <= 0) return
  if (style.alpha !== undefined) context.globalAlpha = style.alpha
  if (style.background) {
    context.fillStyle = style.background
    context.fillRect(rect.x, rect.y, rect.width, rect.height)
  }
  if (style.skin) {
    const image = skinOf(document, style.skin)
    if (image) paintFrame(context, image, rect)
  }
  if (style.icon) {
    const image = pictureOf(style.icon)
    if (image) context.drawImage(image, rect.x, rect.y, rect.width, rect.height)
  }
  if (style.alpha !== undefined) context.globalAlpha = 1
}

/** Рисует дерево интерфейса на холст кадра: сперва узел, потом его дети — как это делает страница. */
export function draw(document: Document, context: CanvasRenderingContext2D, node: Node = document.body): void {
  // Пиксельная графика растягивается без сглаживания: с ним у значка размазались бы края.
  context.imageSmoothingEnabled = false
  if (node instanceof Element) paint(document, context, node)
  for (const child of node.childNodes) draw(document, context, child)
}
