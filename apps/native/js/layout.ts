// Раскладка интерфейса: место каждого узла в пикселях кадра. В браузере её делает движок по CSS, а здесь — этот
// файл по короткому списку свойств (см. `dom.ts`). Раскладка одна: flex — ряд или столбец с промежутком, отступом и
// выравниванием, как у панелей интерфейса; узел, вынутый из потока (`position: absolute`), встаёт по своим краям.
//
// Размер считается дважды — сперва желаемый (`measure`), потом настоящий (`place`): родитель, растянувший ребёнка,
// должен знать, сколько тот просит. Свободное место ряда уходит растущим узлам (`flexGrow`): так инструментовая
// полоса занимает остаток панели, а не висит по её середине.
//
// Место — целые пиксели: интерфейс пиксельный, и половинка пикселя на framebuffer'е дала бы мыло.

import { Element, Node, type Style } from './dom'

/** Место узла на экране: отсчёт от левого верхнего угла кадра. */
export type Rect = { x: number; y: number; width: number; height: number }

/** Размер кадра интерфейса — размер буфера окна: интерфейс рисуется поверх сцены, во всё окно. */
export type Size = { width: number; height: number }

/** Места узлов: раскладка кладёт их сюда, а рисование и проверки читают. */
const frames = new WeakMap<Node, Rect>()

/** Место узла: у неразложенного узла места нет. */
export function frame(node: Node): Rect {
  return frames.get(node) ?? { x: 0, y: 0, width: 0, height: 0 }
}

/** Отступ по всем сторонам и промежуток между детьми — так их задаёт `style`. */
const paddingOf = (style: Style) => style.padding ?? 0
const gapOf = (style: Style) => style.gap ?? 0

/** Сторона, заданная стилем: `auto` (или ничего) — по содержимому. */
const fixed = (value: number | 'auto' | undefined) => (typeof value === 'number' ? value : null)

/** Дети, стоящие в потоке: невидимые и вынутые из потока в размере родителя не участвуют. */
function flowing(node: Element): Element[] {
  return node.childNodes.filter(
    (child): child is Element => child instanceof Element && child.style.display !== 'none' && child.style.position !== 'absolute',
  )
}

/** Столбец ли это: без `display: flex` узел укладывает детей друг под другом, как браузерный блок. */
const stack = (node: Element) => node.style.flexDirection === 'column' || node.style.display !== 'flex'

/**
 * Размер, который узел просит у родителя: заданный стилем или набранный содержимым. Складывается по главной оси —
 * дети и промежутки между ними, — а поперёк берётся самый широкий (или высокий) ребёнок.
 */
export function measure(node: Node): Size {
  if (!(node instanceof Element) || node.style.display === 'none') return { width: 0, height: 0 }
  const style = node.style
  const width = fixed(style.width)
  const height = fixed(style.height)
  if (width !== null && height !== null) return { width, height }
  const kids = flowing(node)
  const sizes = kids.map(measure)
  const gaps = gapOf(style) * Math.max(0, kids.length - 1)
  const pad = paddingOf(style) * 2
  const along = sizes.reduce((total, size) => total + (stack(node) ? size.height : size.width), 0) + gaps + pad
  const across = sizes.reduce((most, size) => Math.max(most, stack(node) ? size.width : size.height), 0) + pad
  return stack(node)
    ? { width: width ?? across, height: height ?? along }
    : { width: width ?? along, height: height ?? across }
}

/** Раскладывает дерево по кадру: корень занимает кадр целиком, дети — как велит их стиль. */
export function layout(root: Element, size: Size): void {
  const rect = { x: 0, y: 0, width: Math.round(size.width), height: Math.round(size.height) }
  frames.set(root, rect)
  arrange(root, rect)
}

/**
 * Кладёт детей узла внутрь его места. По главной оси — ряд или столбец — дети идут друг за другом с промежутком, и
 * на неё же уходит свободное место растущих; поперёк ребёнок выравнивается (`alignItems`), а `stretch` тянет его на
 * весь родитель: так утопленное окно занимает высоту панели.
 */
function arrange(node: Element, rect: Rect): void {
  const style = node.style
  const column = stack(node)
  const pad = paddingOf(style)
  const inner: Rect = {
    x: rect.x + pad,
    y: rect.y + pad,
    width: Math.max(0, rect.width - pad * 2),
    height: Math.max(0, rect.height - pad * 2),
  }
  const kids = flowing(node)
  // Размер каждого ребёнка: что просит стиль, а что не просит — то по содержимому.
  const sizes = kids.map((kid) => {
    const wanted = measure(kid)
    return {
      along: fixed(column ? kid.style.height : kid.style.width) ?? (column ? wanted.height : wanted.width),
      across: fixed(column ? kid.style.width : kid.style.height) ?? (column ? wanted.width : wanted.height),
    }
  })
  const gap = gapOf(style)
  const acrossFull = column ? inner.width : inner.height
  const alongFull = column ? inner.height : inner.width
  const taken = sizes.reduce((total, size) => total + size.along, 0) + gap * Math.max(0, kids.length - 1)
  // Свободное место ряда делят растущие — по весу (`flexGrow`); прижатие остатка задаёт `justifyContent`.
  const weights = kids.reduce((total, kid) => total + (kid.style.flexGrow ?? 0), 0)
  const free = weights > 0 ? Math.max(0, alongFull - taken) : 0
  if (weights > 0) for (const [at, kid] of kids.entries()) sizes[at].along += (free * (kid.style.flexGrow ?? 0)) / weights
  const rest = Math.max(0, alongFull - taken - free)
  const between = gap + (style.justifyContent === 'space-between' && kids.length > 1 ? rest / (kids.length - 1) : 0)
  const start = style.justifyContent === 'center' ? rest / 2 : style.justifyContent === 'end' ? rest : 0
  let along = (column ? inner.y : inner.x) + start
  for (const [at, kid] of kids.entries()) {
    const child = kid.style
    const own = fixed(column ? child.width : child.height)
    const across = own ?? (style.alignItems === undefined || style.alignItems === 'stretch' ? acrossFull : sizes[at].across)
    const aside = style.alignItems === 'center' ? (acrossFull - across) / 2 : style.alignItems === 'end' ? acrossFull - across : 0
    const frame = column
      ? { x: inner.x + aside, y: along, width: across, height: sizes[at].along }
      : { x: along, y: inner.y + aside, width: sizes[at].along, height: across }
    place(kid, { x: Math.round(frame.x), y: Math.round(frame.y), width: Math.round(frame.width), height: Math.round(frame.height) })
    along += sizes[at].along + between
  }
  // Узлы вне потока: их место — от родителя, и на остальных они не влияют.
  for (const kid of node.childNodes) {
    if (kid instanceof Element && kid.style.position === 'absolute' && kid.style.display !== 'none') place(kid, rect)
  }
}

/**
 * Ставит узел по месту родителя: `left` и `top` отсчитываются от его угла, `right` и `bottom` — от его правого и
 * нижнего края. Заданы сразу оба края, а сторона — `auto`, — узел растягивается между ними, как в CSS: так панель
 * встаёт во всю ширину окна, не зная его размера.
 */
function place(child: Element, parent: Rect): void {
  const style = child.style
  const wanted = measure(child)
  const stretch = fixed(style.width) ?? (style.left !== undefined && style.right !== undefined ? parent.width - style.left - style.right : null)
  const tall = fixed(style.height) ?? (style.top !== undefined && style.bottom !== undefined ? parent.height - style.top - style.bottom : null)
  const width = stretch ?? wanted.width
  const height = tall ?? wanted.height
  const x = style.left !== undefined ? parent.x + style.left : style.right !== undefined ? parent.x + parent.width - width - style.right : parent.x
  const y = style.top !== undefined ? parent.y + style.top : style.bottom !== undefined ? parent.y + parent.height - height - style.bottom : parent.y
  const rect = { x: Math.round(x), y: Math.round(y), width: Math.round(width), height: Math.round(height) }
  frames.set(child, rect)
  arrange(child, rect)
}

