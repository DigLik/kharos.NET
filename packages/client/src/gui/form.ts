// Поля окон меню и редактора на холсте: окно с заголовком и подвалом, кнопки, ползунок, флажок, ряд вариантов с
// переносом, числовое и текстовое поле, раскрывающийся блок, подпись слева от поля. Вид — тот же, что был у страницы
// (.menu__* и .editor__* в packages/client/src/style.css).

import type { SkinName } from '../ui/skin'
import { textWidth } from './text'
import type { Click, Ui } from './ui'
import { COLORS, SHADOW } from './widgets'

/** Кнопка меню: высота 32, текст с тенью. primary — главное действие окна, danger — опасное. */
export function menuButton(
  ui: Ui,
  id: string,
  label: string,
  options: { primary?: boolean; danger?: boolean; active?: boolean; disabled?: boolean; grow?: number; width?: number; tip?: string; height?: number; padding?: [number, number] } = {},
): Click | null {
  const skin: SkinName | undefined = options.danger ? 'danger' : options.primary ? 'button-primary' : undefined
  const color = options.disabled ? COLORS.muted : options.primary || options.danger || options.active ? COLORS.bright : COLORS.text
  const [vertical, horizontal] = options.padding ?? [4, 12]
  // Отступ — как у кнопки страницы: свой и рамка в 4 пикселя. Подпись шире кнопки сокращается многоточием.
  const click = ui.button(id, { skin, active: options.active, disabled: options.disabled, grow: options.grow, width: options.width, height: options.height ?? 32, padding: [vertical, horizontal + FRAME], alpha: options.disabled ? 0.6 : undefined }, () => ui.text(label, { ...SHADOW, color, ellipsis: true }))
  if (options.tip) ui.tip(id, options.tip)
  return click
}

/** Рамка кнопки страницы: border в 4 пикселя поверх отступа. */
const FRAME = 4

/** Ширина кнопки с подписью — для переноса рядов. */
export const buttonWidth = (label: string, padding = 12) => textWidth('body', label) + 1 + (padding + FRAME) * 2

export interface WindowOptions {
  id: string
  title: string
  width: number
  /** Где окно: по умолчанию — посередине экрана. */
  anchor?: import('./ui').Anchor
  /** Предел высоты: длинное содержимое прокручивается внутри окна. */
  maxHeight?: number
  /** Кнопка «Назад» слева в подвале. */
  back?: string
  /** Кнопки действий справа в подвале; главное — последним. */
  actions?: () => void
}

/**
 * Окно меню: заголовок, содержимое и подвал — «Назад» слева, действия экрана справа, главное из них последним. Так на
 * всех экранах кнопки стоят на одних и тех же местах. Окно не выше экрана: длинное содержимое прокручивается внутри.
 * Возвращает true, если нажали «Назад».
 */
export function menuWindow(ui: Ui, options: WindowOptions, body: () => void): boolean {
  let back = false
  const anchor = options.anchor ?? { centerX: 0, centerY: 0 }
  // Не ниже экрана: от верхнего края привязки до низа с полем в 16.
  const room = ui.height - (anchor.top ?? anchor.bottom ?? 16) - 16
  const maxHeight = Math.min(options.maxHeight ?? room, room)
  ui.box({ id: options.id, anchor, width: options.width, maxHeight, skin: 'panel', padding: 18, direction: 'column', align: 'stretch' }, () => {
    ui.box({ padding: [0, 0, 12, 0], direction: 'row', justify: 'center' }, () => ui.text(options.title, { font: 'title', color: COLORS.dim, shadow: COLORS.ink, lift: 2, ellipsis: true }))
    // Тело уступает высоту, когда окну тесно (см. place в ui.ts): заголовок и подвал остаются на месте.
    ui.box({ id: `${options.id}:body`, direction: 'column', align: 'stretch', gap: 10, scroll: true, minHeight: 48 }, body)
    if (!options.back && !options.actions) return
    // Подвал отбит от тела отступом и чертой — как margin-top и border-top подвала у страницы.
    ui.space(0, 14)
    ui.box({ height: 2, background: '#1c2b3e' })
    // «Назад» слева, действия — группой справа; тесно — группа уходит строкой ниже и переносится сама.
    ui.box({ direction: 'row', align: 'center', gap: 6, padding: [12, 0, 0, 0], wrap: true }, () => {
      if (options.back && menuButton(ui, `${options.id}:back`, options.back)) back = true
      if (options.actions) ui.box({ direction: 'row', gap: 6, wrap: true, grow: 1, justify: 'end' }, options.actions)
    })
  })
  return back
}

/** Подпись поля слева и поле справа — как .menu__field--slider и --choice: подпись фиксированной ширины. */
export function row(ui: Ui, label: string, labelWidth: number, control: () => void, tip?: string, id?: string) {
  ui.box({ direction: 'row', align: 'center', gap: 8 }, () => {
    const labelId = id ?? `label:${label}`
    ui.box({ id: tip ? labelId : undefined, width: labelWidth }, () => ui.text(label, { color: COLORS.dim, wrap: true }))
    if (tip) ui.tip(labelId, tip)
    control()
  })
}

/**
 * Ползунок: дорожка в утопленном окне, заливка огней команды до ручки. Тянут или щёлкают — значение идёт за
 * указателем, по шагу step. Возвращает новое значение (или прежнее).
 */
export function slider(ui: Ui, id: string, value: number, options: { min: number; max: number; step: number; width: number; disabled?: boolean }): number {
  const { min, max, step, width, disabled } = options
  const share = max > min ? Math.max(0, Math.min(1, (value - min) / (max - min))) : 0
  const inner = width - 8
  ui.box({ id, width, height: 20, skin: 'well', padding: 4, direction: 'row', alpha: disabled ? 0.5 : undefined }, () => {
    ui.box({ width: Math.round(inner * share), height: 12, background: COLORS.teamMid })
    ui.box({ width: 4, height: 12, background: COLORS.bright })
  })
  const held = !disabled && ui.held(id)
  if (!held) return value
  const raw = min + held.u * (max - min)
  const stepped = Math.round((raw - min) / step) * step + min
  return Math.max(min, Math.min(max, Math.round(stepped * 1e6) / 1e6))
}

/** Флажок с подписью. Возвращает новое состояние. */
export function checkbox(ui: Ui, id: string, checked: boolean, label: string, disabled = false): boolean {
  ui.box({ id, direction: 'row', align: 'center', gap: 8, alpha: disabled ? 0.5 : undefined }, () => {
    // Отмеченный — квадрат огней команды в утопленном окне, как флажок страницы.
    ui.box({ width: 18, height: 18, skin: 'well', direction: 'row', justify: 'center', align: 'center' }, () => {
      if (checked) ui.box({ width: 8, height: 8, background: COLORS.team })
    })
    ui.box({ shrink: true }, () => ui.text(label, { wrap: true }))
  })
  return !disabled && ui.clicked(id) ? !checked : checked
}

/**
 * Ряд вариантов: кнопки, выбранная нажата; не влезают в width — переносятся. fill — кнопки делят ширину строки
 * поровну. Возвращает выбранный щелчком вариант или undefined.
 */
export function chips<T>(ui: Ui, prefix: string, options: readonly (readonly [T, string])[], value: T | undefined, width: number, settings: { fill?: boolean; gap?: number; small?: boolean; tips?: (string | undefined)[] } = {}): { picked: T } | null {
  const gap = settings.gap ?? 6
  const padding = settings.small ? 6 : 12
  let picked: { picked: T } | null = null
  const rows: (readonly [T, string, number])[][] = [[]]
  let used = 0
  options.forEach(([option, label], index) => {
    const w = buttonWidth(label, padding)
    const line = rows[rows.length - 1]
    if (line.length && used + gap + w > width) {
      rows.push([[option, label, index]])
      used = w
    } else {
      line.push([option, label, index])
      used += (line.length > 1 ? gap : 0) + w
    }
  })
  ui.box({ direction: 'column', gap: settings.small ? 2 : gap, width }, () => {
    for (const line of rows) {
      ui.box({ direction: 'row', gap: settings.small ? 2 : gap }, () => {
        for (const [option, label, index] of line) {
          const id = `${prefix}:${index}`
          const click = menuButton(ui, id, label, { active: option === value, grow: settings.fill ? 1 : undefined, height: settings.small ? 24 : 32, padding: [2, padding], tip: settings.tips?.[index] })
          if (click) picked = { picked: option }
        }
      })
    }
  })
  return picked
}

/** Текстовое поле. Возвращает набранное (сразу, по буквам) и нажатые в нём клавиши. */
export function textField(ui: Ui, id: string, value: string, options: { width?: number; placeholder?: string; maxLength?: number; password?: boolean; focus?: boolean }) {
  return ui.input(id, value, { ...options, color: COLORS.bright })
}

/**
 * Числовое поле, значение которого может меняться само (панель редактора, количество на складе): пока в нём курсор,
 * оно показывает набранное. Новое число уходит по Enter или когда поле теряет фокус — его и возвращает, иначе null.
 */
export function numberField(ui: Ui, id: string, value: number, options: { width?: number; min?: number; max?: number; live?: boolean }): number | null {
  const field = ui.input(id, String(value), { width: options.width, digits: true, maxLength: 12, color: COLORS.bright })
  // Без live число уходит по каждой правке (как onInput у страницы), с live — только по Enter или уходу фокуса.
  const commit = !options.live || field.keys.includes('Enter') || field.keys.includes('Blur')
  if (!commit) return null
  const number = Number(field.value)
  if (field.value.trim() === '' || !Number.isFinite(number)) return null
  const clamped = Math.min(options.max ?? Infinity, Math.max(options.min ?? -Infinity, number))
  return clamped === value ? null : clamped
}

/** Раскрывающийся блок: строка-заголовок со стрелкой. Возвращает, открыт ли он теперь. */
export function details(ui: Ui, id: string, title: string, open: boolean, body: () => void): boolean {
  ui.box({ id, direction: 'row', gap: 6 }, () => ui.text(`${open ? '▾' : '▸'} ${title}`, { color: COLORS.dim, wrap: true }))
  const next = ui.clicked(id) ? !open : open
  if (open) body()
  return next
}

/** Заголовок группы полей: мелкий, бледный, ПРОПИСНЫМИ, с чертой сверху (кроме первого). */
export function groupTitle(ui: Ui, title: string, first = false) {
  if (!first) ui.box({ height: 2, background: '#1c2b3e' })
  ui.box({ padding: [first ? 0 : 6, 0, 0, 0] }, () => ui.text(title.toUpperCase(), { color: COLORS.muted }))
}

/** Ссылка: открывает адрес в браузере (у оболочки — там, где сможет). */
export function link(ui: Ui, id: string, label: string, url: string, center = false) {
  const color = ui.hovered(id) ? '#a5d0ff' : '#7fb7ff'
  ui.box({ id, direction: 'row', justify: center ? 'center' : 'start' }, () => ui.text(label, { color, ellipsis: true }))
  if (ui.clicked(id)) openUrl(url)
}

/** Открыть адрес снаружи: на странице — новая вкладка; у оболочки — системный браузер, если он ей доступен. */
export function openUrl(url: string) {
  const open = (globalThis as { open?: (url: string, target?: string, features?: string) => unknown }).open
  if (typeof open === 'function') open(url, '_blank', 'noopener,noreferrer')
}
