import type { Entity, World } from '@kharos/ecs'
import buildings from '@kharos/base/buildings.json'
import { Terrain, isBuildable, isCliffFoot, terrainAt, tileKey } from '@kharos/shared/terrain'
import { isOwn } from './common'
import { Accumulator, Assembly, Building, Gate, Health, Interceptor, Inventory, Reactor, Repair, Owner, Position, Producer, Shield, Silo, Site, Wanted } from './components'
import { equipStorage, type BeamSpec } from './inventory'
import type { Amounts, Good, Ore, Product } from './resources'
import type { Sim } from './sim'
import { mountTurrets, type MountSpec } from './turrets'
import { isPaved, isRoadway } from './paved'
import { hasResearch, type Topic } from './topics'
import { TRACE_CELL } from './traces'
import type { UnitType } from './units'

/** Что симуляция знает о виде здания. Как оно выглядит, знает клиент: см. game/buildings/buildingArt.ts. */
export interface BuildingSpec {
  /** Сколько тайлов здание занимает на земле. */
  width: number
  height: number
  /** Цена в кредитах. Из неё же считается время стройки: см. BUILD_RATE. */
  cost: number
  /**
   * Радиус собственной зоны строительства в тайлах от центра здания. Такое здание начинает зону само,
   * и ставить его можно вне своих зон; остальные здания только расширяют зону, в которой стоят.
   */
  zone?: number
  /** На сколько тайлов расширяет зону готовое здание; без поля — общий EXPAND_RADIUS, 0 — не расширяет. */
  expand?: number
  /** Оборонительную постройку можно ставить на песок, где она получает только SAND_DURABILITY прочности. */
  defense?: boolean
  /** Абсолютная прочность; без поля считается из цены здания, см. buildingHp в combat.ts. */
  hp?: number
  /** Турели на здании: каждая — отдельная прикреплённая сущность, см. turrets.ts. */
  mounts?: MountSpec[]
  /** Сколько тайлов от центра видит готовое здание; без поля — на BUILDING_SIGHT от края. См. vision.ts. */
  sight?: number
  /** Ворота: открыты, пока рядом нет чужих, см. gates.ts. */
  gate?: boolean
  /** На сколько тайлов от центра здание разведывает вспышками, см. radar.ts; с уровнем радиус растёт. */
  scan?: number
  /** Энергия: больше нуля — вырабатывает, меньше — потребляет. */
  power?: number
  /**
   * Добывает из месторождения под собой в свой склад, пока там есть место. Что и как быстро — решает
   * месторождение, см. DEPOSIT_KINDS. Ставится только на месторождение.
   */
  extract?: boolean
  /** Материалы на стройку вдобавок к кредитам: их привозят на площадку грузовики из хранилищ зоны. */
  materials?: Amounts
  /** Склад: сколько груза помещается в здании. См. inventory.ts. */
  inventory?: number
  /** Какой груз принимает склад здания: без поля — любой. Хранилищам кладут готовое, переработке — руду. */
  accepts?: readonly Good[]
  /** Предел по каждому виду груза вдобавок к общему объёму склада: см. roomFor. */
  limits?: Amounts
  /** Склад здания — хранилище: в него свозят готовое, из него берут на нужды зоны и на продажу. */
  stores?: boolean
  /**
   * Перерабатывает эту руду из своего склада в её ресурс со скоростью REFINE_RATE. Принимает руду от шахт,
   * а готовое отдаёт по общим заявкам. На каждую руду — своё здание. См. refining.ts.
   */
  refines?: Ore
  /**
   * Завод изделия: собирает его из ресурсов своего склада по рецепту. Новый завод стоит выключенным, пока игрок
   * его не включит. Сырьё заказывает у своей зоны, готовое отдаёт по заявкам и вывозит в хранилища. См. assembly.ts.
   */
  assembles?: Product
  /**
   * Турели здания стреляют боеприпасами с его склада: каждый выстрел тратит WeaponSpec.ammo, пустой склад —
   * турель молчит. Склад здание заказывает у зоны само; достроенное стоит пустым, пока не подвезут.
   */
  ammo?: boolean
  /** Транспортный луч: им здание отдаёт ресурсы со своего склада или забирает на него. См. inventory.ts. */
  beam?: BeamSpec
  /**
   * Станция поставки: держит на складе груз машин снабжения и своим лучом грузит их, когда они рядом. Склад заказывает
   * у зоны сама, как турель патроны; по общим заявкам не отдаёт — это запас армии. См. supply.ts.
   */
  depot?: boolean
  /**
   * Аэродром: над ним авиация пополняет магазины из хранилищ его сети. В кругах сети, но не над аэродромом, ей
   * не пополниться — пустая летит сюда или к машине снабжения. См. supply.ts.
   */
  rearms?: boolean
  /**
   * Станция запроса: держит у себя то, что заказал игрок (Wanted), — грузовики и трубы довозят недостающее, лишнее
   * увозят в хранилища. Отдаёт запас нуждам своей сети, как хранилище, но не другим станциям. См. logistics.ts.
   */
  requester?: boolean
  /** Через это здание продают ресурсы: грузовики свозят их сюда из хранилищ его зоны, см. trade.ts. */
  trades?: boolean
  /** Каких юнитов здание производит, когда достроено. Потребитель энергии при её нехватке производит медленнее. */
  produces?: UnitType[]
  /** Доход в кредитах в секунду. У потребителя энергии он падает вместе с её нехваткой. */
  income?: number
  /**
   * Таким зданиям тесно в одной зоне: каждое следующее просит на одну норму энергии больше предыдущего.
   * Поэтому отдача от них падает, и ставить их без счёта невыгодно.
   */
  crowding?: boolean
  /**
   * Готовое здание чинит повреждённые свои здания и юниты в этом радиусе, в тайлах от своего центра до края цели,
   * а пехоте там помогает поправляться быстрее (STATION_REGEN). Не строит. Работает с долей энергии своей зоны.
   */
  repair?: number
  /**
   * Труба сети: наземная (surface) связывает соседние по стороне трубы и здания; колодец (well) — ещё и с ближайшим
   * своим колодцем по прямой не дальше UNDERGROUND_REACH, подземным отрезком. См. zones.ts и pipes.ts.
   */
  pipe?: 'surface' | 'well'
  /** Здание не подключается к сети труб: стене нечего брать и отдавать. */
  isolated?: boolean
  /** Здание ведёт исследования, см. research.ts. */
  researches?: boolean
  /** Генератор щита: радиус купола от центра, ёмкость в единицах прочности и рост в секунду при полной энергии (shields.ts). */
  shield?: { radius: number; capacity: number; regen: number }
  /**
   * Накопитель энергии: копит излишек выработки своей зоны и отдаёт его, когда энергии не хватает. capacity — сколько
   * держит, в единицах энергии на секунду; rate — сколько энергии в секунду берёт или отдаёт. См. storeEnergy в income.ts.
   */
  battery?: { capacity: number; rate: number }
  /**
   * Реактор: вырабатывает power, только пока горит стержень, — один на seconds секунд, пока включён, выключенный
   * не жжёт. Стержни держит на своём складе и заказывает у зоны сам, как турель патроны. См. reactors.ts.
   */
  burns?: { good: Product; seconds: number }
  /**
   * Разрушенное с топливом внутри взрывается: урон damage в центре до нуля на краю radius, по всем без разбора.
   * Разбор строителями не взрывает. См. meltdown в reactors.ts.
   */
  meltdown?: { radius: number; damage: number }
  /**
   * Ракетная шахта: собирает боеголовку — cost кредитов и materials из сети, seconds секунд при полной энергии — и пускает
   * её в любую точку карты через launch секунд после приказа. Ракета летит speed тайлов в секунду; взрыв blast — урон
   * damage до core тайлов от точки удара, к radius спадает до нуля. См. silos.ts.
   */
  warhead?: {
    cost: number
    materials: Amounts
    seconds: number
    launch: number
    speed: number
    blast: { radius: number; core: number; damage: number }
  }
  /**
   * Перехватчик: собирает ракеты-перехватчики по одной — cost кредитов и materials из сети, seconds секунд при полной
   * энергии — сам, пока их меньше capacity. Каждая сбивает одну чужую ядерную ракету, чья точка удара не дальше radius
   * тайлов от его центра, когда та входит в этот круг; ракета-перехватчик летит speed тайлов в секунду. См. silos.ts.
   */
  intercept?: { radius: number; cost: number; materials: Amounts; seconds: number; speed: number; capacity: number }
  /**
   * Дерево технологий: здание появляется в списке строителя, когда у игрока стоят готовыми все здания из этого списка.
   * Снесли нужное — новое такое не заложить, пока его не восстановят; построенное остаётся. Главное здание строители
   * не ставят: его привозит MCV. Покрытия доступны всегда. Строки (BuildingType); имена сверяет тест.
   */
  requires?: readonly string[]
  /**
   * Исследование (Topic), которое открывает здание вдобавок к requires: без него строители его не ставят. Переработку
   * открывает исследование её руды, цех — его изделия, армию — свои темы; шахту, космопорт и накопитель — открытия.
   * Трубы и колодцы строители в меню не берут, их тянут: темы проверяет протяжка, см. pipeStroke.
   */
  research?: string
  /** Место в меню строителя: без поля строители это здание не возводят. См. BUILDABLE. */
  menu?: number
  /** Пояснение к записи для того, кто правит данные; игре не нужно. */
  about?: string
}

/** На сколько тайлов от своего центра расширяет зону готовая труба или колодец. */
export const PIPE_REACH = 3
/** Дальше скольких тайлов по прямой колодец не видит парный: подземный отрезок длиннее не проложить. */
export const UNDERGROUND_REACH = 10

/** Сколько ресурсов помещается в производящем юнитов здании: материалы на очередной заказ. */
const PRODUCER_HOLD = 100

/** Сколько кредитов цены здания один строитель возводит за секунду: здание за 300 строится 15 секунд. */
export const BUILD_RATE = 20

/** Доля обычной прочности у оборонительной постройки на песке. */
export const SAND_DURABILITY = 0.7

/**
 * Виды зданий — данные мода base (packages/base/data/buildings.json). Там же у каждого вида дерево технологий
 * (requires), открывающее его исследование (research) и место в меню строителя (menu).
 */
export const BUILDINGS = buildings as Record<keyof typeof buildings, BuildingSpec>

export type BuildingType = keyof typeof BUILDINGS
/** Описание вида здания со всеми необязательными полями. */
export const buildingSpec = (type: BuildingType): BuildingSpec => BUILDINGS[type]
/** Главное здание — Settlement Core: в него разворачивается MCV. */
export const CORE: BuildingType = 'command'
export const BUILDING_TYPES = Object.keys(BUILDINGS) as BuildingType[]
/** Что возводят строители, в порядке меню (BuildingSpec.menu). Остальные здания появятся вместе с тем, для чего они нужны. */
export const BUILDABLE: BuildingType[] = BUILDING_TYPES.filter((type) => BUILDINGS[type].menu !== undefined).sort((a, b) => BUILDINGS[a].menu! - BUILDINGS[b].menu!)

/** Выше этого уровня здания не улучшают. */
export const MAX_LEVEL = 3
/** На какую долю от первого уровня каждый следующий поднимает то, что здание делает: второй — в полтора раза, третий — вдвое. */
export const LEVEL_BONUS = 0.5
/** Во сколько раз здание этого уровня сильнее первого. */
export const levelFactor = (level: number) => 1 + LEVEL_BONUS * (level - 1)
/** Уровень здания; у стройки и у того, что не здание, — первый. */
export const levelOf = (world: World, entity: Entity) => world.get(entity, Building)?.level ?? 1

/**
 * Что уровень поднимает у здания: добычу, скорость переработки, ёмкость, трюм и время рейса, выработку или радиус
 * разведки — у радара вместе с ним растёт и потребление.
 */
export type LevelGain = 'extract' | 'refine' | 'store' | 'trade' | 'power' | 'scan'

/** Что даёт уровень зданию этого вида; undefined — его не улучшают. */
export function levelGain(type: BuildingType): LevelGain | undefined {
  const spec = buildingSpec(type)
  if (spec.extract) return 'extract'
  if (spec.refines) return 'refine'
  if (spec.stores) return 'store'
  if (spec.trades) return 'trade'
  if (spec.scan) return 'scan'
  // Реактор не улучшают: выработка с уровнем росла бы, а расход стержней — нет.
  return (spec.power ?? 0) > 0 && !spec.burns ? 'power' : undefined
}

/** Какую долю цены здания стоит в кредитах подъём с первого уровня; с каждого следующего — на столько же больше. */
const UPGRADE_SHARE = 0.5
/** На сколько кредитов цены здания идёт один стройблок за уровень и один компонент на верхних уровнях. */
const UPGRADE_BLOCKS = 30
const UPGRADE_PARTS = 60

/**
 * Цена подъёма здания с уровня level на следующий: кредиты и готовое. Стройблоки нужны всегда, с третьего уровня —
 * ещё и компоненты: первый подъём по силам базе с одним цехом, дальше нужна вся цепочка изделий. scale — во сколько
 * раз дороже по правилам мира (Rules.upgradeScale); единица — цена матча.
 */
export function upgradeCost(type: BuildingType, level: number, scale = 1): { credits: number; materials: Amounts } {
  const { cost } = buildingSpec(type)
  const materials: Amounts = { blocks: Math.ceil(Math.ceil(cost / UPGRADE_BLOCKS) * level * scale) }
  if (level >= 2) materials.parts = Math.ceil(Math.ceil(cost / UPGRADE_PARTS) * (level - 1) * scale)
  return { credits: Math.round(cost * UPGRADE_SHARE * level * scale), materials }
}

/**
 * Сколько кредитов вложено в уровни здания сверх его цены — по цене матча, без Rules.upgradeScale: по какой цене
 * поднимали на самом деле, здание не помнит, а правила между запусками сервера меняются. Разбор возвращает долю этого.
 */
export function upgradeSpent(type: BuildingType, level: number) {
  let spent = 0
  for (let below = 1; below < level; below++) spent += upgradeCost(type, below).credits
  return spent
}

/** Подгоняет склад здания под его уровень: хранилище и трюм космопорта с уровнем вмещают больше. */
export function applyLevel(world: World, entity: Entity) {
  const building = world.get(entity, Building)
  const inventory = world.get(entity, Inventory)
  if (!building || !inventory) return
  const spec = buildingSpec(building.type)
  if ((spec.stores || spec.trades) && spec.inventory) inventory.capacity = Math.round(spec.inventory * levelFactor(building.level))
}

/** Что здание само заказывает на свой склад: патроны турели и станции поставки, стержни реактора; undefined — ничего. */
export const feedOf = (spec: BuildingSpec): Product | undefined => (spec.ammo || spec.depot ? 'ammo' : spec.burns?.good)

/** Выключается ли здание игроком: потребитель энергии и реактор — тот бережёт стержни. */
export const isSwitchable = (type: BuildingType) => (buildingSpec(type).power ?? 0) < 0 || !!buildingSpec(type).burns

/** Труба ли это или колодец. */
export const isPipe = (type: BuildingType) => !!buildingSpec(type).pipe

/** Дерево технологий по видам зданий: см. BuildingSpec.requires. */
export const REQUIRES = Object.fromEntries(BUILDING_TYPES.flatMap((type) => (BUILDINGS[type].requires ? [[type, BUILDINGS[type].requires]] : []))) as Partial<
  Record<BuildingType, readonly BuildingType[]>
>

/** Какое исследование открывает здание: см. BuildingSpec.research. */
export const RESEARCH_FOR = Object.fromEntries(BUILDING_TYPES.flatMap((type) => (BUILDINGS[type].research ? [[type, BUILDINGS[type].research]] : []))) as Partial<
  Record<BuildingType, Topic>
>

/** Каких готовых зданий из списка у игрока нет. */
export function missingBuildings(sim: Sim, player: number, needs: readonly BuildingType[] | undefined): BuildingType[] {
  if (!needs?.length) return []
  const have = new Set<BuildingType>()
  for (const [entity, building] of sim.world.query(Building)) if (needs.includes(building.type) && isReady(sim, player, entity)) have.add(building.type)
  return needs.filter((type) => !have.has(type))
}

/** Открыто ли игроку здание: строится строителями и все нужные для него здания стоят. */
export const isUnlocked = (sim: Sim, player: number, type: BuildingType) =>
  BUILDABLE.includes(type) && (!sim.rules.techTree || (missingBuildings(sim, player, REQUIRES[type]).length === 0 && hasResearch(sim, player, RESEARCH_FOR[type])))

/** Хранилища — по одному на каждое готовое. */
export const STORES = BUILDING_TYPES.filter((type) => buildingSpec(type).stores)
/** Хранилище, которое принимает этот груз; у руды его нет. */
export const storeFor = (good: Good) => STORES.find((type) => buildingSpec(type).accepts?.includes(good))

/** Какие тайлы заняты зданиями. Обновляется сам: следит за появлением и исчезновением зданий в мире. */
export interface Occupancy {
  /** Здание, занимающее тайл, или undefined. */
  at(x: number, y: number): Entity | undefined
  /**
   * Здания, основание которых задевает ячейку стороной TRACE_CELL тайлов; cell — tileKey от номера ячейки, как
   * у Vision.seenCells. Так здания в обзоре игрока находят по ячейкам обзора, а не перебором всех.
   */
  inCell(cell: number): ReadonlySet<Entity> | undefined
  destroy(): void
}

export function createOccupancy(world: World): Occupancy {
  const tiles = new Map<number, Entity>()
  const cells = new Map<number, Set<Entity>>()
  const stop = world.observe([Position, Building], (entity, position, building) => {
    const { width, height } = BUILDINGS[building.type]
    const keys: number[] = []
    for (let y = position.y; y < position.y + height; y++) {
      for (let x = position.x; x < position.x + width; x++) keys.push(tileKey(x, y))
    }
    for (const key of keys) tiles.set(key, entity)
    const spots: number[] = []
    for (let y = Math.floor(position.y / TRACE_CELL); y <= Math.floor((position.y + height - 1) / TRACE_CELL); y++) {
      for (let x = Math.floor(position.x / TRACE_CELL); x <= Math.floor((position.x + width - 1) / TRACE_CELL); x++) spots.push(tileKey(x, y))
    }
    for (const spot of spots) {
      const found = cells.get(spot)
      if (found) found.add(entity)
      else cells.set(spot, new Set([entity]))
    }
    return () => {
      for (const key of keys) if (tiles.get(key) === entity) tiles.delete(key)
      for (const spot of spots) {
        const found = cells.get(spot)
        if (found?.delete(entity) && !found.size) cells.delete(spot)
      }
    }
  })
  return {
    at: (x, y) => (tiles.size ? tiles.get(tileKey(x, y)) : undefined),
    inCell: (cell) => cells.get(cell),
    destroy: stop,
  }
}

/** Готовое здание игрока: не площадка, не недострой и не под разбором. */
export const isReady = (sim: Sim, player: number, building: Entity) =>
  sim.world.has(building, Building) && !sim.world.has(building, Site) && isOwn(sim, player, building)

/** Открытые ли это ворота: готовые и с поднятой створкой. Через такие ходят, и выстрел они не держат. См. gates.ts. */
export const isOpenGate = (sim: Sim, building: Entity) => sim.world.get(building, Gate)?.open === true && !sim.world.has(building, Site)

/** Стена ли это здание: только стены и закрытые ворота укрывают от выстрелов. См. wallOnPath в combat.ts. */
export const isWall = (sim: Sim, entity: Entity) => {
  const type = sim.world.get(entity, Building)?.type
  return type === 'wall' || (type === 'gate' && !isOpenGate(sim, entity))
}

/** Площадка, основание которой накрывает тайл (x, y), или undefined. Площадок мало, поэтому простой перебор. */
export function siteAt(sim: Sim, x: number, y: number): Entity | undefined {
  for (const [entity, position, site] of sim.world.query(Position, Site)) {
    const { width, height } = BUILDINGS[site.type]
    if (x >= position.x && x < position.x + width && y >= position.y && y < position.y + height) return entity
  }
  return undefined
}

/**
 * Можно ли поставить здание левым верхним углом основания в тайл (x, y): основное здание целиком на скале
 * или фундаменте, оборонительное — ещё и на песке; не у подножия обрыва и не на дороге; внутри границ карты и без
 * других зданий и площадок. Это же правило у разворачивания MCV, см. canDeploy. gap — зазор до соседних зданий в тайлах.
 */
export function canPlace(sim: Sim, type: BuildingType, x: number, y: number, gap = 0) {
  if (!Number.isInteger(x) || !Number.isInteger(y)) return false
  const spec: BuildingSpec = BUILDINGS[type]
  const { width, height } = spec
  const { bounds } = sim
  if (x < bounds.left || y < bounds.top || x + width > bounds.right || y + height > bounds.bottom) return false
  // Оборону и трубы ставят и на песок.
  const accepts = spec.defense || spec.pipe ? (terrain: Terrain) => terrain === Terrain.Sand || isBuildable(terrain) : isBuildable
  for (let tileY = y; tileY < y + height; tileY++) {
    for (let tileX = x; tileX < x + width; tileX++) {
      // Готовый фундамент делает песок пригодным для любого здания.
      if (!accepts(terrainAt(sim.land, tileX, tileY)) && !isPaved(sim, 'foundation', tileX, tileY)) return false
      // У подножия обрыва стоит его стенка: здание на ней смотрелось бы как танк на стене, и проезд под обрывом
      // закрыт и так. Ярусы здание не ограничивают: через пологий въезд оно может стоять на двух.
      if (isCliffFoot(sim.land, tileX, tileY)) return false
      // Дорога — для транспорта: на ней и на той, что кладут, не строят ничего, даже трубу.
      if (isRoadway(sim, tileX, tileY)) return false
    }
  }
  for (let tileY = y - gap; tileY < y + height + gap; tileY++) {
    for (let tileX = x - gap; tileX < x + width + gap; tileX++) {
      if (sim.occupancy.at(tileX, tileY) !== undefined) return false
    }
  }
  // Площадка, которую ещё не начали строить, тайлов не занимает, но место за ней уже закреплено.
  for (const [, position, site] of sim.world.query(Position, Site)) {
    const other = BUILDINGS[site.type]
    if (x - gap < position.x + other.width && x + width + gap > position.x) {
      if (y - gap < position.y + other.height && y + height + gap > position.y) return false
    }
  }
  return true
}

/**
 * Максимальная доля прочности здания на этом месте. Оборона на песке без фундамента получает 70%; на скале и у обычных
 * зданий предел равен единице. Для многотайловой обороны достаточно одного песчаного тайла под основанием.
 */
export function durabilityOf(sim: Sim, type: BuildingType, x: number, y: number) {
  const spec: BuildingSpec = BUILDINGS[type]
  if (!spec.defense) return 1
  for (let tileY = y; tileY < y + spec.height; tileY++) {
    for (let tileX = x; tileX < x + spec.width; tileX++) {
      if (terrainAt(sim.land, tileX, tileY) === Terrain.Sand && !isPaved(sim, 'foundation', tileX, tileY)) return SAND_DURABILITY
    }
  }
  return 1
}

/** Заготовка компонента нового здания: сдвиг анимации у каждого свой. */
export const newBuilding = (world: World, type: BuildingType) => Building({ type, phase: world.count(Building) * 5 })

/** Даёт готовому зданию то, что положено его виду помимо производства. */
export function equip(world: World, entity: Entity, type: BuildingType) {
  const spec = buildingSpec(type)
  if (spec.repair) world.add(entity, Repair({ radius: spec.repair }))
  if (spec.shield) world.add(entity, Shield())
  if (spec.battery) world.add(entity, Accumulator())
  if (spec.requester) world.add(entity, Wanted())
  if (spec.burns) world.add(entity, Reactor())
  if (spec.warhead) world.add(entity, Silo())
  if (spec.intercept) world.add(entity, Interceptor())
  if (spec.produces && !spec.inventory) {
    equipStorage(world, entity, { inventory: PRODUCER_HOLD })
  }
  equipStorage(world, entity, spec)
  // Завод изделий стоит выключенным, пока игрок его не включит: иначе он сразу съел бы сырьё зоны.
  if (spec.assembles) world.add(entity, Assembly({ recipe: spec.assembles }))
  mountTurrets(world, entity)
}

/** Ставит здание без проверок. player — владелец; 0 — ничьё. */
export function placeBuilding(world: World, type: BuildingType, x: number, y: number, player = 0, durability = 1) {
  const entity = world.spawn(Position({ x, y }), newBuilding(world, type), Health({ value: durability, max: durability }), Owner({ player }))
  if (buildingSpec(type).produces && player) world.add(entity, Producer)
  equip(world, entity, type)
  return entity
}
