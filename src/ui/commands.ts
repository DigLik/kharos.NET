import type { HudState, Stack } from '../game/hud'
import type { PaveIcon } from '../game/portraits'
import type { PaveTool } from '../game/scene'
import { BRIDGE_COST, BUILDINGS, UNDERGROUND_REACH, BUILDING_TYPES, DEPOSIT_TYPES, REQUIRES, FOUNDATION_COST, LEASH, ORES, ROAD_COST, WARES, type Stance, type BuildingType, type Command, type DepositKind, type Good } from '../sim'
import { t } from '../i18n'

/** Клавиши ячеек сетки команд по порядку: три ряда по четыре, как на клавиатуре, справа от WASD. */
export const GRID_KEYS = ['KeyT', 'KeyY', 'KeyU', 'KeyI', 'KeyG', 'KeyH', 'KeyJ', 'KeyK', 'KeyB', 'KeyN', 'KeyM', 'Comma'] as const
/** Подпись клавиши на кнопке. */
export const keyLabel = (code: string) => (code === 'Comma' ? ',' : code.slice(3))
export const GRID_SIZE = GRID_KEYS.length
/** Постоянные места: превращение, отмена заказа, разбор, назад. Так рука привыкает к ним. */
const CONVERT = 8
const CANCEL = 9
const DEMOLISH = 10
const BACK = 11
/** Выключатель потребителя энергии — в углу корня, где у страниц «Назад». */
const SWITCH = BACK
/** Кнопки продажи и закупки космопорта: начало нижнего ряда. Отмена заказа техники у космопорта тогда — на месте «Назад». */
const TRADE = 8
/** Сколько ячеек сверху отдаётся под списки: заказы, здания, рецепты. */
const LIST = 8

/** Страница сетки строителя: корень с разделами или сами здания раздела. */
export type Page = 'root' | 'economy' | 'storage' | 'industry' | 'military' | 'defense' | 'paving' | 'logistics' | 'filter' | 'reserve' | 'sell' | 'buy'

/** Разделы строителя: в какой странице какое здание. Остальное — хозяйство: энергия, добыча, торговля. */
const SECTIONS: Partial<Record<Page, BuildingType[]>> = {
  storage: ['metalYard', 'siliconStore', 'fuelTank', 'khariteVault', 'blockYard', 'ammoBunker', 'partsLocker'],
  industry: ['smelter', 'siliconWorks', 'distillery', 'enricher', 'blockPlant', 'ammoPlant', 'partsPlant'],
  military: ['barracks', 'factory', 'airfield', 'techCenter'],
  defense: ['wall', 'turret', 'rocketTurret', 'cannonTurret', 'laserTurret', 'arcTurret', 'radar'],
}
/** Описание здания для карточки: что делает и какие здания открывает. */
function aboutOf(building: BuildingType) {
  const opens = BUILDING_TYPES.filter((type) => REQUIRES[type]?.includes(building))
  const info = t(`buildingInfo.${building}`)
  return opens.length ? t('grid.unlocks', { info, list: opens.map((type) => t(`buildings.${type}`)).join(', ') }) : info
}

const sectionOf = (building: BuildingType): Page =>
  (Object.keys(SECTIONS) as Page[]).find((page) => SECTIONS[page]!.includes(building)) ?? 'economy'

/** Ячейка сетки команд. */
export interface Slot {
  label: string
  /** Картинка: портрет здания или юнита, либо значок груза. */
  building?: BuildingType
  unit?: HudState['units'][number]['type']
  good?: Good
  /** Значок покрытия. */
  pave?: PaveIcon
  /** Раздел, а не действие: сколько в нём открытых кнопок. Рисуется стопкой карточек со счётчиком. */
  group?: number
  cost?: number
  /** cost — не трата, а доход: продажа, возврат за разбор. */
  gain?: boolean
  power?: number
  materials?: Stack[]
  disabled?: boolean
  active?: boolean
  /** Что это такое: описание на карточке. */
  about?: string
  /** Пояснение или почему кнопка недоступна. */
  title?: string
  run(): void
}

interface Actions {
  send: (command: Command) => void
  place: (building: BuildingType | null) => void
  pave: (tool: PaveTool | null) => void
  route: (start: boolean) => void
  serve: (start: boolean) => void
  patrol: (start: boolean) => void
  open: (page: Page) => void
}

/** Стойки по порядку кнопок. */
const STANCES: Stance[] = ['aggressive', 'defensive', 'hold', 'passive']
/** Кнопки стоек: подпись и подсказка на текущем языке. */
export const stanceSlots = () => STANCES.map((stance) => ({ stance, label: t(`grid.stances.${stance}`), title: t(`grid.stanceTips.${stance}`, { leash: LEASH }) }))

type GridTool = 'foundation' | 'road' | 'pipe' | 'well'
/** Инструменты раздела «Покрытие» и цена тайла; подпись и подсказка — в словаре. */
const PAVE_TOOLS: { tool: GridTool; cost: number }[] = [
  { tool: 'foundation', cost: FOUNDATION_COST },
  { tool: 'road', cost: ROAD_COST },
]

/** Инструменты раздела «Логистика»: трубы и колодцы. */
const PIPE_TOOLS: { tool: GridTool; cost: number }[] = [
  { tool: 'pipe', cost: BUILDINGS.pipe.cost },
  { tool: 'well', cost: BUILDINGS.well.cost },
]

/** Сетка команд для выбранного: GRID_SIZE ячеек, пустые — null. */
export function commandsOf(state: HudState, page: Page, { send, place, pave, route, serve, patrol, open }: Actions): (Slot | null)[] {
  const slots: (Slot | null)[] = Array(GRID_SIZE).fill(null)
  const list = (items: Slot[]) => items.slice(0, LIST).forEach((slot, i) => (slots[i] = slot))
  const { construction, production, conversion, assembly, trade, site, demolish, credits } = state

  if (construction) {
    /** Кнопка здания из списка строителя. */
    const buildingSlot = ({ building, cost, affordable, power, materials }: (typeof construction.options)[number]): Slot => ({
      label: t(`buildings.${building}`),
      building,
      cost,
      power,
      materials,
      active: construction.placing === building,
      about: aboutOf(building),
      disabled: !affordable,
      title: affordable ? undefined : t('grid.noCredits'),
      run: () => {
        pave(null)
        place(construction.placing === building ? null : building)
      },
    })
    /** Стену не ставят по одной, а тянут мышью линией, как трубу: её кнопка включает инструмент протяжки. */
    const wallSlot = (option: (typeof construction.options)[number]): Slot => {
      const active = construction.paving?.tool === 'wall'
      return {
        ...buildingSlot(option),
        cost: active && construction.paving!.tiles > 1 ? construction.paving!.cost : option.cost,
        active,
        title: option.affordable ? t('grid.wallTip') : t('grid.noCredits'),
        run: () => {
          place(null)
          pave(active ? null : 'wall')
        },
      }
    }
    if (page === 'root') {
      // Раздел появляется, когда в нём открыто хоть одно здание; покрытие доступно всегда.
      const opened = (page: Page) => construction.options.some(({ building }) => sectionOf(building) === page)
      const section = (page: 'economy' | 'storage' | 'industry' | 'military' | 'defense' | 'paving' | 'logistics') => ({
        page,
        label: t(`grid.sections.${page}`),
        title: t(`grid.sectionTips.${page}`),
        run: () => open(page),
      })
      const sections: (Slot & { page: Page })[] = [
        { ...section('economy'), building: 'generator' },
        { ...section('storage'), building: 'metalYard' },
        { ...section('industry'), building: 'smelter' },
        { ...section('military'), building: 'factory' },
        { ...section('defense'), building: 'wall' },
      ]
      // У каждого раздела своё место: закрытый оставляет пустую ячейку, и кнопки не сдвигаются, когда открываются новые.
      const count = (page: Page) => construction.options.filter(({ building }) => sectionOf(building) === page).length
      sections.forEach((section, i) => (slots[i] = opened(section.page) ? { ...section, group: count(section.page) } : null))
      slots[sections.length] = { ...section('paving'), pave: 'road', group: PAVE_TOOLS.length }
      slots[sections.length + 1] = { ...section('logistics'), building: 'pipe', group: PIPE_TOOLS.length }
      // Разбор — сразу инструмент, без раздела.
      const active = construction.paving?.tool === 'remove'
      slots[sections.length + 2] = { label: t('grid.remove'), pave: 'remove', active, title: t('grid.removeTip'), run: () => pave(active ? null : 'remove') }
    } else if (page === 'paving' || page === 'logistics') {
      ;(page === 'paving' ? PAVE_TOOLS : PIPE_TOOLS).forEach(({ tool, cost }, i) => {
        const active = construction.paving?.tool === tool
        slots[i] = {
          label: t(`grid.tools.${tool}`),
          ...(tool === 'pipe' || tool === 'well' ? { building: tool } : { pave: tool }),
          cost: active && construction.paving!.tiles > 1 ? construction.paving!.cost : cost,
          active,
          disabled: credits < cost,
          title: t(`grid.toolTips.${tool}`, { bridge: BRIDGE_COST, reach: UNDERGROUND_REACH }),
          run: () => {
            place(null)
            pave(active ? null : tool)
          },
        }
      })
      // Здания раздела, если они у него есть, — после инструментов.
      if (page === 'logistics') {
        construction.options.filter(({ building }) => sectionOf(building) === page).forEach((option, i) => (slots[PIPE_TOOLS.length + i] = buildingSlot(option)))
      }
      slots[BACK] = {
        label: t('common.back'),
        title: t('grid.backFromPaving'),
        run: () => {
          pave(null)
          place(null)
          open('root')
        },
      }
    } else {
      const wanted = construction.options.filter(({ building }) => sectionOf(building) === page)
      wanted.slice(0, BACK).forEach((option, i) => (slots[i] = option.building === 'wall' ? wallSlot(option) : buildingSlot(option)))
      slots[BACK] = {
        label: t('common.back'),
        title: t('grid.backFromPlacing'),
        run: () => {
          pave(null)
          place(null)
          open('root')
        },
      }
    }
  }

  // Страницы торговли закрывают собой заказ техники и разбор.
  if (production && page === 'root') {
    list(
      production.options.map(({ unit, cost, affordable, materials, missing }) => ({
        label: t(`units.${unit}`),
        unit,
        cost,
        materials,
        disabled: !affordable || production.full || missing.length > 0,
        title: missing.length
          ? t('grid.needs', { list: missing.map((type) => t(`buildings.${type}`)).join(', ') })
          : production.full ? t('grid.queueFull') : affordable ? undefined : t('grid.noCredits'),
        run: () => send({ type: 'produce', producer: production.producer, unit }),
      })),
    )
    if (production.queue.length) {
      slots[trade ? BACK : CANCEL] = {
        label: t('grid.cancelOrder'),
        title: t('grid.cancelOrderTip'),
        run: () => send({ type: 'cancelProduction', producer: production.busiest }),
      }
    }
  }

  if (conversion) {
    if (conversion.progress === null) {
      slots[CONVERT] = {
        label: conversion.kind === 'deploy' ? t('grid.deploy') : t('grid.pack'),
        building: 'command',
        about: conversion.kind === 'deploy' ? t('buildingInfo.command') : undefined,
        disabled: !conversion.possible,
        title: conversion.possible ? undefined : t('grid.deployBlocked'),
        run: () => send(conversion.command),
      }
    } else if (conversion.cancel) {
      const cancel = conversion.cancel
      slots[CANCEL] = { label: t('grid.cancel'), run: () => send(cancel) }
    }
  }

  const { tactics } = state
  if (tactics && !construction && page === 'root') {
    const { units } = tactics
    stanceSlots().forEach(({ stance, label, title }, i) => {
      slots[i] = { label, active: tactics.stance === stance, title, run: () => send({ type: 'stance', units, stance }) }
    })
    slots[4] = {
      label: t('grid.patrol'),
      active: tactics.picking || tactics.patrolling > 0,
      title: t('grid.patrolTip'),
      run: () => patrol(!tactics.picking),
    }
  }

  const { haul } = state
  if (haul && !construction) {
    const { units, filter } = haul
    if (page === 'root') {
      slots[0] = {
        label: haul.routing === null ? t('grid.route') : t('grid.routeStops', { stops: haul.routing }),
        active: haul.routing !== null,
        title: t('grid.routeTip'),
        run: () => route(haul.routing === null),
      }
      if (haul.routed > 0) {
        slots[1] = { label: t('grid.dropRoute'), title: t('grid.backToRequests'), run: () => send({ type: 'route', units, stops: [] }) }
      }
      slots[2] = {
        label: filter.length ? t('grid.filterCount', { count: filter.length }) : t('grid.filter'),
        active: filter.length > 0,
        title: t('grid.filterTip'),
        run: () => open('filter'),
      }
      slots[3] = {
        label: haul.serving === null ? t('grid.serve') : t('grid.serveCount', { count: haul.serving }),
        active: haul.serving !== null,
        title: t('grid.serveTip'),
        run: () => serve(haul.serving === null),
      }
      if (haul.served > 0) {
        slots[4] = { label: t('grid.dropServe'), title: t('grid.backToRequests'), run: () => send({ type: 'serve', units, buildings: [] }) }
      }
    } else if (page === 'filter') {
      /** Руда в фильтре — вся сразу: по видам её различает месторождение, а не игрок. */
      const has = (goods: readonly Good[]) => goods.every((good) => filter.includes(good))
      const toggle = (goods: readonly Good[]) => {
        const next = has(goods) ? filter.filter((good) => !goods.includes(good)) : [...filter, ...goods.filter((good) => !filter.includes(good))]
        send({ type: 'filter', units, goods: next })
      }
      WARES.forEach((ware, i) => {
        slots[i] = { label: t(`goods.${ware}`), good: ware, active: has([ware]), title: t('grid.carryOrNot'), run: () => toggle([ware]) }
      })
      slots[WARES.length] = { label: t('grid.ore'), good: ORES[0], active: has(ORES), title: t('grid.oreTip'), run: () => toggle(ORES) }
      slots[CANCEL] = { label: t('grid.all'), active: !filter.length, title: t('grid.allTip'), run: () => send({ type: 'filter', units, goods: [] }) }
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    }
  }

  const { reserve } = state
  if (reserve && !construction && !haul) {
    const { buildings, amount, capacity } = reserve
    const set = (next: number) => send({ type: 'reserve', buildings, amount: Math.max(0, Math.min(capacity, next)) })
    if (page === 'root') {
      slots[0] = {
        label: amount > 0 ? t('grid.reserveCount', { amount }) : t('grid.reserve'),
        active: amount > 0,
        title: t('grid.reserveTip'),
        run: () => open('reserve'),
      }
    } else if (page === 'reserve') {
      // Верхний ряд — прибавить, второй — убавить: по 10 и по 50.
      ;[10, 50].forEach((step, i) => {
        slots[i] = { label: `+${step}`, disabled: amount >= capacity, title: t('grid.reserveCount', { amount }), run: () => set(amount + step) }
        slots[i + 4] = { label: `−${step}`, disabled: amount <= 0, title: t('grid.reserveCount', { amount }), run: () => set(amount - step) }
      })
      slots[2] = { label: t('grid.reserveAll'), active: amount >= capacity, title: t('grid.reserveAllTip'), run: () => set(capacity) }
      slots[CANCEL] = { label: t('grid.reserveNone'), active: amount <= 0, title: t('grid.reserveNoneTip'), run: () => set(0) }
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    }
  }

  const { harvest } = state
  if (harvest && page === 'root') {
    // Поиск месторождения: любого или своего вида, по столбцу на вид.
    const seek = (kind: DepositKind | 'any') => send({ type: 'seek', units: harvest.units, kind })
    slots[0] = { label: t('grid.seekAny'), active: harvest.seek === 'any' && !harvest.parked, title: t('grid.seekAnyTip'), run: () => seek('any') }
    DEPOSIT_TYPES.forEach((kind, i) => {
      slots[1 + i] = {
        label: t('grid.seek', { name: t(`goods.${kind}`) }),
        good: kind,
        active: harvest.seek === kind && !harvest.parked,
        title: t('grid.seekTip'),
        run: () => seek(kind),
      }
    })
  }

  if (assembly) {
    slots[0] = {
      label: assembly.on ? t('common.turnOff') : t('common.turnOn'),
      good: assembly.recipe,
      active: assembly.on,
      title: assembly.on
        ? t('grid.plantOffTip')
        : t('grid.plantOnTip'),
      run: () => send({ type: 'work', building: assembly.plant, on: !assembly.on }),
    }
  }

  const { switchable } = state
  if (switchable && page === 'root' && !slots[SWITCH]) {
    slots[SWITCH] = {
      label: switchable.on ? t('common.turnOff') : t('common.turnOn'),
      active: switchable.on,
      title: switchable.on ? t('grid.switchOffTip') : t('grid.switchOnTip'),
      run: () => switchable.buildings.forEach((building) => send({ type: 'work', building, on: !switchable.on })),
    }
  }

  if (trade) {
    const { port, order } = trade
    // Продажа и закупка — каждая на своей странице: верхний ряд корня космопорта занят заказом техники.
    if (page === 'root') {
      slots[TRADE] = {
        label: t('grid.sell'),
        building: 'spaceport',
        active: !!order,
        title: order ? t('grid.sellingTip') : t('grid.sellTip'),
        run: () => open('sell'),
      }
      slots[TRADE + 1] = {
        label: t('grid.buy'),
        building: 'spaceport',
        disabled: !!order,
        title: order ? t('grid.buyBusyTip') : t('grid.buyTip'),
        run: () => open('buy'),
      }
    } else if (!order && page === 'buy') {
      // Закупка: верхний ряд — по 10, второй — по 50; столбец на каждый ресурс.
      trade.purchases.slice(0, 4).forEach(({ resource, price, room }, i) => {
        for (const [row, amount] of [[0, 10], [1, 50]] as const) {
          const cost = amount * price
          slots[i + row * 4] = {
            label: `${t(`goods.${resource}`)} ×${amount}`,
            good: resource,
            cost,
            disabled: credits < cost || room < amount,
            title: room < amount ? t('grid.noRoom') : credits < cost ? t('grid.noCredits') : t('grid.buyPrice', { price }),
            run: () => send({ type: 'buy', port, resource, amount }),
          }
        }
      })
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    } else if (!order) {
      // Верхний ряд — продать полсотни, второй — всё, что есть: столбец на каждый ресурс.
      trade.offers.slice(0, 4).forEach(({ resource, available, price }, i) => {
        const part = Math.min(50, available)
        slots[i] = {
          label: `${t(`goods.${resource}`)} ×${part}`,
          good: resource,
          cost: part * price,
          gain: true,
          title: t('grid.sellPrice', { price }),
          run: () => send({ type: 'sell', port, resource, amount: part }),
        }
        if (available > part) {
          slots[i + 4] = {
            label: t('grid.sellAll', { name: t(`goods.${resource}`) }),
            good: resource,
            cost: available * price,
            gain: true,
            title: t('grid.sellAllTip', { amount: available, price }),
            run: () => send({ type: 'sell', port, resource, amount: available }),
          }
        }
      })
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    } else {
      if (order.flight === null) {
        slots[CANCEL] = {
          label: order.delivered > 0 ? t('grid.send') : t('grid.dropSale'),
          title: order.delivered > 0 ? t('grid.sendTip') : undefined,
          run: () => send({ type: 'closeSale', port }),
        }
      }
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    }
  }

  if (site) {
    slots[DEMOLISH] = {
      label: site.demolish ? t('grid.cancelDemolish') : t('grid.cancelBuild'),
      run: () => send({ type: 'cancelBuild', site: site.entity }),
    }
  }
  if (demolish && page === 'root') {
    slots[DEMOLISH] = {
      label: t('grid.remove'),
      cost: demolish.refund,
      gain: true,
      title: t('grid.demolishTip'),
      run: () => demolish.buildings.forEach((building) => send({ type: 'demolish', building, builders: [] })),
    }
  }
  return slots
}
