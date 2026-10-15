// Лёгкий документ интерфейса: узлы, дерево и стиль коротким списком свойств. В браузере разметку и стили ведёт
// движок, а здесь нужен ровно тот минимум, из которого складывается панель, и ничего сверх: интерфейс собирается
// кодом, стили живут в самих компонентах, а CSS нет вовсе (так и задумано, см. `docs/design.md`, 10.4).
//
// Узлы повторяют браузерные настолько, насколько их спрашивает тот, кто в документ рендерит (Preact — как undom):
// `nodeType`, `parentNode`, `childNodes`, вставка и удаление детей, атрибуты и слушатели. Ни раскладки, ни
// рисования документ не делает: это `layout.ts` и `draw.ts`, и зовёт их кадр интерфейса (см. `js/hud.ts`).

import { fromUrl } from './png'

/**
 * Стиль узла: короткий список свойств. Именованы по-браузерному, но значения — числа и слова, а не строки CSS:
 * раскладке нужны пиксели (`gap: 8`), а рисованию — цвета и адреса картинок.
 */
export type Style = {
  /** `flex` — дети встают рядом или друг под другом (см. `flexDirection`), `none` — узел не рисуется и не считается. */
  display?: 'flex' | 'none'
  flexDirection?: 'row' | 'column'
  justifyContent?: 'start' | 'center' | 'end' | 'space-between'
  alignItems?: 'start' | 'center' | 'end' | 'stretch'
  /** Место узла: без `absolute` узел встаёт в поток родителя. */
  position?: 'absolute' | 'static'
  left?: number
  top?: number
  right?: number
  bottom?: number
  /** Сторона узла; `auto` — по содержимому (у картинки — по самой картинке). */
  width?: number | 'auto'
  height?: number | 'auto'
  /** Прибавка к свободному месту по главной оси: ею инструментовая полоса растягивается на остаток ряда. */
  flexGrow?: number
  padding?: number
  gap?: number
  /** Рисование: заливка цветом, рамка скина (имя переменной корня) и картинка адресом. */
  background?: string
  skin?: string
  icon?: string
  alpha?: number
}

/** Ребёнок узла: элемент или подпись. */
export type Child = Node | null | false

/**
 * Узел дерева. Слушатели собираются здесь же (`addEventListener`): события придут вместе с вводом, а вешать их
 * на кнопку интерфейс умеет уже сейчас.
 */
export class Node {
  nodeType = 0
  parentNode: Element | null = null
  childNodes: Node[] = []
  readonly listeners = new Map<string, Set<(event: unknown) => void>>()

  get firstChild(): Node | null {
    return this.childNodes[0] ?? null
  }

  get nextSibling(): Node | null {
    const brothers = this.parentNode?.childNodes
    if (!brothers) return null
    return brothers[brothers.indexOf(this) + 1] ?? null
  }

  appendChild<T extends Node>(child: T): T {
    child.parentNode?.removeChild(child)
    child.parentNode = this as unknown as Element
    this.childNodes.push(child)
    return child
  }

  insertBefore<T extends Node>(child: T, before: Node | null): T {
    child.parentNode?.removeChild(child)
    const at = before ? this.childNodes.indexOf(before) : -1
    child.parentNode = this as unknown as Element
    if (at < 0) this.childNodes.push(child)
    else this.childNodes.splice(at, 0, child)
    return child
  }

  removeChild<T extends Node>(child: T): T {
    const at = this.childNodes.indexOf(child)
    if (at >= 0) this.childNodes.splice(at, 1)
    child.parentNode = null
    return child
  }

  addEventListener(type: string, listener: (event: unknown) => void): void {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set())
    this.listeners.get(type)!.add(listener)
  }

  removeEventListener(type: string, listener: (event: unknown) => void): void {
    this.listeners.get(type)?.delete(listener)
  }
}

/** Подпись: строка текста. Место и размер ей даёт шрифт — пока его нет, и подпись ничего не занимает (см. README). */
export class Text extends Node {
  nodeType = 3
  constructor(public data: string) {
    super()
  }
}

/** Элемент: коробка, картинка или холст. Стиль его — здесь же, а не в таблице стилей. */
export class Element extends Node {
  nodeType = 1
  readonly attributes = new Map<string, string>()
  readonly style: Style
  constructor(readonly tag: string, style: Style = {}) {
    super()
    this.style = style
  }

  get className(): string {
    return this.attributes.get('class') ?? ''
  }

  set className(value: string) {
    this.setAttribute('class', value)
  }

  setAttribute(name: string, value: string | number): void {
    this.attributes.set(name, String(value))
  }

  getAttribute(name: string): string | null {
    return this.attributes.get(name) ?? null
  }

  removeAttribute(name: string): void {
    this.attributes.delete(name)
  }

  /** Все подписи под узлом строкой: так видно, что собралось, — по ней же сверяет проверка. */
  get textContent(): string {
    return this.childNodes.map((child) => (child instanceof Text ? child.data : (child as Element).textContent)).join('')
  }
}

/**
 * Документ интерфейса: корень дерева (`body`) и корень переменных (`documentElement`). Переменные — скины и
 * места панелей — ставит интерфейс в обёртку окна (`--skin-*`, см. `src/ui/skin.ts`): там они и лежат, и оттуда
 * их читает рисование.
 */
export class Document {
  nodeType = 9
  readonly body = new Element('body')
  readonly documentElement: { style: { getPropertyValue(name: string): string } }

  constructor() {
    // Корень переменных берётся у окна: в браузере скины ставит тот же `documentElement`.
    this.documentElement = document.documentElement
  }

  /** Холст заводит обёртка окна: за номером холста стоят пиксели в Rust (см. `src/canvas`). */
  createElement(tag: string): Element {
    return tag === 'canvas' ? (document.createElement(tag) as unknown as Element) : new Element(tag)
  }

  createTextNode(data: string): Text {
    return new Text(data)
  }
}

/** Документ интерфейса с пустым деревом: с него начинается кадр интерфейса. */
export function createDocument(): Document {
  return new Document()
}

/** Коробка: из неё складывается каждый компонент — панель, ряд, кнопка. */
export function box(style: Style, ...children: Child[]): Element {
  const make = new Element('div', style)
  for (const child of children) if (child) make.appendChild(child)
  return make
}

/** Подпись: так интерфейс пишет числа и названия. */
export function text(data: string): Text {
  return new Text(data)
}

/**
 * Картинка адресом: так рисуются значки и портреты. Сторона берётся у самой картинки, если её не задали:
 * нарисовать картинку в чужом размере значит растянуть её, а интерфейс рисует пиксель в пиксель.
 */
export function picture(url: string, style: Style = {}): Element {
  const made = new Element('img', { ...style, icon: url })
  if (made.style.width === undefined || made.style.height === undefined) {
    const size = fromUrl(url)
    made.style.width ??= size.width
    made.style.height ??= size.height
  }
  return made
}

