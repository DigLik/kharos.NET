// Панели игрока на холсте (пункт 10.4 плана). Переводятся с Preact (src/ui/Hud.tsx) по одной; первая — верхняя
// полоса: кредиты, энергия, защита, запас, часы, исследования и меню. Вид тот же, что у страницы: рамки скина, шрифт
// Ark Pixel, цвета из src/style.css.

import { STEPS } from '../game/firstSteps'
import type { GridState, HudState, Stack } from '../game/hud'
import { goodCanvas } from '../game/goodIcons'
import { t } from '../i18n'
import { skins } from '../ui/skin'
import { links } from './links'
import type { Ui } from './ui'

/** Цвета текста — те же переменные, что у страницы (src/style.css). */
export const COLORS = {
  text: '#d6e6f5',
  bright: '#eef6ff',
  dim: '#8fa9c2',
  team: '#5aa9ff',
  gold: '#f0c95a',
  good: '#8fe0a8',
  warn: '#ffb070',
  bad: '#ff7a66',
  power: '#8fd0ff',
}

/** Высота верхней полосы: от неё отступают окна под ней (--top-height у страницы). На узком — два ряда. */
export const TOP_HEIGHT = 38
const TOP_HEIGHT_NARROW = 64
/** Уже скольких пикселей экран узкий: полоса в два ряда, как у страницы (@media max-width: 999px). */
const NARROW = 1000

export const topHeight = (width: number) => (width < NARROW ? TOP_HEIGHT_NARROW : TOP_HEIGHT)

/** Пульс подсказки «Первых шагов»: обводка огней команды разгорается и гаснет раз в секунду (cell-hint у страницы). */
function pulse() {
  const phase = (performance.now() % 1000) / 1000
  // Восемь ступеней: плавнее не разглядеть, а каждая новая — перерисовка холста.
  const level = Math.round((0.65 - 0.35 * Math.cos(phase * Math.PI * 2)) * 8) / 8
  return `rgb(90 169 255 / ${level})`
}

/** С какой доли выработки энергия на полосе желтеет — по полной нагрузке, когда все потребители в работе. */
const GRID_TIGHT = 0.8

/** Энергия сети словами: сколько просят сейчас и при полной нагрузке, сколько из накопителей, перегружены ли станции. */
export const gridText = (grid: GridState) =>
  [
    t('hud.zonePower', { demand: grid.demand, produced: grid.produced }),
    grid.stored > 0 ? t('hud.zoneStored', { stored: grid.stored }) : '',
    grid.peak > grid.demand ? t('hud.zonePeak', { peak: grid.peak }) : '',
    grid.overload ? t('hud.overloaded') : '',
  ].join('')

/** Значок груза и количество; название — в подсказке. size — сторона значка (его пиксель — size / 10 экрана). */
function stack(ui: Ui, { resource, amount }: Stack, size: number, id: string) {
  ui.box({ id, direction: 'row', align: 'center', gap: 3 }, () => {
    ui.image(goodCanvas(resource), size, size)
    ui.text(String(amount))
  })
  ui.tip(id, t(`goods.${resource}`))
}

/** Верхняя полоса: кредиты, энергия, защита, запас, часы, кнопки исследований и меню. На узком экране — в два ряда:
 * сверху кредиты и кнопки, ниже энергия, запас и часы. */
export function topBar(ui: Ui, state: HudState) {
  const narrow = ui.width < NARROW
  const credits = () => {
    ui.box({ id: 'credits', direction: 'row', align: 'center', gap: 6 }, () => {
      ui.image(skins().coin, 16, 16)
      ui.text(String(state.credits), { font: 'title', color: COLORS.gold })
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
      for (const item of items) stack(ui, item, 20, `stock:${item.resource}`)
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
      if (ui.button('tech', { active: links.techOpen, padding: [2, 10], height: 30, outline: hint ? pulse() : undefined }, () => {
        ui.text(t('tech.button'))
        if (current) ui.text(`${Math.round(current.progress * 100)}%`, { color: COLORS.team })
      })) links.toggleTech()
      ui.tip('tech', t('tech.openTip'))
    }
    if (ui.button('menu', { padding: [2, 10], height: 30 }, () => ui.text(t('game.menu')))) links.openMenu()
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

/** Все панели на холсте — по порядку снизу вверх. */
export function drawHud(ui: Ui, state: HudState) {
  topBar(ui, state)
}
