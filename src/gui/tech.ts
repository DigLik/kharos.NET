// Окно исследований на холсте, как в Factorio: сверху очередь, ниже все темы плитками по порядку зависимостей —
// сначала то, что нужно раньше. Зелёные можно исследовать сейчас, жёлтые станут можно, когда очередь дойдёт до нужного
// им, красные — пока нельзя. Щелчок ставит тему в конец очереди вместе с недостающими предшественниками, Shift — в
// начало; щелчок по теме в очереди убирает её. Наведение показывает карточку: что тема даёт и что от неё зависит.

import type { TechState, TopicView } from '../game/hud'
import { goodCanvas } from '../game/goodIcons'
import { buildingCanvas, levelCanvas, paveCanvas, unitCanvas } from '../game/portraits'
import { t } from '../i18n'
import type { Command, LabLack, Topic } from '../sim'
import type { Ui } from './ui'
import { COLORS, SHADOW, coin, res } from './widgets'

/** Стороны значков: на плитке и в строке. */
const ICON = 48
const SMALL = 24
/** Плитка не уже этого; колонок — сколько влезет. */
const TILE_MIN = 120
const GAP = 6
const CARD_WIDTH = 330

/** Порядок плиток по доступности: что можно вести сейчас — первым. */
const STATUS_ORDER: Record<TopicView['status'], number> = { available: 0, soon: 1, locked: 2, done: 3 }

/** Цвет доступности: фон значка и полоса под ним, цвет строки темы. */
const STATUS_COLORS: Record<TopicView['status'], { line: string; back: string }> = {
  available: { line: COLORS.good, back: 'rgb(143 224 168 / 0.2)' },
  soon: { line: COLORS.gold, back: 'rgb(240 201 90 / 0.2)' },
  locked: { line: COLORS.bad, back: 'rgb(255 122 102 / 0.22)' },
  done: { line: COLORS.steel, back: 'rgb(97 132 163 / 0.18)' },
}
const TEXT_COLORS: Record<TopicView['status'], string> = { available: COLORS.good, soon: COLORS.gold, locked: COLORS.bad, done: COLORS.dim }

/** Руда, которую находит открытие месторождения: её значок и рисуется на теме. */
const FOUND = { metal: 'metalOre', silicon: 'siliconOre', fuel: 'fuelOre', kharite: 'khariteOre', lithium: 'lithiumOre', uranium: 'uraniumOre' } as const

/** Значок темы: модернизация — здание со стрелками уровня, находка — руда, научный метод — лаборатория, иначе — первое, что тема открывает. */
function iconOf(view: TopicView, size: number): HTMLCanvasElement {
  if (view.topic === 'levels2' || view.topic === 'levels3') return levelCanvas(view.topic === 'levels2' ? 2 : 3, size)
  if (view.trigger && view.trigger in FOUND) return goodCanvas(FOUND[view.trigger as keyof typeof FOUND])
  if (view.trigger === 'lab') return buildingCanvas('lab', size)
  if (view.paves.length) return paveCanvas(view.paves[0], size)
  if (view.buildings.length) return buildingCanvas(view.buildings[0], size)
  if (view.units.length) return unitCanvas(view.units[0], size)
  return levelCanvas(1, size)
}

/** Почему лаборатории стоят: «кредитов», «энергии» или название груза. */
const lackName = (lack: LabLack) =>
  lack === 'credits' ? t('tech.lackCredits') : lack === 'off' ? t('tech.lackOff') : lack === 'power' || lack === 'idle' ? t('tech.lackPower') : t(`goods.${lack}`)

/** Строка темы: значок и название цвета её доступности. */
function line(ui: Ui, view: TopicView) {
  ui.box({ direction: 'row', align: 'center', gap: 4 }, () => {
    ui.image(iconOf(view, SMALL), SMALL, SMALL)
    ui.text(t(`research.${view.topic}`), { color: TEXT_COLORS[view.status] })
  })
}

/** Подсказка темы: что она даёт и открывает, цена или условие, что нужно раньше и что откроется дальше — деревом. */
function card(ui: Ui, view: TopicView, byTopic: Map<Topic, TopicView>, children: Map<Topic, Topic[]>) {
  const inner = CARD_WIDTH - 20
  ui.box({ direction: 'column', gap: 6, width: inner }, () => {
    ui.text(t(`research.${view.topic}`), { font: 'title', color: COLORS.bright, wrap: inner })
    ui.text(view.trigger && view.status !== 'done' ? t('tech.triggerTopic') : t(`tech.status.${view.status}`), { color: TEXT_COLORS[view.status] })
    ui.text(t(`researchInfo.${view.topic}`), { wrap: inner })
    const opens = [
      ...view.buildings.map((type) => ({ image: buildingCanvas(type, SMALL), name: t(`buildings.${type}`) })),
      ...view.units.map((type) => ({ image: unitCanvas(type, SMALL), name: t(`units.${type}`) })),
      ...view.paves.map((kind) => ({ image: paveCanvas(kind, SMALL), name: t(`grid.tools.${kind}`) })),
    ]
    for (const { image, name } of opens) {
      ui.box({ direction: 'row', align: 'center', gap: 4 }, () => {
        ui.image(image, SMALL, SMALL)
        ui.text(name)
      })
    }
    if (view.trigger) {
      if (view.status !== 'done') ui.text(t(`tech.triggers.${view.trigger}`), { color: COLORS.power, wrap: inner })
    } else if (view.status !== 'done') {
      ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
        ui.box({ direction: 'row', align: 'center', gap: 3 }, () => {
          coin(ui)
          ui.text(String(view.cost), { color: COLORS.gold })
        })
        for (const { resource, amount } of view.materials) res(ui, resource, { amount, color: COLORS.soft })
        ui.space(0, 0, 1)
        ui.text(t('tech.time', { time: view.time }), { color: COLORS.dim })
      })
    }
    if (view.after.length) {
      ui.box({ direction: 'column', gap: 2 }, () => {
        ui.text(t('tech.before'), { color: COLORS.dim })
        for (const before of view.after) line(ui, byTopic.get(before)!)
      })
    }
    if (children.get(view.topic)?.length) {
      ui.box({ direction: 'column', gap: 2 }, () => {
        ui.text(t('tech.leadsTo'), { color: COLORS.dim })
        // Каждая тема — один раз, там, где встретилась впервые; вложенность — отступом.
        const seen = new Set<Topic>([view.topic])
        const walk = (topic: Topic, depth: number) => {
          const next = (children.get(topic) ?? []).filter((child) => !seen.has(child))
          next.forEach((child) => seen.add(child))
          for (const child of next) {
            ui.box({ padding: [0, 0, 0, 6 + depth * 14] }, () => line(ui, byTopic.get(child)!))
            walk(child, depth + 1)
          }
        }
        walk(view.topic, 0)
      })
    }
  })
}

/** Плитка темы: значок на цвете доступности, место в очереди, готовность, название, цена и срок. */
function tile(ui: Ui, view: TopicView, width: number, send: (command: Command) => void, byTopic: Map<Topic, TopicView>, children: Map<Topic, Topic[]>) {
  const id = `tech:${view.topic}`
  // Тему-открытие и открытую в очередь не ставят.
  const idle = view.status === 'done' || !!view.trigger
  const colors = STATUS_COLORS[view.status]
  const click = ui.button(id, { width, direction: 'column', align: 'stretch', justify: 'start', gap: 2, padding: 6, active: view.order > 0, alpha: view.status === 'done' ? 0.7 : undefined }, () => {
    ui.box({ height: 56, direction: 'stack', background: colors.back }, () => {
      ui.box({ self: ['center', 'center'] }, () => ui.image(iconOf(view, ICON), ICON, ICON))
      ui.box({ self: ['start', 'end'], grow: 0, width: width - 12, height: 3, background: colors.line })
      if (view.order > 0) ui.box({ self: ['end', 'start'], background: COLORS.teamDark, padding: [0, 4] }, () => ui.text(String(view.order), { color: COLORS.bright }))
      if (view.status !== 'done' && view.progress > 0) {
        ui.box({ self: ['center', 'end'], padding: [0, 0, 6, 0] }, () => {
          ui.box({ width: width - 20, height: 4, background: COLORS.well, direction: 'row' }, () => ui.box({ width: Math.round((width - 20) * view.progress), height: 4, background: COLORS.team }))
        })
      }
    })
    ui.box({ minHeight: 28, padding: [0, 2] }, () => ui.text(t(`research.${view.topic}`), { ...SHADOW, color: COLORS.bright, wrap: width - 16 }))
    if (view.status !== 'done' && !view.trigger) {
      ui.box({ direction: 'row', align: 'center', gap: 6, padding: [0, 2] }, () => {
        ui.box({ direction: 'row', align: 'center', gap: 3 }, () => {
          coin(ui)
          ui.text(String(view.cost), { color: COLORS.gold })
        })
        ui.space(0, 0, 1)
        ui.text(view.time, { color: COLORS.dim })
      })
    }
    if (view.trigger && view.status !== 'done') ui.box({ padding: [0, 2] }, () => ui.text(t(`tech.triggers.${view.trigger!}`), { color: COLORS.power, wrap: width - 16 }))
  })
  // Карточка — справа от плитки, а у правого края экрана — слева; в нижней половине — вровень с низом плитки.
  const at = ui.rect(id)
  if (at) {
    const right = at.x + at.width + 8 + CARD_WIDTH > ui.width
    const low = at.y > ui.height / 2
    ui.tip(id, () => card(ui, view, byTopic, children), {
      ...(right ? { right: ui.width - at.x + 8 } : { left: at.x + at.width + 8 }),
      ...(low ? { bottom: Math.max(8, ui.height - at.y - at.height) } : { top: at.y }),
    })
  }
  if (click && !idle) send({ type: 'research', topic: view.topic, front: click.shift })
}

/** Окно исследований поверх игры: под верхней полосой, во всю высоту экрана. Возвращает true, если его закрыли. */
export function techWindow(ui: Ui, tech: TechState, top: number, send: (command: Command) => void): boolean {
  const byTopic = new Map(tech.topics.map((view) => [view.topic, view]))
  const children = new Map<Topic, Topic[]>()
  for (const view of tech.topics) for (const before of view.after) children.set(before, [...(children.get(before) ?? []), view.topic])
  // Сначала зелёные, потом жёлтые; внутри цвета — по ярусам. Красные — под своим заголовком, открытое — в конце.
  const order = (list: TopicView[]) => [...list].sort((a, b) => STATUS_ORDER[a.status] - STATUS_ORDER[b.status] || a.tier - b.tier)
  const open = order(tech.topics.filter((view) => view.status === 'available' || view.status === 'soon'))
  const later = order(tech.topics.filter((view) => view.status === 'locked'))
  const done = order(tech.topics.filter((view) => view.status === 'done'))
  const { labs, plan, left, current } = tech
  const width = Math.min(1180, ui.width - 16)
  const height = ui.height - top - 16
  const inner = width - 40
  const columns = Math.max(1, Math.floor((inner - 4 + GAP) / (TILE_MIN + GAP)))
  const tileWidth = Math.floor((inner - 4 - GAP * (columns - 1)) / columns)
  let closed = false

  const grid = (list: TopicView[]) => {
    ui.box({ direction: 'column', gap: GAP }, () => {
      for (let start = 0; start < list.length; start += columns) {
        ui.box({ direction: 'row', gap: GAP, align: 'stretch' }, () => {
          for (const view of list.slice(start, start + columns)) tile(ui, view, tileWidth, send, byTopic, children)
        })
      }
    })
  }

  ui.box({ id: 'tech', anchor: { centerX: 0, top: top + 8 }, width, height, skin: 'panel', padding: [16, 20], direction: 'column', align: 'stretch', gap: 8 }, () => {
    ui.box({ direction: 'row', align: 'center', gap: 16 }, () => {
      ui.text(t('tech.title'), { font: 'title', color: COLORS.bright, shadow: COLORS.ink, lift: 2 })
      ui.text(labs.count === 0 ? t('tech.noLabs') : t('tech.labs', { count: labs.count, working: labs.working, speed: labs.speed }), { color: COLORS.dim })
      ui.space(0, 0, 1)
      if (ui.button('tech:close', { padding: [2, 8] }, () => ui.text('✕', SHADOW))) closed = true
      ui.tip('tech:close', t('tech.closeTip'))
    })

    ui.box({ direction: 'row', align: 'center', gap: 6, minHeight: 36, clip: true }, () => {
      ui.text(t('tech.queue'), { color: COLORS.dim })
      if (!plan.length) ui.text(t('tech.queueEmpty'), { color: COLORS.dim })
      plan.forEach((topic, i) => {
        const view = byTopic.get(topic)!
        const id = `tech:queued:${topic}`
        const click = ui.button(id, { padding: [2, 6, 2, 2], gap: 4, active: topic === current }, () => {
          ui.image(iconOf(view, SMALL), SMALL, SMALL)
          ui.text(t(`research.${topic}`), SHADOW)
          if (i === 0) ui.text(`${Math.round(view.progress * 100)}%${left ? ` · ${left}` : ''}`, { color: COLORS.team })
        })
        ui.tip(id, t('tech.dequeueTip'))
        if (click) send({ type: 'dequeue', topic })
      })
      if (plan.length) {
        ui.space(0, 0, 1)
        if (ui.button('tech:clear', { padding: [2, 10] }, () => ui.text(t('tech.cancel'), SHADOW))) send({ type: 'cancelResearch' })
        ui.tip('tech:clear', t('tech.cancelTip'))
      }
    })
    if (current && labs.lacks.length) {
      ui.box({ direction: 'row', align: 'center', gap: 12 }, () => {
        ui.text(t('tech.lacks'), { color: COLORS.warn })
        for (const { lack, count } of labs.lacks) {
          ui.box({ direction: 'row', align: 'center', gap: 2 }, () => {
            if (lack !== 'credits' && lack !== 'off' && lack !== 'power' && lack !== 'idle') res(ui, lack)
            ui.text(lackName(lack), { color: COLORS.warn })
            ui.text(` ${t('tech.labCount', { count })}`, { color: COLORS.dim })
          })
        }
      })
    }

    ui.box({ id: 'tech:list', grow: 1, direction: 'column', gap: 6, scroll: true, padding: 2 }, () => {
      grid(open)
      if (later.length) {
        ui.box({ padding: [6, 0, 0, 0] }, () => ui.text(t('tech.later'), { color: COLORS.muted }))
        grid(later)
      }
      if (done.length) {
        ui.box({ padding: [6, 0, 0, 0] }, () => ui.text(t('tech.researched'), { color: COLORS.muted }))
        grid(done)
      }
    })
    ui.text(t('tech.hint'), { color: COLORS.muted, wrap: inner })
  })
  return closed
}
