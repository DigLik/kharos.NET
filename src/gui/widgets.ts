// Общие кусочки панелей на холсте: цвета, значок груза с числом, полоса готовности, монета, подпись клавиши. Вид — тот
// же, что был у страницы (src/style.css): рамки скина, Ark Pixel, тени текста.

import type { GridState } from '../game/hud'
import { goodCanvas } from '../game/goodIcons'
import { t } from '@kharos/i18n'
import type { Good } from '../sim'
import { INK, skins } from '../ui/skin'
import type { Run, TextStyle, Ui } from './ui'

/** Цвета текста — те же переменные, что у страницы (src/style.css). */
export const COLORS = {
  ink: INK,
  bright: '#eef6ff',
  text: '#d6e6f5',
  soft: '#b4c9dd',
  dim: '#8fa9c2',
  muted: '#6184a3',
  team: '#5aa9ff',
  teamMid: '#1f63d8',
  teamDark: '#10358f',
  gold: '#f0c95a',
  good: '#8fe0a8',
  warn: '#ffb070',
  bad: '#ff7a66',
  power: '#8fd0ff',
  ally: '#4ec973',
  well: '#070d15',
  steel: '#41617f',
}

/** Тень подписей кнопок и ячеек: пиксель чернил правее и ниже. */
export const SHADOW: TextStyle = { shadow: INK, lift: 1 }

/** Значок груза в строке: картинка 15×15 (пиксель маски — полтора пикселя экрана, как у страницы). */
export const GOOD = 15

/** Монета кредитов. */
export const coin = (ui: Ui, size = 16) => ui.image(skins().coin, size, size)

/**
 * Значок груза и количество (или «есть/из»); название — в подсказке, если дан id. Так груз пишется везде: в строках
 * сведений, на верхней полосе, в окнах.
 */
export function res(ui: Ui, resource: Good, options: { amount?: number; of?: number; id?: string; size?: number; color?: string } = {}) {
  const { amount, of, id, size = GOOD, color } = options
  ui.box({ id, direction: 'row', align: 'center', gap: 3 }, () => {
    ui.image(goodCanvas(resource), size, size)
    if (amount !== undefined) ui.text(of !== undefined ? `${amount}/${of}` : String(amount), { color })
  })
  if (id) ui.tip(id, t(`goods.${resource}`))
}

/** Груз в строке с переносом: значок с подсказкой и число. */
export const resRun = (resource: Good, id: string, amount?: number, of?: number): Run[] => [
  { image: goodCanvas(resource), width: GOOD, height: GOOD, id, tip: t(`goods.${resource}`) },
  ...(amount !== undefined ? [of !== undefined ? `${amount}/${of} ` : `${amount} `] : [' ']),
]

/**
 * Полоса готовности с подписью: утопленное окно, внутри — полоса огней команды на долю value, поверх — подпись.
 * width — ширина окна; без неё полоса тянется на ширину родителя (grow в столбце со stretch).
 */
export function bar(ui: Ui, value: number, label: string, width?: number) {
  const fill = Math.max(0, Math.min(1, value))
  ui.box({ skin: 'well', width, height: 24, padding: 4, direction: 'stack', clip: true }, () => {
    ui.box({ grow: 1, direction: 'row' }, () => {
      ui.box({ grow: fill, background: COLORS.teamMid })
      ui.box({ grow: 1 - fill })
    })
    ui.box({ self: ['start', 'center'], padding: [0, 8] }, () => ui.text(label, { ...SHADOW, color: COLORS.text, ellipsis: true }))
  })
}

/** Подпись клавиши — золотая, как <kbd> у страницы. */
export const key = (ui: Ui, label: string, color = COLORS.gold) => ui.text(label, { ...SHADOW, color })

/** Энергия сети словами: сколько просят сейчас и при полной нагрузке, сколько из накопителей, перегружены ли станции. */
export const gridText = (grid: GridState) =>
  [
    t('hud.zonePower', { demand: grid.demand, produced: grid.produced }),
    grid.stored > 0 ? t('hud.zoneStored', { stored: grid.stored }) : '',
    grid.peak > grid.demand ? t('hud.zonePeak', { peak: grid.peak }) : '',
    grid.overload ? t('hud.overloaded') : '',
  ].join('')
