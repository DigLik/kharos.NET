import type { HudState, Stack } from '../game/hud'
import type { PaveIcon } from '../game/portraits'
import type { PaveTool } from '../game/scene'
import { BRIDGE_COST, CLIFF_COST, PEAK_COST, BUILDINGS, SUPPLY, UNDERGROUND_REACH, BUILDING_TYPES, REQUIRES, FOUNDATION_COST, LEASH, ORES, ROAD_COST, TRANSFER_CAPACITY, WARES, type Amounts, type Stance, type BuildingType, type Command, type Good, type Resource } from '../sim'
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
/** Улучшение здания: конец второго ряда корня — у зданий, которые улучшают, списков там нет. */
const UPGRADE = 7
const BACK = 11
/** Выключатель потребителя энергии — в углу корня, где у страниц «Назад». */
const SWITCH = BACK
/** Отдать юнитов союзнику — тоже в углу корня: у юнитов там больше ничего нет. */
const GIFT = BACK
/** Уничтожить своих юнитов — на месте отмены: у юнитов оно занято, только пока разворачивается MCV. */
const SCUTTLE = CANCEL
/** Кнопки продажи и закупки космопорта: начало нижнего ряда. Отмена заказа техники у космопорта тогда — на месте «Назад». */
const TRADE = 8
/** Подарок союзнику — сверху: нижний ряд космопорта уже занят торговлей, разбором и выключателем. */
const FERRY = 0
/** Сколько ячеек сверху отдаётся под списки: заказы, здания, рецепты. */
const LIST = 8

/** Страница сетки строителя: корень с разделами или сами здания раздела. */
export type Page = 'root' | 'economy' | 'storage' | 'industry' | 'production' | 'military' | 'defense' | 'paving' | 'logistics' | 'filter' | 'reserve' | 'sell' | 'buy' | 'ally' | 'ferryGoods' | 'ferryMore' | 'gift' | 'scuttle'

/** Разделы строителя: в какой странице какое здание. Остальное — хозяйство: энергия, добыча, торговля. */
const SECTIONS: Partial<Record<Page, BuildingType[]>> = {
  storage: ['metalYard', 'siliconStore', 'fuelTank', 'khariteVault', 'lithiumStore', 'uraniumCask', 'blockYard', 'ammoBunker', 'partsLocker', 'rodRack'],
  industry: ['smelter', 'siliconWorks', 'distillery', 'enricher', 'evaporator', 'uraniumMill'],
  production: ['blockPlant', 'ammoPlant', 'partsPlant', 'rodPlant'],
  military: ['barracks', 'factory', 'airfield', 'repairStation', 'silo'],
  defense: ['wall', 'gate', 'turret', 'rocketTurret', 'cannonTurret', 'laserTurret', 'arcTurret', 'shieldGenerator', 'interceptor', 'radar'],
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
  /** Сколько энергии просит здание без дела, если не столько же, сколько в работе (power). */
  idle?: number
  materials?: Stack[]
  disabled?: boolean
  active?: boolean
  /** Переключатель: лампа в углу горит, пока включено (true), и гаснет красным выключенное. */
  lamp?: boolean
  /** Пока курсор на ячейке, на карте видны границы зон строительства: кнопка здания или трубы, даже недоступная. */
  zones?: boolean
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
  /** Начать наводку ядерного удара шахтой или бросить её (null). */
  aim: (silo: number | null) => void
  /** Начать выбор места, где развернуть MCV, или бросить его (null). */
  deploy: (mcv: number | null) => void
  open: (page: Page) => void
  /** Набрать сделку космопорта или бросить её (null). */
  pick: (deal: Deal | null) => void
  /** Набрать подарок союзнику или бросить его (null). */
  draft: (ferry: FerryDraft | null) => void
  /** Открыть окно исследований. */
  tech: () => void
}

/** Стойки по порядку кнопок. */
const STANCES: Stance[] = ['aggressive', 'defensive', 'hold', 'passive']
/** Кнопки стоек: подпись и подсказка на текущем языке. */
export const stanceSlots = () => STANCES.map((stance) => ({ stance, label: t(`grid.stances.${stance}`), title: t(`grid.stanceTips.${stance}`, { leash: LEASH }) }))

type GridTool = 'foundation' | 'road' | 'peak' | 'cliff' | 'pipe' | 'well'
/** Инструменты раздела «Покрытие» — с землеройными работами — и цена тайла; подпись и подсказка — в словаре. */
const PAVE_TOOLS: { tool: GridTool; cost: number }[] = [
  { tool: 'foundation', cost: FOUNDATION_COST },
  { tool: 'road', cost: ROAD_COST },
  { tool: 'peak', cost: PEAK_COST },
  { tool: 'cliff', cost: CLIFF_COST },
]

/** Инструменты раздела «Логистика»: трубы и колодцы. */
const PIPE_TOOLS: { tool: GridTool; cost: number }[] = [
  { tool: 'pipe', cost: BUILDINGS.pipe.cost },
  { tool: 'well', cost: BUILDINGS.well.cost },
]

/** Сделка, которую игрок набирает у космопорта: что продать или купить и сколько. */
export interface Deal {
  resource: Resource
  amount: number
}
/** Шаги количества в сделке и с какого числа она начинается. */
const DEAL_STEPS = [10, 50, 100]
const DEAL_START = 50

/**
 * Подарок союзнику, который игрок набирает у космопорта: кому, сколько денег и какого груза. Юнитов так
 * не передают — их отдают сразу, из меню юнитов, см. gift.
 * editing — что правится сейчас: null — список груза, иначе количество; amount — набираемое число, его
 * кладёт в черновик «Добавить».
 */
export interface FerryDraft {
  to: number
  name: string
  credits: number
  goods: Amounts
  editing: { kind: 'good'; good: Good } | { kind: 'credits' } | null
  amount: number
}

/** Шаги количества для подарка: груз — десятками, деньги — сотнями. */
const FERRY_STEPS: Record<'good' | 'credits', number[]> = {
  good: DEAL_STEPS,
  credits: [100, 500, 1000],
}

/** Пустой подарок этому союзнику: с него начинается набор. */
export const ferryDraftFor = (to: number, name: string): FerryDraft => ({ to, name, credits: 0, goods: {}, editing: null, amount: 0 })

/**
 * Сделка в пределах возможного: количество — не больше max (продажа — сколько есть в запасе, закупка — на сколько
 * хватает кредитов и места в трюме, room), и почём единица. null — такого ресурса космопорт сейчас не предлагает.
 */
export function dealOf(trade: NonNullable<HudState['trade']>, credits: number, buying: boolean, deal: Deal) {
  const purchase = trade.purchases.find(({ resource }) => resource === deal.resource)
  const offer = trade.offers.find(({ resource }) => resource === deal.resource)
  const price = (buying ? purchase : offer)?.price
  if (price === undefined) return null
  const room = purchase?.room ?? 0
  const max = buying ? Math.max(0, Math.min(room, Math.floor(credits / price))) : offer!.available
  return { resource: deal.resource, amount: Math.max(0, Math.min(max, Math.floor(deal.amount) || 0)), price, max, room }
}

/**
 * Сетка команд для выбранного: GRID_SIZE ячеек, пустые — null. deal — набираемая сделка космопорта (см. Deal),
 * ferry — набираемый подарок союзнику (см. FerryDraft).
 */
export function commandsOf(
  state: HudState,
  page: Page,
  { send, place, pave, route, serve, patrol, aim, deploy, open, pick, draft, tech }: Actions,
  deal: Deal | null = null,
  ferry: FerryDraft | null = null,
): (Slot | null)[] {
  const slots: (Slot | null)[] = Array(GRID_SIZE).fill(null)
  const list = (items: Slot[]) => items.slice(0, LIST).forEach((slot, i) => (slots[i] = slot))
  const { construction, production, conversion, assembly, trade, site, demolish, credits } = state

  if (construction) {
    /** Кнопка здания из списка строителя. */
    const buildingSlot = ({ building, cost, affordable, power, idle, materials }: (typeof construction.options)[number]): Slot => ({
      label: t(`buildings.${building}`),
      building,
      cost,
      power,
      idle: idle === power ? undefined : idle,
      materials,
      active: construction.placing === building,
      zones: true,
      about: aboutOf(building),
      disabled: !affordable,
      title: affordable ? undefined : t('grid.noCredits'),
      run: () => {
        pave(null)
        place(construction.placing === building ? null : building)
      },
    })
    /** Стену и ворота не ставят по одной, а тянут мышью линией, как трубу: их кнопка включает инструмент протяжки. */
    const wallSlot = (option: (typeof construction.options)[number]): Slot => {
      const tool = option.building === 'gate' ? 'gate' : 'wall'
      const active = construction.paving?.tool === tool
      return {
        ...buildingSlot(option),
        cost: active && construction.paving!.tiles > 1 ? construction.paving!.cost : option.cost,
        active,
        title: option.affordable ? t('grid.wallTip') : t('grid.noCredits'),
        run: () => {
          place(null)
          pave(active ? null : tool)
        },
      }
    }
    if (page === 'root') {
      // Раздел появляется, когда в нём открыто хоть одно здание.
      const opened = (page: Page) => construction.options.some(({ building }) => sectionOf(building) === page)
      const section = (page: 'economy' | 'storage' | 'industry' | 'production' | 'military' | 'defense' | 'paving' | 'logistics') => ({
        page,
        label: t(`grid.sections.${page}`),
        title: t(`grid.sectionTips.${page}`),
        run: () => open(page),
      })
      const sections: (Slot & { page: Page })[] = [
        { ...section('economy'), building: 'generator' },
        { ...section('storage'), building: 'metalYard' },
        { ...section('industry'), building: 'smelter' },
        { ...section('production'), building: 'blockPlant' },
        { ...section('military'), building: 'factory' },
        { ...section('defense'), building: 'wall' },
      ]
      // У каждого раздела своё место: закрытый оставляет пустую ячейку, и кнопки не сдвигаются, когда открываются новые.
      const count = (page: Page) => construction.options.filter(({ building }) => sectionOf(building) === page).length
      sections.forEach((section, i) => (slots[i] = opened(section.page) ? { ...section, group: count(section.page) } : null))
      // Покрытие и логистику открывают исследования: раздел — с первым открытым инструментом.
      const known = (tools: { tool: GridTool }[]) => tools.filter(({ tool }) => construction.tools.includes(tool)).length
      slots[sections.length] = known(PAVE_TOOLS) ? { ...section('paving'), pave: 'road', group: known(PAVE_TOOLS) } : null
      const logistics = known(PIPE_TOOLS) + count('logistics')
      slots[sections.length + 1] = logistics ? { ...section('logistics'), building: 'pipe', group: logistics } : null
      // Разбор — сразу инструмент, без раздела. Место — то же, что у «Разобрать» здания: ячейка разворота MCV под ним
      // бывает занята, если MCV выбран вместе со строителем.
      const active = construction.paving?.tool === 'remove'
      slots[DEMOLISH] = { label: t('grid.remove'), pave: 'remove', active, title: t('grid.removeTip'), run: () => pave(active ? null : 'remove') }
    } else if (page === 'paving' || page === 'logistics') {
      ;(page === 'paving' ? PAVE_TOOLS : PIPE_TOOLS).forEach(({ tool, cost }, i) => {
        // Неисследованный инструмент — пустая ячейка: остальные не сдвигаются.
        if (!construction.tools.includes(tool)) return
        const active = construction.paving?.tool === tool
        slots[i] = {
          label: t(`grid.tools.${tool}`),
          ...(tool === 'pipe' || tool === 'well' ? { building: tool, zones: true } : { pave: tool }),
          cost: active && construction.paving!.tiles > 1 ? construction.paving!.cost : cost,
          active,
          disabled: credits < cost,
          title: t(`grid.toolTips.${tool}`, { bridge: BRIDGE_COST, reach: UNDERGROUND_REACH, cost }),
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
      wanted.slice(0, BACK).forEach((option, i) => (slots[i] = option.building === 'wall' || option.building === 'gate' ? wallSlot(option) : buildingSlot(option)))
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
      production.options.map(({ unit, cost, affordable, materials, missing, research, capped }) => ({
        label: t(`units.${unit}`),
        unit,
        cost,
        materials,
        disabled: !affordable || production.full || missing.length > 0 || !!research || capped,
        title: missing.length
          ? t('grid.needs', { list: missing.map((type) => t(`buildings.${type}`)).join(', ') })
          : research ? t('grid.needsResearch', { name: t(`research.${research}`) })
          : production.full ? t('grid.queueFull')
          : capped && production.army ? t('grid.armyLimit', { used: production.army.used, limit: production.army.limit, supply: SUPPLY[unit] })
          : affordable ? undefined : t('grid.noCredits'),
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
    // MCV разворачивают как строят: кнопка включает выбор места, он доедет туда сам.
    const mcv = conversion.command.type === 'deploy' ? conversion.command.unit : null
    if (conversion.progress === null) {
      slots[CONVERT] = {
        label: conversion.kind === 'deploy' ? t('grid.deploy') : t('grid.pack'),
        building: 'command',
        about: conversion.kind === 'deploy' ? t('buildingInfo.command') : undefined,
        disabled: !conversion.possible,
        active: conversion.choosing,
        title: mcv !== null ? t('grid.deployTip') : undefined,
        run: () => (mcv !== null ? deploy(conversion.choosing ? null : mcv) : send(conversion.command)),
      }
    }
    if ((conversion.progress !== null || conversion.traveling) && conversion.cancel) {
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
      // Под фильтром — сбросить его: на странице фильтра для этой кнопки места нет.
      if (filter.length) slots[6] = { label: t('grid.all'), title: t('grid.allTip'), run: () => send({ type: 'filter', units, goods: [] }) }
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
      // Готового десять видов: руда — сразу за ними, «Всё» — в корне грузовика.
      slots[WARES.length] = { label: t('grid.ore'), good: ORES[0], active: has(ORES), title: t('grid.oreTip'), run: () => toggle(ORES) }
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

  const { gift } = state
  if (gift && !construction) {
    // Отдать юнитов союзнику: они перейдут под его управление там же, где стоят, см. gift.ts.
    if (page === 'root') {
      const allies = state.players.filter(({ own, relation }) => !own && relation === 'ally').length
      slots[GIFT] = {
        label: t('grid.gift'),
        disabled: allies < 1,
        title: allies ? t('grid.giftTip') : t('grid.noAllies'),
        run: () => open('gift'),
      }
    } else if (page === 'gift') {
      // Кому отдать: союзник на ячейку, клик — юниты переходят к нему.
      list(
        state.players
          .filter(({ own, relation }) => !own && relation === 'ally')
          .map(({ player, name }): Slot => ({
            label: name,
            title: t('grid.giftWho'),
            run: () => {
              send({ type: 'gift', units: gift, to: player })
              open('root')
            },
          })),
      )
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    }
  }

  if (gift) {
    // Уничтожить выбранных юнитов: сначала кнопка, потом подтверждение на своей странице — и на другой клавише.
    if (page === 'root' && !slots[SCUTTLE]) {
      slots[SCUTTLE] = { label: t('grid.scuttle'), title: t('grid.scuttleTip'), run: () => open('scuttle') }
    } else if (page === 'scuttle') {
      slots[0] = {
        label: t('grid.scuttleConfirm', { count: gift.length }),
        title: t('grid.scuttleConfirmTip'),
        run: () => {
          send({ type: 'scuttle', units: gift })
          open('root')
        },
      }
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    }
  }

  if (assembly) {
    slots[0] = {
      label: assembly.on ? t('common.turnOff') : t('common.turnOn'),
      good: assembly.recipe,
      lamp: assembly.on,
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
      lamp: switchable.on,
      title: switchable.on ? t('grid.switchOffTip') : t('grid.switchOnTip'),
      run: () => switchable.buildings.forEach((building) => send({ type: 'work', building, on: !switchable.on })),
    }
  }

  if (trade) {
    const { port, order, transfer } = trade
    // Космопорт один, и корабль у него один: пока в пути заказ или подарок, ни то, ни другое не начать.
    const busy = !!order || !!transfer
    /** Груз из хранилищ зон, который можно положить в корабль: ячейка на груз, клик — выбрать количество. */
    const ferryGoodsOf = (ferry: FerryDraft) =>
      (state.stock?.items ?? []).map(({ resource, amount }): Slot => ({
        label: `${t(`goods.${resource}`)} ×${amount}`,
        good: resource,
        title: t('grid.ferryAmount'),
        run: () => draft({ ...ferry, editing: { kind: 'good', good: resource }, amount: Math.floor(ferry.goods[resource] ?? 0) }),
      }))
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
      // Подарок союзнику: деньги и груз кораблём. Набранное ждёт возвращения на страницу подарка.
      const allies = state.players.filter(({ own, relation }) => !own && relation === 'ally').length
      slots[FERRY] = {
        label: t('grid.give'),
        building: 'spaceport',
        active: !!ferry,
        disabled: busy || allies < 1,
        title: busy ? t('grid.giveBusyTip') : allies ? t('grid.giveTip') : t('grid.noAllies'),
        run: () => open(ferry ? 'ferryGoods' : 'ally'),
      }
    } else if (page === 'ally') {
      // Кому корабль: союзник на ячейку, клик — набор подарка ему.
      list(
        state.players
          .filter(({ own, relation }) => !own && relation === 'ally')
          .map(({ player, name }): Slot => ({
            label: name,
            active: ferry?.to === player,
            title: t('grid.ferryWho'),
            run: () => {
              draft(ferryDraftFor(player, name))
              open('ferryGoods')
            },
          })),
      )
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    } else if (page === 'ferryMore' && ferry && !ferry.editing) {
      // Хвост списка груза, который не влез на первую страницу.
      ferryGoodsOf(ferry).slice(LIST).forEach((slot, i) => (slots[i] = slot))
      slots[BACK] = { label: t('common.back'), run: () => open('ferryGoods') }
    } else if ((page === 'ferryGoods' || page === 'ferryMore') && ferry) {
      // Что положить в корабль: груз из хранилищ зон и деньги. Набранное видно в панели сведений.
      if (!ferry.editing) {
        const goods = ferryGoodsOf(ferry)
        list(goods)
        // Под список восемь ячеек: девятый груз — в свободную ячейку нижнего ряда, а если их больше — там «Ещё».
        if (goods.length === LIST + 1) slots[DEMOLISH] = goods[LIST]
        else if (goods.length > LIST + 1) slots[DEMOLISH] = { label: t('grid.more'), title: t('grid.moreTip'), run: () => open('ferryMore') }
        const empty = !ferry.credits && !Object.keys(ferry.goods).length
        slots[TRADE] = {
          label: `${t('grid.ferrySend')}: ${ferry.name}`,
          building: 'spaceport',
          disabled: empty || busy,
          title: busy ? t('grid.giveBusyTip') : empty ? t('grid.ferryEmpty') : t('grid.giveTip'),
          run: () => {
            send({ type: 'ferry', port, to: ferry.to, credits: ferry.credits, goods: ferry.goods })
            draft(null)
            open('root')
          },
        }
        slots[TRADE + 1] = {
          label: ferry.credits ? `${t('grid.ferryCredits')} ×${ferry.credits}` : t('grid.ferryCredits'),
          title: t('grid.ferryAmount'),
          run: () => draft({ ...ferry, editing: { kind: 'credits' }, amount: ferry.credits }),
        }
        slots[BACK] = { label: t('common.back'), run: () => open('root') }
      } else {
        // Количество: верхний ряд — прибавить, второй — убавить, «Всё» — сколько есть, «Добавить» — в корабль.
        const editing = ferry.editing
        const max = editing.kind === 'good'
          ? Math.min(state.stock?.items.find(({ resource }) => resource === editing.good)?.amount ?? 0, TRANSFER_CAPACITY)
          : credits
        const loaded = editing.kind === 'good' ? Math.floor(ferry.goods[editing.good] ?? 0) : ferry.credits
        const steps = FERRY_STEPS[editing.kind]
        const set = (next: number) => draft({ ...ferry, amount: Math.max(0, Math.min(max, next)) })
        steps.forEach((step, i) => {
          slots[i] = { label: `+${step}`, disabled: ferry.amount >= max, run: () => set(ferry.amount + step) }
          slots[i + 4] = { label: `−${step}`, disabled: ferry.amount <= 0, run: () => set(ferry.amount - step) }
        })
        slots[steps.length] = { label: t('grid.all'), active: ferry.amount >= max, title: `×${max}`, run: () => set(max) }
        slots[TRADE] = {
          label: `${t('grid.ferryAdd')} ×${ferry.amount}`,
          good: editing.kind === 'good' ? editing.good : undefined,
          disabled: ferry.amount === loaded,
          title: t('grid.ferryAmount'),
          run: () => {
            // Ноль убирает груз из корабля: так набранное и правят, и снимают.
            if (editing.kind === 'credits') return void draft({ ...ferry, credits: ferry.amount, editing: null, amount: 0 })
            const goods = { ...ferry.goods }
            if (ferry.amount > 0) goods[editing.good] = ferry.amount
            else delete goods[editing.good]
            draft({ ...ferry, goods, editing: null, amount: 0 })
          },
        }
        slots[BACK] = {
          label: t('common.back'),
          run: () => {
            draft({ ...ferry, editing: null, amount: 0 })
            open('ferryGoods')
          },
        }
      }
    } else if (page === 'ferryGoods') {
      slots[BACK] = { label: t('common.back'), run: () => open('root') }
    } else if (!order && (page === 'buy' || page === 'sell')) {
      const buying = page === 'buy'
      const picked = deal && dealOf(trade, credits, buying, deal)
      if (!picked) {
        // Сначала — что продать или купить: ячейка на ресурс. Количество — на следующем шаге.
        list(
          (buying ? trade.purchases : trade.offers).map((item): Slot => {
            const { resource, price } = item
            const limit = dealOf(trade, credits, buying, { resource, amount: DEAL_START })!
            // Ресурс, который игрок ещё не научился делать, с орбиты не продают: ячейка видна, но закрыта.
            const research = 'research' in item ? item.research : null
            return {
              label: buying ? t(`goods.${resource}`) : `${t(`goods.${resource}`)} ×${limit.max}`,
              good: resource,
              disabled: limit.max < 1 || !!research,
              title: research ? t('grid.needsResearch', { name: t(`research.${research}`) })
                : limit.max >= 1 ? t(buying ? 'grid.buyPrice' : 'grid.sellPrice', { price }) : limit.room < 1 ? t('grid.noRoom') : t('grid.noCredits'),
              run: () => pick({ resource, amount: limit.amount }),
            }
          }),
        )
        slots[BACK] = { label: t('common.back'), run: () => open('root') }
      } else {
        // Верхний ряд — прибавить, второй — убавить; «Всё» — сколько есть в запасе или на сколько хватает кредитов и трюма.
        const { resource, amount, price, max } = picked
        const set = (next: number) => pick({ resource, amount: Math.max(0, Math.min(max, next)) })
        DEAL_STEPS.forEach((step, i) => {
          slots[i] = { label: `+${step}`, disabled: amount >= max, run: () => set(amount + step) }
          slots[i + 4] = { label: `−${step}`, disabled: amount <= 0, run: () => set(amount - step) }
        })
        slots[DEAL_STEPS.length] = { label: t('grid.all'), active: amount >= max, title: `×${max}`, run: () => set(max) }
        slots[TRADE] = {
          label: `${t(buying ? 'grid.buy' : 'grid.sell')} ×${amount}`,
          good: resource,
          cost: amount * price,
          gain: !buying,
          disabled: amount < 1,
          title: t(buying ? 'grid.buyPrice' : 'grid.sellPrice', { price }),
          run: () => {
            send({ type: buying ? 'buy' : 'sell', port, resource, amount })
            pick(null)
          },
        }
        slots[BACK] = { label: t('common.back'), run: () => pick(null) }
      }
    } else if (order) {
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

  const { level } = state
  if (level && page === 'root' && !construction) {
    const { next, raising } = level
    if (next) {
      slots[UPGRADE] = {
        label: t('grid.upgrade', { level: next.level }),
        building: state.building ?? undefined,
        cost: next.cost,
        materials: next.materials,
        disabled: !!next.lack,
        title: next.lack === 'research' ? t('grid.needsResearch', { name: t(`research.${next.topic}`) })
          : next.lack ? t(next.lack === 'credits' ? 'grid.noCredits' : 'grid.upgradeNoMaterials')
          : t('grid.upgradeTip', { gain: t(`levelGains.${level.gain}`), factor: next.factor }),
        run: () => next.buildings.forEach((building) => send({ type: 'upgrade', building, builders: [] })),
      }
    } else if (raising) {
      slots[UPGRADE] = {
        label: t('grid.cancel'),
        title: t('grid.cancelUpgradeTip'),
        run: () => raising.buildings.forEach((building) => send({ type: 'cancelUpgrade', building })),
      }
    }
  }

  const { research } = state
  if (research && page === 'root' && !construction) {
    slots[0] = { label: t('grid.techTree'), title: t('grid.techTreeTip'), about: t('buildingInfo.lab'), run: tech }
    if (research.topic) slots[CANCEL] = { label: t('grid.cancel'), title: t('grid.cancelResearchTip'), run: () => send({ type: 'cancelResearch' }) }
  }

  const { silo } = state
  if (silo && page === 'root' && !construction) {
    const { entity, stage, lack } = silo
    slots[0] = {
      label: t('grid.warhead'),
      good: 'rods',
      cost: stage === 'empty' ? silo.cost : undefined,
      materials: stage === 'empty' ? silo.materials : undefined,
      about: t('grid.warheadAbout'),
      active: stage !== 'empty',
      disabled: !!lack,
      title: stage !== 'empty' ? t('grid.warheadBusy') : lack === 'credits' ? t('grid.noCredits') : lack === 'materials' ? t('grid.warheadNoMaterials') : t('grid.warheadTip', { time: silo.time }),
      run: () => send({ type: 'warhead', silo: entity }),
    }
    slots[1] = {
      label: t('grid.launch'),
      about: t('grid.launchAbout'),
      active: silo.aiming,
      disabled: stage !== 'ready' && stage !== 'launching',
      title: stage === 'launching' ? t('grid.launchRetarget') : stage === 'ready' ? t('grid.launchTip', { seconds: BUILDINGS.silo.warhead.launch }) : t('grid.launchNoWarhead'),
      run: () => aim(silo.aiming ? null : entity),
    }
    if (stage === 'assembling') slots[CANCEL] = { label: t('grid.cancel'), title: t('grid.cancelWarheadTip'), run: () => send({ type: 'cancelWarhead', silo: entity }) }
    if (stage === 'launching') slots[CANCEL] = { label: t('grid.abortLaunch'), title: t('grid.abortLaunchTip'), run: () => send({ type: 'abortLaunch', silo: entity }) }
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
