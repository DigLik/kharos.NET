// Интерфейс в немедленном режиме, нарисованный на холсте (Canvas 2D) — и на странице, и в нативной оболочке (пункт
// 10.4 плана). Панели описываются вызовами каждый кадр интерфейса: `ui.box(...)`, `ui.text(...)`, `ui.button(...)`.
// Из них за кадр собирается дерево; в конце кадра оно раскладывается (ряды и столбцы с промежутками, отступами,
// выравниванием и растяжением — как flex у страницы) и рисуется. Состояния у панелей нет: всё, что им нужно, они
// берут из состояния игры, а ui помнит только места узлов прошлого кадра — по ним видно, что под указателем и что
// нажали. Задержка в кадр интерфейса незаметна, а раскладка от этого простая: место узла не нужно знать, пока он
// описывается.
//
// Нарисованное сравнивается с прошлым кадром подписью: если ничего не поменялось, холст не перерисовывается, и
// проход не заливает его в текстуру заново (см. pass.ts) — интерфейс почти всегда стоит.

import { CORNER, SKIN_SCALE, skins, type SkinName } from '../ui/skin'
import { FONTS, fontsLoaded, textImage, textWidth, type FontName } from './text'

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
}

/** Как узел раскладывает детей и как выглядит сам. */
export interface BoxStyle {
  /** Номер для ввода: по нему узнаётся наведение и нажатие, и он должен быть одним и тем же от кадра к кадру. */
  id?: string
  direction?: 'row' | 'column'
  gap?: number
  /** Отступ внутри: одно число на все стороны или [сверху и снизу, слева и справа]. */
  padding?: number | [number, number]
  /** Выравнивание детей поперёк: stretch тянет их на всю ширину (высоту) узла. */
  align?: 'start' | 'center' | 'end' | 'stretch'
  /** Расстановка детей вдоль: between — крайние к краям, остальные поровну между. */
  justify?: 'start' | 'center' | 'end' | 'between'
  width?: number
  height?: number
  minWidth?: number
  /** Доля свободного места вдоль родителя, которую забирает узел. */
  grow?: number
  /** Рамка скина девятью частями — как border-image у страницы. */
  skin?: SkinName
  background?: string
  /** Обрезать детей по своему месту — для списков с прокруткой. */
  clip?: boolean
  /** Обводка цветом внутри рамки (3 пикселя) — подсветка подсказки «Первых шагов». */
  outline?: string
  /** Только у корневых панелей: где они стоят на экране. */
  anchor?: Anchor
}

export interface TextStyle {
  font?: FontName
  color?: string
}

type Leaf =
  | { kind: 'text'; text: string; font: FontName; color: string }
  | { kind: 'image'; image: CanvasImageSource; width: number; height: number; smooth: boolean }
  | { kind: 'space' }

interface Node {
  style: BoxStyle
  leaf: Leaf | null
  children: Node[]
  /** Желаемый размер — считается при раскладке. */
  want: { width: number; height: number }
  rect: Rect
}

/** Что ввод сообщил за кадр интерфейса. */
interface Pointer {
  x: number
  y: number
  /** Указатель над холстом. */
  inside: boolean
  /** Узел, на котором зажали кнопку, — щелчок засчитывается, если её там же и отпустили. */
  pressed: string | null
  /** Узел, на котором щёлкнули за прошедший кадр. */
  clicked: string | null
  /** Когда указатель встал на нынешний узел, мс по performance.now: подсказка появляется не сразу. */
  since: number
  over: string | null
}

/** Через сколько мс наведения появляется подсказка. */
const TIP_DELAY = 350
/** Толщина обводки подсказки в пикселях CSS. */
const OUTLINE = 3
/** Сторона рамки скина в пикселях CSS. */
const BORDER = CORNER * SKIN_SCALE

export interface Ui {
  /** Размер экрана в пикселях CSS: по нему панели выбирают раскладку (узкий экран — в два ряда). */
  readonly width: number
  readonly height: number
  /** Узел с детьми: вызовы внутри children — его дети. На верхнем уровне это корневая панель (см. anchor). */
  box(style: BoxStyle, children?: () => void): void
  /** Строка текста. */
  text(text: string, style?: TextStyle): void
  /** Картинка: холст или изображение, нарисованное в размер width × height. smooth — сглаживать при растяжении. */
  image(image: CanvasImageSource, width: number, height: number, smooth?: boolean): void
  /** Пустое место; grow — растягивается на свободное место ряда. */
  space(width?: number, height?: number, grow?: number): void
  /**
   * Кнопка: узел с рамкой кнопки, сменой рамки при наведении и нажатии. Возвращает true в кадре после щелчка.
   * active — нажатое состояние (выбранная вкладка, включённый режим); disabled — не нажимается.
   */
  button(id: string, style: BoxStyle & { active?: boolean; disabled?: boolean }, children: () => void): boolean
  /** Указатель над узлом id (по месту прошлого кадра) — и ничто не лежит поверх. */
  hovered(id: string): boolean
  /** Подсказка узла id: появляется у указателя, когда он задержался над узлом. */
  tip(id: string, text: string): void
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

const pad = (style: BoxStyle): [number, number] => (typeof style.padding === 'number' ? [style.padding, style.padding] : (style.padding ?? [0, 0]))

/** Желаемый размер узла: заданный стилем или набранный детьми (или содержимым листа). */
function measure(node: Node): { width: number; height: number } {
  const { style, leaf } = node
  const [padY, padX] = pad(style)
  let width = 0
  let height = 0
  if (leaf?.kind === 'text') {
    width = textWidth(leaf.font, leaf.text)
    height = FONTS[leaf.font].line
  } else if (leaf?.kind === 'image') {
    width = leaf.width
    height = leaf.height
  } else {
    const row = style.direction === 'row'
    const sizes = node.children.map(measure)
    const gaps = (style.gap ?? 0) * Math.max(0, sizes.length - 1)
    const along = sizes.reduce((sum, size) => sum + (row ? size.width : size.height), 0) + gaps
    const across = sizes.reduce((most, size) => Math.max(most, row ? size.height : size.width), 0)
    width = row ? along : across
    height = row ? across : along
  }
  width = Math.max(style.width ?? width + padX * 2, style.minWidth ?? 0)
  height = style.height ?? height + padY * 2
  node.want = { width, height }
  return node.want
}

/** Ставит детей узла на места: вдоль — по порядку с промежутком, свободное место — растущим; поперёк — выравнивание. */
function place(node: Node, rect: Rect) {
  node.rect = { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width), height: Math.round(rect.height) }
  const { style, children } = node
  if (!children.length) return
  const row = style.direction === 'row'
  const [padY, padX] = pad(style)
  const inner = { x: node.rect.x + padX, y: node.rect.y + padY, width: node.rect.width - padX * 2, height: node.rect.height - padY * 2 }
  const gap = style.gap ?? 0
  const room = row ? inner.width : inner.height
  const wanted = children.reduce((sum, child) => sum + (row ? child.want.width : child.want.height), 0) + gap * (children.length - 1)
  const free = Math.max(0, room - wanted)
  const grows = children.reduce((sum, child) => sum + (child.style.grow ?? 0), 0)
  let offset = 0
  let spacing = gap
  if (!grows) {
    if (style.justify === 'center') offset = free / 2
    else if (style.justify === 'end') offset = free
    else if (style.justify === 'between' && children.length > 1) spacing = gap + free / (children.length - 1)
  }
  let along = row ? inner.x : inner.y
  along += offset
  for (const child of children) {
    const extra = grows ? (free * (child.style.grow ?? 0)) / grows : 0
    const size = (row ? child.want.width : child.want.height) + extra
    const crossRoom = row ? inner.height : inner.width
    const align = style.align ?? 'start'
    const crossWant = align === 'stretch' ? crossRoom : row ? child.want.height : child.want.width
    const crossOffset = align === 'center' ? (crossRoom - crossWant) / 2 : align === 'end' ? crossRoom - crossWant : 0
    const cross = (row ? inner.y : inner.x) + crossOffset
    place(child, row ? { x: along, y: cross, width: size, height: crossWant } : { x: cross, y: along, width: crossWant, height: size })
    along += size + spacing
  }
}

/** Место корневой панели по привязке: заданы оба края — растянута между ними, иначе — своего размера. */
function anchored(node: Node, width: number, height: number): Rect {
  const anchor = node.style.anchor ?? {}
  const { want } = node
  const w = anchor.left !== undefined && anchor.right !== undefined ? width - anchor.left - anchor.right : want.width
  const h = anchor.top !== undefined && anchor.bottom !== undefined ? height - anchor.top - anchor.bottom : want.height
  const x = anchor.left ?? (anchor.right !== undefined ? width - anchor.right - w : anchor.centerX !== undefined ? (width - w) / 2 + anchor.centerX : 0)
  const y = anchor.top ?? (anchor.bottom !== undefined ? height - anchor.bottom - h : 0)
  return { x, y, width: w, height: h }
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

/**
 * Интерфейс на холсте. target — холст игры: с него берутся указатель и нажатия (у оболочки они приходят туда же,
 * см. `__kharos_input` в native/js/prelude.js).
 */
export function createGui(target: HTMLElement): Gui {
  const surface = document.createElement('canvas')
  const context = surface.getContext('2d')!
  const pointer: Pointer = { x: 0, y: 0, inside: false, pressed: null, clicked: null, since: 0, over: null }
  /** Места узлов с номерами по прошлому кадру — в порядке рисования: кто позже, тот сверху. */
  let hits: { id: string; rect: Rect }[] = []
  /** Места корневых панелей прошлого кадра: под ними мир нажатий не получает. */
  let roots: Rect[] = []
  let signature = ''
  let touched = true

  const topmost = (x: number, y: number) => {
    for (let index = hits.length - 1; index >= 0; index--) {
      const { id, rect } = hits[index]
      if (x >= rect.x && y >= rect.y && x < rect.x + rect.width && y < rect.y + rect.height) return id
    }
    return null
  }
  const covers = (x: number, y: number) => roots.some((rect) => x >= rect.x && y >= rect.y && x < rect.x + rect.width && y < rect.y + rect.height)

  const onMove = (event: PointerEvent) => {
    pointer.x = event.offsetX
    pointer.y = event.offsetY
    pointer.inside = true
    touched = true
  }
  const onDown = (event: PointerEvent) => {
    onMove(event)
    if (event.button === 0) pointer.pressed = topmost(pointer.x, pointer.y)
  }
  const onUp = (event: PointerEvent) => {
    onMove(event)
    if (event.button !== 0) return
    const over = topmost(pointer.x, pointer.y)
    if (over && over === pointer.pressed) pointer.clicked = over
    pointer.pressed = null
  }
  const onLeave = () => {
    pointer.inside = false
    touched = true
  }
  target.addEventListener('pointermove', onMove)
  target.addEventListener('pointerdown', onDown)
  target.addEventListener('pointerup', onUp)
  target.addEventListener('pointerleave', onLeave)

  return {
    surface,
    covers,
    get touched() {
      return touched
    },
    frame(width, height, density, build) {
      touched = false
      const top: Node[] = []
      const stack: Node[] = []
      const tips: { id: string; text: string }[] = []
      const clicked = pointer.clicked
      pointer.clicked = null
      const over = pointer.inside ? topmost(pointer.x, pointer.y) : null
      if (over !== pointer.over) {
        pointer.over = over
        pointer.since = performance.now()
      }
      const add = (node: Node) => (stack.length ? stack[stack.length - 1].children : top).push(node)
      const make = (style: BoxStyle, leaf: Leaf | null = null): Node => ({ style, leaf, children: [], want: { width: 0, height: 0 }, rect: { x: 0, y: 0, width: 0, height: 0 } })
      const ui: Ui = {
        width,
        height,
        box(style, children) {
          const node = make(style)
          add(node)
          if (!children) return
          stack.push(node)
          children()
          stack.pop()
        },
        text(text, style = {}) {
          add(make({}, { kind: 'text', text, font: style.font ?? 'body', color: style.color ?? '#d6e6f5' }))
        },
        image(image, width, height, smooth = false) {
          add(make({}, { kind: 'image', image, width, height, smooth }))
        },
        space(width = 0, height = 0, grow) {
          add(make({ width, height, grow }, { kind: 'space' }))
        },
        button(id, { active, disabled, ...style }, children) {
          const hover = !disabled && over === id
          const skin: SkinName = disabled ? 'button-disabled' : active || (hover && pointer.pressed === id) ? 'button-active' : hover ? 'button-hover' : (style.skin ?? 'button')
          ui.box({ direction: 'row', align: 'center', justify: 'center', padding: [4, 10], gap: 6, ...style, id, skin }, children)
          return !disabled && clicked === id
        },
        hovered: (id) => over === id,
        tip(id, text) {
          if (over === id && text && performance.now() - pointer.since >= TIP_DELAY) tips.push({ id, text })
          // Подсказка появится по сроку — кадр для неё нужен, даже если указатель стоит.
          else if (over === id && text) touched = true
        },
      }
      build(ui)

      // Подсказка — своей корневой панелью у указателя, поверх всего.
      for (const tip of tips.slice(-1)) {
        const node = make({ direction: 'column', gap: 2, padding: [6, 10], skin: 'panel' })
        for (const line of tip.text.split('\n')) node.children.push(make({}, { kind: 'text', text: line, font: 'body', color: '#d6e6f5' }))
        measure(node)
        const x = Math.max(4, Math.min(width - node.want.width - 4, pointer.x + 14))
        const y = pointer.y + 20 + node.want.height > height ? pointer.y - node.want.height - 8 : pointer.y + 20
        node.style.anchor = { left: x, top: y }
        top.push(node)
      }

      // Раскладка: корневые панели — по привязке, их дети — рядами и столбцами.
      for (const root of top) {
        measure(root)
        place(root, anchored(root, width, height))
      }

      // Подпись нарисованного: совпала с прошлой — холст тот же, рисовать и заливать незачем.
      const parts: string[] = [`${width}x${height}@${density}#${fontsLoaded()}`]
      const nextHits: { id: string; rect: Rect }[] = []
      const walk = (node: Node) => {
        const { style, leaf, rect } = node
        parts.push(`${rect.x},${rect.y},${rect.width},${rect.height}|${style.skin ?? ''}|${style.background ?? ''}|${style.clip ? 1 : 0}|${style.outline ?? ''}`)
        if (leaf?.kind === 'text') parts.push(`t${leaf.font}|${leaf.color}|${leaf.text}`)
        if (leaf?.kind === 'image') parts.push(`i${imageId(leaf.image)}|${leaf.smooth}`)
        if (style.id) nextHits.push({ id: style.id, rect })
        parts.push('(')
        node.children.forEach(walk)
        parts.push(')')
      }
      top.forEach(walk)
      hits = nextHits
      roots = top.map((root) => root.rect)
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
          const image = textImage(leaf.font, leaf.color, leaf.text, density)
          if (image) context.drawImage(image, rect.x, rect.y, image.width / density, image.height / density)
        } else if (leaf?.kind === 'image') {
          context.imageSmoothingEnabled = leaf.smooth
          context.drawImage(leaf.image, rect.x, rect.y, rect.width, rect.height)
          context.imageSmoothingEnabled = false
        }
        if (style.clip) {
          context.save()
          context.beginPath()
          context.rect(rect.x, rect.y, rect.width, rect.height)
          context.clip()
        }
        node.children.forEach(paint)
        if (style.clip) context.restore()
      }
      top.forEach(paint)
      return true
    },
    destroy() {
      target.removeEventListener('pointermove', onMove)
      target.removeEventListener('pointerdown', onDown)
      target.removeEventListener('pointerup', onUp)
      target.removeEventListener('pointerleave', onLeave)
    },
  }
}
