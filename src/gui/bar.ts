// Нижняя панель на холсте: мини-карта | сведения о выбранном | сетка команд 4×3. Перенесена со страницы
// (src/ui/Hud.tsx) как была: те же размеры (--bar-inner 140, ячейка 132), рамки, подсказки и карточки ячеек.

import type { HudState, Stack, TruckJob } from '../game/hud'
import { goodCanvas } from '../game/goodIcons'
import type { Minimap } from '../game/minimap'
import { buildingCanvas, paveCanvas, unitCanvas } from '../game/portraits'
import { prefs } from '../game/prefs'
import { rich, t } from '@kharos/i18n'
import { RELIEF_BELOW, RELIEF_INCOME, WARES, resourceOf, type Good, type UnitType } from '../sim'
import { GRID_KEYS, keyLabel, type FerryDraft, type Slot, type dealOf } from '../ui/commands'
import { markSeen } from '../ui/seen'
import { sourceOf } from '../ui/sources'
import { unitStats, unitStatsLine, unitStatsText } from '../ui/unitStats'
import type { Run, Ui } from './ui'
import { COLORS, SHADOW, bar, coin, gridText, key, resRun } from './widgets'

/** Внутренняя высота панели: мини-карта, портрет и три ряда сетки ложатся в неё без остатка. На узком — меньше. */
const INNER = 140
const INNER_NARROW = 116
/** Высота сетки на узком экране: три ряда по 40. */
const GRID_NARROW = 3 * 40 + 2 * 4
const CELL_WIDTH = 132
const NARROW = 1000
/** Стороны картинок: портрет выбранного, ячейка сетки, юнит в очереди. */
const PORTRAIT = 96
const TILE = 32
const QUEUED = 24

/** Ширина сетки в ряд: четыре ячейки по CELL_WIDTH. */
const GRID_WIDTH = 4 * CELL_WIDTH + 3 * 4
/** Меньше этого сведениям о выбранном не дать: портрет, промежуток и полторы сотни на текст. */
const INFO_MIN = 104 + 10 + 24 + 150
/** Отступы панели и промежутки между мини-картой, сведениями и сеткой. */
const BAR_SPACING = 16 + 8 + 8

/** Высота нижней панели: от неё отступают чат, группы и союзники над ней. */
export const barHeight = (width: number) => (width < NARROW ? INNER_NARROW + GRID_NARROW + 16 + 4 : INNER + 16)

/**
 * Сторона мини-карты. На узком экране — в ряд со сведениями; в широкий ряд крупная из настроек встаёт, только пока
 * сведениям остаётся INFO_MIN: иначе — меньше, вплоть до высоты панели. Настройка от этого не меняется.
 */
export const minimapSide = (width: number) => (width < NARROW ? INNER_NARROW : Math.max(INNER, Math.min(prefs().minimap, width - BAR_SPACING - GRID_WIDTH - INFO_MIN)))

const percent = (value: number) => `${Math.round(value * 100)}%`

/** Что панели нужно от игры: мини-карта и действия по щелчкам. */
export interface BarActions {
  minimap: Minimap
  lookAt(x: number, y: number): void
  moveSelected(x: number, y: number): void
  lookAtSelection(): void
  narrow(type: UnitType, remove: boolean): void
  send: (command: import('../sim').Command) => void
  /** Курсор на кнопке здания: на карте видны границы зон. */
  zones(shown: boolean): void
}

/** Что набирается в панели: сделка космопорта и подарок союзнику (их держит hud.ts). */
export interface BarDraft {
  deal: (NonNullable<ReturnType<typeof dealOf>> & { buying: boolean }) | null
  ferry: FerryDraft | null
  setAmount(amount: number): void
  confirm(): void
}

/** Что уже набрано в подарок союзнику: груз по видам и деньги. */
function listOfDraft(ferry: FerryDraft) {
  const parts = (Object.entries(ferry.goods) as [Good, number][]).map(([resource, amount]) => `${t(`goods.${resource}`)} ×${Math.floor(amount)}`)
  if (ferry.credits > 0) parts.push(`${ferry.credits} ${t('hud.cr')}`)
  return parts.join(', ')
}

/** Чем заняты выбранные грузовики — словами, с названиями зданий, см. TruckJob. */
function jobText(job: TruckJob) {
  const names = job.buildings.map((type) => t(`buildings.${type}`))
  const good = job.resource ? t(`goods.${job.resource}`) : ''
  switch (job.kind) {
    case 'carry':
      return names.length > 1 ? t('hud.job.carry', { name: good, from: names[0], to: names[1], left: job.left }) : t('hud.job.unload', { name: good, to: names[0] ?? '', left: job.left })
    case 'route': return t('hud.job.route', { stops: names.join(' → ') })
    case 'serve': return t('hud.job.serve', { list: names.join(', ') })
    case 'supply': return t('hud.job.supply', { name: names[0] ?? '' })
    default: return t(`hud.job.${job.kind}`)
  }
}

/** Мини-карта: левая кнопка (и протяжка) ставит туда камеру, правая шлёт выделенных, Alt — сигнал союзникам. */
function minimapView(ui: Ui, side: number, actions: BarActions) {
  const id = 'minimap'
  // Холст карты — пиксель в пиксель с экраном: сторона в пикселях холста интерфейса, рисует его minimap.ts.
  const density = Math.min(2, globalThis.devicePixelRatio || 1)
  const canvas = minimapCanvas(Math.round((side - 8) * density))
  actions.minimap.draw(canvas.getContext('2d')!)
  ui.box({ id, width: side, height: side, skin: 'well', padding: 4 }, () => ui.image(canvas, side - 8, side - 8, { live: true }))
  const held = ui.held(id)
  const click = ui.clicked(id)
  if (held && !held.click.alt) {
    const { x, y } = actions.minimap.tileAt(held.u, held.v)
    actions.lookAt(x, y)
  }
  if (click && (click.button === 2 || click.alt)) {
    const rect = ui.rect(id)
    if (!rect) return
    const u = (ui.pointer.x - rect.x - 4) / (rect.width - 8)
    const v = (ui.pointer.y - rect.y - 4) / (rect.height - 8)
    const { x, y } = actions.minimap.tileAt(Math.max(0, Math.min(1, u)), Math.max(0, Math.min(1, v)))
    if (click.button === 2) actions.moveSelected(x, y)
    else actions.send({ type: 'ping', x, y })
  }
}

let mapCanvas: HTMLCanvasElement | null = null
/** Холст мини-карты: один на игру, размер — по стороне. */
function minimapCanvas(size: number) {
  mapCanvas ??= document.createElement('canvas')
  if (mapCanvas.width !== size) mapCanvas.width = mapCanvas.height = Math.max(1, size)
  return mapCanvas
}

/** Картинка ячейки: портрет здания или юнита, значок груза или покрытия. */
function slotImage(slot: Slot): HTMLCanvasElement | null {
  if (slot.building) return buildingCanvas(slot.building, TILE)
  if (slot.unit) return unitCanvas(slot.unit, TILE)
  if (slot.good) return goodCanvas(slot.good)
  if (slot.pave) return paveCanvas(slot.pave, TILE)
  return null
}

/** Пульс подсветки ячейки: новая — золотом раз в 1,6 с, подсказка «Первых шагов» — огнями команды раз в секунду. */
function pulse(ui: Ui, rgb: string, period: number, low: number, high: number) {
  const phase = (ui.now % period) / period
  const level = Math.round((low + (high - low) * (0.5 - 0.5 * Math.cos(phase * Math.PI * 2))) * 8) / 8
  return `rgb(${rgb} / ${level})`
}

/**
 * Карточка ячейки: название с клавишей, пояснение, параметры юнита, полная цена и энергия, материалы строками с
 * названием. Чего в хранилищах меньше, чем нужно, — «есть/нужно» и откуда это берётся.
 */
function slotCard(ui: Ui, slot: Slot, hotkey: string, stock: Stack[]) {
  const stored = (good: Good) => Math.floor(stock.find(({ resource }) => resource === good)?.amount ?? 0)
  const WIDTH = 264
  ui.box({ direction: 'column', gap: 4, width: WIDTH, align: 'stretch' }, () => {
    ui.box({ direction: 'row', justify: 'between', align: 'end', gap: 12 }, () => {
      ui.text(slot.label, { font: 'title', color: COLORS.bright })
      ui.text(hotkey, { font: 'title', color: COLORS.gold })
    })
    if (slot.about) ui.text(slot.about, { color: COLORS.dim, wrap: WIDTH })
    if (slot.title) ui.text(slot.title, { color: slot.about && slot.disabled ? COLORS.warn : COLORS.dim, wrap: WIDTH })
    if (slot.unit) {
      for (const { label, value } of unitStats(slot.unit)) {
        ui.box({ direction: 'row', gap: 10 }, () => {
          ui.box({ width: 90 }, () => ui.text(label, { color: COLORS.dim }))
          ui.text(String(value))
        })
      }
    }
    if (slot.cost !== undefined || slot.power) {
      ui.box({ direction: 'row', align: 'center', gap: 10 }, () => {
        if (slot.cost !== undefined) {
          ui.box({ direction: 'row', align: 'center', gap: 4 }, () => {
            coin(ui)
            ui.text(slot.gain ? `+${slot.cost}` : String(slot.cost), { color: slot.gain ? COLORS.good : COLORS.gold })
          })
        }
        if (slot.power) ui.text(`⚡${slot.power > 0 ? `+${slot.power}` : slot.power}`, { color: COLORS.power })
        if (slot.power && slot.idle !== undefined) ui.text(t('grid.idlePower', { idle: slot.idle }), { color: COLORS.dim })
      })
    }
    for (const { resource, amount } of slot.materials ?? []) {
      const short = stored(resource) < amount
      ui.box({ direction: 'column' }, () => {
        ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
          ui.image(goodCanvas(resource), 16, 16)
          ui.box({ grow: 1 }, () => ui.text(t(`goods.${resource}`), { ellipsis: true }))
          ui.text(short ? `${stored(resource)}/${amount}` : String(amount), { color: short ? COLORS.warn : COLORS.text })
        })
        if (short) ui.box({ padding: [0, 0, 0, 22] }, () => ui.text(sourceOf(resource), { color: COLORS.dim, wrap: WIDTH - 22 }))
      })
    }
  })
}

/**
 * Сетка команд 4×3: ячейка — кнопка со своей клавишей, картинкой, названием и ценой в кредитах; полная цена с
 * материалами и энергией — в карточке при наведении, над сеткой у правого края.
 */
function commandGrid(ui: Ui, slots: (Slot | null)[], stock: Stack[], width: number, height: number, actions: BarActions) {
  const cellWidth = (width - 3 * 4) / 4
  const cellHeight = (height - 2 * 4) / 3
  let zones = false
  ui.box({ id: 'commands', direction: 'column', gap: 4, width, height }, () => {
    for (let row = 0; row < 3; row++) {
      ui.box({ direction: 'row', gap: 4 }, () => {
        for (let column = 0; column < 4; column++) {
          const i = row * 4 + column
          const slot = slots[i]
          const hotkey = keyLabel(GRID_KEYS[i])
          if (!slot) {
            ui.box({ width: cellWidth, height: cellHeight, skin: 'well', alpha: 0.35 })
            continue
          }
          const id = `cell:${i}`
          const picture = slotImage(slot)
          const outline = slot.hint && !slot.disabled ? pulse(ui, '90 169 255', 1000, 0.3, 1) : slot.fresh && !slot.disabled ? pulse(ui, '240 201 90', 1600, 0.2, 0.95) : undefined
          // Отступы — как у кнопки страницы: с картинкой она стоит почти у рамки, без неё текст отходит на 8.
          const click = ui.button(id, { width: cellWidth, height: cellHeight, padding: [0, 6, 0, picture ? 4 : 8], gap: 6, justify: 'start', active: slot.active, disabled: slot.disabled, group: slot.group !== undefined, outline }, () => {
            if (picture) {
              ui.box({ direction: 'stack' }, () => {
                // Раздел — стопкой из двух карточек: одна выглядывает из-за другой.
                if (slot.group !== undefined) ui.box({ self: ['end', 'start'], width: TILE, height: TILE, background: '#2d4560', padding: [0, 0, 3, 3] })
                ui.box({ width: TILE, height: TILE, background: slot.group !== undefined ? '#1c2b3e' : undefined, padding: slot.good ? 1 : 0 }, () => ui.image(picture, slot.good ? 30 : TILE, slot.good ? 30 : TILE))
                ui.box({ self: ['start', 'start'], background: COLORS.ink, padding: [0, 2] }, () => key(ui, hotkey, slot.disabled ? COLORS.muted : COLORS.gold))
              })
            }
            ui.box({ direction: 'column', grow: 1, gap: 0, clip: true }, () => {
              ui.box({ direction: 'row', gap: 6 }, () => {
                ui.box({ grow: 1 }, () => ui.text(slot.label, { ...SHADOW, color: COLORS.bright, ellipsis: true }))
                if (!picture) key(ui, hotkey, slot.disabled ? COLORS.muted : COLORS.gold)
              })
              ui.box({ direction: 'row', align: 'center', gap: 4 }, () => {
                if (slot.group !== undefined) ui.text(`${slot.group} ▸`, { color: COLORS.dim })
                if (slot.cost !== undefined) {
                  coin(ui)
                  ui.text(slot.gain ? `+${slot.cost}` : String(slot.cost), { ...SHADOW, color: slot.gain ? COLORS.good : slot.short ? COLORS.warn : COLORS.gold })
                }
                if (slot.cost === undefined && slot.materials?.length) ui.text('…', { color: COLORS.dim })
              })
            })
            if (slot.lamp !== undefined) ui.box({ width: 6, height: 6, background: slot.lamp ? COLORS.good : COLORS.bad })
          })
          // Подсказка — карточкой над сеткой, правым краем у указателя.
          const grid = ui.rect('commands')
          ui.tip(id, () => slotCard(ui, slot, hotkey, stock), grid ? { right: Math.max(8, ui.width - ui.pointer.x - 40), bottom: ui.height - grid.y + 12 } : undefined)
          if (ui.hovered(id) && slot.zones) zones = true
          // Новое под указателем становится знакомым: подсветка с него уходит, а с раздела — когда знакомо всё в нём.
          if (ui.hovered(id) && slot.fresh && slot.seen) markSeen(slot.seen)
          if (click) slot.run()
        }
      })
    }
  })
  actions.zones(zones)
}

/** Строка подсказки панели сведений: текст со значками, перенос по ширине. */
function hint(ui: Ui, runs: Run[] | string, width: number, color: string = COLORS.soft) {
  ui.flow(typeof runs === 'string' ? [runs] : runs, { width, color })
}

/** rich() со значками: подстановки-кусочки разворачиваются в строку. */
const richRuns = (parts: (string | Run[])[]): Run[] => parts.flatMap((part) => (typeof part === 'string' ? [part] : part))

/** Сведения о выбранном: портрет (или плитки отряда), название, прочность и всё, что здание или отряд делает сейчас. */
function info(ui: Ui, state: HudState, width: number, height: number, actions: BarActions, draft: BarDraft) {
  const { units, building, deposit, site, construction, conversion, production, assembly, trade } = state
  const nameOf = (player: number) => state.players.find((one) => one.player === player)?.name ?? ''
  const many = units.length > 1 || (units[0]?.count ?? 0) > 1
  const health = building !== null ? (state.health ?? 1) : (state.army?.health ?? 1)
  const stats = building === null && units.length === 1 ? units[0].type : null
  const title =
    building !== null
      ? state.buildings > 1
        ? state.mixed ? t('hud.buildingsCount', { count: state.buildings }) : `${t(`buildings.${building}`)} ×${state.buildings}`
        : site ? t(site.undo ? 'hud.undoing' : site.demolish ? 'hud.demolition' : 'hud.construction', { name: t(`buildings.${building}`) }) : t(`buildings.${building}`)
      : units.map(({ type, count }) => (count > 1 ? `${t(`units.${type}`)} ×${count}` : t(`units.${type}`))).join(', ')
  // Плиток отряда в ряд — сколько влезает рядом с текстом (до четырёх). Ширина подробностей — остаток: панель без
  // рамки и отступов, без портрета (или плиток) и промежутка.
  const columns = Math.max(1, Math.min(4, Math.floor((width - 24 - 10 - 150 + 4) / 44)))
  const left = many ? columns * 40 + (columns - 1) * 4 : 104
  const text = Math.max(40, width - 24 - left - 10)
  let n = 0
  const id = () => `info:${n++}`
  const good = (resource: Good, amount?: number, of?: number) => resRun(resource, id(), amount, of)

  ui.box({ width, height, skin: 'well', padding: [10, 12], direction: 'row', gap: 10 }, () => {
    if (many) {
      ui.box({ direction: 'column', gap: 4, width: left, maxHeight: height - 20, scroll: true, id: 'info:units' }, () => {
        for (let start = 0; start < units.length; start += columns) {
          ui.box({ direction: 'row', gap: 4 }, () => {
            for (const { type, count } of units.slice(start, start + columns)) {
              const unitId = `unit:${type}`
              const click = ui.button(unitId, { width: 40, height: 40, padding: 0, skin: 'well', direction: 'stack' }, () => {
                ui.box({ self: ['center', 'center'] }, () => ui.image(unitCanvas(type, TILE), TILE, TILE))
                if (count > 1) ui.box({ self: ['end', 'end'], padding: [0, 5, 3, 0] }, () => ui.text(String(count), { color: COLORS.bright, shadow: COLORS.ink }))
              })
              ui.tip(unitId, t('hud.narrowTip', { name: t(`units.${type}`) }))
              if (click) actions.narrow(type, click.shift)
            }
          })
        }
      })
    } else {
      const portrait = building !== null ? buildingCanvas(building, PORTRAIT) : unitCanvas(units[0].type, PORTRAIT)
      const click = ui.button('portrait', { width: 104, height: 104, padding: 0, skin: 'well', direction: 'stack' }, () => {
        ui.box({ self: ['center', 'center'] }, () => ui.image(portrait, PORTRAIT, PORTRAIT))
        // Прочность — полоской по низу портрета: красной, когда её мало.
        ui.box({ self: ['start', 'end'], width: Math.max(2, Math.round(104 * health)), height: 6, background: health < 0.35 ? COLORS.bad : '#4fd07a' })
      })
      ui.tip('portrait', t('hud.showOnMap'))
      if (click) actions.lookAtSelection()
    }

    ui.box({ id: 'info:details', direction: 'column', gap: 4, grow: 1, maxHeight: height - 20, scroll: true }, () => {
      ui.box({ id: 'info:title', width: text }, () => ui.text(title, { font: 'title', color: COLORS.bright, shadow: COLORS.ink, lift: 2, ellipsis: true }))
      ui.tip('info:title', many ? title : stats ? unitStatsText(stats) : '')
      if (stats) {
        ui.box({ id: 'info:stats', width: text }, () => ui.text(unitStatsLine(stats), { color: COLORS.dim, ellipsis: true }))
        ui.tip('info:stats', unitStatsText(stats))
      }
      if (state.power) hint(ui, gridText(state.power), text, state.power.demand > state.power.produced || state.power.overload ? COLORS.bad : COLORS.power)
      if (state.coreless) hint(ui, t('hud.coreless'), text, COLORS.warn)
      if (state.relief) hint(ui, t('hud.relief', { below: RELIEF_BELOW, income: RELIEF_INCOME }), text)
      if (state.unreachable) hint(ui, t('hud.unreachable'), text, COLORS.warn)
      if (state.trapped > 0) hint(ui, t('hud.trapped', { count: state.trapped }), text, COLORS.warn)
      if (state.starved) hint(ui, t('hud.starved'), text, COLORS.bad)
      if (state.switchable && !state.switchable.on) hint(ui, t('hud.switchedOff'), text, COLORS.warn)
      if (state.health !== null) hint(ui, t('hud.health', { percent: percent(state.health), repair: state.repair }), text)
      if (state.shield) hint(ui, state.shield.down > 0 ? t('hud.shieldDown', { seconds: state.shield.down }) : t('hud.shield', { charge: state.shield.charge, capacity: state.shield.capacity }), text)
      if (state.battery) {
        const { flow, charge, capacity } = state.battery
        hint(ui, t(flow > 0 ? 'hud.batteryGiving' : flow < 0 ? 'hud.batteryCharging' : 'hud.battery', { charge, capacity, flow: Math.abs(flow) }), text)
      }
      if (state.army && state.army.health < 1) hint(ui, t('hud.armyHealth', { percent: percent(state.army.health) }), text)
      if (state.army && state.army.ammo !== null && state.army.ammo < 1) hint(ui, t('hud.armyAmmo', { percent: percent(state.army.ammo) }), text)
      if (state.army && state.army.empty > 0) hint(ui, t('hud.armyEmpty', { count: state.army.empty }), text)
      if (state.army && state.army.armed > 0 && !construction) hint(ui, t('hud.armed'), text)

      if (state.stored?.slots) hint(ui, [`${t('hud.stock')}: `, ...state.stored.slots.flatMap(({ resource, amount, of }) => good(resource, amount, of))], text)
      if (state.stored && !state.stored.slots) {
        const { stored } = state
        hint(ui, [
          `${stored.buffer ? t('hud.orderMaterials') : stored.store ? t('hud.storage') : t('hud.stock')}: `,
          ...stored.items.flatMap(({ resource, amount }) => good(resource, amount)),
          stored.items.length === 0 ? t('common.empty') : '',
          !stored.buffer ? t('hud.holds', { capacity: stored.capacity }) : '',
          state.reserve && state.reserve.amount > 0 ? t('hud.reserve', { amount: state.reserve.amount }) : '',
        ], text)
      }
      if (state.request) {
        const { request } = state
        hint(ui, [`${t('hud.request')}: `, ...WARES.filter((ware) => request.wanted[ware]).flatMap((ware) => good(ware, request.have[ware] ?? 0, request.wanted[ware])), Object.keys(request.wanted).length === 0 ? t('hud.requestNone') : ''], text)
      }
      if (state.refinery) {
        const { ore } = state.refinery
        hint(ui, richRuns(rich<Run[]>('hud.refines', { ore: good(ore), oreName: t(`goods.${ore}`), product: good(resourceOf(ore)), productName: t(`goods.${resourceOf(ore)}`), intake: state.refinery.intake })), text)
      }
      if (state.ammo) hint(ui, [`${t('hud.ammo')}: `, ...good('ammo', state.ammo.have, state.ammo.capacity), state.ammo.have <= 0 ? t('hud.ammoEmpty') : t('hud.ammoTip')], text, state.ammo.have <= 0 ? COLORS.warn : COLORS.soft)
      for (const bay of state.supplies?.bays ?? []) {
        hint(ui, [`${t(`goods.${bay.resource}`)}: `, ...good(bay.resource, bay.have, bay.capacity), bay.have <= 0 ? t('hud.suppliesEmpty') : t('hud.suppliesTip', { radius: state.supplies!.radius })], text, bay.have <= 0 ? COLORS.warn : COLORS.soft)
      }
      if (state.rods) hint(ui, [`${t('hud.rods')}: `, ...good('rods', state.rods.have, state.rods.capacity), state.rods.burning ? t('hud.rodsTip') : t('hud.rodsEmpty')], text, state.rods.have <= 0 && !state.rods.burning ? COLORS.warn : COLORS.soft)
      if (assembly) {
        hint(ui, richRuns(rich<Run[]>('hud.recipe', { inputs: assembly.inputs.flatMap(({ resource, amount }) => good(resource, amount)), output: good(assembly.recipe, assembly.yield), seconds: assembly.seconds })), text)
        const phase = !assembly.on ? (assembly.progress > 0 ? 'offFinishing' : 'off') : assembly.progress > 0 ? 'working' : assembly.full ? 'full' : 'waiting'
        bar(ui, assembly.progress, t('hud.assembly', { name: t(`goods.${assembly.recipe}`), have: assembly.have, state: t(`hud.assemblyStates.${phase}`) }), text)
        if (!assembly.on) hint(ui, t('hud.plantOff'), text, COLORS.warn)
      }
      if (state.materials) hint(ui, [`${t('hud.materials')}: `, ...state.materials.items.flatMap(({ resource, have, need }) => good(resource, have, need)), state.materials.waiting ? t('hud.materialsWaiting') : ''], text, state.materials.waiting ? COLORS.warn : COLORS.soft)
      if (state.cargo) {
        const { cargo } = state
        hint(ui, [...richRuns(rich<Run[]>('hud.cargo', { items: cargo.items.length ? cargo.items.flatMap(({ resource, amount }) => good(resource, amount)) : [t('common.empty')], capacity: cargo.capacity })), state.haul ? ` ${jobText(state.haul.job)}` : ''], text)
      }
      if (state.carry) {
        const { carry } = state
        hint(ui, [...richRuns(rich<Run[]>('hud.cargo', { items: carry.items.length ? carry.items.flatMap(({ resource, amount }) => good(resource, amount)) : [t('common.empty')], capacity: carry.capacity })), ' ', carry.going > 0 ? t('hud.fetchGoing') : carry.toSite > 0 ? t('hud.fetchToSite') : carry.toStore > 0 ? t('hud.fetchToStore') : t('hud.fetchHolds')], text)
      }
      if (trade) {
        if (trade.order) {
          const { order } = trade
          const name = t(`goods.${order.resource}`)
          bar(ui, order.flight ?? order.delivered / order.wanted, order.buy ? t('hud.buying', { name, wanted: order.wanted, paid: order.wanted * order.price }) : order.flight !== null ? t('hud.shipping', { name, wanted: order.wanted, total: order.wanted * order.price }) : t('hud.hauling', { name, delivered: order.delivered, wanted: order.wanted }), text)
        } else if (trade.transfer) {
          bar(ui, trade.transfer.flight, t('hud.ferrying', { name: nameOf(trade.transfer.to), percent: percent(trade.transfer.flight) }), text)
        } else if (draft.deal) {
          const { deal } = draft
          ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
            ui.flow([`${t(deal.buying ? 'grid.buy' : 'grid.sell')}: `, ...good(deal.resource, deal.amount)], { width: 200, color: COLORS.soft })
            const field = ui.input('deal', String(deal.amount), { width: 72, digits: true, maxLength: 6 })
            const amount = Math.max(0, Math.min(deal.max, Math.floor(Number(field.value)) || 0))
            if (amount !== deal.amount) draft.setAmount(amount)
            if (field.keys.includes('Enter')) draft.confirm()
          })
          hint(ui, deal.buying ? t('hud.dealBuy', { total: deal.amount * deal.price, room: deal.room - deal.amount }) : t('hud.dealSell', { total: deal.amount * deal.price, left: deal.max - deal.amount }), text)
        } else hint(ui, `${trade.offers.length ? t('hud.sellHint') : t('hud.nothingToSell')}${t('hud.buyHint')}`, text)
      }
      if (draft.ferry) hint(ui, t('hud.ferryDraft', { name: draft.ferry.name, list: listOfDraft(draft.ferry) || t('common.empty') }), text)
      if (deposit) {
        hint(ui, richRuns(rich<Run[]>('hud.depositLeft', { icon: good(deposit.kind), name: t(`goods.${deposit.kind}`), left: deposit.left > 0 ? t('hud.left', { amount: deposit.left }) : t('hud.depositDepleted') })), text)
      }
      if (state.level) {
        const { level } = state
        hint(ui, level.level > 1 ? t('hud.level', { level: level.level, max: level.max, gain: t(`levelGains.${level.gain}`), factor: level.factor }) : t('hud.levelBase', { level: level.level, max: level.max }), text)
        if (level.raising) bar(ui, level.raising.progress, level.raising.progress > 0 ? t('hud.upgrading', { percent: percent(level.raising.progress) }) : t('hud.waitsToUpgrade'), text)
      }
      if (state.silo) {
        const { silo } = state
        if (silo.stage === 'assembling') bar(ui, silo.progress, t('hud.warheadAssembling', { percent: percent(silo.progress) }), text)
        else hint(ui, silo.stage === 'launching' ? t('hud.launching', { seconds: silo.launch }) : silo.stage === 'ready' ? t('hud.warheadReady') : t('hud.warheadEmpty'), text, silo.stage === 'launching' ? COLORS.warn : COLORS.soft)
      }
      if (state.interceptor) {
        const { missiles: count, capacity, assembling, progress, lack, radius, cost, materials } = state.interceptor
        if (assembling) bar(ui, progress, t('hud.interceptorAssembling', { count, capacity, percent: percent(progress) }), text)
        else {
          hint(ui, lack === 'off' ? t('hud.interceptorOff', { count, capacity })
            : lack === 'credits' ? t('hud.interceptorNoCredits', { count, capacity, cost })
            : lack === 'materials' ? t('hud.interceptorNoMaterials', { count, capacity, materials: materials.map(({ resource, amount }) => `${amount} ${t(`goods.${resource}`)}`).join(', ') })
            : t('hud.interceptorReady', { count, capacity, radius }), text)
        }
      }
      if (state.research && !state.research.topic) hint(ui, t('hud.researchIdle'), text)
      if (state.research?.topic) {
        const { research } = state
        bar(ui, research.progress, t('hud.researching', { name: t(`research.${research.topic!}`), percent: percent(research.progress) }), text)
        if (research.lack !== 'off') {
          hint(ui, research.lack === 'power' ? t('hud.labNoPower')
            : research.lack === 'credits' ? t('hud.labNoCredits')
            : research.lack && research.lack !== 'idle' ? t('hud.labLacks', { name: t(`goods.${research.lack}`) })
            : t('hud.labSpeed', { speed: research.speed }), text, research.lack ? COLORS.warn : COLORS.soft)
        }
      }
      if (site) {
        bar(ui, site.progress, site.demolish
          ? site.progress < 1 ? t('hud.demolishing', { percent: percent(site.progress) }) : t('hud.waitsToDemolish')
          : site.started
            ? site.broke ? t('hud.buildingBroke', { percent: percent(site.progress) }) : t('hud.building', { percent: percent(site.progress) })
            : site.blocked ? t('hud.blocked') : t('hud.waitsBuilder'), text)
      }
      if (conversion && conversion.progress !== null) bar(ui, conversion.progress, conversion.blocked ? t('hud.blocked') : conversion.kind === 'deploy' ? t('hud.deploying') : t('hud.packing'), text)
      if (production && production.queue.length > 0) {
        bar(ui, production.progress, t(`units.${production.queue[0]}`), text)
        ui.box({ direction: 'row', gap: 4 }, () => {
          production.queue.forEach((unit, i) => {
            const queued = `queue:${i}`
            ui.box({ id: queued, width: 32, height: 32, skin: 'well', padding: 4 }, () => ui.image(unitCanvas(unit, QUEUED), QUEUED, QUEUED))
            ui.tip(queued, t(`units.${unit}`))
          })
        })
      }
      if (production && !production.building && production.queue.length > 0) hint(ui, t('hud.packedQueue'), text)
      if (production?.army) hint(ui, t('hud.army', production.army), text)
      if (production?.building) hint(ui, production.rally ? t('hud.rallySet') : t('hud.rallyTip'), text)
      if (construction && !construction.placing && !construction.paving) hint(ui, t('hud.builderTip'), text)
      if (conversion?.kind === 'deploy' && conversion.progress === null && !conversion.choosing) hint(ui, conversion.traveling ? t('hud.deployTravel') : t('hud.mcvTip'), text)
    })
  })
}

/** Подсказка по управлению, когда ничего не выбрано: по предложению в строке. */
function nothing(ui: Ui, width: number, height: number) {
  ui.box({ width, height, skin: 'well', padding: [10, 12], direction: 'column', justify: 'center', gap: 4, clip: true }, () => {
    for (const line of t('hud.nothingSelected').split(/(?<=\.)\s+/)) ui.text(line, { color: COLORS.muted, wrap: true })
  })
}

/**
 * Нижняя панель целиком. На широком экране — в ряд: мини-карта, сведения, сетка; на узком — в два ряда: сверху
 * мини-карта и сведения, снизу сетка во всю ширину. В редакторе — только мини-карта (справа его панель).
 */
export function bottomBar(ui: Ui, state: HudState, slots: (Slot | null)[], actions: BarActions, draft: BarDraft) {
  const narrow = ui.width < NARROW
  const selected = state.units.length > 0 || state.building !== null
  const inner = narrow ? INNER_NARROW : INNER
  const side = minimapSide(ui.width)
  const stock = state.stock?.items ?? []
  if (state.editor) {
    ui.box({ id: 'bar', anchor: { left: 0, bottom: 0 }, skin: 'panel', padding: [4, 8], direction: 'row', align: 'end', height: inner + 16 }, () => minimapView(ui, side, actions))
    return
  }
  if (narrow) {
    const full = ui.width - 16
    ui.box({ id: 'bar', anchor: { left: 0, right: 0, bottom: 0 }, height: barHeight(ui.width), skin: 'panel', padding: [8, 8], direction: 'column', gap: 4 }, () => {
      ui.box({ direction: 'row', gap: 4, height: inner }, () => {
        minimapView(ui, side, actions)
        if (selected) info(ui, state, full - side - 4, inner, actions, draft)
        else nothing(ui, full - side - 4, inner)
      })
      commandGrid(ui, selected ? slots : Array(GRID_KEYS.length).fill(null), stock, full, GRID_NARROW, actions)
    })
    return
  }
  const gridWidth = GRID_WIDTH
  // Мини-карта занимает в ряду всю свою сторону, даже когда выступает над панелью; сведениям — остаток.
  const infoWidth = ui.width - BAR_SPACING - side - gridWidth
  ui.box({ id: 'bar', anchor: { left: 0, right: 0, bottom: 0 }, height: INNER + 16, skin: 'panel', padding: [4, 8], direction: 'row', align: 'end', gap: 8 }, () => {
    // Мини-карта крупнее панели выступает над ней: панель её не обрезает.
    ui.box({ height: INNER, direction: 'column', justify: 'end' }, () => minimapView(ui, side, actions))
    ui.box({ direction: 'row', gap: 8, height: INNER, align: 'center' }, () => {
      if (selected) info(ui, state, infoWidth, INNER, actions, draft)
      else nothing(ui, infoWidth, INNER)
      commandGrid(ui, selected ? slots : Array(GRID_KEYS.length).fill(null), stock, gridWidth, INNER, actions)
    })
  })
}
