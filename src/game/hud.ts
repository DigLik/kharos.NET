import type { CommandInfo } from '../net/chatCommands'
import type { AlertKind, AlertText } from './alerts'
import type { Replica } from '../net/replica'
import { knownReserve } from './knownReserve'
import type { Entity } from '../ecs'
import { textOf } from '../i18n'
import {
  Assembly, BUILDABLE, isUnlocked, buyPrice, isOrbital, roomFor, BUILDINGS, Building, Converting, Hauler, Health, CORE, GOODS, PRODUCT_SPECS, REFINE_RATE, RESOURCES, RESOURCE_SPECS, buildingSpec, cycleSeconds, isOwn, missingRequirements, producibleBy, productStock, hasRoom, Trade, Inventory, amountOf, loadOf, deliveredTo, stockOf, stockOfZone, isPipe, networkOf, zoneWith, Producer, QUEUE_LIMIT, Site, UNITS, UNIT_TYPES, Unit, unitSpec,
  awaitsMaterials, buildTicks, canDemolish, canFight, canDeploy, canPack, depositAt, depositNear, DEPOSIT_SIZE, entriesOf, isDeployBlocked, creditsOf, protectionLeft, coreless, economyOf, isSiteBlocked, materialsFor, reserveLeft, powerOf, powerStates, refundOf, repairCostOf, rewardsOf, siteTicks, spareOf, unlinked, zoneEconomies, gridsOf,
  RESEARCH, Research, TOPICS, isLab, levelTopic, missingResearch, topicLack, topicPrice, type Topic, type TopicPrice, type TopicLack,
  MAX_LEVEL, Upgrade, isUpgradable, levelFactor, levelGain, upgradeCost, upgradeLack, upgradeTicks, type LevelGain, type UpgradeLack,
  Silo, launchLeft, warheadLack, type WarheadLack,
  Accumulator, isBurning, isSwitchable, Off, Owner, Player, Position, Shield, Tactics, Transfer, allyHeadIn, giftable, isDefeated, stanceOf, type Stance, type Amounts, type BuildingType, type Command, type DepositKind, type Good, type Ore, type Product, type Resource, type UnitType, type Sim,
} from '../sim'
import { paveStrokeOf } from './placing'
import type { PaveTool, Scene } from './scene'

/** Что интерфейс игрока показывает прямо сейчас. Обычные данные: их можно сравнивать и хранить в состоянии. */
export interface HudState {
  credits: number
  /** Уведомления, от старых к новым: at — когда, по нему строка узнаётся между опросами. */
  alerts: { kind: AlertKind; text: AlertText; x: number; y: number; at: number }[]
  /**
   * Месторождение под указателем мыши на карте: что в нём, сколько осталось и где указатель на экране,
   * чтобы подсказка встала рядом. null — указатель не над месторождением.
   */
  hover: { kind: DepositKind; left: number | null; x: number; y: number } | null
  /** Своя труба или колодец под указателем: сколько зданий в его сети и что у неё в запасе. */
  pipeHover: { buildings: number; stock: Stack[]; capacity: number; x: number; y: number } | null
  /** Награды, которые игрок уже получил, по порядку. */
  rewards: string[]
  /** Время суток в мире, «чч:мм», и идёт ли непогода. */
  clock: string
  storm: boolean
  /** Кто ещё на сервере: ники, свой ли, подключён ли. Пусто в локальной игре. */
  /**
   * relation — что между игроком и нами: союз, он предложил союз или мы ему, см. allies.ts. credits — счёт союзника
   * (свой и союзнический хост шлёт целиком), base — где его главное здание: туда ведёт клик по панели союзников.
   * player — номер игрока: по нему команда ferry называет получателя подарка. head — глава его союза: он сам, если
   * он глава, иначе его глава, и -1, если игрок ни с кем. По head кнопки союза понимают, кто вправе исключать;
   * чужих, не союзников, клиент не видит и считает одиночками — отказ скажет хост, см. host.ts.
   */
  players: { player: number; name: string; own: boolean; online: boolean; safe: boolean; relation: 'ally' | 'allyAsked' | 'allyOffered' | null; credits: number; base: { x: number; y: number } | null; head: number }[]
  /** Сколько осталось своей защиты новичка, «м:сс» десятками секунд; пусто — её нет. */
  protection: string
  /** Чат сетевой игры: сообщения и когда пришли (мс, performance.now); null — игра локальная, чата нет. */
  chat: { name: string; own: boolean; text: string; at: number; system: boolean; whisper?: string }[] | null
  /** Команды чата для дополнения и администратор ли игрок; в локальной игре команд нет. */
  commands: readonly CommandInfo[]
  admin: boolean
  /** Игрок в редакторе живого мира, см. /editor: поверх игры — панель редактора. */
  editor: boolean
  /** Игрок набирает маршрут или список обслуживания: Enter сейчас завершает его, а не открывает чат. */
  picking: boolean
  /** Пришёл ли мир от хоста: до этого наград ноль не потому, что их нет, а потому, что мира ещё нет. */
  loaded: boolean
  /** Игрок проиграл: у него не осталось ни зданий, ни MCV. */
  defeated: boolean
  /** Доход в кредитах в секунду. */
  income: number
  /**
   * Энергия зоны строительства, в которой стоит выбранная электростанция: сколько вырабатывается и сколько
   * просят потребители. У каждой зоны она своя, поэтому общего счётчика нет.
   */
  power: { produced: number; demand: number; stored: number } | null
  /** Выбранный накопитель: заряд, ёмкость и сколько энергии он сейчас отдаёт (больше нуля) или копит (меньше). */
  battery: { charge: number; capacity: number; flow: number } | null
  /** Прочность выбранного здания от 0 до 1, если оно повреждено, и сколько кредитов стоит его дочинить. */
  health: number | null
  repair: number
  /**
   * Щит выбранного генератора: запас и ёмкость в единицах прочности; down — через сколько секунд поднимется
   * пробитый купол, ноль — купол держится или растёт.
   */
  shield: { charge: number; capacity: number; down: number } | null
  /** Выбранному зданию не хватает энергии, и оно работает медленнее. */
  starved: boolean
  /** Выбранное здание со складом не подключено к сети труб, см. unlinked. */
  unlinked: boolean
  /** Выбранное доходное здание стоит в зоне без главного здания и денег не приносит. */
  coreless: boolean
  /** Выбранные юниты по видам. */
  units: { type: UnitType; count: number }[]
  /**
   * Свои юниты среди выбранных, которых можно отдать союзнику: их номера — для команды gift, см. gift.ts.
   * null — отдавать нечего: выбран не свой юнит, турель на носителе или обречённый.
   */
  gift: number[] | null
  /** Бой: сколько среди выбранных юнитов вооружённых и средняя прочность выбранных юнитов от 0 до 1. */
  army: { armed: number; health: number } | null
  /**
   * Тактика выбранных бойцов: кому слать стойку и патруль; stance — общая стойка или null, если у них разные;
   * patrolling — сколько из них в патруле; picking — игрок выбирает точку патруля.
   */
  tactics: { units: number[]; stance: Stance | null; patrolling: number; picking: boolean } | null
  /** Выбранное здание, если выбрано оно. */
  building: BuildingType | null
  /** Месторождение под выбранной шахтой: что в нём и сколько осталось. */
  deposit: { kind: DepositKind; left: number } | null
  /** Запас игрока во всех хранилищах; null — хранить негде. */
  stock: { items: Stack[]; capacity: number } | null
  /** Склад выбранного здания: что в нём, сколько помещается всего; store — это хранилище. */
  stored: { items: Stack[]; capacity: number; store: boolean; buffer: boolean; slots: { resource: Good; amount: number; of: number }[] | null } | null
  /** Переработка: какую руду она принимает и сколько в секунду. */
  refinery: { ore: Ore; intake: number } | null
  /**
   * Завод изделий: что собирает, включён ли, готовность нынешней сборки от 0 до 1 и сколько изделия уже
   * есть у зоны; inputs — из чего одна сборка, yield — сколько штук она даёт и за сколько секунд.
   */
  /** Выбранные свои готовые здания — потребители энергии, кроме заводов изделий: их можно выключить. on — включено хоть одно. */
  switchable: { buildings: number[]; on: boolean } | null
  /** Сколько выбрано зданий и площадок; mixed — они разных видов. */
  buildings: number
  mixed: boolean
  assembly: {
    plant: number
    recipe: Product
    on: boolean
    progress: number
    have: number
    /** Готовому некуда лечь: хранилища полны, цех стоит. */
    full: boolean
    inputs: Stack[]
    yield: number
    seconds: number
  } | null
  /** Боеприпасы выбранной турели: сколько есть и сколько помещается. */
  ammo: { have: number; capacity: number } | null
  /** Стержни выбранного своего реактора: сколько на складе, сколько помещается и горит ли стержень сейчас. */
  rods: { have: number; capacity: number; burning: boolean } | null
  /**
   * Материалы, которых ждёт выбранная стройка или первый заказ выбранного производителя: сколько нужно и сколько
   * уже на месте. waiting — работа стоит, пока их не привезут.
   */
  materials: { items: { resource: Good; have: number; need: number }[]; waiting: boolean } | null
  /**
   * Продажа, если выбран свой готовый космопорт. offers — что лежит в хранилищах его зоны и почём.
   * order — открытая заявка: что и сколько продаётся, сколько уже привезли; flight — готовность полёта от 0 до 1,
   * null — корабль ещё грузится.
   */
  trade: {
    port: number
    offers: { resource: Resource; available: number; price: number }[]
    /** Закупка с орбиты: почём каждый ресурс и сколько единиц влезет в склад космопорта. */
    purchases: { resource: Resource; price: number; room: number }[]
    /** buy — это закупка: деньги уже отданы, корабль летит с грузом. */
    order: { resource: Resource; wanted: number; delivered: number; price: number; flight: number | null; buy: boolean } | null
    /**
     * Корабль с подарком союзнику в пути: кому (to — номер игрока), что везёт и как далеко улетел (flight — от 0
     * до 1). Груз и деньги выгружаются при посадке: с этого мига их тут уже нет.
     */
    transfer: { to: number; credits: number; goods: Stack[]; flight: number } | null
  } | null
  /**
   * Резерв выбранных своих хранилищ: кому слать, сколько не выдаётся у первого из них и сколько они вмещают
   * (наименьшее из всех).
   */
  reserve: { buildings: number[]; amount: number; capacity: number } | null
  /** Груз выбранных грузовиков вместе; bound — сколько из них привязано к шахте, busy — сколько заняты работой. */
  cargo: { items: Stack[]; capacity: number; bound: number; busy: number } | null
  /**
   * Выбранные грузовики (не харвестеры): кому слать маршрут и фильтр. routed — сколько из них ездят по маршруту,
   * filter — фильтр первого из них, routing — сколько остановок игрок уже набрал, null — не набирает.
   */
  haul: { units: number[]; routed: number; served: number; filter: Good[]; routing: number | null; serving: number | null } | null
  /** Стройка, если выбранное здание ещё не достроено. */
  site: {
    entity: number
    /** Строитель уже начал работу: до этого площадка только размечена. */
    started: boolean
    /** На ещё не начатой площадке стоят юниты: пока не уйдут, стройка не начнётся. */
    blocked: boolean
    /** Готовность от 0 до 1. */
    progress: number
    /** Здание разбирают: готовность идёт к нулю. */
    demolish: boolean
  } | null
  /** Разбор выбранных своих готовых зданий, которые можно разобрать. refund — сколько кредитов вернётся за все. */
  demolish: { buildings: number[]; refund: number } | null
  /**
   * Уровень выбранных своих зданий, которые улучшают: level, gain и factor — уровень первого из них, что он поднимает
   * и во сколько раз. next — заказ следующего уровня: кому слать, сколько это стоит за всех и чего не хватает;
   * raising — улучшения в работе: чьи и готовность первого от 0 до 1.
   */
  level: {
    level: number
    max: number
    gain: LevelGain
    factor: number
    /** topic — исследование, которое открывает этот уровень: его называет подсказка, когда lack — research. */
    next: { buildings: number[]; level: number; factor: number; cost: number; materials: Stack[]; lack: UpgradeLack | null; topic: Topic } | null
    raising: { buildings: number[]; progress: number } | null
  } | null
  /**
   * Исследования выбранного своего готового техцентра: темы с ценой и тем, что мешает начать (lack; after — тема,
   * которую надо открыть раньше), и current — тема в работе с готовностью от 0 до 1.
   */
  research: {
    lab: number
    topics: { topic: Topic; cost: number; materials: Stack[]; time: string; lack: TopicLack | null; after: Topic | null }[]
    current: { topic: Topic; progress: number } | null
  } | null
  /**
   * Ракетная шахта: выбранная своя готовая. stage — что с боеголовкой: нет, собирается (progress от 0 до 1), готова,
   * в пуске (launch — секунд до старта). cost, materials и time — цена и срок сборки, lack — что мешает её начать.
   * aiming — игрок наводит её на цель.
   */
  silo: {
    entity: number
    stage: 'empty' | 'assembling' | 'ready' | 'launching'
    progress: number
    launch: number
    cost: number
    materials: Stack[]
    time: string
    lack: WarheadLack | null
    aiming: boolean
  } | null
  /** Что можно построить, если среди выбранного есть строитель. */
  construction: {
    /** Здание, для которого сейчас выбирается место. */
    placing: BuildingType | null
    /** Покрытие, которое сейчас кладут или снимают, и во что обойдётся протянутое мышью. */
    paving: { tool: PaveTool; cost: number; tiles: number; short: number } | null
    /** power — как здание изменит баланс энергии: больше нуля — даст, меньше — попросит; materials — что привезти на стройку. */
    options: { building: BuildingType; cost: number; affordable: boolean; power: number; materials: Stack[] }[]
  } | null
  /** Превращение выбранного: MCV разворачивается (deploy), главное здание сворачивается (pack). */
  conversion: {
    kind: 'deploy' | 'pack'
    command: Command
    /** Можно ли начать прямо сейчас. */
    possible: boolean
    /** Доля от 0 до 1, если превращение уже идёт; иначе null. */
    progress: number | null
    /** MCV готов развернуться, но под будущим зданием стоят юниты. */
    blocked: boolean
    /** Команда отмены, если идущее превращение можно отменить. */
    cancel: Command | null
  } | null
  /**
   * Производство, если среди выбранного один производитель — MCV или готовое здание, выпускающее юнитов, — или
   * несколько зданий одного вида. producer — кому идёт заказ: наименее занятому; busiest — самый занятый: его очередь
   * видна и отменяется; count — сколько их.
   */
  production: {
    producer: number
    busiest: number
    count: number
    queue: UnitType[]
    /** Готовность первого заказа, от 0 до 1. */
    progress: number
    full: boolean
    /** Здание, а не MCV: ему можно поставить точку сбора. rally — она, если поставлена. */
    building: boolean
    rally: boolean
    /** missing — каких зданий не хватает для заказа, research — какого исследования. */
    options: { unit: UnitType; cost: number; affordable: boolean; materials: Stack[]; missing: BuildingType[]; research: Topic | null }[]
  } | null
}

/** Сколько груза: для списков в интерфейсе. */
export interface Stack {
  resource: Good
  amount: number
}

/** Груз списком в порядке GOODS, без нулей; доли округляются вниз. Руда показывается рядом с готовым. */
const stacksOf = (amounts: Amounts): Stack[] =>
  GOODS.filter((resource) => (amounts[resource] ?? 0) >= 1).map((resource) => ({ resource, amount: Math.floor(amounts[resource]!) }))

/** Готовые ресурсы списком: для продажи и материалов, куда руда не идёт. */
const resourceStacksOf = (amounts: Amounts): { resource: Resource; amount: number }[] =>
  RESOURCES.filter((resource) => (amounts[resource] ?? 0) >= 1).map((resource) => ({ resource, amount: Math.floor(amounts[resource]!) }))

/** То же для цен в материалах: без округления. */
const exactOf = (amounts: Amounts): Stack[] => entriesOf(amounts).map(([resource, amount]) => ({ resource, amount }))

const round = (value: number) => Math.round(value * 100) / 100

/** Месторождение под указателем мыши, см. HudState.hover. */
function hoverOf(scene: Scene): HudState['hover'] {
  const { camera, sim } = scene
  const tile = camera.pointerTile
  if (!tile || !camera.pointer) return null
  // Месторождение занимает DEPOSIT_SIZE × DEPOSIT_SIZE тайлов: подходит то, что накрывает тайл под указателем.
  const spot = depositNear(sim, tile.x + 0.5, tile.y + 0.5, DEPOSIT_SIZE)
  const covers = spot && tile.x >= spot.x && tile.x < spot.x + DEPOSIT_SIZE && tile.y >= spot.y && tile.y < spot.y + DEPOSIT_SIZE
  if (!spot || !covers) return null
  // В тумане — остаток, каким его видели в последний раз: чужую добычу сквозь туман не видно.
  const left = knownReserve(sim, scene.player, spot)
  return { kind: spot.kind, left: left === null ? null : Math.floor(left), x: camera.pointer.x, y: camera.pointer.y }
}

/** Своя труба или колодец под указателем мыши, см. HudState.pipeHover. */
function pipeHoverOf(scene: Scene): HudState['pipeHover'] {
  const { camera, sim } = scene
  const tile = camera.pointerTile
  if (!tile || !camera.pointer) return null
  const entity = sim.occupancy.at(tile.x, tile.y)
  if (entity === undefined || sim.world.get(entity, Owner)?.player !== scene.player) return null
  const type = sim.world.get(entity, Building)?.type
  if (type === undefined || !isPipe(type)) return null
  const network = networkOf(sim, entity)
  if (!network) return null
  const buildings = network.buildings.filter((member) => !isPipe(sim.world.get(member, Building)!.type)).length
  const { items, capacity } = stockOfZone(sim, network)
  return { buildings, stock: stacksOf(items), capacity, x: camera.pointer.x, y: camera.pointer.y }
}

/** Срок для интерфейса: минуты и секунды, от часа — часы и минуты. */
const clockOf = (seconds: number) => {
  const pad = (value: number) => String(value).padStart(2, '0')
  const whole = Math.round(seconds)
  return whole < 3600 ? `${Math.floor(whole / 60)}:${pad(whole % 60)}` : `${Math.floor(whole / 3600)}:${pad(Math.floor((whole % 3600) / 60))}:${pad(whole % 60)}`
}

/** Цена темы исследования для интерфейса. */
const topicCost = ({ cost, materials, seconds }: TopicPrice) => ({ cost, materials: exactOf(materials), time: clockOf(seconds) })

/** Остаток защиты новичка для интерфейса: вверх до десятка секунд, чтобы он не перерисовывался каждый тик. */
function protectionOf({ sim, player }: Scene) {
  const seconds = Math.ceil((protectionLeft(sim, player) * sim.time.step) / 10) * 10
  return seconds > 0 ? clockOf(seconds) : ''
}

/** Собирает состояние интерфейса из симуляции и выделения. */
export function readHud(scene: Scene): HudState {
  const { sim, player, selection } = scene
  const { world } = sim
  const credits = creditsOf(sim, player)

  const counts = new Map<UnitType, number>()
  let building: BuildingType | null = null
  let site: HudState['site'] = null
  let demolish: HudState['demolish'] = null
  let level: HudState['level'] = null
  let research: HudState['research'] = null
  let silo: HudState['silo'] = null
  /** Готовое на улучшение всех выбранных вместе. */
  const upgradeMaterials: Amounts = {}
  let deposit: HudState['deposit'] = null
  let stored: HudState['stored'] = null
  let reserve: HudState['reserve'] = null
  let refinery: HudState['refinery'] = null
  let assembly: HudState['assembly'] = null
  let switchable: HudState['switchable'] = null
  let buildingCount = 0
  let mixed = false
  let firstKind: BuildingType | undefined
  let ammo: HudState['ammo'] = null
  let rods: HudState['rods'] = null
  let materials: HudState['materials'] = null
  let cargo: HudState['cargo'] = null
  let haul: HudState['haul'] = null
  let tactics: HudState['tactics'] = null
  let trade: HudState['trade'] = null
  let power: HudState['power'] = null
  let battery: HudState['battery'] = null
  let health: number | null = null
  let shield: HudState['shield'] = null
  let repair = 0
  let starved = false
  let idle = false
  let loose = false
  const zones = zoneEconomies(sim, player)
  const producers: Entity[] = []
  let armed = 0
  let unitHealth = 0
  let unitCount = 0
  for (const entity of selection) {
    const unit = world.get(entity, Unit)
    if (unit) {
      counts.set(unit.type, (counts.get(unit.type) ?? 0) + 1)
      unitHealth += world.get(entity, Health)?.value ?? 1
      unitCount++
      if (canFight(sim, entity)) {
        armed++
        const stance = stanceOf(sim, entity)
        tactics ??= { units: [], stance, patrolling: 0, picking: scene.patrolling }
        tactics.units.push(entity)
        if (tactics.stance !== stance) tactics.stance = null
        if (world.get(entity, Tactics)?.patrol.length) tactics.patrolling++
      }
    }
    building = world.get(entity, Building)?.type ?? world.get(entity, Site)?.type ?? building
    if (building !== null && buildingSpec(building).extract) {
      const position = world.get(entity, Position)!
      const spot = depositAt(sim, position.x, position.y)
      if (spot) deposit = { kind: spot.kind, left: Math.floor(reserveLeft(sim, position.x, position.y)) }
    }
    if (building !== null && ((buildingSpec(building).power ?? 0) > 0 || buildingSpec(building).battery)) {
      // Недостроенная и отрезанная от зоны электростанция ни в какую зону не входит — показывать нечего.
      const zone = gridsOf(sim, player).findIndex((zone) => zone.buildings.includes(entity))
      if (zone >= 0) power = { produced: round(zones[zone].produced), demand: zones[zone].demand, stored: round(zones[zone].stored) }
    }
    const cell = building !== null && world.get(entity, Accumulator)
    if (cell && building !== null) battery = { charge: Math.floor(cell.charge), capacity: buildingSpec(building).battery!.capacity, flow: round(cell.flow) }
    const hauler = world.get(entity, Hauler)
    const inventory = world.get(entity, Inventory)
    if (hauler && inventory) {
      cargo ??= { items: [], capacity: 0, bound: 0, busy: 0 }
      const items: Amounts = Object.fromEntries(cargo.items.map(({ resource, amount }) => [resource, amount]))
      for (const resource of GOODS) items[resource] = (items[resource] ?? 0) + amountOf(inventory, resource)
      cargo.items = stacksOf(items)
      cargo.capacity += inventory.capacity
      if (hauler.mine >= 0) cargo.bound++
      else if (hauler.from >= 0 || hauler.to >= 0 || hauler.route.length || hauler.serve.length) cargo.busy++
      haul ??= { units: [], routed: 0, served: 0, filter: [...hauler.filter], routing: scene.routing ? scene.routing.length : null, serving: scene.serving ? scene.serving.length : null }
      haul.units.push(entity)
      if (hauler.route.length) haul.routed++
      if (hauler.serve.length) haul.served++
    }
    const built = world.get(entity, Building)
    const ready = !!built && !world.has(entity, Site)
    const spec = built && buildingSpec(built.type)
    // У турели на складе только патроны: их показывает своя строка.
    // Буфер производителя — не склад: показываем его, только пока там лежат материалы заказа.
    const buffer = !!spec && !spec.inventory
    if (ready && inventory && !spec!.ammo && !(buffer && loadOf(inventory) <= 0)) {
      // У переработок и цехов у каждого груза своё место: показываем, сколько в каждом из скольких.
      const slots = inventory.accepts.length && inventory.accepts.every((good) => inventory.limits[good] !== undefined)
        ? inventory.accepts.map((good) => ({ resource: good, amount: Math.floor(amountOf(inventory, good)), of: inventory.limits[good]! }))
        : null
      stored = { items: stacksOf(inventory.items), capacity: inventory.capacity, store: !!spec!.stores, buffer, slots }
    }
    if (ready && inventory && spec!.stores && isOwn(sim, player, entity)) {
      reserve ??= { buildings: [], amount: inventory.reserve, capacity: inventory.capacity }
      reserve.buildings.push(entity)
      reserve.capacity = Math.min(reserve.capacity, inventory.capacity)
    }
    if (ready && spec!.refines) refinery = { ore: spec!.refines, intake: REFINE_RATE * levelFactor(built.level) }
    const gain = ready && isOwn(sim, player, entity) ? levelGain(built.type) : undefined
    if (built && gain) {
      level ??= { level: built.level, max: MAX_LEVEL, gain, factor: levelFactor(built.level), next: null, raising: null }
      const upgrade = world.get(entity, Upgrade)
      if (upgrade) {
        level.raising ??= { buildings: [], progress: round(Math.min(1, upgrade.progress / upgradeTicks(built.type, built.level, sim.time.step, upgrade.scale))) }
        level.raising.buildings.push(entity)
      } else if (isUpgradable(sim, player, entity)) {
        const price = upgradeCost(built.type, built.level, sim.rules.upgradeScale)
        level.next ??= { buildings: [], level: built.level + 1, factor: levelFactor(built.level + 1), cost: 0, materials: [], lack: null, topic: levelTopic(built.level) }
        level.next.buildings.push(entity)
        level.next.cost += price.credits
        for (const [resource, amount] of entriesOf(price.materials)) upgradeMaterials[resource] = (upgradeMaterials[resource] ?? 0) + amount
        const lack = upgradeLack(sim, player, entity)
        if (lack === 'research' || (lack === 'materials' && !level.next.lack)) level.next.lack = lack
      }
    }
    if (!research && built && ready && isLab(sim, player, entity)) {
      const running = world.get(entity, Research)
      research = {
        lab: entity,
        topics: TOPICS.map((topic) => ({
          topic,
          ...topicCost(topicPrice(sim, player, topic)),
          lack: topicLack(sim, player, entity, topic) ?? null,
          after: 'after' in RESEARCH[topic] ? (RESEARCH[topic].after as Topic) : null,
        })),
        current: running ? { topic: running.topic as Topic, progress: round(Math.min(1, running.progress / Math.max(1, running.ticks))) } : null,
      }
    }
    const warhead = spec?.warhead
    if (!silo && warhead && ready && isOwn(sim, player, entity)) {
      const state = world.get(entity, Silo)
      if (state) {
        const left = launchLeft(sim, entity)
        silo = {
          entity,
          stage: left !== undefined ? 'launching' : state.ready ? 'ready' : state.ticks > 0 ? 'assembling' : 'empty',
          progress: state.ticks > 0 ? round(Math.min(1, state.progress / state.ticks)) : 0,
          launch: left === undefined ? 0 : Math.ceil(left * sim.time.step),
          cost: warhead.cost,
          materials: exactOf(warhead.materials),
          time: clockOf(warhead.seconds),
          lack: warheadLack(sim, player, entity) ?? null,
          aiming: scene.aiming === entity,
        }
      }
    }
    if (ready && spec!.ammo && inventory) ammo = { have: Math.floor(amountOf(inventory, 'ammo')), capacity: inventory.capacity }
    if (ready && spec!.burns && inventory && isOwn(sim, player, entity)) {
      rods = { have: Math.floor(amountOf(inventory, spec!.burns.good)), capacity: inventory.capacity, burning: isBurning(sim, entity) }
    }
    if (ready && building !== null && isSwitchable(building) && !world.has(entity, Assembly) && isOwn(sim, player, entity)) {
      switchable ??= { buildings: [], on: false }
      switchable.buildings.push(entity)
      switchable.on ||= !world.has(entity, Off)
    }
    const assembling = ready ? world.get(entity, Assembly) : undefined
    if (assembling && isOwn(sim, player, entity)) {
      const spec = PRODUCT_SPECS[assembling.recipe]
      assembly = {
        plant: entity,
        recipe: assembling.recipe,
        on: assembling.on,
        progress: round(Math.min(1, assembling.progress / cycleSeconds(assembling.recipe))),
        have: Math.floor(productStock(sim, entity)),
        full: !hasRoom(sim, entity),
        inputs: exactOf(spec.recipe),
        yield: spec.yield,
        seconds: cycleSeconds(assembling.recipe),
      }
    }
    const wanted = materialsFor(sim, entity)
    if (wanted && isOwn(sim, player, entity)) {
      const site = world.has(entity, Site)
      materials = {
        items: entriesOf(wanted).map(([resource, need]) => ({ resource, need, have: Math.floor(Math.min(need, spareOf(sim, entity, resource))) })),
        waiting: site ? awaitsMaterials(sim, entity) : entriesOf(wanted).some(([resource, need]) => spareOf(sim, entity, resource) < need - 1e-9),
      }
    }
    if (ready && buildingSpec(built!.type).trades && isOwn(sim, player, entity)) {
      const zone = zoneWith(sim, player, entity)
      const order = world.get(entity, Trade)
      const items = zone ? stockOfZone(sim, zone).items : {}
      const ferry = world.get(entity, Transfer)
      trade = {
        port: entity,
        offers: resourceStacksOf(items).map(({ resource, amount }) => ({ resource, available: amount, price: RESOURCE_SPECS[resource].price })),
        purchases: RESOURCES.filter(isOrbital).map((resource) => ({
          resource,
          price: buyPrice(resource),
          room: Math.floor(roomFor(world.get(entity, Inventory)!, resource)),
        })),
        order: order
          ? {
              resource: order.resource,
              wanted: Math.round(order.wanted),
              delivered: Math.floor(deliveredTo(sim, entity)),
              price: order.buy ? buyPrice(order.resource) : RESOURCE_SPECS[order.resource].price,
              flight: order.total ? round(1 - order.left / order.total) : null,
              buy: order.buy,
            }
          : null,
        transfer: ferry
          ? {
              to: ferry.to,
              credits: Math.floor(ferry.credits),
              goods: stacksOf(ferry.goods),
              flight: ferry.total ? round(Math.min(1, 1 - ferry.left / ferry.total)) : 1,
            }
          : null,
      }
    }
    const record = world.get(entity, Health)
    const left = record?.value ?? 1
    const max = record?.max ?? 1
    if (built && left < max) {
      health = round(left / max)
      repair = repairCostOf(BUILDINGS[built.type].cost, left, sim.rules.repairCost, max)
    }
    const dome = built && world.get(entity, Shield)
    const domeSpec = built && buildingSpec(built.type).shield
    if (dome && domeSpec) {
      shield = { charge: Math.floor(dome.charge), capacity: domeSpec.capacity, down: Math.max(0, Math.ceil((dome.down - sim.time.tick) * sim.time.step)) }
    }
    if (built && powerStates(sim).get(entity) === 'starved') starved = true
    if (built && coreless(sim).has(entity)) idle = true
    if (unlinked(sim, player, entity)) loose = true
    const work = world.get(entity, Site)
    if (work) {
      const progress = round(Math.min(1, work.progress / siteTicks(work.type, sim.time.step)))
      site = { entity, started: world.has(entity, Building), blocked: isSiteBlocked(sim, entity), progress, demolish: work.demolish }
    }
    if (canDemolish(sim, player, entity)) {
      demolish ??= { buildings: [], refund: 0 }
      demolish.buildings.push(entity)
      demolish.refund += refundOf(built!.type, built!.level)
    }
    const kind = world.get(entity, Building)?.type ?? world.get(entity, Site)?.type
    if (kind) {
      buildingCount++
      firstKind ??= kind
      mixed ||= firstKind !== kind
    }
    if (producibleBy(sim, entity).length) producers.push(entity)
  }

  // Кнопки стройки показывают энергию для первой зоны: в какую попадёт здание, до выбора места неизвестно.
  const economy = zones[0] ?? economyOf(sim, player)
  const stock = stockOf(sim, player)
  // Главные здания всех игроков — одним обходом: по ним панель союзников ведёт камеру. В локальной игре игроков нет.
  const bases = 'players' in sim ? coreBasesOf(sim) : new Map<number, { x: number; y: number }>()
  // Кого из выбранных можно отдать союзнику: по ним меню юнитов и показывает кнопку подарка.
  const gift = giftable(sim, player, [...selection])
  if (level?.next) {
    level.next.materials = exactOf(upgradeMaterials)
    if (credits < level.next.cost && level.next.lack !== 'research') level.next.lack = 'credits'
  }
  const state: HudState = {
    credits,
    hover: hoverOf(scene),
    pipeHover: pipeHoverOf(scene),
    rewards: [...rewardsOf(sim, player)],
    loaded: sim.time.tick > 0,
    defeated: sim.time.tick > 0 && isDefeated(sim, player),
    // Минуты — десятками: интерфейс перерисовывается, только когда состояние изменилось.
    clock: `${String(Math.floor(scene.weather.hour)).padStart(2, '0')}:${Math.floor((scene.weather.hour % 1) * 6)}0`,
    storm: scene.weather.precipitation > 0.05,
    alerts: (scene.alerts?.current() ?? []).map(({ kind, text, x, y, at }) => ({ kind, text, x, y, at })),
    // Список игроков есть только у копии мира с сервера.
    chat: 'say' in sim ? (sim as Replica).chat.map(({ player, name, text, at, system, whisper }) => ({ name, own: player === scene.player, text: textOf(text), at, system: !!system, whisper })) : null,
    commands: 'commands' in sim ? (sim as Replica).commands : [],
    admin: 'admin' in sim && (sim as Replica).admin,
    editor: 'editor' in sim && (sim as Replica).editor,
    picking: !!(scene.routing || scene.serving),
    players: 'players' in sim
      ? (sim as Replica).players.map(({ player, name, online, safe }) => ({
          player,
          name,
          own: player === scene.player,
          online,
          safe: !!safe,
          relation: relationWith(sim, scene.player, player),
          credits: creditsOf(sim, player),
          base: bases.get(player) ?? null,
          head: allyHeadIn(sim.world, player),
        }))
      : [],
    protection: protectionOf(scene),
    income: round(economyOf(sim, player).income),
    power,
    battery,
    health,
    shield,
    repair,
    starved,
    coreless: idle,
    unlinked: loose,
    units: UNIT_TYPES.filter((type) => counts.has(type)).map((type) => ({ type, count: counts.get(type)! })),
    gift: gift.length ? gift : null,
    army: unitCount ? { armed, health: round(unitHealth / unitCount) } : null,
    building,
    deposit,
    stock: stock.capacity ? { items: stacksOf(stock.items), capacity: stock.capacity } : null,
    stored,
    reserve,
    refinery,
    assembly,
    switchable,
    buildings: buildingCount,
    mixed,
    ammo,
    rods,
    materials,
    trade,
    cargo,
    haul,
    tactics,
    site,
    demolish,
    level,
    research,
    silo,
    construction: counts.has('builder')
      ? {
          placing: scene.placing,
          paving: pavingOf(scene),
          options: BUILDABLE.filter((type) => isUnlocked(sim, player, type)).map((type) => ({
            building: type,
            cost: BUILDINGS[type].cost,
            affordable: credits >= BUILDINGS[type].cost,
            power: powerOf(type, economy),
            materials: exactOf(buildingSpec(type).materials ?? {}),
          })),
        }
      : null,
    conversion: null,
    production: null,
  }
  // Производителей несколько — заказ получает наименее занятый, если все они здания одного вида.
  const type = world.get(producers[0], Building)?.type
  if (!producers.length || (producers.length > 1 && producers.some((entity) => world.get(entity, Building)?.type !== type || !type))) return state
  const entity = producers.reduce((best, entity) => (world.get(entity, Producer)!.queue.length < world.get(best, Producer)!.queue.length ? entity : best))
  const many = producers.length > 1
  const converting = world.get(entity, Converting)
  const isUnit = world.has(entity, Unit)
  if (!many && (isUnit || world.get(entity, Building)?.type === CORE)) state.conversion = {
    kind: isUnit ? 'deploy' : 'pack',
    command: isUnit ? { type: 'deploy', unit: entity } : { type: 'pack', building: entity },
    possible: isUnit ? canDeploy(sim, player, entity) : canPack(sim, player, entity),
    progress: converting ? round(1 - converting.left / converting.total) : null,
    blocked: isDeployBlocked(sim, entity),
    cancel: converting && isUnit ? { type: 'cancelDeploy', unit: entity } : null,
  }

  // Видна и отменяется очередь самого занятого из выбранных: заказы, наоборот, идут наименее занятому.
  const busiest = producers.reduce((best, entity) => (world.get(entity, Producer)!.queue.length > world.get(best, Producer)!.queue.length ? entity : best))
  const producer = world.get(busiest, Producer)!
  const [first] = producer.queue
  state.production = {
    producer: entity,
    busiest,
    count: producers.length,
    queue: [...producer.queue],
    progress: first ? round(Math.min(1, producer.progress / buildTicks(first, sim.time.step))) : 0,
    full: world.get(entity, Producer)!.queue.length >= QUEUE_LIMIT,
    building: !isUnit,
    rally: producer.rally.length > 0,
    options: producibleBy(sim, entity).map((unit) => ({
      unit,
      cost: UNITS[unit].cost,
      affordable: credits >= UNITS[unit].cost,
      materials: exactOf(unitSpec(unit).materials ?? {}),
      missing: missingRequirements(sim, player, unit),
      research: missingResearch(sim, player, unit) ?? null,
    })),
  }
  return state
}

/** Укладка покрытия, которую игрок тянет мышью: инструмент, цена и сколько тайлов ляжет. */
function pavingOf(scene: Scene): NonNullable<HudState['construction']>['paving'] {
  if (!scene.paving) return null
  const stroke = paveStrokeOf(scene)
  return { tool: scene.paving, cost: stroke?.cost ?? 0, tiles: stroke ? stroke.allowed.filter(Boolean).length : 0, short: stroke ? stroke.short.filter(Boolean).length : 0 }
}

/** Главные здания всех игроков: центр здания, туда ведёт клик по панели союзников. Все одним обходом зданий. */
function coreBasesOf(sim: Sim) {
  const bases = new Map<number, { x: number; y: number }>()
  const { width, height } = BUILDINGS[CORE]
  for (const [, position, building, owner] of sim.world.query(Position, Building, Owner)) {
    if (building.type === CORE && !bases.has(owner.player)) bases.set(owner.player, { x: position.x + width / 2, y: position.y + height / 2 })
  }
  return bases
}

/** Что между игроком other и нами — для списка игроков. */
function relationWith(sim: Sim, player: number, other: number) {
  for (const [, info] of sim.world.query(Player)) {
    if (info.id !== player) continue
    return info.allies.includes(other) ? 'ally' as const : info.asked.includes(other) ? 'allyAsked' as const : info.offered.includes(other) ? 'allyOffered' as const : null
  }
  return null
}
