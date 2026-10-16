// Интерфейс в немедленном режиме, нарисованный на холсте (Canvas 2D) — и на странице, и в нативной оболочке (пункт
// 10.4 плана). Панели описываются вызовами каждый кадр интерфейса: `ui.box(...)`, `ui.text(...)`, `ui.button(...)`.
// Из них за кадр собирается дерево; в конце кадра оно раскладывается (ряды и столбцы с промежутками, отступами,
// выравниванием и растяжением — как flex у страницы) и рисуется. Своё состояние у панелей — в их замыканиях
// (см. hud.ts), а ui помнит только места узлов прошлого кадра — по ним видно, что под указателем и что нажали, —
// прокрутку списков и правку полей ввода. Задержка в кадр интерфейса незаметна, а раскладка от этого простая: место
// узла не нужно знать, пока он описывается.
//
// Нарисованное сравнивается с прошлым кадром подписью: если ничего не поменялось, холст не перерисовывается, и
// проход не заливает его в текстуру заново (см. pass.ts) — интерфейс почти всегда стоит.

import { CORNER, SKIN_SCALE, skins, type SkinName } from '../ui/skin'
import { FONTS, clipText, fontsLoaded, textImage, textWidth, wrapText, type FontName } from './text'

/** Место на экране в пикселях CSS: отсчёт от левого верхнего угла холста. */
export interface Rect {
  x: number
  y: number
  width: number
  height: number
}

/** Привязка корневой панели к краям экрана: заданы left и right — панель растягивается между ними. */
export interface Anchor {
  left?: number
  right?: number
  top?: number
  bottom?: number
  /** По середине экрана по горизонтали — со сдвигом на столько. */
  centerX?: number
  /** По середине экрана по вертикали — со сдвигом на столько. */
  centerY?: number
}

/** Как узел раскладывает детей и как выглядит сам. */
export interface BoxStyle {
  /** Номер для ввода: по нему узнаётся наведение и нажатие, и он должен быть одним и тем же от кадра к кадру. */
  id?: string
  /** row и column — дети друг за другом; stack — друг на друге, каждый по своему self (значок поверх портрета). */
  direction?: 'row' | 'column' | 'stack'
  /** Место ребёнка в стопке (stack): по горизонтали и по вертикали. */
  self?: ['start' | 'center' | 'end', 'start' | 'center' | 'end']
  gap?: number
  /** Отступ внутри: одно число на все стороны, [сверху и снизу, слева и справа] или [сверху, справа, снизу, слева]. */
  padding?: number | [number, number] | [number, number, number, number]
  /** Выравнивание детей поперёк: stretch тянет их на всю ширину (высоту) узла. */
  align?: 'start' | 'center' | 'end' | 'stretch'
  /** Расстановка детей вдоль: between — крайние к краям, остальные поровну между. */
  justify?: 'start' | 'center' | 'end' | 'between'
  width?: number
  height?: number
  minWidth?: number
  maxWidth?: number
  minHeight?: number
  /** Предел высоты: содержимое выше него прокручивается (с scroll) или обрезается. */
  maxHeight?: number
  /** Доля свободного места вдоль родителя, которую забирает узел. */
  grow?: number
  /** В тесном ряду узел уступает место — его текст переносится или сокращается (растущий уступает и без этого). */
  shrink?: boolean
  /** Ряд, которому тесно, переносит детей на следующие строки (промежуток между строками — тот же gap). */
  wrap?: boolean
  /** Рамка скина девятью частями — как border-image у страницы. */
  skin?: SkinName
  background?: string
  /** Обводка цветом внутри рамки — подсветка подсказок «Первых шагов» и новых кнопок. */
  outline?: string
  /** Прозрачность узла с детьми: гаснущий чат. */
  alpha?: number
  /** Обрезать детей по своему месту. */
  clip?: boolean
  /** Прокрутка колесом по вертикали (нужен id): содержимое выше узла ездит внутри него. end — держаться низа (чат). */
  scroll?: boolean | 'end'
  /** Только у корневых панелей: где они стоят на экране. */
  anchor?: Anchor
  /** Корневая панель, сквозь которую нажатия проходят в мир: подсказки у указателя, уведомления о наградах. */
  passive?: boolean
  /** Нажатие сюда не снимает фокус с поля ввода: подсказки дополнения под строкой чата. */
  keepFocus?: boolean
}

export interface TextStyle {
  font?: FontName
  color?: string
  /** Тень текста этим цветом правее и ниже — как text-shadow у страницы. */
  shadow?: string
  /** На сколько пикселей тень сдвинута: у кнопок и подписей — на один, у крупного и у чата — на два. */
  lift?: number
  /**
   * Перенос по словам: true — в ширину, что даёт родитель, число — не шире него (и не шире родителя). Строка
   * становится столбцом строк.
   */
  wrap?: number | true
  /** Не влезает в ширину родителя — сокращается многоточием. */
  ellipsis?: boolean
}

/** Кусок строки с переносом: текст своим цветом или картинка (значок груза) — с подсказкой, если дан id. */
export type Run = string | { text: string; color?: string } | { image: CanvasImageSource; width: number; height: number; tip?: string; id?: string }

/** Щелчок по узлу: какой кнопкой и с какими клавишами. Долгое нажатие пальцем — правая кнопка с long. */
export interface Click {
  button: number
  shift: boolean
  ctrl: boolean
  alt: boolean
  meta: boolean
  long: boolean
}

/** Поле ввода: что набрано и что нажали в нём за кадр. */
export interface Field {
  value: string
  focused: boolean
  /** Enter, Escape, Tab, ArrowUp, ArrowDown и Blur (фокус ушёл) — их поле не берёт себе, а отдаёт панели. */
  keys: string[]
}

export interface FieldOptions {
  /** Ширина поля; без неё поле растёт на свободное место ряда (или тянется на ширину столбца со stretch). */
  width?: number
  placeholder?: string
  password?: boolean
  maxLength?: number
  /** Только цифры: количество в сделке. */
  digits?: boolean
  /** Поле получает фокус в этом кадре — так чат открывается по Enter. */
  focus?: boolean
  /** Вместо утопленной рамки — заливка этим цветом: строка чата. */
  background?: string
  /** Обводка поля: строка чата администратора. */
  outline?: string
  /** Цвет текста. */
  color?: string
}

type Leaf =
  | { kind: 'text'; text: string; font: FontName; color: string; shadow?: string; lift: number; wrap?: number | true; ellipsis?: boolean; lines: string[] }
  | { kind: 'image'; image: CanvasImageSource; width: number; height: number; smooth: boolean; live: boolean }
  | { kind: 'space' }
  | { kind: 'caret'; color: string }

interface Node {
  style: BoxStyle
  leaf: Leaf | null
  children: Node[]
  /** Желаемый размер — считается при раскладке. */
  want: { width: number; height: number }
  rect: Rect
  /** Высота содержимого с отступами — у прокручиваемого узла она больше его самого. */
  content: number
  /** Строки ряда с переносом (wrap): дети по строкам. */
  lines?: Node[][]
}

/** Через сколько мс наведения появляется подсказка. */
const TIP_DELAY = 350
/** Сколько мс держать палец, чтобы нажатие стало долгим (правым щелчком). */
const HOLD_MS = 500
/** Насколько можно сдвинуть палец, чтобы нажатие осталось нажатием, а не протяжкой. */
const HOLD_SLOP = 8
/** Толщина обводки подсказки в пикселях CSS. */
const OUTLINE = 3
/** Сторона рамки скина в пикселях CSS. */
const BORDER = CORNER * SKIN_SCALE
/** Шаг прокрутки колесом в пикселях, если колесо считает строками. */
const WHEEL_LINE = 40
/** Ширина подсказки, дальше строки переносятся. */
const TIP_WIDTH = 320

/**
 * Событие окна: кнопку интерфейса нажали или навели на неё. Звук слушает его (interfaceSounds.ts) — так ему не нужно
 * знать о кнопках, а им о звуке. group — кнопка открывает раздел; hover — только навели.
 */
export const GUI_PRESS = 'kharos:gui-press'
export interface GuiPress {
  disabled: boolean
  group: boolean
  hover: boolean
}
const sound = (detail: GuiPress) => window.dispatchEvent(new CustomEvent<GuiPress>(GUI_PRESS, { detail }))

export interface Ui {
  /** Размер экрана в пикселях CSS: по нему панели выбирают раскладку (узкий экран — в два ряда). */
  readonly width: number
  readonly height: number
  /** Указатель в пикселях CSS; inside — над холстом. */
  readonly pointer: { x: number; y: number; inside: boolean }
  /** Время кадра интерфейса, мс по performance.now: для гаснущего и мигающего. */
  readonly now: number
  /** Узел с детьми: вызовы внутри children — его дети. На верхнем уровне это корневая панель (см. anchor). */
  box(style: BoxStyle, children?: () => void): void
  /** Строка текста (или столбец строк — с wrap или переводами строки). */
  text(text: string, style?: TextStyle): void
  /** Строка из текста и значков с переносом в width: так пишутся подсказки панели сведений. */
  flow(runs: Run[], style: TextStyle & { width: number }): void
  /** Картинка: холст или изображение в размер width × height. smooth — сглаживать; live — она меняется сама. */
  image(image: CanvasImageSource, width: number, height: number, options?: { smooth?: boolean; live?: boolean }): void
  /** Пустое место; grow — растягивается на свободное место ряда. */
  space(width?: number, height?: number, grow?: number): void
  /**
   * Кнопка: узел с рамкой кнопки, сменой рамки при наведении и нажатии. Возвращает щелчок (или null) в кадре после
   * него. active — нажатое состояние; disabled — не нажимается.
   */
  button(id: string, style: BoxStyle & { active?: boolean; disabled?: boolean; group?: boolean }, children: () => void): Click | null
  /** Щелчок по узлу с id за прошедший кадр — любой кнопкой. */
  clicked(id: string): Click | null
  /** Указатель над узлом id (по месту прошлого кадра) — и ничто не лежит поверх. */
  hovered(id: string): boolean
  /** Указатель над узлом id или над чем-то внутри него. */
  within(id: string): boolean
  /** Левая кнопка зажата на узле id: где указатель в нём сейчас (доли ширины и высоты от 0 до 1), иначе null. */
  held(id: string): { u: number; v: number; click: Click } | null
  /** Подсказка узла id у указателя (или по anchor) — строкой или своими узлами; появляется, когда указатель задержался. */
  tip(id: string, content: string | (() => void), anchor?: Anchor): void
  /** Поле ввода. */
  input(id: string, value: string, options: FieldOptions): Field
  /** Место узла id по прошлому кадру. */
  rect(id: string): Rect | null
}

export interface Gui {
  /** Холст интерфейса: проход (pass.ts) заливает его в текстуру и кладёт поверх кадра. */
  readonly surface: HTMLCanvasElement
  /**
   * Кадр интерфейса: build описывает панели. width и height — размер экрана в пикселях CSS, density — пикселей
   * холста на пиксель CSS. Возвращает true, если нарисованное поменялось и холст надо залить заново.
   */
  frame(width: number, height: number, density: number, build: (ui: Ui) => void): boolean
  /** Лежит ли в точке (пиксели CSS) панель интерфейса: управление миром такие нажатия не берёт. */
  covers(x: number, y: number): boolean
  /** Был ли ввод с прошлого кадра интерфейса: тогда кадр стоит построить сразу, не дожидаясь срока. */
  readonly touched: boolean
  /** В поле ввода набирают текст: клавиши игре не достаются. */
  readonly typing: boolean
  destroy(): void
}

/** Номера картинок для подписи кадра: сама картинка в строку не войдёт. */
const imageIds = new WeakMap<object, number>()
let nextImage = 1
const imageId = (image: CanvasImageSource) => {
  let id = imageIds.get(image as object)
  if (!id) imageIds.set(image as object, (id = nextImage++))
  return id
}

/** Отступы узла: сверху, справа, снизу, слева. */
function pads(style: BoxStyle): [number, number, number, number] {
  const { padding } = style
  if (typeof padding === 'number') return [padding, padding, padding, padding]
  if (!padding) return [0, 0, 0, 0]
  if (padding.length === 2) return [padding[0], padding[1], padding[0], padding[1]]
  return padding
}

const clamp = (value: number, min = 0, max = Infinity) => Math.max(min, Math.min(max, value))

/** Узел уступает место в тесном ряду: его ширина не задана, а текст умеет переноситься или сокращаться. */
const yields = (node: Node) => node.style.width === undefined && (!!node.style.grow || !!node.style.shrink || (node.leaf?.kind === 'text' && (!!node.leaf.wrap || !!node.leaf.ellipsis)))

/**
 * Желаемый размер узла: заданный стилем или набранный детьми (или содержимым листа). room — сколько места по ширине
 * даёт родитель: в него переносится и сокращается текст, а тесный ряд делит его между уступчивыми детьми. column —
 * родитель кладёт детей столбцом: растущий прокручиваемый узел тогда высоты под содержимое не просит.
 */
function measure(node: Node, room = Infinity, column = false): { width: number; height: number } {
  const { style, leaf } = node
  const [top, right, bottom, left] = pads(style)
  const inner = Math.max(0, Math.min(style.width ?? room, style.maxWidth ?? Infinity) - left - right)
  let width = 0
  let height = 0
  if (leaf?.kind === 'text') {
    const lift = leaf.shadow ? leaf.lift : 0
    const space = Math.max(1, (leaf.wrap === true ? inner : Math.min(leaf.wrap ?? Infinity, inner)) - lift)
    leaf.lines = leaf.text.split('\n').flatMap((line) => (leaf.wrap ? wrapText(leaf.font, line, space) : [line]))
    if (leaf.ellipsis) leaf.lines = leaf.lines.map((line) => clipText(leaf.font, line, Math.max(1, inner - lift)))
    width = leaf.lines.reduce((most, line) => Math.max(most, textWidth(leaf.font, line)), 0) + lift
    height = FONTS[leaf.font].line * leaf.lines.length
  } else if (leaf?.kind === 'image') {
    width = leaf.width
    height = leaf.height
  } else if (leaf?.kind === 'caret') {
    width = 1
    height = FONTS.body.line
  } else if (style.direction === 'stack') {
    const sizes = node.children.map((child) => measure(child, inner))
    width = sizes.reduce((most, size) => Math.max(most, size.width), 0)
    height = sizes.reduce((most, size) => Math.max(most, size.height), 0)
  } else {
    const row = style.direction === 'row'
    const gap = style.gap ?? 0
    const gaps = gap * Math.max(0, node.children.length - 1)
    let sizes = node.children.map((child) => measure(child, inner, !row))
    node.lines = undefined
    if (row && style.wrap && sizes.reduce((sum, size) => sum + size.width, 0) + gaps > inner) {
      // Перенос: дети по порядку, пока строка влезает в ширину.
      const lines: Node[][] = [[]]
      let used = 0
      for (const child of node.children) {
        const line = lines[lines.length - 1]
        if (line.length && used + gap + child.want.width > inner) {
          lines.push([child])
          used = child.want.width
        } else {
          line.push(child)
          used += (line.length > 1 ? gap : 0) + child.want.width
        }
      }
      node.lines = lines
      const widthOf = (line: Node[]) => line.reduce((sum, child) => sum + child.want.width, 0) + gap * (line.length - 1)
      const heightOf = (line: Node[]) => line.reduce((most, child) => Math.max(most, child.want.height), 0)
      width = Math.max(...lines.map(widthOf))
      height = lines.reduce((sum, line) => sum + heightOf(line), 0) + gap * (lines.length - 1)
      sizes = []
    }
    // Тесный ряд: уступчивые дети делят оставшееся место — узкие берут своё, остальное поровну — и меряются заново.
    const total = sizes.reduce((sum, size) => sum + size.width, 0) + gaps
    if (row && total > inner && !node.lines) {
      const flexible = node.children.filter(yields).sort((a, b) => a.want.width - b.want.width)
      let free = inner - total + flexible.reduce((sum, child) => sum + child.want.width, 0)
      flexible.forEach((child, index) => {
        const share = Math.max(0, free / (flexible.length - index))
        if (child.want.width > share) measure(child, share, false)
        free -= child.want.width
      })
      if (flexible.length) sizes = node.children.map((child) => child.want)
    }
    if (!node.lines) {
      const along = sizes.reduce((sum, size) => sum + (row ? size.width : size.height), 0) + gaps
      const across = sizes.reduce((most, size) => Math.max(most, row ? size.height : size.width), 0)
      width = row ? along : across
      height = row ? across : along
    }
  }
  node.content = height + top + bottom
  width = clamp(style.width ?? width + left + right, style.minWidth ?? 0, style.maxWidth)
  // Прокручиваемый растущий узел места под содержимое не просит: сколько дадут, столько и покажет.
  const fill = style.scroll && style.grow && column ? (style.minHeight ?? 0) : height + top + bottom
  height = clamp(style.height ?? fill, style.minHeight ?? 0, style.maxHeight)
  node.want = { width, height }
  return node.want
}

/**
 * Ставит детей узла на места: вдоль — по порядку с промежутком, свободное место — растущим; поперёк — выравнивание.
 * Столбцу не хватает высоты — уступают прокручиваемые дети: тело окна выше экрана прокручивается.
 */
function place(node: Node, rect: Rect, scrolls: Map<string, number>) {
  node.rect = { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width), height: Math.round(rect.height) }
  const { style, children } = node
  if (!children.length) return
  const row = style.direction === 'row'
  const [top, right, bottom, left] = pads(style)
  const inner = { x: node.rect.x + left, y: node.rect.y + top, width: node.rect.width - left - right, height: node.rect.height - top - bottom }
  // Прокручиваемый узел раскладывает детей во всю их высоту и сдвигает на прокрутку.
  if (style.scroll && style.id) {
    const room = Math.max(0, node.content - node.rect.height)
    const offset = clamp(scrolls.get(style.id) ?? (style.scroll === 'end' ? room : 0), 0, room)
    scrolls.set(style.id, offset)
    inner.y -= offset
    inner.height = node.content - top - bottom
  }
  if (style.direction === 'stack') {
    const at = (where: string | undefined, room: number, want: number) => (where === 'center' ? (room - want) / 2 : where === 'end' ? room - want : 0)
    for (const child of children) {
      const [horizontal, vertical] = child.style.self ?? ['start', 'start']
      const w = child.style.grow ? inner.width : child.want.width
      const h = child.style.grow ? inner.height : child.want.height
      place(child, { x: inner.x + at(horizontal, inner.width, w), y: inner.y + at(vertical, inner.height, h), width: w, height: h }, scrolls)
    }
    return
  }
  const gap = style.gap ?? 0
  if (node.lines) {
    // Ряд с переносом: каждая строка — свой ряд во всю ширину, строки — столбцом.
    let y = inner.y
    for (const line of node.lines) {
      const height = line.reduce((most, child) => Math.max(most, child.want.height), 0)
      const part: Node = { ...node, style: { ...style, wrap: false, padding: 0, scroll: false }, children: line, lines: undefined }
      place(part, { x: inner.x, y, width: inner.width, height }, scrolls)
      y += height + gap
    }
    return
  }
  const room = row ? inner.width : inner.height
  const sizes = children.map((child) => (row ? child.want.width : child.want.height))
  // Столбцу не хватает высоты — прокручиваемые дети отдают свою сверх minHeight.
  let short = sizes.reduce((sum, size) => sum + size, 0) + gap * (children.length - 1) - room
  if (!row) {
    children.forEach((child, index) => {
      if (short <= 0 || !child.style.scroll) return
      const cut = Math.min(short, Math.max(0, sizes[index] - (child.style.minHeight ?? 0)))
      sizes[index] -= cut
      short -= cut
    })
  }
  // Зазор бывает и отрицательным: ребёнок крупнее родителя выступает за него — с end вверх (мини-карта над панелью).
  const slack = -short
  const free = Math.max(0, slack)
  const grows = children.reduce((sum, child) => sum + (child.style.grow ?? 0), 0)
  let offset = 0
  let spacing = gap
  if (!grows) {
    // По середине — только пока влезает (safe center у страницы): не влезшее уходит вниз, а не за верх.
    if (style.justify === 'center') offset = free / 2
    else if (style.justify === 'end') offset = slack
    else if (style.justify === 'between' && children.length > 1) spacing = gap + free / (children.length - 1)
  }
  let along = (row ? inner.x : inner.y) + offset
  children.forEach((child, index) => {
    const extra = grows ? (free * (child.style.grow ?? 0)) / grows : 0
    const size = sizes[index] + extra
    const crossRoom = row ? inner.height : inner.width
    const align = style.align ?? 'start'
    const crossWant = align === 'stretch' ? crossRoom : row ? child.want.height : child.want.width
    const crossOffset = align === 'center' ? (crossRoom - crossWant) / 2 : align === 'end' ? crossRoom - crossWant : 0
    const cross = (row ? inner.y : inner.x) + crossOffset
    place(child, row ? { x: along, y: cross, width: size, height: crossWant } : { x: cross, y: along, width: crossWant, height: size }, scrolls)
    along += size + spacing
  })
}

/** Место корневой панели по привязке: заданы оба края — растянута между ними, иначе — своего размера. */
function anchored(node: Node, width: number, height: number): Rect {
  const anchor = node.style.anchor ?? {}
  const { want } = node
  const w = anchor.left !== undefined && anchor.right !== undefined ? width - anchor.left - anchor.right : want.width
  const h = anchor.top !== undefined && anchor.bottom !== undefined ? height - anchor.top - anchor.bottom : want.height
  const x = anchor.left ?? (anchor.right !== undefined ? width - anchor.right - w : anchor.centerX !== undefined ? (width - w) / 2 + anchor.centerX : 0)
  const y = anchor.top ?? (anchor.bottom !== undefined ? height - anchor.bottom - h : anchor.centerY !== undefined ? (height - h) / 2 + anchor.centerY : 0)
  return { x, y, width: w, height: h }
}

const inside = (rect: Rect, x: number, y: number) => x >= rect.x && y >= rect.y && x < rect.x + rect.width && y < rect.y + rect.height

/** Пересечение мест: так попадание обрезается прокручиваемым списком. */
function intersect(a: Rect, b: Rect): Rect {
  const x = Math.max(a.x, b.x)
  const y = Math.max(a.y, b.y)
  return { x, y, width: Math.max(0, Math.min(a.x + a.width, b.x + b.width) - x), height: Math.max(0, Math.min(a.y + a.height, b.y + b.height) - y) }
}

/** Рамка скина девятью частями: углы срезом в CORNER пикселей скина, полосы тянутся, середина разливается. */
function paintSkin(context: CanvasRenderingContext2D, image: HTMLCanvasElement, rect: Rect) {
  const size = image.width
  const middle = size - CORNER * 2
  const cuts = [0, CORNER, size - CORNER]
  const sizes = [CORNER, middle, CORNER]
  for (let row = 0; row < 3; row++) {
    for (let column = 0; column < 3; column++) {
      const width = column === 1 ? Math.max(0, rect.width - BORDER * 2) : Math.min(BORDER, rect.width / 2)
      const height = row === 1 ? Math.max(0, rect.height - BORDER * 2) : Math.min(BORDER, rect.height / 2)
      const x = column === 0 ? rect.x : column === 1 ? rect.x + BORDER : rect.x + rect.width - width
      const y = row === 0 ? rect.y : row === 1 ? rect.y + BORDER : rect.y + rect.height - height
      if (width > 0 && height > 0) context.drawImage(image, cuts[column], cuts[row], sizes[column], sizes[row], x, y, width, height)
    }
  }
}

/** Правка поля ввода: текст, место курсора и начало выделения (anchor === caret — выделения нет). */
interface Edit {
  value: string
  caret: number
  anchor: number
}

/** Буфер обмена: на странице — системный, у оболочки пока свой, в памяти. */
let clipboard = ''
const writeClipboard = (text: string) => {
  clipboard = text
  void navigator.clipboard?.writeText(text).catch(() => {})
}

/**
 * Интерфейс на холсте. target — холст игры: с него берутся указатель и нажатия (у оболочки они приходят туда же,
 * см. `__kharos_input` в apps/native/js/prelude.js). Клавиши — с окна: поле ввода в фокусе забирает их себе первым.
 */
export function createGui(target: HTMLElement): Gui {
  const surface = document.createElement('canvas')
  const context = surface.getContext('2d')!
  const pointer = { x: 0, y: 0, inside: false }
  /** Нажатие: на каком узле, какой кнопкой и где; long — долгое уже сработало, и отпускание щелчком не будет. */
  let press: { id: string | null; button: number; x: number; y: number; timer: ReturnType<typeof setTimeout> | undefined; long: boolean; click: Click } | null = null
  /** Щелчки, накопленные до следующего кадра интерфейса. */
  const clicks = new Map<string, Click>()
  /** Места узлов с номерами по прошлому кадру — в порядке рисования: кто позже, тот сверху. path — номера предков. */
  let hits: { id: string; rect: Rect; path: string[] }[] = []
  /** Места корневых панелей прошлого кадра: под ними мир нажатий не получает. */
  let roots: Rect[] = []
  /** Прокрутка списков по их номерам. */
  const scrolls = new Map<string, number>()
  /** Колесо, накопленное до кадра, — и над каким списком. */
  let wheel: { id: string; delta: number } | null = null
  /** Поле ввода в фокусе, правки полей и клавиши, отданные панели. */
  let focused: string | null = null
  const edits = new Map<string, Edit>()
  const fieldKeys = new Map<string, string[]>()
  /** Над каким узлом указатель и с каких пор: подсказка появляется не сразу. */
  let over: string | null = null
  let since = 0
  let signature = ''
  let touched = true
  let liveFrame = 0
  /** Кнопки прошлого кадра: на нажатие и наведение они звучат. */
  let buttons = new Map<string, { disabled: boolean; group: boolean }>()
  /** Узлы, нажатие на которые фокус поля не снимает (keepFocus). */
  let keeps = new Set<string>()

  const topmost = (x: number, y: number) => {
    for (let index = hits.length - 1; index >= 0; index--) if (inside(hits[index].rect, x, y)) return hits[index]
    return null
  }
  // Под панелью или под любым узлом с номером: мини-карта крупнее нижней панели выступает над ней.
  const covers = (x: number, y: number) => roots.some((rect) => inside(rect, x, y)) || hits.some((one) => inside(one.rect, x, y))
  const modifiers = (event: MouseEvent, button: number, long = false): Click => ({ button, shift: event.shiftKey, ctrl: event.ctrlKey, alt: event.altKey, meta: event.metaKey, long })

  const blur = () => {
    if (!focused) return
    const keys = fieldKeys.get(focused) ?? []
    keys.push('Blur')
    fieldKeys.set(focused, keys)
    focused = null
    touched = true
  }

  const onMove = (event: PointerEvent) => {
    pointer.x = event.offsetX
    pointer.y = event.offsetY
    pointer.inside = true
    touched = true
    // Палец уехал — это протяжка, а не долгое нажатие.
    if (press && Math.hypot(pointer.x - press.x, pointer.y - press.y) > HOLD_SLOP) clearTimeout(press.timer)
  }
  const onDown = (event: PointerEvent) => {
    onMove(event)
    const hit = topmost(pointer.x, pointer.y)
    // Нажатие мимо поля ввода снимает с него фокус; по полю — ставит.
    if (focused && hit?.id !== focused && !(hit && (keeps.has(hit.id) || hit.path.some((id) => keeps.has(id))))) blur()
    if (hit && edits.has(hit.id)) focused = hit.id
    const button = hit && buttons.get(hit.id)
    if (button && event.button === 0) sound({ ...button, hover: false })
    if (press) clearTimeout(press.timer)
    const pressed = { id: hit?.id ?? null, button: event.button, x: pointer.x, y: pointer.y, timer: undefined as ReturnType<typeof setTimeout> | undefined, long: false, click: modifiers(event, event.button) }
    // Долгое нажатие пальцем — правый щелчок: у сенсорного экрана правой кнопки нет.
    if (hit && event.pointerType !== 'mouse' && event.button === 0) {
      pressed.timer = setTimeout(() => {
        pressed.long = true
        clicks.set(hit.id, modifiers(event, 2, true))
        touched = true
      }, HOLD_MS)
    }
    press = pressed
  }
  const onUp = (event: PointerEvent) => {
    onMove(event)
    const pressed = press
    press = null
    if (!pressed) return
    clearTimeout(pressed.timer)
    if (pressed.long || event.button !== pressed.button) return
    const hit = topmost(pointer.x, pointer.y)
    if (hit && hit.id === pressed.id) clicks.set(hit.id, modifiers(event, event.button))
  }
  const onCancel = () => {
    if (press) clearTimeout(press.timer)
    press = null
  }
  const onLeave = () => {
    pointer.inside = false
    touched = true
  }
  const onWheel = (event: WheelEvent) => {
    // Колесо достаётся ближайшему прокручиваемому списку под указателем.
    const hit = topmost(event.offsetX, event.offsetY)
    if (!hit) return
    const list = [...hit.path, hit.id].reverse().find((id) => scrolls.has(id))
    if (!list) return
    const delta = event.deltaMode === 1 ? event.deltaY * WHEEL_LINE : event.deltaY
    wheel = wheel?.id === list ? { id: list, delta: wheel.delta + delta } : { id: list, delta }
    touched = true
  }
  const onContextMenu = (event: Event) => {
    if (covers(pointer.x, pointer.y)) event.preventDefault()
  }

  /**
   * Клавиши поля в фокусе: буквы, стирание, стрелки, выделение и буфер обмена. Остальное (Enter, Escape, Tab,
   * стрелки вверх и вниз) поле отдаёт панели — чат по ним отправляет, закрывается и листает подсказки. Дальше
   * нажатие не идёт: игра и её клавиши его не видят.
   */
  const onKey = (event: KeyboardEvent) => {
    if (!focused) return
    const edit = edits.get(focused)
    if (!edit) return
    event.stopImmediatePropagation()
    const command = event.ctrlKey || event.metaKey
    const from = Math.min(edit.caret, edit.anchor)
    const to = Math.max(edit.caret, edit.anchor)
    const replace = (text: string) => {
      edit.value = edit.value.slice(0, from) + text + edit.value.slice(to)
      edit.caret = edit.anchor = from + text.length
    }
    const move = (caret: number) => {
      edit.caret = clamp(caret, 0, edit.value.length)
      if (!event.shiftKey) edit.anchor = edit.caret
    }
    touched = true
    switch (event.key) {
      case 'Enter':
      case 'Escape':
      case 'Tab':
      case 'ArrowUp':
      case 'ArrowDown': {
        event.preventDefault()
        const keys = fieldKeys.get(focused) ?? []
        keys.push(event.key)
        fieldKeys.set(focused, keys)
        return
      }
      case 'Backspace':
        event.preventDefault()
        if (from !== to) replace('')
        else if (from > 0) {
          edit.value = edit.value.slice(0, from - 1) + edit.value.slice(from)
          edit.caret = edit.anchor = from - 1
        }
        return
      case 'Delete':
        event.preventDefault()
        if (from !== to) replace('')
        else edit.value = edit.value.slice(0, from) + edit.value.slice(from + 1)
        return
      case 'ArrowLeft':
        event.preventDefault()
        move(!event.shiftKey && from !== to ? from : edit.caret - 1)
        return
      case 'ArrowRight':
        event.preventDefault()
        move(!event.shiftKey && from !== to ? to : edit.caret + 1)
        return
      case 'Home':
        event.preventDefault()
        move(0)
        return
      case 'End':
        event.preventDefault()
        move(edit.value.length)
        return
    }
    if (command) {
      const key = event.code
      if (key === 'KeyA') {
        event.preventDefault()
        edit.anchor = 0
        edit.caret = edit.value.length
      } else if (key === 'KeyC' || key === 'KeyX') {
        event.preventDefault()
        if (from !== to) writeClipboard(edit.value.slice(from, to))
        if (key === 'KeyX') replace('')
      } else if (key === 'KeyV') {
        event.preventDefault()
        const field = focused
        const paste = (text: string) => {
          if (focused !== field) return
          replace(text.replace(/[\r\n]+/g, ' '))
          touched = true
        }
        if (navigator.clipboard?.readText) void navigator.clipboard.readText().then(paste, () => paste(clipboard))
        else paste(clipboard)
      }
      return
    }
    // Буква — всё, что печатается одним знаком: клавиши-имена вроде Shift или F5 длиннее.
    if ([...event.key].length === 1) {
      event.preventDefault()
      replace(event.key)
    }
  }

  target.addEventListener('pointermove', onMove)
  target.addEventListener('pointerdown', onDown)
  target.addEventListener('pointerup', onUp)
  target.addEventListener('pointercancel', onCancel)
  target.addEventListener('pointerleave', onLeave)
  target.addEventListener('wheel', onWheel)
  target.addEventListener('contextmenu', onContextMenu)
  // Раньше клавиш игры: поле в фокусе забирает нажатие себе (у страницы — фазой перехвата, у оболочки — порядком).
  window.addEventListener('keydown', onKey, true)

  return {
    surface,
    covers,
    get touched() {
      return touched
    },
    get typing() {
      return focused !== null
    },
    frame(width, height, density, build) {
      touched = false
      const now = performance.now()
      const top: Node[] = []
      const stack: Node[] = []
      const tips: { content: string | (() => void); anchor?: Anchor }[] = []
      const frameClicks = new Map(clicks)
      clicks.clear()
      const frameKeys = new Map(fieldKeys)
      fieldKeys.clear()
      if (wheel) {
        scrolls.set(wheel.id, (scrolls.get(wheel.id) ?? 0) + wheel.delta)
        wheel = null
      }
      const hit = pointer.inside ? topmost(pointer.x, pointer.y) : null
      const overId = hit?.id ?? null
      if (overId !== over) {
        over = overId
        since = now
        const button = overId && buttons.get(overId)
        if (button && !button.disabled) sound({ ...button, hover: true })
      }
      const nextButtons = new Map<string, { disabled: boolean; group: boolean }>()
      /** Поля, что описаны в этом кадре: остальные забываются вместе с фокусом. */
      const seenFields = new Set<string>()
      const add = (node: Node) => (stack.length ? stack[stack.length - 1].children : top).push(node)
      const make = (style: BoxStyle, leaf: Leaf | null = null): Node => ({ style, leaf, children: [], want: { width: 0, height: 0 }, rect: { x: 0, y: 0, width: 0, height: 0 }, content: 0 })
      const textLeaf = (text: string, style: TextStyle = {}): Leaf => ({ kind: 'text', text, font: style.font ?? 'body', color: style.color ?? '#d6e6f5', shadow: style.shadow, lift: style.lift ?? 1, wrap: style.wrap, ellipsis: style.ellipsis, lines: [text] })
      const ui: Ui = {
        width,
        height,
        pointer: { ...pointer },
        now,
        box(style, children) {
          const node = make(style)
          add(node)
          if (!children) return
          stack.push(node)
          children()
          stack.pop()
        },
        text(text, style = {}) {
          // Строки — при раскладке: перенос и многоточие зависят от места, что даст родитель.
          add(make({}, textLeaf(text, style)))
        },
        flow(runs, style) {
          // Куски — словами и значками; строка переносится, когда следующий кусок не влезает в ширину.
          const font = style.font ?? 'body'
          const pieces: { width: number; build: () => void; space: boolean }[] = []
          for (const run of runs) {
            if (typeof run === 'object' && 'image' in run) {
              const { image, width: w, height: h, tip, id } = run
              pieces.push({
                width: w,
                space: false,
                build: () => {
                  if (!id) return ui.image(image, w, h)
                  ui.box({ id }, () => ui.image(image, w, h))
                  if (tip) ui.tip(id, tip)
                },
              })
              continue
            }
            const text = typeof run === 'string' ? run : run.text
            const color = typeof run === 'string' ? style.color : (run.color ?? style.color)
            for (const word of text.split(/(\s+)/)) {
              if (!word) continue
              const space = /^\s+$/.test(word)
              const shown = space ? ' ' : word
              pieces.push({ width: textWidth(font, shown), space, build: () => add(make({}, textLeaf(shown, { font, color, shadow: style.shadow, lift: style.lift }))) })
            }
          }
          const lines: (typeof pieces)[] = [[]]
          let used = 0
          for (const piece of pieces) {
            const line = lines[lines.length - 1]
            if (piece.space && !line.length) continue
            if (used + piece.width > style.width && line.length && !piece.space) {
              while (line.length && line[line.length - 1].space) line.pop()
              lines.push([piece])
              used = piece.width
            } else {
              line.push(piece)
              used += piece.width
            }
          }
          ui.box({ direction: 'column' }, () => {
            for (const line of lines) ui.box({ direction: 'row', align: 'center' }, () => line.forEach((piece) => piece.build()))
          })
        },
        image(image, width, height, options = {}) {
          add(make({}, { kind: 'image', image, width, height, smooth: options.smooth ?? false, live: options.live ?? false }))
        },
        space(width = 0, height = 0, grow) {
          add(make({ width, height, grow }, { kind: 'space' }))
        },
        button(id, { active, disabled, group, ...style }, children) {
          nextButtons.set(id, { disabled: !!disabled, group: !!group })
          const hover = !disabled && overId === id
          const held = hover && press?.id === id
          const primary = style.skin === 'button-primary'
          const skin: SkinName = disabled ? 'button-disabled' : active || held ? 'button-active' : hover ? (primary ? 'button-primary-hover' : 'button-hover') : (style.skin ?? 'button')
          ui.box({ direction: 'row', align: 'center', justify: 'center', padding: [4, 10], gap: 6, ...style, id, skin }, children)
          return disabled ? null : (frameClicks.get(id) ?? null)
        },
        clicked: (id) => frameClicks.get(id) ?? null,
        hovered: (id) => overId === id,
        within: (id) => !!hit && (hit.id === id || hit.path.includes(id)),
        held(id) {
          if (!press || press.id !== id || press.button !== 0) return null
          const rect = hits.find((one) => one.id === id)?.rect
          if (!rect) return null
          return { u: clamp((pointer.x - rect.x) / rect.width, 0, 1), v: clamp((pointer.y - rect.y) / rect.height, 0, 1), click: press.click }
        },
        tip(id, content, anchor) {
          if (overId !== id || !content || press) return
          if (now - since >= TIP_DELAY) tips.push({ content, anchor })
          // Подсказка появится по сроку — кадр для неё нужен, даже если указатель стоит.
          else touched = true
        },
        input(id, value, options) {
          seenFields.add(id)
          let edit = edits.get(id)
          if (!edit) {
            edit = { value, caret: value.length, anchor: value.length }
            edits.set(id, edit)
          } else if (edit.value !== value && focused !== id) {
            // Текст поменяли снаружи (отправили, подставили подсказку) — курсор в конец.
            edit.value = value
            edit.caret = edit.anchor = value.length
          }
          if (options.focus && focused !== id) {
            focused = id
            touched = true
          }
          if (options.digits) edit.value = edit.value.replace(/\D+/g, '')
          if (options.maxLength) edit.value = edit.value.slice(0, options.maxLength)
          edit.caret = clamp(edit.caret, 0, edit.value.length)
          edit.anchor = clamp(edit.anchor, 0, edit.value.length)
          const isFocused = focused === id
          const shown = options.password ? '•'.repeat(edit.value.length) : edit.value
          const current = edit
          // Длинный текст сдвигается так, чтобы курсор был виден; без фокуса поле, как у страницы, показывает начало.
          const room = options.width ?? hits.find((one) => one.id === id)?.rect.width ?? 120
          const shift = focused === id ? Math.max(0, textWidth('body', shown.slice(0, edit.caret)) - (room - 18)) : 0
          const look = options.background ? { background: options.background, outline: options.outline } : { skin: 'well' as const, outline: options.outline }
          const color = options.color
          ui.box({ id, width: options.width, grow: options.width === undefined ? 1 : undefined, minWidth: 40, height: 28, ...look, padding: [6, 8], clip: true }, () => {
            // Отступ слева отрицательный: строка уезжает влево, и курсор остаётся в поле.
            ui.box({ direction: 'row', align: 'center', padding: [0, 0, 0, -shift] }, () => {
              if (!shown && options.placeholder) ui.text(options.placeholder, { color: '#8a8a8a' })
              if (isFocused && current.anchor !== current.caret) {
                const from = Math.min(current.anchor, current.caret)
                const to = Math.max(current.anchor, current.caret)
                if (from) ui.text(shown.slice(0, from), { color })
                ui.box({ background: '#1f63d8' }, () => ui.text(shown.slice(from, to), { color: '#eef6ff' }))
                if (to < shown.length) ui.text(shown.slice(to), { color })
              } else {
                if (current.caret && shown) ui.text(shown.slice(0, current.caret), { color })
                // Курсор мигает дважды в секунду, пока поле в фокусе.
                if (isFocused) {
                  if (Math.floor(now / 500) % 2 === 0) add(make({}, { kind: 'caret', color: '#eef6ff' }))
                  else ui.space(1, FONTS.body.line)
                  touched = true
                }
                if (current.caret < shown.length) ui.text(shown.slice(current.caret), { color })
              }
            })
          })
          return { value: edit.value, focused: isFocused, keys: frameKeys.get(id) ?? [] }
        },
        rect: (id) => hits.find((one) => one.id === id)?.rect ?? null,
      }
      build(ui)
      buttons = nextButtons
      // Поле, которого больше не описывают, теряет и фокус, и правку.
      for (const id of edits.keys()) if (!seenFields.has(id)) edits.delete(id)
      if (focused && !seenFields.has(focused)) focused = null

      // Подсказка — своей корневой панелью у указателя (или где ей велено), поверх всего; нажатия сквозь неё идут.
      for (const tip of tips.slice(-1)) {
        const node = make({ direction: 'column', gap: 2, padding: [6, 10], skin: 'panel', passive: true })
        stack.push(node)
        if (typeof tip.content === 'string') ui.text(tip.content, { wrap: TIP_WIDTH })
        else tip.content()
        stack.pop()
        measure(node, width - 16)
        if (tip.anchor) node.style.anchor = tip.anchor
        else {
          let x = pointer.x + 14
          if (x + node.want.width > width - 8) x = pointer.x - 14 - node.want.width
          let y = pointer.y - node.want.height - 12
          if (y < 8) y = pointer.y + 20
          node.style.anchor = { left: clamp(x, 8, width - 8 - node.want.width), top: clamp(y, 8, height - 8 - node.want.height) }
        }
        top.push(node)
      }

      // Раскладка: корневые панели — по привязке, их дети — рядами и столбцами.
      for (const root of top) {
        const anchor = root.style.anchor ?? {}
        measure(root, width - (anchor.left ?? 0) - (anchor.right ?? 0))
        place(root, anchored(root, width, height), scrolls)
      }

      // Подпись нарисованного: совпала с прошлой — холст тот же, рисовать и заливать незачем.
      const parts: string[] = [`${width}x${height}@${density}#${fontsLoaded()}`]
      const nextHits: typeof hits = []
      const nextRoots: Rect[] = []
      const nextKeeps = new Set<string>()
      let live = false
      const walk = (node: Node, clip: Rect, path: string[]) => {
        const { style, leaf, rect } = node
        parts.push(`${rect.x},${rect.y},${rect.width},${rect.height}|${style.skin ?? ''}|${style.background ?? ''}|${style.clip || style.scroll ? 1 : 0}|${style.outline ?? ''}|${style.alpha ?? ''}`)
        if (leaf?.kind === 'text') parts.push(`t${leaf.font}|${leaf.color}|${leaf.shadow ?? ''}${leaf.lift}|${leaf.lines.join('\n')}`)
        else if (leaf?.kind === 'image') {
          parts.push(`i${imageId(leaf.image)}|${leaf.smooth}`)
          live ||= leaf.live
        } else if (leaf?.kind === 'caret') parts.push('c')
        const visible = intersect(rect, clip)
        if (style.id) nextHits.push({ id: style.id, rect: visible, path })
        if (style.id && style.keepFocus) nextKeeps.add(style.id)
        const inner = style.clip || style.scroll ? visible : clip
        const childPath = style.id ? [...path, style.id] : path
        parts.push('(')
        for (const child of node.children) walk(child, inner, childPath)
        parts.push(')')
      }
      const screen = { x: 0, y: 0, width, height }
      for (const root of top) {
        walk(root, screen, [])
        if (!root.style.passive) nextRoots.push(root.rect)
      }
      hits = nextHits
      roots = nextRoots
      keeps = nextKeeps
      if (live) parts.push(`live${liveFrame++}`)
      const next = parts.join('\n')
      if (next === signature) return false
      signature = next

      const pixelWidth = Math.max(1, Math.round(width * density))
      const pixelHeight = Math.max(1, Math.round(height * density))
      if (surface.width !== pixelWidth || surface.height !== pixelHeight) {
        surface.width = pixelWidth
        surface.height = pixelHeight
      }
      context.setTransform(1, 0, 0, 1, 0, 0)
      context.clearRect(0, 0, pixelWidth, pixelHeight)
      context.setTransform(density, 0, 0, density, 0, 0)
      context.imageSmoothingEnabled = false
      const skinImages = skins()
      const paint = (node: Node) => {
        const { style, leaf, rect } = node
        if (rect.width <= 0 || rect.height <= 0) return
        const faded = style.alpha !== undefined && style.alpha < 1
        if (faded) {
          context.save()
          context.globalAlpha *= Math.max(0, style.alpha!)
        }
        if (style.background) {
          context.fillStyle = style.background
          context.fillRect(rect.x, rect.y, rect.width, rect.height)
        }
        if (style.skin) paintSkin(context, skinImages[style.skin], rect)
        if (style.outline) {
          context.strokeStyle = style.outline
          context.lineWidth = OUTLINE
          context.strokeRect(rect.x + OUTLINE / 2, rect.y + OUTLINE / 2, rect.width - OUTLINE, rect.height - OUTLINE)
        }
        if (leaf?.kind === 'text') {
          const { line } = FONTS[leaf.font]
          leaf.lines.forEach((text, index) => {
            const y = rect.y + index * line
            if (leaf.shadow) {
              const shade = textImage(leaf.font, leaf.shadow, text, density)
              if (shade) context.drawImage(shade, rect.x + leaf.lift, y + leaf.lift, shade.width / density, shade.height / density)
            }
            const image = textImage(leaf.font, leaf.color, text, density)
            if (image) context.drawImage(image, rect.x, y, image.width / density, image.height / density)
          })
        } else if (leaf?.kind === 'image') {
          context.imageSmoothingEnabled = leaf.smooth
          context.drawImage(leaf.image, rect.x, rect.y, rect.width, rect.height)
          context.imageSmoothingEnabled = false
        } else if (leaf?.kind === 'caret') {
          context.fillStyle = leaf.color
          context.fillRect(rect.x, rect.y + 2, 1, rect.height - 4)
        }
        const clipped = style.clip || style.scroll
        if (clipped) {
          context.save()
          context.beginPath()
          context.rect(rect.x, rect.y, rect.width, rect.height)
          context.clip()
        }
        node.children.forEach(paint)
        if (clipped) context.restore()
        if (faded) context.restore()
      }
      top.forEach(paint)
      return true
    },
    destroy() {
      target.removeEventListener('pointermove', onMove)
      target.removeEventListener('pointerdown', onDown)
      target.removeEventListener('pointerup', onUp)
      target.removeEventListener('pointercancel', onCancel)
      target.removeEventListener('pointerleave', onLeave)
      target.removeEventListener('wheel', onWheel)
      target.removeEventListener('contextmenu', onContextMenu)
      window.removeEventListener('keydown', onKey, true)
      if (press) clearTimeout(press.timer)
    },
  }
}
