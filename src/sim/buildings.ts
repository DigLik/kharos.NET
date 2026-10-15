import type { Entity, World } from '../ecs'
import { Terrain, isBuildable, isCliffFoot, terrainAt, tileKey } from '../map/terrain'
import { isOwn } from './common'
import { Accumulator, Assembly, Building, Gate, Health, Interceptor, Inventory, Reactor, Repair, Owner, Position, Producer, Shield, Silo, Site, Wanted } from './components'
import { equipStorage, type BeamSpec } from './inventory'
import { ORE_SPECS, PRODUCT_SPECS, WARES, stockedFor, type Amounts, type Good, type Ore, type Product } from './resources'
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
}

/** На сколько тайлов от своего центра расширяет зону готовая труба или колодец. */
export const PIPE_REACH = 3
/** Дальше скольких тайлов по прямой колодец не видит парный: подземный отрезок длиннее не проложить. */
export const UNDERGROUND_REACH = 10

/** Сколько ресурсов помещается в производящем юнитов здании: материалы на очередной заказ. */
const PRODUCER_HOLD = 100

/**
 * Склад переработки поделён на два места: под руду и под готовое, по REFINERY_HOLD каждое. Общий склад вёл себя
 * непредсказуемо — накопленное готовое отнимало место у руды, и привёзший руду не мог разгрузиться. Руды помещается
 * полный кузов грузовика.
 */
const REFINERY_HOLD = 30
/** Сколько готового изделия держит цех, пока его не развезут: своё место, сырью оно не мешает. */
const PLANT_OUTPUT = 30

/**
 * Сколько боеприпасов держит оборонительная турель: 35–45 секунд непрерывного огня (см. WeaponSpec.ammo).
 * Хватает отбить налёт, но долгую осаду турель держит, только пока к ней подвозят патроны.
 */
const TURRET_AMMO = 120
const TURRET_STORE = { inventory: TURRET_AMMO, accepts: ['ammo'] as const, ammo: true }

/**
 * Переработка одной руды: принимает только её и складывает её ресурс. Размер, цена и энергия — по руде:
 * металл нужен больше всего, и плавильня дешёвая, но громоздкая; харит редок и дорог, и обогатитель компактный,
 * но дорогой и прожорливый.
 */
function refinery(ore: Ore, width: number, height: number, cost: number, power: number) {
  return {
    width, height, cost, power, inventory: REFINERY_HOLD * 2, refines: ore,
    accepts: [ore, ORE_SPECS[ore].resource], limits: { [ore]: REFINERY_HOLD, [ORE_SPECS[ore].resource]: REFINERY_HOLD },
  } as const satisfies BuildingSpec
}

/** Завод одного изделия: принимает сырьё его рецепта и складывает готовое — у каждого груза своё место. */
function plant(product: Product) {
  const limits: Amounts = { ...stockedFor(product), [product]: PLANT_OUTPUT }
  return {
    width: 2, height: 2, cost: 300, power: -3, inventory: Object.values(limits).reduce((sum, amount) => sum + amount!, 0), assembles: product,
    accepts: [...Object.keys(PRODUCT_SPECS[product].recipe), product] as Good[], limits,
  } as const satisfies BuildingSpec
}

/**
 * Хранилище одного груза: принимает только его. Склада «всего» нет — под каждый ресурс и каждое изделие
 * своё здание со своим размером и ценой: металла нужно больше всего, и двор под него просторный и дешёвый;
 * харит редок и дорог, его сейф маленький и дорогой.
 */
function store(accepts: readonly Good[], width: number, height: number, cost: number, inventory: number) {
  return { width, height, cost, inventory, accepts, stores: true } as const satisfies BuildingSpec
}

/** Сколько кредитов цены здания один строитель возводит за секунду: здание за 300 строится 15 секунд. */
export const BUILD_RATE = 20

/** Доля обычной прочности у оборонительной постройки на песке. */
export const SAND_DURABILITY = 0.7

export const BUILDINGS = {
  // Доход главного здания не даёт остаться без кредитов совсем: на генератор он копит долго, но копит.
  // Своего склада у главного здания нет: всё готовое лежит в хранилищах своего вида.
  // Пушка на крыше держит всю зону главного здания: одиночкам и разведке к нему не подойти. Строители возводят его
  // только в своей зоне и из тех же материалов, что MCV: новое место занимают MCV, а отстроенная база восстанавливает ядро.
  command: { width: 3, height: 3, cost: 2000, materials: { blocks: 20, metal: 20 }, income: 0.2, zone: 12, sight: 12, produces: ['builder', 'truck'], mounts: [{ turret: 'core', along: 0, across: -1.125 }] },
  // Переработка — по зданию на руду: каждое принимает только свою руду и выдаёт её ресурс, один передел
  // (§4.3 шаг 5). Приём 1,5 руды/с — примерно полторы шахты металла: где поставить завод между шахтами
  // и хранилищем, решает игрок. Руду держит про запас на кузов, готовое отдаёт по общим заявкам.
  smelter: refinery('metalOre', 3, 3, 300, -3),
  siliconWorks: refinery('siliconOre', 3, 2, 400, -4),
  distillery: refinery('fuelOre', 2, 3, 400, -5),
  enricher: refinery('khariteOre', 2, 2, 650, -7),
  // Выпарка лития из рассола солончаков и урановая фабрика: второй слой, база работает и без них.
  evaporator: refinery('lithiumOre', 3, 2, 500, -5),
  uraniumMill: refinery('uraniumOre', 2, 2, 800, -8),
  // Заводы изделий: каждый собирает своё изделие из ресурсов (§3.7 design.md). Склад общий на сырьё и готовое.
  blockPlant: plant('blocks'),
  ammoPlant: plant('ammo'),
  partsPlant: plant('parts'),
  rodPlant: plant('rods'),
  // Машинный завод: машинки и тяжёлая техника. Строится из стройблоков: военная промышленность требует цеха.
  factory: { width: 3, height: 3, cost: 450, power: -5, materials: { blocks: 10 }, produces: ['buggy', 'flak', 'lancer', 'supplier', 'tank', 'artillery', 'tesla', 'carrier', 'mcv'] },
  // Лаборатория ведёт исследования: они открывают переработку, цеха, армию и уровни зданий. Нужна с самого начала —
  // до плавильни, — поэтому дешёвая и без материалов; несколько лабораторий исследуют одну тему вместе, быстрее.
  // Склад — под сырьё нынешней темы: его подвозят грузовики и трубы, см. requestsOf в logistics.ts.
  lab: { width: 2, height: 2, cost: 300, power: -3, researches: true, inventory: 40 },
  // Ремонтная станция чинит технику и здания вокруг себя — так же и за ту же цену, что строитель, и тоже не под
  // огнём: это пункт обслуживания в тылу. Пехота рядом поправляется быстрее. Без энергии не работает.
  repairStation: { width: 3, height: 3, cost: 500, power: -3, materials: { blocks: 8 }, repair: 5.5 },
  // Станция поставки: здесь машины снабжения грузятся вчетверо быстрее, чем у хранилищ сети. Без неё машину снабжения
  // не заказать.
  supplyDepot: { width: 3, height: 2, cost: 350, materials: { blocks: 6 }, inventory: 300, accepts: ['ammo'], depot: true, beam: { radius: 3, rate: 40, give: true } },
  // Аэродром выпускает летающих: им нужно топливо, бомбардировщику ещё и техцентр.
  airfield: { width: 4, height: 3, cost: 500, power: -4, materials: { metal: 15, blocks: 5 }, produces: ['airTruck', 'drone', 'gunship', 'bomber'], rearms: true },
  // Электростанция.
  generator: { width: 2, height: 2, cost: 300, power: 10 },
  // Накопитель держит минуту работы электростанции: база переживает снесённую станцию или перегруз, пока его хватает.
  // Строится из лития: это его работа.
  accumulator: { width: 2, height: 2, cost: 400, materials: { lithium: 20 }, battery: { capacity: 600, rate: 10 } },
  // Реактор даёт шесть электростанций на 9 тайлах вместо 24 и жжёт стержень в минуту — около 90 кредитов сырья.
  // Разрушенный с топливом взрывается: его ставят за стеной и в стороне.
  reactor: {
    width: 3, height: 3, cost: 2500, power: 60, materials: { blocks: 10, parts: 10 }, inventory: 4, accepts: ['rods'],
    burns: { good: 'rods', seconds: 60 }, meltdown: { radius: 4, damage: 600 },
  },
  // Ракетная шахта: ядерный удар в любую точку карты (§9 п. 4 design.md). Боеголовка — 20 стержней: удар стоит урана
  // на двадцать минут реактора. Ракета чуть быстрее дрона: от пуска до удара через всю карту около двух минут.
  // Разрушенная с боеголовкой рвётся, как реактор, — без ядерного взрыва.
  silo: {
    width: 3, height: 3, cost: 3000, power: -10, materials: { blocks: 20, parts: 10 }, meltdown: { radius: 4, damage: 600 },
    warhead: { cost: 2000, materials: { rods: 20, parts: 10 }, seconds: 180, launch: 30, speed: 8, blast: { radius: 12, core: 6, damage: 3000 } },
  },
  // Перехватчик: защита от ядерного удара. Без урана — его с орбиты не купить, и защита из урана оставила бы
  // беззащитным каждого, кто сел не в рыжих пустошах. Дешевле удара (2100 против 5000 с лишним), но строится заранее.
  // Держит четыре ракеты: залп из нескольких шахт один перехватчик тоже встречает.
  interceptor: {
    width: 2, height: 2, cost: 1500, power: -4, materials: { parts: 8 },
    intercept: { radius: 80, cost: 600, materials: { parts: 4, fuel: 10 }, seconds: 60, speed: 30, capacity: 4 },
  },
  // Генератор материи — базовый доход: превращает энергию в кредиты. Первая электростанция окупает его быстро,
  // дальше теснота делает каждый следующий всё дороже. Он страховка на случай, когда линий добычи нет (§4.3 шаг 3):
  // одной электростанции хватает на два генератора.
  matter: { width: 2, height: 2, cost: 250, power: -3, income: 1.5, crowding: true },
  // Радар открывает туман далеко вокруг: так видно, кто идёт к базе, и куда бить артиллерией.
  radar: { width: 2, height: 2, cost: 400, power: -3, sight: 14, scan: 60 },
  windtrap: { width: 2, height: 2, cost: 300 },
  barracks: { width: 3, height: 2, cost: 250, power: -2, produces: ['infantry', 'rocketeer', 'flamer'] },
  // Шахта энергии не просит и начинает свою зону: тянуть к месторождению цепочку зданий не нужно.
  // Добытое копится в шахте, пока его не выкачают грузовики.
  mine: { width: 2, height: 2, cost: 300, zone: 7, extract: true, inventory: 60 },
  // Хранилища: по одному на каждый ресурс и каждое изделие. Стройблоки громоздкие; боеприпасов делают много,
  // и бункер под них вместительный; компоненты дорогие и мелкие.
  metalYard: store(['metal'], 3, 2, 150, 400),
  siliconStore: store(['silicon'], 2, 2, 150, 300),
  fuelTank: store(['fuel'], 2, 2, 200, 300),
  khariteVault: store(['kharite'], 1, 1, 250, 60),
  lithiumStore: store(['lithium'], 2, 1, 200, 120),
  uraniumCask: store(['uranium'], 1, 1, 300, 60),
  blockYard: store(['blocks'], 2, 2, 150, 200),
  ammoBunker: store(['ammo'], 2, 1, 200, 300),
  partsLocker: store(['parts'], 1, 1, 200, 60),
  rodRack: store(['rods'], 1, 1, 300, 40),
  // Станция запроса: держит заказанный игроком груз там, где он нужен, — у дальней стройки, у турелей, у армии.
  requestStation: { width: 2, height: 2, cost: 250, materials: { blocks: 4 }, inventory: 100, requester: true },
  // Космопорт торгует: энергию просит всегда.
  // Товар на продажу грузовики сгружают в трюм корабля. Строится из металла, а не из стройблоков: с него
  // начинаются деньги, и первой продаже хватает одной линии металла — без цеха и кремния.
  spaceport: { width: 3, height: 3, cost: 450, power: -5, materials: { metal: 20 }, trades: true, inventory: 400, accepts: WARES },
  // Дешёвая стена не расширяет зону: иначе цепочкой стен можно было бы бесплатно протянуть контроль через карту.
  wall: { width: 1, height: 1, cost: 30, hp: 400, defense: true, expand: 0, isolated: true },
  gate: { width: 1, height: 1, cost: 90, hp: 400, defense: true, expand: 0, isolated: true, gate: true },
  // Трубы связывают здания в сеть: по ним идут груз и энергия. Наземная дешёвая и хрупкая и перекрывает проезд,
  // как стена; колодцы ведут подземный отрезок, под которым ездят и строят, а уязвимы только сами колодцы.
  pipe: { width: 1, height: 1, cost: 5, hp: 60, expand: PIPE_REACH, sight: 1, pipe: 'surface' },
  well: { width: 1, height: 1, cost: 40, hp: 250, expand: PIPE_REACH, sight: 1, pipe: 'well' },
  // Узел связи подключает здания вокруг без труб: дороже трубы, просит энергию и уязвим — снесли узел, распалась сеть.
  // Оборонительные турели используют то же оружие, что техника, и стреляют боеприпасами со своего склада.
  // На песке все оборонительные постройки слабее.
  turret: { width: 1, height: 1, cost: 250, hp: 450, defense: true, ...TURRET_STORE, mounts: [{ turret: 'gunner', along: 0, across: 0 }] },
  rocketTurret: { width: 1, height: 1, cost: 300, hp: 400, defense: true, ...TURRET_STORE, mounts: [{ turret: 'rocket', along: 0, across: 0 }] },
  // Лазерная турель — противотанковая: бьёт мгновенно и лучше всего по тяжёлой броне. Патронов не просит, а питается
  // от энергии зоны: грузовики ей не нужны, но электростанция — нужна.
  laserTurret: { width: 1, height: 1, cost: 400, hp: 450, defense: true, power: -2, mounts: [{ turret: 'laser', along: 0, across: 0 }] },
  // Разрядная турель — против толпы: разряд бьёт цель и перескакивает на соседей, достаёт и до летающих, но недалеко.
  // Как и лазерная, патронов не просит и стреляет энергией зоны, только берёт её больше.
  arcTurret: { width: 1, height: 1, cost: 500, hp: 450, defense: true, power: -3, mounts: [{ turret: 'arc', along: 0, across: 0 }] },
  // Генератор щита гасит урон по своим под куполом от стрелков снаружи: против артиллерии и бомбардировщиков,
  // а не против штурма вплотную. Энергию берёт всегда; строится из компонентов.
  shieldGenerator: { width: 2, height: 2, cost: 1200, power: -6, materials: { parts: 5 }, shield: { radius: 7, capacity: 4000, regen: 100 } },
  cannonTurret: { width: 1, height: 1, cost: 450, hp: 500, defense: true, ...TURRET_STORE, mounts: [{ turret: 'cannon', along: 0, across: 0 }] },
} satisfies Record<string, BuildingSpec>

export type BuildingType = keyof typeof BUILDINGS
/** Описание вида здания со всеми необязательными полями. */
export const buildingSpec = (type: BuildingType): BuildingSpec => BUILDINGS[type]
/** Главное здание — Settlement Core: в него разворачивается MCV. */
export const CORE: BuildingType = 'command'
export const BUILDING_TYPES = Object.keys(BUILDINGS) as BuildingType[]
/** Что возводят строители. Остальные здания появятся вместе с тем, для чего они нужны. */
export const BUILDABLE: BuildingType[] = [
  'generator', 'matter', 'mine', 'smelter', 'siliconWorks', 'distillery', 'enricher', 'evaporator', 'uraniumMill', 'blockPlant', 'ammoPlant', 'partsPlant', 'rodPlant',
  'metalYard', 'siliconStore', 'fuelTank', 'khariteVault', 'lithiumStore', 'uraniumCask', 'blockYard', 'ammoBunker', 'partsLocker', 'rodRack', 'requestStation', 'accumulator', 'reactor', 'silo', 'spaceport', 'barracks', 'factory', 'airfield', 'lab',
  'repairStation', 'supplyDepot', 'radar', 'wall', 'gate', 'turret', 'rocketTurret', 'cannonTurret', 'laserTurret', 'arcTurret', 'shieldGenerator', 'interceptor',
]

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

/**
 * Дерево технологий: здание появляется в списке строителя, когда у игрока стоят готовыми все здания из его списка.
 * Снесли нужное — новое такое не заложить, пока его не восстановят; построенное остаётся. Главное здание
 * строители не ставят: его привозит MCV. Покрытия доступны всегда.
 */
export const REQUIRES: Partial<Record<BuildingType, readonly BuildingType[]>> = {
  generator: ['command'],
  wall: ['command'],
  gate: ['command'],
  turret: ['radar'],
  matter: ['generator'],
  mine: ['generator'],
  radar: ['generator'],
  spaceport: ['generator'],
  lab: ['generator'],
  // Переработку открывает электростанция, а не шахта: так решили, когда руду возили и харвестеры (2026-10-07).
  smelter: ['generator'],
  siliconWorks: ['generator'],
  distillery: ['generator'],
  enricher: ['generator'],
  evaporator: ['generator'],
  uraniumMill: ['generator'],
  accumulator: ['generator'],
  metalYard: ['smelter'],
  siliconStore: ['siliconWorks'],
  fuelTank: ['distillery'],
  khariteVault: ['enricher'],
  lithiumStore: ['evaporator'],
  uraniumCask: ['uraniumMill'],
  blockPlant: ['smelter'],
  ammoPlant: ['smelter'],
  partsPlant: ['siliconWorks'],
  rodPlant: ['uraniumMill'],
  blockYard: ['blockPlant'],
  ammoBunker: ['ammoPlant'],
  partsLocker: ['partsPlant'],
  rodRack: ['rodPlant'],
  requestStation: ['blockPlant'],
  barracks: ['radar'],
  factory: ['radar'],
  airfield: ['radar'],
  rocketTurret: ['radar'],
  cannonTurret: ['radar'],
  repairStation: ['factory'],
  supplyDepot: ['factory'],
  laserTurret: ['radar'],
  arcTurret: ['radar'],
  reactor: ['uraniumMill'],
  silo: ['rodPlant'],
  // Перехватчик своей темы не просит: защита не должна ждать дерева. Радар нужен — им он видит ракету.
  interceptor: ['radar'],
}

/**
 * Какое исследование открывает здание вдобавок к REQUIRES: без него строители его не ставят. Переработку открывает
 * исследование её руды, цех — его изделия, армию — свои темы; шахту, космопорт и накопитель — открытия. Трубы и
 * колодцы строители в меню не берут, их тянут: темы проверяет протяжка, см. pipeStroke.
 */
export const RESEARCH_FOR: Partial<Record<BuildingType, Topic>> = {
  mine: 'prospecting',
  spaceport: 'trade',
  wall: 'walls',
  gate: 'gates',
  radar: 'radar',
  pipe: 'pipes',
  well: 'wells',
  accumulator: 'accumulators',
  smelter: 'metalOre',
  siliconWorks: 'siliconOre',
  distillery: 'fuelOre',
  enricher: 'khariteOre',
  evaporator: 'lithiumOre',
  uraniumMill: 'uraniumOre',
  blockPlant: 'blocks',
  ammoPlant: 'ammo',
  partsPlant: 'parts',
  rodPlant: 'rods',
  barracks: 'infantry',
  turret: 'ammo',
  rocketTurret: 'rockets',
  factory: 'vehicles',
  airfield: 'aviation',
  repairStation: 'repair',
  supplyDepot: 'vehicles',
  laserTurret: 'lasers',
  arcTurret: 'arcs',
  cannonTurret: 'armor',
  shieldGenerator: 'shields',
  reactor: 'atom',
  silo: 'warheads',
}

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
