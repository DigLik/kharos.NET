// Интерфейс игрока на холсте (пункт 10.4 плана) — перенесён со страницы (src/ui/Hud.tsx) целиком: верхняя полоса,
// нижняя панель (bar.ts), группы, союзники, список игроков, уведомления, всплывающие сообщения, полоса режима, «Первые
// шаги», подсказки по месту на карте, окно приказа грузовикам, окно исследований (tech.ts) и чат (chat.ts). Вид тот
// же, что был у страницы: рамки скина, шрифт Ark Pixel, цвета из src/style.css.
//
// Своё состояние у панелей — здесь, в замыкании createHud: страница сетки, набираемая сделка и подарок, открытые окна,
// всплывающие сообщения. Клавиши — тоже здесь: сетка команд, R — исследования, Tab — список игроков, Esc — закрыть
// окна. Поле ввода в фокусе (чат, количество) забирает клавиши раньше (см. ui.ts), и игра их не видит.

import { STEPS } from '../game/firstSteps'
import { GROUP_DIGITS, type HudState, type ModeState, type TopicView } from '../game/hud'
import { GRID_PRESS, type GridPress } from '../game/interfaceSounds'
import { buildingCanvas, unitCanvas } from '../game/portraits'
import { prefs, setPrefs } from '../game/prefs'
import type { CarryPick, PaveTool } from '../game/scene'
import { t, type Key } from '@kharos/i18n'
import { quoteName } from '@kharos/protocol/chatCommands'
import { REWARDS, type BuildingType, type Command, type Good, type Reward } from '@kharos/sim-ts'
import { GRID_KEYS, commandsOf, dealOf, type Deal, type FerryDraft, type Page, type Slot } from '../ui/commands'
import { chainOf } from '../ui/sources'
import { barHeight, minimapSide, bottomBar, type BarActions } from './bar'
import { createChat } from './chat'
import { links } from './links'
import { techWindow } from './tech'
import type { Gui, Ui } from './ui'
import { COLORS, SHADOW, coin, gridText, res } from './widgets'

export { gridText } from './widgets'

/** Высота верхней полосы: от неё отступают окна под ней (--top-height у страницы). На узком — два ряда. */
export const TOP_HEIGHT = 38
const TOP_HEIGHT_NARROW = 64
/** Уже скольких пикселей экран узкий: полоса в два ряда, как у страницы (@media max-width: 999px). */
const NARROW = 1000

export const topHeight = (width: number) => (width < NARROW ? TOP_HEIGHT_NARROW : TOP_HEIGHT)

/** С какой доли выработки энергия на полосе желтеет — по полной нагрузке, когда все потребители в работе. */
const GRID_TIGHT = 0.8
/** Сколько секунд сообщение о награде или новой теме висит на экране. */
const TOAST_SECONDS = 5
/** Сколько секунд живёт уведомление: три четверти — ярко, потом гаснет до четверти яркости. */
const ALERT_SECONDS = 8
/** Сторона ячейки группы. */
const GROUP_SLOT = 44
/** Портрет в ячейке группы. */
const TILE = 32
/** Шаг, на который меняется количество в окне приказа грузовикам. */
const CARRY_STEP = 10

/** Что HUD делает с игрой: всё это — методы Game (см. game.ts). */
export interface HudActions extends BarActions {
  send(command: Command): void
  place(building: BuildingType | null): void
  pave(tool: PaveTool | null): void
  route(start: boolean): void
  serve(start: boolean): void
  carry(pick: CarryPick): void
  truckMenu(building: number | null): void
  patrol(start: boolean): void
  aim(silo: number | null): void
  deploy(mcv: number | null): void
  flyTo(x: number, y: number): void
  selectGroup(digit: string, add: boolean): void
  assignGroup(digit: string): void
  clearGroup(digit: string): void
  cancelMode(): void
  finishMode(): void
  say(text: string): void
}

/** Пульс подсказки «Первых шагов»: обводка огней команды разгорается и гаснет раз в секунду (cell-hint у страницы). */
function pulse(ui: Ui) {
  const phase = (ui.now % 1000) / 1000
  // Восемь ступеней: плавнее не разглядеть, а каждая новая — перерисовка холста.
  const level = Math.round((0.65 - 0.35 * Math.cos(phase * Math.PI * 2)) * 8) / 8
  return `rgb(90 169 255 / ${level})`
}

/** Строка списка игроков, см. HudState. */
type PlayerRow = HudState['players'][number]

/**
 * Кнопки союза в списке игроков: что можно сделать с этим игроком прямо сейчас, см. allies.ts. Союзом распоряжается
 * глава: он исключает рядовых, а рядовой может только выйти сам. Команды те же, что набираются в чате.
 */
function allyActions(row: PlayerRow, self: PlayerRow | undefined): { label: Key; command: string }[] {
  const quoted = quoteName(row.name)
  const leads = !!self && (self.head < 0 || self.head === self.player)
  if (row.own) {
    if (row.head < 0) return []
    return [{ label: row.head === row.player ? 'hud.allyDissolve' : 'hud.allyExit', command: '/unally' }]
  }
  if (row.relation === 'ally') return leads && row.head === self!.player ? [{ label: 'hud.allyKick', command: `/unally ${quoted}` }] : []
  if (row.relation === 'allyAsked') {
    const decline: { label: Key; command: string } = { label: 'hud.allyDecline', command: `/unally ${quoted}` }
    return leads ? [{ label: 'hud.allyAccept', command: `/ally ${quoted}` }, decline] : [decline]
  }
  if (row.relation === 'allyOffered') return [{ label: 'hud.allyWithdraw', command: `/unally ${quoted}` }]
  return leads ? [{ label: 'hud.allyOffer', command: `/ally ${quoted}` }] : []
}

/** Что открыла тема: здания, юниты и инструменты покрытия, через запятую. */
const opensOf = (view: TopicView) =>
  [...view.buildings.map((type) => t(`buildings.${type}`)), ...view.units.map((type) => t(`units.${type}`)), ...view.paves.map((kind) => t(`grid.tools.${kind}`))].join(', ')

/** Как называется инструмент протяжки: у стены и ворот — как здание, у разбора — как его кнопка. */
const toolName = (tool: PaveTool) => (tool === 'remove' ? t('grid.remove') : tool === 'wall' || tool === 'gate' ? t(`buildings.${tool}`) : t(`grid.tools.${tool}`))

/** Что делать в режиме дальше. */
function modeTip(mode: NonNullable<ModeState>) {
  switch (mode.kind) {
    case 'carryTarget': return t('mode.tips.carryTarget', { amount: mode.amount, name: t(`goods.${mode.resource}`) })
    case 'placing': return t('mode.tips.placing')
    case 'paving':
      if (mode.tool === 'remove') return t('mode.tips.remove')
      if (!mode.tiles) return t('mode.tips.pavingStart')
      return t('mode.tips.paving', { tiles: mode.tiles, cost: mode.cost }) + (mode.short ? t('mode.tips.pavingShort', { short: mode.short }) : '')
    case 'routing': return t('mode.tips.routing', { stops: mode.stops })
    case 'serving': return t('mode.tips.serving', { count: mode.count })
    default: return t(`mode.tips.${mode.kind}`)
  }
}

/** Цвет уведомления по его виду. */
const ALERT_COLORS: Record<string, string> = { attacked: COLORS.bad, spotted: COLORS.warn, found: COLORS.good, ping: COLORS.ally }

/** Верхняя полоса: кредиты, энергия, защита, запас, часы, кнопки исследований и меню. На узком экране — в два ряда. */
function topBar(ui: Ui, state: HudState, techOpen: boolean, toggleTech: () => void) {
  const narrow = ui.width < NARROW
  const credits = () => {
    ui.box({ id: 'credits', direction: 'row', align: 'center', gap: 6 }, () => {
      coin(ui)
      ui.text(String(state.credits), { font: 'title', color: COLORS.gold, shadow: COLORS.ink, lift: 2 })
      if (state.income > 0) ui.text(t('hud.perSecond', { amount: state.income }), { color: COLORS.good })
    })
    ui.tip('credits', t('common.credits'))
    if (state.protection) {
      ui.box({ id: 'protection' }, () => ui.text(t('hud.protection', { left: state.protection }), { color: COLORS.good }))
      ui.tip('protection', t('hud.protectionTip'))
    }
  }
  const grid = () => {
    if (!state.grid) return
    const { grid } = state
    const color = grid.demand > grid.produced || grid.overload ? COLORS.bad : grid.peak > grid.produced * GRID_TIGHT ? COLORS.warn : COLORS.power
    ui.box({ id: 'grid' }, () => ui.text(`⚡ ${grid.demand} / ${grid.produced}`, { color }))
    ui.tip('grid', `${gridText(grid)}\n${t('hud.gridTip')}`)
  }
  const stock = () => {
    if (!state.stock) return
    const { items, capacity } = state.stock
    // На узком экране запас — одной строкой: что не влезло, обрезается.
    ui.box({ id: 'stock', direction: 'row', align: 'center', gap: 8, ...(narrow && { grow: 1, clip: true, width: 0 }) }, () => {
      for (const { resource, amount } of items) res(ui, resource, { amount, size: 20, id: `stock:${resource}` })
      if (!items.length) ui.text(t('hud.storesEmpty'))
    })
    ui.tip('stock', t('hud.stockTip', { capacity }))
  }
  const clock = () => {
    ui.box({ id: 'clock', direction: 'row', gap: 4 }, () => {
      ui.text(state.clock, { color: COLORS.dim })
      if (state.storm) ui.text(t('hud.storm'), { color: COLORS.dim })
    })
    ui.tip('clock', state.storm ? t('hud.clockStorm') : t('hud.clock'))
  }
  const buttons = () => {
    if (!state.editor) {
      const current = state.tech.current && state.tech.topics.find(({ topic }) => topic === state.tech.current)
      // Подсказка «Первых шагов»: следующий шаг — исследовать тему.
      const hint = state.steps && STEPS[state.steps.indexOf(false)]?.research
      if (ui.button('tech', { active: techOpen, padding: [2, 10], height: 30, outline: hint ? pulse(ui) : undefined }, () => {
        ui.text(t('tech.button'), SHADOW)
        if (current) ui.text(`${Math.round(current.progress * 100)}%`, { ...SHADOW, color: COLORS.team })
      })) toggleTech()
      ui.tip('tech', t('tech.openTip'))
    }
    if (ui.button('menu', { padding: [2, 10], height: 30 }, () => ui.text(t('game.menu'), SHADOW))) links.openMenu()
    ui.tip('menu', t('game.menuTip'))
  }

  const bar = { id: 'top', anchor: { left: 0, right: 0, top: 0 }, height: topHeight(ui.width), skin: 'panel' as const, padding: [4, 12] as [number, number] }
  if (narrow) {
    ui.box({ ...bar, direction: 'column', justify: 'center', align: 'stretch', gap: 0 }, () => {
      ui.box({ direction: 'row', align: 'center', gap: 10, height: 28 }, () => {
        credits()
        ui.space(0, 0, 1)
        buttons()
      })
      ui.box({ direction: 'row', align: 'center', gap: 10, height: 24 }, () => {
        grid()
        stock()
        clock()
      })
    })
    return
  }
  ui.box({ ...bar, direction: 'row', align: 'center', gap: 12 }, () => {
    credits()
    grid()
    stock()
    // Часы прижаты вправо, к кнопкам.
    ui.space(0, 0, 1)
    clock()
    buttons()
  })
}

export interface Hud {
  draw(ui: Ui, state: HudState): void
  destroy(): void
}

/**
 * Интерфейс игрока: один на игру. actions — игра (зовётся лениво: HUD заводится раньше, чем игра готова), gui —
 * интерфейс на холсте: по нему видно, что в поле ввода набирают текст.
 */
export function createHud(actions: () => HudActions, gui: Gui): Hud {
  // Страница сетки строителя, сделка космопорта и подарок союзнику — сбрасываются, когда выбрано другое.
  let page: Page = 'root'
  let deal: Deal | null = null
  let ferry: FerryDraft | null = null
  let selectionKey = ''
  let slots: (Slot | null)[] = []
  let techOpen = false
  /** Список игроков: показан, пока зажат Tab, и пока под ним мышь, когда Tab уже отпущен. */
  let roster = false
  let tabHeld = false
  let overRoster = false
  /** Всплывающие сообщения: награды и новые темы — с временем, до которого видны. */
  let rewardsSeen = 0
  let rewardsReady = false
  const rewards: { key: string; until: number }[] = []
  let topicsSeen: Set<string> | null = null
  let topicsReady = false
  const learned: { view: TopicView; until: number }[] = []
  /** Когда уведомление показано впервые: оно гаснет от этого времени. */
  const alertSince = new Map<number, number>()
  /** Пройдены ли «Первые шаги» уже при входе: тогда список не нужен. */
  let stepsKnown: boolean | null = null
  /** Выбранный груз в окне грузовика и сколько его. */
  let picked: { building: number; resource: Good; amount: number } | null = null
  const toggleTech = () => (techOpen = !techOpen)

  const typing = () => gui.typing
  const chat = createChat((text) => actions().say(text), typing)

  const onKey = (event: KeyboardEvent) => {
    if (typing() || event.altKey || event.ctrlKey || event.metaKey) return
    if (event.code === 'Tab') {
      event.preventDefault()
      tabHeld = roster = true
      return
    }
    if (event.repeat) return
    // Esc закрывает окна интерфейса, которые он же и открывал; дальше нажатие не идёт — иначе игра открыла бы меню.
    if (event.code === 'Escape' && (roster || techOpen)) {
      event.preventDefault()
      event.stopImmediatePropagation()
      roster = tabHeld = techOpen = false
      return
    }
    if (event.code === 'KeyR') {
      toggleTech()
      return
    }
    const i = (GRID_KEYS as readonly string[]).indexOf(event.code)
    const slot = i >= 0 ? slots[i] : null
    if (!slot) return
    // Клавиша звучит, как нажатая кнопка: звук интерфейса слушает это событие, см. interfaceSounds.ts.
    window.dispatchEvent(new CustomEvent<GridPress>(GRID_PRESS, { detail: { disabled: !!slot.disabled, group: slot.group !== undefined } }))
    if (slot.disabled) return
    event.preventDefault()
    slot.run()
  }
  const onKeyUp = (event: KeyboardEvent) => {
    if (event.code !== 'Tab') return
    tabHeld = false
    roster = overRoster
  }
  const clear = () => (roster = tabHeld = false)
  const onHidden = () => document.hidden && clear()
  // Перехватом: Esc должен закрыть окно раньше, чем игра откроет меню (у оболочки — порядком: HUD заводится первым).
  window.addEventListener('keydown', onKey, true)
  window.addEventListener('keyup', onKeyUp)
  window.addEventListener('blur', clear)
  document.addEventListener('visibilitychange', onHidden)

  /** Всплывающие награды и темы: новые с прошлого опроса — на TOAST_SECONDS. Что было до входа в мир — не новое. */
  const toasts = (state: HudState, now: number) => {
    if (rewardsReady) for (const key of state.rewards.slice(rewardsSeen)) rewards.push({ key, until: now + TOAST_SECONDS * 1000 })
    rewardsReady = state.loaded
    // Список стал короче — мир начался заново: считаем с нуля.
    rewardsSeen = state.rewards.length
    const done = state.tech.topics.filter(({ status }) => status === 'done')
    if (topicsReady && topicsSeen && done.length >= topicsSeen.size) {
      for (const view of done) if (!topicsSeen.has(view.topic)) learned.push({ view, until: now + TOAST_SECONDS * 1000 })
    }
    topicsReady = state.loaded
    topicsSeen = new Set(done.map(({ topic }) => topic))
    for (const list of [rewards, learned]) {
      for (let i = list.length - 1; i >= 0; i--) if (list[i].until <= now) list.splice(i, 1)
    }
  }

  /**
   * Панель групп над нижней панелью: десять ячеек по цифрам 1–9 и 0. В занятой — портрет того, кого в группе больше
   * всех, и сколько в ней. Нажатие на занятую выбирает группу (Shift — добавить), на пустую — кладёт туда выделенных.
   * Правая кнопка или долгое нажатие кладёт выделенных и в занятую, а по выбранной сейчас группе — очищает ячейку.
   */
  const groups = (ui: Ui, state: HudState, bottom: number, game: HudActions) => {
    const side = ui.width <= 520 ? Math.floor((ui.width - 24 - 18) / 10) : GROUP_SLOT
    // Ячейки — на панели, как у страницы: у неё любая панель HUD была в рамке скина с границей в 4.
    ui.box({ anchor: { centerX: 0, bottom: bottom + 8 }, skin: 'panel', padding: 4, direction: 'row', gap: 2 }, () => {
      for (const digit of GROUP_DIGITS) {
        const group = state.groups.find((one) => one.digit === digit)
        const id = `group:${digit}`
        const open = !group && state.groupable
        const portrait = group?.unit ? unitCanvas(group.unit, TILE) : group?.building ? buildingCanvas(group.building, TILE) : null
        const picture = Math.min(TILE, side - 8)
        const click = ui.button(id, { width: side, height: side, padding: 0, direction: 'stack', active: group?.selected, skin: open ? 'button-hover' : 'button', alpha: group ? undefined : open ? 0.85 : 0.5 }, () => {
          if (portrait) ui.box({ self: ['center', 'center'] }, () => ui.image(portrait, picture, picture))
          // Цифра и счётчик — внутри рамки в 4 пикселя, как у кнопки страницы (top: 1px; left: 2px от её края).
          ui.box({ self: ['start', 'start'], padding: [5, 0, 0, 6] }, () => ui.text(digit, { color: COLORS.gold, shadow: COLORS.ink }))
          if (group) ui.box({ self: ['end', 'end'], padding: [0, 6, 5, 0] }, () => ui.text(String(group.count), { color: COLORS.bright, shadow: COLORS.ink }))
        })
        ui.tip(id, group ? t('hud.groupTip', { digit }) : state.groupable ? t('hud.groupPutTip', { digit }) : t('hud.groupEmptyTip', { digit }))
        if (!click) continue
        if (click.button === 2) {
          if (group?.selected) game.clearGroup(digit)
          else game.assignGroup(digit)
        } else if (group) game.selectGroup(digit, click.shift)
        else if (state.groupable) game.assignGroup(digit)
      }
    })
  }

  /** Союзники — справа над панелью, всегда на виду: имя, глава ли он, кредиты; щелчок — камера к его базе. */
  const allies = (ui: Ui, state: HudState, bottom: number, game: HudActions) => {
    const list = state.players.filter(({ own, relation }) => !own && relation === 'ally')
    if (!list.length) return
    ui.box({ anchor: { right: 12, bottom: bottom + 8 }, skin: 'panel', padding: 10, direction: 'column', gap: 4, align: 'stretch' }, () => {
      for (const { name, online, credits, base, player, head } of list) {
        const id = `ally:${player}`
        const click = ui.button(id, { padding: [3, 12], gap: 6, justify: 'start', alpha: online ? undefined : 0.45 }, () => {
          ui.box({ width: 8, height: 8, background: COLORS.ally })
          ui.text(name, { ...SHADOW, color: COLORS.ally })
          if (head === player) ui.text(t('hud.allyHead'), { color: COLORS.gold })
          ui.text(String(credits), { color: COLORS.gold })
        })
        ui.tip(id, t('hud.friendTip', { name, credits }))
        if (click && base) game.flyTo(base.x, base.y)
      }
    })
  }

  /** Уведомления слева под полосой: строка цвета вида с маркером; щелчок ведёт камеру к месту. Со временем гаснут. */
  const alerts = (ui: Ui, state: HudState, top: number, game: HudActions) => {
    for (const at of alertSince.keys()) if (!state.alerts.some((alert) => alert.at === at)) alertSince.delete(at)
    ui.box({ anchor: { left: 12, top: top + 6 }, skin: 'panel', padding: [10, 12], direction: 'column', gap: 4 }, () => {
      for (const { kind, text, x, y, at } of state.alerts) {
        if (!alertSince.has(at)) alertSince.set(at, ui.now)
        const age = (ui.now - alertSince.get(at)!) / 1000
        const bright = ALERT_SECONDS * 0.75
        const fade = age < bright ? 1 : Math.max(0.25, 1 - (0.75 * (age - bright)) / (ALERT_SECONDS - bright))
        const id = `alert:${at}`
        const color = ALERT_COLORS[kind] ?? COLORS.text
        ui.box({ id, direction: 'row', align: 'center', gap: 8, padding: [2, 4], alpha: Math.round(fade * 8) / 8 }, () => {
          ui.box({ width: 6, height: 6, background: color })
          ui.text(t(`hud.alerts.${text}`), { color, shadow: COLORS.ink })
        })
        ui.tip(id, t('hud.showPlace'))
        if (ui.clicked(id)) game.flyTo(x, y)
      }
    })
  }

  /**
   * Первые шаги новичка в углу: список с галочками, у текущего — что сделать и зачем. Заголовок сворачивает список,
   * крестик прячет совсем (вернуть — в меню). Мир, где всё уже пройдено при входе, списка не показывает.
   */
  const steps = (ui: Ui, state: HudState, top: number, game: HudActions) => {
    const done = state.steps!
    const finished = done.every(Boolean)
    stepsKnown ??= finished
    const shown = prefs().steps
    if (shown === 'off' || (finished && stepsKnown)) return
    const set = (next: 'open' | 'folded' | 'off') => setPrefs({ ...prefs(), steps: next })
    const current = done.indexOf(false)
    const count = done.filter(Boolean).length
    const width = Math.min(300, ui.width - 24)
    const target = state.stepTarget
    ui.box({ anchor: { right: 12, top: top + 6 }, width, skin: 'panel', padding: [8, 10, 10, 10], direction: 'column', align: 'stretch', gap: 6 }, () => {
      ui.box({ direction: 'row', gap: 4 }, () => {
        if (ui.button('steps:toggle', { height: 28, padding: [0, 8], justify: 'start' }, () => {
          ui.text(t('steps.title'), SHADOW)
          ui.text(`${count}/${STEPS.length}`, { ...SHADOW, font: 'small', color: COLORS.gold })
          ui.text(shown === 'open' ? '▾' : '▸', { ...SHADOW, font: 'small' })
        })) set(shown === 'open' ? 'folded' : 'open')
        ui.tip('steps:toggle', t(shown === 'open' ? 'steps.foldTip' : 'steps.unfoldTip'))
        if (ui.button('steps:close', { height: 28, padding: [0, 8] }, () => ui.text('×', SHADOW))) set('off')
        ui.tip('steps:close', t('steps.closeTip'))
      })
      if (shown !== 'open') return
      if (finished) {
        ui.box({ padding: [0, 2] }, () => ui.text(t('steps.finished'), { color: COLORS.good, wrap: true }))
        return
      }
      const narrow = ui.width < NARROW
      // Список — плотнее панели: строки через 4, описание текущего шага — мелким шрифтом.
      ui.box({ direction: 'column', align: 'stretch', gap: 4, padding: [0, 2] }, () => STEPS.forEach(({ key }, i) => {
        // На узком экране — только текущий шаг: список закрыл бы поле.
        if (narrow && i !== current) return
        const color = done[i] ? COLORS.dim : i === current ? COLORS.bright : COLORS.muted
        ui.box({ direction: 'row', gap: 8 }, () => {
          ui.box({ width: 14, direction: 'row', justify: 'end' }, () => ui.text(done[i] ? '✓' : String(i + 1), { color: done[i] ? COLORS.good : i === current ? COLORS.team : color }))
          ui.box({ direction: 'column', gap: 2, grow: 1 }, () => {
            ui.text(t(`steps.items.${key}.title`), { color, wrap: true })
            if (i !== current) return
            ui.text(t(`steps.items.${key}.tip`), { font: 'small', color: COLORS.soft, wrap: true })
            if (target) {
              ui.space(0, 2)
              if (ui.button('steps:show', { height: 28, padding: [0, 10] }, () => ui.text(t('steps.show'), SHADOW))) game.flyTo(target.x + target.width / 2, target.y + target.height / 2)
              ui.tip('steps:show', t('steps.showTip'))
            }
          })
        })
      }))
    })
  }

  /**
   * Полоса режима над полем: какой режим, что делать дальше и как из него выйти. Esc и правая кнопка работают
   * по-прежнему, но на сенсорном экране их нет вовсе. Стройка и укладка кончаются «Готово»; маршрут и обслуживание —
   * «Готово» отдаёт набранное, «Отмена» бросает; остальное — «Отмена».
   */
  const modeBar = (ui: Ui, mode: NonNullable<ModeState>, room: number, game: HudActions) => {
    const picking = mode.kind === 'routing' || mode.kind === 'serving'
    const ends = mode.kind === 'placing' || mode.kind === 'paving'
    const name = mode.kind === 'placing' ? t('mode.placing', { name: t(`buildings.${mode.building}`) }) : mode.kind === 'paving' ? toolName(mode.tool) : t(`mode.${mode.kind}`)
    ui.box({ id: 'mode', skin: 'panel', padding: [10, 12, 10, 16], direction: 'row', align: 'center', gap: 12, maxWidth: room }, () => {
      ui.text(name, { color: COLORS.team })
      ui.text(modeTip(mode), { color: COLORS.soft, wrap: Math.max(160, room - 320) })
      if (picking || ends) {
        if (ui.button('mode:done', { skin: 'button-primary', height: 32, padding: [2, 14] }, () => ui.text(t('mode.done'), SHADOW))) game.finishMode()
        ui.tip('mode:done', t(picking ? 'mode.doneTip' : 'mode.endTip'))
      }
      if (!ends) {
        if (ui.button('mode:cancel', { height: 32, padding: [2, 14] }, () => ui.text(t('mode.cancel'), SHADOW))) game.cancelMode()
        ui.tip('mode:cancel', t(picking ? 'mode.cancelTip' : 'mode.endTip'))
      }
    })
  }

  /** Сверху по центру, друг под другом: полоса режима, награды, новые исследования. */
  const toastColumn = (ui: Ui, state: HudState, top: number, game: HudActions) => {
    const mode = state.editor ? null : state.mode
    if (!mode && !rewards.length && !learned.length) return
    // Сбоку — уведомления и «Первые шаги»: середина не наезжает на них.
    const room = Math.min(ui.width - 24, state.steps && ui.width >= NARROW ? Math.max(280, ui.width - 2 * 324) : ui.width - 24)
    if (mode) ui.box({ anchor: { centerX: 0, top: top + 6 } }, () => modeBar(ui, mode, room, game))
    const below = mode ? top + 6 + (ui.rect('mode')?.height ?? 52) + 6 : top + 6
    if (!rewards.length && !learned.length) return
    ui.box({ anchor: { centerX: 0, top: below }, direction: 'column', align: 'center', gap: 6 }, () => {
      if (rewards.length) {
        ui.box({ skin: 'gold', padding: [10, 14], direction: 'column', gap: 4 }, () => {
          for (const { key } of rewards) {
            ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
              coin(ui)
              ui.text(`+${REWARDS[key as Reward]}`, { color: COLORS.gold })
              ui.text(t(`rewards.${key as Reward}`))
            })
          }
        })
      }
      for (const { view, until } of learned) {
        const id = `learned:${view.topic}`
        const left = (until - ui.now) / (TOAST_SECONDS * 1000)
        const opens = opensOf(view)
        const click = ui.button(id, { skin: 'panel', direction: 'column', align: 'center', gap: 2, padding: [10, 16], alpha: left > 0.2 ? 1 : Math.round((0.3 + (left / 0.2) * 0.7) * 8) / 8 }, () => {
          ui.flow([t(view.trigger ? 'hud.discovered' : 'hud.researched'), ' ', { text: t(`research.${view.topic}`), color: COLORS.team }], { width: room - 32 })
          if (opens) ui.text(t('hud.opens', { list: opens }), { color: COLORS.soft, wrap: room - 32 })
        })
        ui.tip(id, t('tech.openTip'))
        if (click) techOpen = true
      }
    })
  }

  /** Подсказки по месту на карте — правее и ниже указателя, а у правого края — левее него. */
  const hoverTips = (ui: Ui, state: HudState) => {
    const beside = (x: number, y: number) => (x > ui.width - 260 ? { right: ui.width - x + 16, top: y + 16 } : { left: x + 16, top: y + 16 })
    if (state.hover) {
      const { hover } = state
      ui.box({ anchor: beside(hover.x, hover.y), skin: 'panel', padding: [8, 12], direction: 'column', gap: 2, passive: true }, () => {
        ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
          res(ui, hover.kind)
          ui.text(t('hud.depositTip', { name: t(`goods.${hover.kind}`) }))
          ui.text(hover.left === null ? t('hud.leftUnknown') : hover.left > 0 ? t('hud.left', { amount: hover.left }) : t('hud.depleted'), { color: COLORS.dim })
        })
        // Что из него выйдет и через что: руду ещё надо переработать.
        ui.text(chainOf(hover.kind), { color: COLORS.dim, wrap: 300 })
      })
    }
    if (state.pipeHover) {
      const { pipeHover } = state
      ui.box({ anchor: beside(pipeHover.x, pipeHover.y), skin: 'panel', padding: [8, 12], direction: 'row', align: 'center', gap: 6, passive: true }, () => {
        ui.text(t('hud.network', { buildings: pipeHover.buildings }))
        ui.box({ direction: 'row', align: 'center', gap: 4 }, () => {
          if (pipeHover.stock.length) for (const { resource, amount } of pipeHover.stock) res(ui, resource, { amount, color: COLORS.dim })
          else ui.text(pipeHover.capacity ? t('hud.storesEmpty') : t('hud.noStores'), { color: COLORS.dim })
        })
      })
    }
  }

  /**
   * Список игроков сервера — пока зажат Tab, и пока под ним мышь, когда Tab уже отпущен: так кнопки союза не исчезают
   * из-под руки. Ушла мышь с отпущенным Tab — уходит и он.
   */
  const rosterWindow = (ui: Ui, state: HudState, top: number, game: HudActions) => {
    overRoster = ui.within('roster')
    if (!tabHeld && !overRoster) {
      roster = false
      return
    }
    const self = state.players.find(({ own }) => own)
    const width = Math.min(420, ui.width - 32)
    ui.box({ id: 'roster', anchor: { centerX: 0, top: top + 26 }, width, skin: 'panel', padding: [16, 20], direction: 'column', align: 'stretch' }, () => {
      ui.box({ padding: [0, 0, 8, 0] }, () => ui.text(t('hud.roster', { online: state.players.filter(({ online }) => online).length }), { color: COLORS.dim }))
      for (const row of state.players) {
        const acts = allyActions(row, self)
        ui.box({ height: 1, background: 'rgb(155 185 209 / 0.15)' })
        ui.box({ direction: 'row', align: 'center', gap: 16, padding: [4, 0], alpha: row.own || row.online ? undefined : 0.45 }, () => {
          ui.box({ direction: 'row', gap: 8, grow: 1 }, () => {
            ui.text(row.name, { color: row.own ? COLORS.bright : COLORS.text })
            if (row.safe) ui.text(t('hud.sheltered'), { color: COLORS.good })
            if (row.head === row.player) ui.text(t('hud.allyHead'), { color: COLORS.ally })
            if (row.relation) ui.text(t(`hud.${row.relation}`), { color: row.relation === 'ally' ? COLORS.ally : COLORS.good })
          })
          if (acts.length && (row.own || row.online)) {
            ui.box({ direction: 'row', gap: 6 }, () => {
              for (const { label, command } of acts) {
                if (ui.button(`roster:${row.player}:${label}`, { padding: [3, 8] }, () => ui.text(t(label), SHADOW))) game.say(command)
              }
            })
          }
          ui.text(row.own ? t('hud.you') : row.online ? t('hud.online') : t('hud.offline'), { color: COLORS.muted })
        })
      }
    })
  }

  /**
   * Окно приказа выбранным грузовикам по зданию — открывается правым щелчком по нему. Взять отсюда: груз и сколько,
   * потом щелчок по получателю на карте. Выгрузить сюда — то, что уже в кузовах. Для шахты — возить её руду, для
   * здания, которому нужен груз, — обеспечивать его. Щелчок мимо окна закрывает его.
   */
  const truckWindow = (ui: Ui, state: HudState, game: HudActions) => {
    const menu = state.truckMenu!
    const units = state.haul!.units
    if (picked && picked.building !== menu.building) picked = null
    const chosen = picked
    const available = chosen ? (menu.take.find(({ resource }) => resource === chosen.resource)?.amount ?? 0) : 0
    const amount = chosen ? Math.max(1, Math.min(available, chosen.amount)) : 0
    const name = menu.type ? t(`buildings.${menu.type}`) : ''
    const close = () => game.truckMenu(null)
    const order = (command: Command) => {
      game.send(command)
      close()
    }
    const width = Math.min(440, ui.width - 24)
    ui.box({ id: 'truck:backdrop', anchor: { left: 0, right: 0, top: 0, bottom: 0 }, background: 'rgb(0 0 0 / 0.35)' })
    if (ui.clicked('truck:backdrop')) return close()
    const goods = (list: { resource: Good; amount: number }[], prefix: string, pick: (resource: Good, have: number) => void, active?: Good) => {
      ui.box({ direction: 'column', gap: 4, align: 'stretch' }, () => {
        for (const { resource, amount: have } of list) {
          if (ui.button(`${prefix}:${resource}`, { height: 36, padding: [0, 10], justify: 'start', active: active === resource }, () => {
            res(ui, resource, { amount: have })
            ui.text(t(`goods.${resource}`), SHADOW)
          })) pick(resource, have)
        }
      })
    }
    ui.box({ id: 'truck', anchor: { centerX: 0, centerY: 0 }, width, maxHeight: ui.height - 24, skin: 'panel', padding: [16, 18], direction: 'column', gap: 10, align: 'stretch' }, () => {
      ui.box({ direction: 'row', align: 'center', gap: 8 }, () => {
        ui.box({ grow: 1 }, () => ui.text(t('truck.title', { name }), { color: COLORS.bright, wrap: width - 90 }))
        if (ui.button('truck:close', { height: 36, padding: [0, 10] }, () => ui.text('✕', SHADOW))) close()
        ui.tip('truck:close', t('truck.closeTip'))
      })
      if (menu.take.length) {
        ui.box({ direction: 'column', gap: 6, align: 'stretch' }, () => {
          ui.text(t('truck.take'), { color: COLORS.soft })
          goods(menu.take, 'truck:take', (resource, have) => (picked = { building: menu.building, resource, amount: have }), chosen?.resource)
          if (chosen && available >= 1) {
            ui.box({ direction: 'row', align: 'center', gap: 4 }, () => {
              if (ui.button('truck:less', { height: 36, padding: [0, 10], disabled: amount <= 1 }, () => ui.text(`−${CARRY_STEP}`, SHADOW))) picked = { ...chosen, amount: Math.max(1, amount - CARRY_STEP) }
              ui.box({ minWidth: 56, direction: 'row', justify: 'center' }, () => res(ui, chosen.resource, { amount }))
              if (ui.button('truck:more', { height: 36, padding: [0, 10], disabled: amount >= available }, () => ui.text(`+${CARRY_STEP}`, SHADOW))) picked = { ...chosen, amount: Math.min(available, amount + CARRY_STEP) }
              if (ui.button('truck:all', { height: 36, padding: [0, 10], active: amount >= available }, () => ui.text(t('grid.all'), SHADOW))) picked = { ...chosen, amount: available }
              if (ui.button('truck:where', { height: 36, padding: [0, 10], skin: 'button-primary' }, () => ui.text(t('truck.where'), SHADOW))) game.carry({ from: menu.building, resource: chosen.resource, amount })
              ui.tip('truck:where', t('truck.whereTip', { capacity: menu.capacity }))
            })
          }
        })
      }
      if (menu.unload.length) {
        ui.box({ direction: 'column', gap: 6, align: 'stretch' }, () => {
          ui.text(t('truck.unload'), { color: COLORS.soft })
          goods(menu.unload, 'truck:unload', (resource, have) => order({ type: 'carry', units, from: -1, to: menu.building, resource, amount: have }))
        })
      }
      if (menu.mine || menu.supply) {
        ui.box({ direction: 'column', gap: 6, align: 'stretch' }, () => {
          if (menu.mine && ui.button('truck:mine', { height: 36, padding: [0, 10], justify: 'start' }, () => ui.text(t('truck.mine'), SHADOW))) order({ type: 'haul', units, mine: menu.building })
          if (menu.supply && ui.button('truck:supply', { height: 36, padding: [0, 10], justify: 'start' }, () => ui.text(t('truck.supply'), SHADOW))) order({ type: 'supply', units, target: menu.building })
        })
      }
      if (!menu.take.length && !menu.unload.length && !menu.mine && !menu.supply) ui.text(t('truck.nothing'), { color: COLORS.soft, wrap: width - 36 })
    })
  }

  return {
    draw(ui, state) {
      const game = actions()
      const top = topHeight(ui.width)
      const bottom = barHeight(ui.width)
      const selected = state.units.length > 0 || state.building !== null

      const key = `${state.building}:${state.units.map(({ type, count }) => `${type}${count}`).join()}`
      if (key !== selectionKey) {
        selectionKey = key
        if (!state.construction?.placing && !state.construction?.paving) page = 'root'
        deal = null
        ferry = null
      }
      const { trade } = state
      const dealing = trade && !trade.order && deal && (page === 'sell' || page === 'buy') ? dealOf(trade, state.credits, page === 'buy', deal) : null
      slots = selected
        ? commandsOf(state, page, {
            send: (command) => game.send(command),
            place: (building) => game.place(building),
            pave: (tool) => game.pave(tool),
            route: (start) => game.route(start),
            serve: (start) => game.serve(start),
            carry: (pick) => game.carry(pick),
            patrol: (start) => game.patrol(start),
            aim: (silo) => game.aim(silo),
            deploy: (mcv) => game.deploy(mcv),
            open: (next) => {
              page = next
              deal = null
            },
            pick: (next) => (deal = next),
            draft: (next) => (ferry = next),
            tech: () => (techOpen = true),
          }, deal, ferry)
        : []
      toasts(state, ui.now)

      // Снизу вверх: нижняя панель и то, что над ней, уведомления, сообщения, полоса, подсказки и окна.
      bottomBar(ui, state, slots, game, {
        deal: dealing && { ...dealing, buying: page === 'buy' },
        ferry,
        setAmount: (amount) => {
          if (deal) deal = { ...deal, amount }
        },
        confirm: () => {
          if (!trade || !dealing || dealing.amount < 1) return
          game.send({ type: page === 'buy' ? 'buy' : 'sell', port: trade.port, resource: dealing.resource, amount: dealing.amount })
          deal = null
        },
      })
      if (!state.editor) groups(ui, state, bottom, game)
      allies(ui, state, bottom, game)
      // Чат — правее мини-карты, когда она выше панели.
      const side = minimapSide(ui.width)
      chat.draw(ui, state, ui.width >= NARROW && side > 140 ? side + 12 : 12, bottom + 8)
      if (state.alerts.length && !state.editor) alerts(ui, state, top, game)
      if (state.steps) steps(ui, state, top, game)
      toastColumn(ui, state, top, game)
      topBar(ui, state, techOpen, toggleTech)
      hoverTips(ui, state)
      if (roster && state.players.length) rosterWindow(ui, state, top, game)
      if (state.truckMenu && state.haul) truckWindow(ui, state, game)
      if (techOpen && !state.editor && techWindow(ui, state.tech, top, (command) => game.send(command))) techOpen = false
    },
    destroy() {
      chat.destroy()
      window.removeEventListener('keydown', onKey, true)
      window.removeEventListener('keyup', onKeyUp)
      window.removeEventListener('blur', clear)
      document.removeEventListener('visibilitychange', onHidden)
    },
  }
}
