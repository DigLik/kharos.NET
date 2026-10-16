import type { Entity } from '@kharos/ecs'
import { inputsOf } from './assembly'
import { BUILDINGS, buildingSpec, feedOf, isReady, type BuildingSpec } from './buildings'
import { NONE, isOwn } from './common'
import { Assembly, Batch, Building, Converting, Drop, Fetch, Hauler, Inventory, Off, Owner, Path, Position, Producer, Site, Trade, Unit, Unreachable, Wanted } from './components'
import { depositAt } from './deposits'
import { POCKET, amountOf, loadOf, roomFor } from './inventory'
import { entriesOf, GOODS, isOre, isProduct, ORE_OF, WARES, stockedFor, type Amounts, type Good, type Ore, type Resource, type Ware } from './resources'
import type { Sim } from './sim'
import { researchNeeds } from './topics'
import { deliveredTo, isStore } from './trade'
import { unitSpec, vehicleReach } from './units'
import { inCircles, networkOf, zonesOf, type Zone } from './zones'

/**
 * Зональные заявки. Всё, что потребляет груз, само заказывает его у своей зоны строительства: стройка — материалы
 * на здание, производитель — материалы на первый заказ очереди, космопорт — товар по заявке на продажу, переработка
 * — руду из шахт (её берут где угодно: месторождение решает, где шахта, а не зона), цех — сырьё своего рецепта,
 * турель и станция поставки — патроны, машина снабжения — патроны туда, где она стоит, станция запроса — то, что
 * заказал игрок. Свободные грузовики игрока берут самую важную заявку и везут груз из хранилищ, переработки
 * и цехов той же зоны. Когда заявок нет, они развозят готовое по хранилищам. Руду без переработки никто не возит:
 * она копится в шахте, пока завод не построят.
 * Игроку не нужно указывать каждому грузовику, что и куда везти.
 */

/** Заявка: складу to не хватает amount груза resource. Чем выше priority, тем раньше её везут. */
export interface Request {
  to: Entity
  resource: Good
  amount: number
  priority: number
  zone: Zone
  /** Где искать груз: в своей зоне (готовое) или в своих шахтах где угодно (руда). */
  source: 'zone' | 'mines'
  /** Заявка на продажу: ей хранилища отдают и резерв, см. givenBy. */
  sale: boolean
}

/**
 * Что важнее везти: стройка, потом руда на переработку и патроны турелям — без них стоит добыча и молчит
 * оборона, — потом сырьё производству юнитов и цехам, потом товар на продажу.
 */
export const PRIORITY = { site: 4, refine: 3, ammo: 3, production: 2, trade: 1 } as const

/**
 * Турель заказывает патроны, когда расстреляла столько своего запаса: по одному патрону грузовики не возят,
 * а четверти хватает, чтобы подвоз успел до того, как она замолчит.
 */
export const AMMO_REORDER = 0.25

/** Сколько тайлов пути перевешивает одна ступень важности: ближняя мелкая заявка не обгоняет важную дальнюю. */
const PRIORITY_WEIGHT = 1000
/**
 * Сколько добытого должно накопиться, чтобы за ним поехал грузовик и увёз в хранилище: PUSH_MIN единиц
 * или половина того, что шахта вмещает, — что меньше.
 */
const PUSH_MIN = 10
const PUSH_SHARE = 0.5
/** Сколько каждого сырья нынешней темы лаборатория держит про запас: больше она не заказывает. */
const LAB_STOCK = 10
/** Заявки меньше этого не возят. */
const MIN_JOB = 0.5

/** Груз грузовиков: сколько каждого ресурса едет на склады и сколько ещё заберут со складов. */
export interface Flows {
  incoming: Map<Entity, Amounts>
  outgoing: Map<Entity, Amounts>
}

const add = (map: Map<Entity, Amounts>, entity: Entity, resource: Good, amount: number) => {
  if (amount <= 0) return
  let amounts = map.get(entity)
  if (!amounts) map.set(entity, (amounts = {}))
  amounts[resource] = (amounts[resource] ?? 0) + amount
}

/**
 * Что сейчас везут грузовики, трубы и строители. Набравший груз везёт то, что в кузове; ещё не набравший обещал привезти
 * и забрать столько, сколько в его работе.
 */
export function flowsOf(sim: Sim, except = NONE as Entity): Flows {
  const flows: Flows = { incoming: new Map(), outgoing: new Map() }
  // Пачки в трубах, ещё не пришедшие: их груз уже ушёл со склада отправителя.
  for (const [, batch] of sim.world.query(Batch)) {
    if (batch.arrive > sim.time.tick) add(flows.incoming, batch.to as Entity, batch.resource, batch.amount)
  }
  for (const [truck, hauler, cargo] of sim.world.query(Hauler, Inventory)) {
    if (truck === except || (hauler.from === NONE && hauler.to === NONE)) continue
    const carried = amountOf(cargo, hauler.resource)
    if (hauler.to !== NONE) add(flows.incoming, hauler.to as Entity, hauler.resource, hauler.full ? carried : Math.max(carried, hauler.amount))
    if (hauler.from !== NONE && !hauler.full) add(flows.outgoing, hauler.from as Entity, hauler.resource, hauler.amount - carried)
  }
  // Грузовики с ручным приказом: везут в to, а ещё не набравшие обещали забрать из from, см. carrying.ts.
  for (const [truck, hauler, cargo] of sim.world.query(Hauler, Inventory)) {
    const order = hauler.order
    if (!order || truck === except) continue
    const carried = amountOf(cargo, order.resource)
    add(flows.incoming, order.to as Entity, order.resource, order.full ? carried : Math.max(carried, Math.min(order.left, cargo.capacity)))
    if (order.from !== NONE && !order.full) add(flows.outgoing, order.from as Entity, order.resource, Math.min(order.left, cargo.capacity) - carried)
  }
  // Строители, которые сами везут материал на стройку, см. fetching.ts.
  for (const [builder, fetch, cargo] of sim.world.query(Fetch, Inventory)) {
    if (builder === except) continue
    const carried = amountOf(cargo, fetch.resource)
    add(flows.incoming, fetch.to as Entity, fetch.resource, fetch.full ? carried : Math.max(carried, fetch.amount))
    if (fetch.from !== NONE && !fetch.full) add(flows.outgoing, fetch.from as Entity, fetch.resource, fetch.amount - carried)
  }
  return flows
}

/** Сколько груза уже едет на склад в кузовах грузовиков. */
export const incomingTo = (sim: Sim, entity: Entity, resource: Good) => flowsOf(sim).incoming.get(entity)?.[resource] ?? 0

/** Сколько груза склада занято заявкой на продажу: его не тратят на производство. */
function reservedIn(sim: Sim, entity: Entity, resource: Good) {
  const order = sim.world.get(entity, Trade)
  return order && order.resource === resource ? deliveredTo(sim, entity) : 0
}

/** Сколько груза на складе можно потратить на нужды самого здания: всё, кроме товара на продажу. */
export function spareOf(sim: Sim, entity: Entity, resource: Good) {
  const inventory = sim.world.get(entity, Inventory)
  return inventory ? Math.max(0, amountOf(inventory, resource) - reservedIn(sim, entity, resource)) : 0
}

/**
 * Сколько груза склад отдаёт по заявкам и маршрутам: всё, что можно потратить, а хранилище — сверх своего резерва.
 * Резерв — подушка игрока на перестройку: стройка, производство, цеха и турели его не трогают. Продажа (sale) —
 * воля игрока: она забирает и резерв.
 */
export function givenBy(sim: Sim, entity: Entity, resource: Good, sale = false) {
  const spare = spareOf(sim, entity, resource)
  return sale ? spare : Math.max(0, spare - (sim.world.get(entity, Inventory)?.reserve ?? 0))
}

/**
 * Ставит своим готовым хранилищам резерв: сколько груза не выдавать. Не больше, чем хранилище вмещает; ноль — снять.
 * Не хранилища, стройки и чужое из списка выбрасываются.
 */
export function setReserve(sim: Sim, player: number, buildings: Entity[], amount: number) {
  if (!Number.isFinite(amount)) return false
  let count = 0
  for (const building of new Set(buildings)) {
    const inventory = sim.world.get(building, Inventory)
    if (!inventory || !isStore(sim, building) || sim.world.has(building, Site) || !isOwn(sim, player, building)) continue
    inventory.reserve = Math.max(0, Math.min(inventory.capacity, Math.round(amount)))
    count++
  }
  return count > 0
}

/** Станция ли это запроса: она держит заказанное игроком, см. BuildingSpec.requester. */
export const isRequester = (sim: Sim, entity: Entity) => {
  const type = sim.world.get(entity, Building)?.type
  return type !== undefined && !!buildingSpec(type).requester
}

/** Сколько этого груза станция запроса держит по заказу игрока; у остальных — ноль. */
export const wantedOf = (sim: Sim, entity: Entity, resource: Good) => sim.world.get(entity, Wanted)?.items[resource] ?? 0

/** Сколько у станции запроса лишнего сверх заказа: его увозят в хранилища. */
export const surplusOf = (sim: Sim, entity: Entity, resource: Good) => Math.max(0, spareOf(sim, entity, resource) - wantedOf(sim, entity, resource))

/**
 * Не берёт ли заказчик to груз у source: станция запроса не берёт у другой станции. Иначе две станции, заказавшие одно
 * и то же, возили бы его друг у друга по кругу.
 */
export const barred = (sim: Sim, to: Entity, source: Entity) => isRequester(sim, to) && isRequester(sim, source)

/**
 * Заказ своих готовых станций запроса: сколько груза resource держать; ноль — не заказывать. Заказ всех грузов вместе
 * не больше склада станции. Руду не заказывают. Не станции, стройки и чужое из списка выбрасываются.
 */
export function setWanted(sim: Sim, player: number, buildings: Entity[], resource: Good, amount: number) {
  if (!Number.isFinite(amount) || !WARES.includes(resource as Ware)) return false
  let count = 0
  for (const building of new Set(buildings)) {
    const wanted = sim.world.get(building, Wanted)
    const inventory = sim.world.get(building, Inventory)
    if (!wanted || !inventory || !isReady(sim, player, building)) continue
    let others = 0
    for (const [good, held] of entriesOf(wanted.items)) if (good !== resource) others += held
    const next = Math.max(0, Math.min(inventory.capacity - others, Math.round(amount)))
    if (next > 0) wanted.items[resource] = next
    else delete wanted.items[resource]
    count++
  }
  return count > 0
}

/**
 * Материалы, которых ждёт сущность: стройка — на здание, производитель — на первый заказ очереди, пока тот не начат.
 * undefined — ничего не ждёт.
 */
export function materialsFor(sim: Sim, entity: Entity): Amounts | undefined {
  const { world } = sim
  const site = world.get(entity, Site)
  if (site) return site.demolish ? undefined : buildingSpec(site.type).materials
  const producer = world.get(entity, Producer)
  const [head] = producer?.queue ?? []
  if (!producer || !head || producer.progress > 0) return undefined
  return unitSpec(head).materials
}

/** Чего из материалов ещё нет на складе: пусто — всё на месте. */
export function missingFor(sim: Sim, entity: Entity): Amounts {
  const missing: Amounts = {}
  for (const [resource, amount] of entriesOf(materialsFor(sim, entity) ?? {})) {
    const lack = amount - spareOf(sim, entity, resource)
    // Крошку меньше погрешности не ждут: стройка её прощает (см. materialShare).
    if (lack > 1e-6) missing[resource] = lack
  }
  return missing
}

/** Центр здания или место юнита. */
function centerOf(sim: Sim, entity: Entity) {
  const position = sim.world.get(entity, Position)!
  const type = sim.world.get(entity, Building)?.type ?? sim.world.get(entity, Site)?.type
  if (type === undefined) return position
  return { x: position.x + BUILDINGS[type].width / 2, y: position.y + BUILDINGS[type].height / 2 }
}

const distance = (sim: Sim, a: Entity, b: Entity) => {
  const one = centerOf(sim, a)
  const two = centerOf(sim, b)
  return Math.hypot(one.x - two.x, one.y - two.y)
}

/** Какая руда лежит под шахтой: вид месторождения, переведённый в руду. */
export function mineOre(sim: Sim, mine: Entity): Ore | undefined {
  const position = sim.world.get(mine, Position)
  const kind = position ? depositAt(sim, position.x, position.y)?.kind : undefined
  return kind === undefined ? undefined : ORE_OF[kind]
}

const NOTHING_OFFERED: Good[] = []

/**
 * Что здание отдаёт по заявкам: хранилище и переработка — готовое, шахта — свою руду, цех — всё, кроме сырья
 * своего рецепта: изделия и остатки прежнего рецепта. Пусто — ничего. Сколько груза есть на деле, решает
 * availableIn; шахту это не касается, её руду берут только заявкой.
 */
export function offersOf(sim: Sim, entity: Entity): readonly Good[] {
  // Дроп отдаёт всё, что на нём лежит, станция запроса — всё, что держит.
  if (sim.world.has(entity, Drop) || isRequester(sim, entity)) {
    const inventory = sim.world.get(entity, Inventory)
    return inventory ? GOODS.filter((good) => amountOf(inventory, good) > 1e-9) : NOTHING_OFFERED
  }
  const type = sim.world.get(entity, Building)?.type
  if (type === undefined || sim.world.has(entity, Site)) return NOTHING_OFFERED
  const spec = buildingSpec(type)
  if (spec.stores || spec.refines) return WARES
  // Космопорт отдаёт то, что привезла закупка или осталось после продажи, — кроме товара открытой продажи.
  // Не отдаёт и материалы своего производства, которые ждут остальных.
  if (spec.trades) {
    const order = sim.world.get(entity, Trade)
    const materials = materialsFor(sim, entity) ?? {}
    return WARES.filter((ware) => !(order && !order.buy && ware === order.resource) && !materials[ware])
  }
  if (spec.assembles) {
    const inputs = inputsOf(sim, entity)
    return WARES.filter((ware) => !inputs.includes(ware as Resource))
  }
  // Лаборатория отдаёт всё, что не нужно ни нынешней теме, ни головной теме плана, которая ждёт сырья: остатки
  // прежних увозят обратно.
  if (spec.researches) {
    const needs = researchNeeds(sim, sim.world.get(entity, Owner)?.player ?? 0)
    return GOODS.filter((good) => !needs[good])
  }
  if (!spec.extract) return NOTHING_OFFERED
  const ore = mineOre(sim, entity)
  return ore ? [ore] : NOTHING_OFFERED
}

/**
 * Кто в сети что отдаёт: возвращённая функция даёт здания сети, отдающие груз, в порядке зданий сети. Трубы, колодцы
 * и электростанции не отдают ничего, а их в сети большой базы тысячи — перебирать их на каждый вопрос дорого. Годится,
 * пока не изменилось, что здания отдают (вид, рецепт, тема исследований, продажа): на один обход системы, не дольше.
 */
export function offerIndex(sim: Sim) {
  const offering = new Map<Zone, Map<Good, Entity[]>>()
  return (zone: Zone, resource: Good): readonly Entity[] => {
    let goods = offering.get(zone)
    if (!goods) {
      offering.set(zone, (goods = new Map()))
      for (const source of zone.buildings) {
        for (const good of offersOf(sim, source)) {
          const list = goods.get(good)
          if (list) list.push(source)
          else goods.set(good, [source])
        }
      }
    }
    return goods.get(resource) ?? NOTHING_OFFERING
  }
}
const NOTHING_OFFERING: readonly Entity[] = []

/** Заявки зон игрока: что, куда и сколько везти, с учётом того, что уже едет. */
export function requestsOf(sim: Sim, player: number, flows = flowsOf(sim)): Request[] {
  const { world } = sim
  const zones = zonesOf(sim, player)
  const requests: Request[] = []
  /** Зона, которая кормит здание. anywhere — здание кормит и ближайшая зона, если оно ни в какую не входит. */
  const zoneOf = (entity: Entity, anywhere = false) => {
    const { x, y } = centerOf(sim, entity)
    const own = networkOf(sim, entity) ?? zones.find((zone) => inCircles(zone.circles, x, y))
    return own ?? (anywhere ? nearestZone(zones, x, y) : undefined)
  }
  const need = (to: Entity, resource: Good, amount: number, priority: number, source: 'zone' | 'mines' = 'zone', anywhere = false, sale = false) => {
    const left = amount - (flows.incoming.get(to)?.[resource] ?? 0)
    if (left < MIN_JOB) return
    const zone = zoneOf(to, anywhere)
    if (zone) requests.push({ to, resource, amount: left, priority, zone, source, sale })
  }

  // Стройки и производство: материалы, которых нет на месте.
  for (const [entity, owner] of world.query(Owner, Inventory)) {
    if (owner.player !== player) continue
    const site = world.has(entity, Site)
    if (!site && !world.has(entity, Producer)) continue
    // Выключенному производству материалы не везут, пока не включат.
    if (!site && (!isReady(sim, player, entity) || world.has(entity, Converting) || world.has(entity, Off))) continue
    // Недостачу заказывают целыми единицами: иначе остаток меньше MIN_JOB не привёз бы никто и заказ встал бы навсегда.
    for (const [resource, amount] of entriesOf(missingFor(sim, entity))) need(entity, resource, Math.ceil(amount - 1e-6), site ? PRIORITY.site : PRIORITY.production)
  }
  // Переработка: своя руда из своих шахт, в какой бы зоне они ни стояли.
  for (const [entity, owner, inventory, building] of world.query(Owner, Inventory, Building)) {
    const ore = buildingSpec(building.type).refines
    if (owner.player !== player || !ore) continue
    if (!isReady(sim, player, entity) || world.has(entity, Converting) || world.has(entity, Off)) continue
    // Сколько ещё поместится: больше буфера руды завод не просит, пока не переработает привезённое.
    const room = roomFor(inventory, ore)
    if (room > 0) need(entity, ore, room, PRIORITY.refine, 'mines')
  }
  // Лаборатории: сырьё нынешней темы и головной темы плана, если ведут не её (см. researchNeeds), по LAB_STOCK каждого, но не больше своей доли того, что теме осталось потратить:
  // остаток делится поровну, и работают все. Раньше каждая просила остаток за вычетом чужого — и первая забирала всё.
  // Лишнее у одной увезут обратно, когда тема кончится. Руду — из своих шахт, где бы они ни стояли, как переработке.
  const labs: Entity[] = []
  for (const [entity, building, owner] of world.query(Building, Owner)) {
    if (owner.player !== player || !buildingSpec(building.type).researches || !world.has(entity, Inventory)) continue
    if (!isReady(sim, player, entity) || world.has(entity, Off)) continue
    labs.push(entity)
  }
  if (labs.length) {
    const needs = researchNeeds(sim, player)
    for (const [good, left] of entriesOf(needs)) {
      const share = Math.min(LAB_STOCK, Math.ceil(left / labs.length - 1e-6))
      for (const lab of labs) need(lab, good, share - amountOf(world.get(lab, Inventory)!, good), PRIORITY.production, isOre(good) ? 'mines' : 'zone')
    }
  }
  // Включённые заводы изделий: сырьё рецепта, сколько его держат про запас.
  for (const [entity, assembly, inventory, owner] of world.query(Assembly, Inventory, Owner)) {
    if (owner.player !== player || !assembly.on || !isReady(sim, player, entity)) continue
    for (const [resource, amount] of entriesOf(stockedFor(assembly.recipe))) {
      need(entity, resource, amount - amountOf(inventory, resource), PRIORITY.production)
    }
  }
  // Турели и реакторы: патроны и стержни, когда израсходована четверть запаса. Оборону ставят и вне зон — её кормит
  // ближайшая зона.
  for (const [entity, building, inventory, owner] of world.query(Building, Inventory, Owner)) {
    const feed = feedOf(buildingSpec(building.type))
    if (owner.player !== player || !feed || !isReady(sim, player, entity)) continue
    const room = roomFor(inventory, feed)
    if (room >= inventory.capacity * AMMO_REORDER) need(entity, feed, room, PRIORITY.ammo, 'zone', true)
  }
  // Машины снабжения: патроны, когда израсходована четверть склада, — грузовики везут их в поле. Стоящая в сети, где
  // патроны есть, грузится из хранилищ сама (см. supply.ts), и грузовик к ней не гонят.
  let offering: ReturnType<typeof offerIndex> | undefined
  for (const [entity, unit, inventory, owner, position] of world.query(Unit, Inventory, Owner, Position)) {
    if (owner.player !== player || !unitSpec(unit.type).supplies) continue
    const room = roomFor(inventory, 'ammo')
    if (room < inventory.capacity * AMMO_REORDER) continue
    const zone = zones.find((one) => inCircles(one.circles, position.x, position.y))
    if (zone && (offering ??= offerIndex(sim))(zone, 'ammo').some((source) => givenBy(sim, source, 'ammo') >= MIN_JOB)) continue
    need(entity, 'ammo', room, PRIORITY.ammo, 'zone', true)
  }
  // Станции запроса: недостающее до заказа игрока.
  for (const [entity, wanted, inventory] of world.query(Wanted, Inventory)) {
    if (!isReady(sim, player, entity)) continue
    for (const [resource, amount] of entriesOf(wanted.items)) need(entity, resource, amount - amountOf(inventory, resource), PRIORITY.production)
  }
  // Продажа: товар заявки.
  for (const [entity, order] of world.query(Trade, Inventory)) {
    if (order.total > 0 || !isReady(sim, player, entity)) continue
    // Недостачу заказывают целыми единицами, как стройка: хранилище отдало дробный остаток — и заявке не хватает
    // меньше MIN_JOB, а такое не привёз бы никто: корабль ждал бы последнюю единицу вечно. Лишнее вернётся в хранилища.
    need(entity, order.resource, Math.ceil(order.wanted - deliveredTo(sim, entity) - 1e-6), PRIORITY.trade, 'zone', false, true)
  }
  return requests.sort((a, b) => b.priority - a.priority)
}

/** Сколько груза можно забрать со склада, кроме того, что уже обещано другим грузовикам; sale — берёт продажа, см. givenBy. */
const availableIn = (sim: Sim, flows: Flows, entity: Entity, resource: Good, sale = false) =>
  Math.max(0, givenBy(sim, entity, resource, sale) - (flows.outgoing.get(entity)?.[resource] ?? 0))

/** Работа грузовику: откуда, куда, что и сколько. to = NONE — куда везти, решится, когда наберёт груз. */
export interface Job {
  from: Entity
  to: Entity
  resource: Good
  amount: number
}

/**
 * Сколько груза ещё поместится на склад для этого грузовика: место за вычетом того, что туда уже везут другие.
 * Иначе грузовики, глядя на одно свободное место, набрали бы каждый по кузову и остались с грузом, который некуда деть.
 */
/** Не подъехать грузовикам игрока к зданию или дропу: отметка Unreachable ещё действует, см. hauling.ts. */
export const unreachable = (sim: Sim, entity: Entity) => (sim.world.get(entity, Unreachable)?.until ?? 0) > sim.time.tick

/** Заперт ли грузовик: из его тайла технике не выехать дальше кармана в POCKET тайлов. */
export function walledIn(sim: Sim, truck: Entity) {
  const position = sim.world.get(truck, Position)
  return !!position && vehicleReach(sim, Math.floor(position.x), Math.floor(position.y), POCKET).size < POCKET
}

export function spaceFor(sim: Sim, truck: Entity, to: Entity, resource: Good, flows = flowsOf(sim, truck)) {
  const inventory = sim.world.get(to, Inventory)
  return inventory ? roomFor(inventory, resource) - (flows.incoming.get(to)?.[resource] ?? 0) : 0
}

/**
 * Своё хранилище, куда грузовик отвезёт груз: самое пустое по этому грузу с учётом едущего туда, из равных — ближайшее.
 * Так запас расходится по всем хранилищам, а не копится в ближайшем, пока соседние стоят пустыми. NONE — места нет нигде.
 */
export function storeFor(sim: Sim, truck: Entity, resource: Good, except: Entity = NONE as Entity): Entity {
  const player = sim.world.get(truck, Owner)?.player ?? 0
  const flows = flowsOf(sim, truck)
  let best = NONE as Entity
  let bestFill = Infinity
  let bestDistance = Infinity
  for (const [entity, inventory] of sim.world.query(Inventory, Building)) {
    if (entity === except || !isStore(sim, entity) || !isReady(sim, player, entity) || sim.world.has(entity, Converting) || unreachable(sim, entity)) continue
    const space = spaceFor(sim, truck, entity, resource, flows)
    if (space <= 1e-9) continue
    const holds = Math.min(inventory.limits[resource] ?? inventory.capacity, inventory.capacity)
    // Доля занятого с учётом едущего — до десятых: почти равные хранилища считаются равными, и решает близость.
    const fill = Math.round((1 - space / holds) * 10)
    const far = distance(sim, truck, entity)
    if (fill < bestFill || (fill === bestFill && far < bestDistance)) {
      best = entity
      bestFill = fill
      bestDistance = far
    }
  }
  return best
}

/** Ближайшая к грузовику своя переработка этой руды, куда она поместится с учётом едущего туда, не из сети skip; NONE — такой нет. */
export function refineryFor(sim: Sim, truck: Entity, ore: Ore, except: Entity = NONE as Entity, skip?: Zone): Entity {
  const player = sim.world.get(truck, Owner)?.player ?? 0
  const flows = flowsOf(sim, truck)
  let best = NONE as Entity
  let bestDistance = Infinity
  for (const [entity, , building] of sim.world.query(Inventory, Building)) {
    if (entity === except || buildingSpec(building.type).refines !== ore || !isReady(sim, player, entity) || sim.world.has(entity, Converting) || sim.world.has(entity, Off)) continue
    if (spaceFor(sim, truck, entity, ore, flows) <= 1e-9) continue
    if ((skip && networkOf(sim, entity) === skip) || unreachable(sim, entity)) continue
    const far = distance(sim, truck, entity)
    if (far < bestDistance) {
      best = entity
      bestDistance = far
    }
  }
  return best
}

/** Куда везти груз без заявки: готовое — в ближайшее хранилище, руду — на ближайшую переработку. */
export const deliveryFor = (sim: Sim, truck: Entity, resource: Good, except: Entity = NONE as Entity): Entity =>
  isOre(resource) ? refineryFor(sim, truck, resource, except) : storeFor(sim, truck, resource, except)

/**
 * Годится ли склад, чтобы выгрузить в него груз: своё хранилище с местом, переработка с местом под руду или то,
 * что этот груз заказывало (стройка, производитель, космопорт с заявкой на него).
 */
export function acceptsDelivery(sim: Sim, player: number, to: Entity, resource: Good) {
  const { world } = sim
  const inventory = world.get(to, Inventory)
  if (!inventory || !isOwn(sim, player, to) || roomFor(inventory, resource) <= 1e-9) return false
  if (world.has(to, Site)) return !!materialsFor(sim, to)?.[resource]
  // Машина снабжения принимает груз своих отсеков где угодно: к ней едут в поле.
  const unit = world.get(to, Unit)
  if (unit) return !!unitSpec(unit.type).supplies
  if (!isReady(sim, player, to) || world.has(to, Converting)) return false
  if (isStore(sim, to)) return true
  // Станция запроса — только заказанное и не сверх заказа.
  if (isRequester(sim, to)) return amountOf(inventory, resource) < wantedOf(sim, to, resource) - 1e-9
  const order = world.get(to, Trade)
  if (order) return order.total <= 0 && order.resource === resource
  if (materialsFor(sim, to)?.[resource]) return true
  // Цех — сырьё своего рецепта.
  if (inputsOf(sim, to).includes(resource as Resource)) return true
  // Свой склад здания: переработка так принимает руду, которой больше негде лежать, турель — патроны, реактор — стержни,
  // лаборатория — сырьё нынешней темы.
  const type = world.get(to, Building)?.type
  if (type === undefined) return false
  const spec = buildingSpec(type)
  // Лаборатория — сырьё своей нынешней темы.
  if (spec.researches && researchNeeds(sim, player)[resource]) return true
  return spec.refines === resource || feedOf(spec) === resource
}

/** Ближайшая к точке зона: по расстоянию до края её ближайшего круга. */
function nearestZone(zones: readonly Zone[], x: number, y: number): Zone | undefined {
  let best: Zone | undefined
  let bestDistance = Infinity
  for (const zone of zones) {
    for (let i = 0; i < zone.circles.length; i += 3) {
      const distance = Math.hypot(x - zone.circles[i], y - zone.circles[i + 1]) - zone.circles[i + 2]
      if (distance < bestDistance) {
        best = zone
        bestDistance = distance
      }
    }
  }
  return best
}

/** Дропы, лежащие в зоне: груз с них берут так же, как со складов зоны. */
export function dropsIn(sim: Sim, zone: Zone): Entity[] {
  const found: Entity[] = []
  for (const [entity, , , position] of sim.world.query(Drop, Inventory, Position)) {
    if (inCircles(zone.circles, position.x + 0.5, position.y + 0.5)) found.push(entity)
  }
  return found
}

/** Откуда в зоне можно взять готовое: её здания и дропы, лежащие в ней. */
export const sourcesIn = (sim: Sim, zone: Zone): Entity[] => [...zone.buildings, ...dropsIn(sim, zone)]

/** Можно ли забирать груз со склада: свой готовый склад, где он есть. */
export function offersPickup(sim: Sim, player: number, from: Entity, resource: Good) {
  return (isReady(sim, player, from) || sim.world.has(from, Drop)) && spareOf(sim, from, resource) > 1e-9
}

/** Свободен ли грузовик для диспетчера: не привязан к шахте, без работы и груза, стоит. */
const isIdle = (sim: Sim, truck: Entity) => {
  const { world } = sim
  const hauler = world.get(truck, Hauler)!
  const cargo = world.get(truck, Inventory)
  return !hauler.route.length && !hauler.order && hauler.mine === NONE && hauler.pickup === NONE && hauler.from === NONE && hauler.to === NONE && (!cargo || loadOf(cargo) <= 1e-9) && !world.has(truck, Path) && !world.has(truck, Converting)
}

/**
 * Раздаёт работу свободным грузовикам. Каждому — лучшая из заявок его игрока: важнее и ближе (путь до склада,
 * откуда брать, и оттуда до заказчика). Готовое берётся в хранилищах зоны заказчика, руда — в шахтах игрока,
 * где бы они ни стояли. Если заявок, которые можно выполнить, нет — грузовик увозит готовое из переработки
 * и цехов в ближайшее хранилище с местом; нет места нигде — не берёт ничего. Руду без заявки не возит никто.
 */
export function dispatch(sim: Sim) {
  const { world } = sim
  // Отметки «не подъехать», чей срок вышел, снимаются: здание снова получает работу.
  const expired: Entity[] = []
  for (const [entity, mark] of world.query(Unreachable)) if (mark.until <= sim.time.tick) expired.push(entity)
  for (const entity of expired) world.remove(entity, Unreachable)
  const idle = new Map<number, Entity[]>()
  for (const [entity, hauler, owner] of world.query(Hauler, Owner, Unit)) {
    if (!isIdle(sim, entity)) continue
    // Запертому работы не дают, пока ему не откроют проход: заливка кармана — несколько десятков тайлов.
    if (hauler.trapped && (hauler.trapped = walledIn(sim, entity))) continue
    let list = idle.get(owner.player)
    if (!list) idle.set(owner.player, (list = []))
    list.push(entity)
  }
  if (!idle.size) return
  const flows = flowsOf(sim)
  const offeringIn = offerIndex(sim)
  /** Хранилища сети: их в ней десятки, а зданий — тысячи. */
  const stores = new Map<Zone, Entity[]>()
  const storesIn = (network: Zone) => {
    let found = stores.get(network)
    if (!found) stores.set(network, (found = network.buildings.filter((entity) => isStore(sim, entity))))
    return found
  }

  for (const [player, trucks] of idle) {
    const requests = requestsOf(sim, player, flows)
    // Откуда брать: переработка и цеха отдают готовое, шахты — руду. Хранилища груз только принимают: возить из
    // одного хранилища в другое незачем, а руду в них не кладут.
    const outlets: Entity[] = []
    const mines: Entity[] = []
    for (const [entity, building] of world.query(Building, Inventory)) {
      if (!isReady(sim, player, entity) || world.has(entity, Converting)) continue
      const spec = buildingSpec(building.type)
      if (spec.refines || spec.assembles || spec.trades || spec.requester) outlets.push(entity)
      else if (spec.extract) mines.push(entity)
    }
    // Дропы ничьи: их подбирает любой, но сам — только в своих зонах. За дропом на другом краю карты грузовик
    // без приказа не едет. С дропа везут и по заявкам — как со склада зоны; руду с него — на переработку.
    const zones = zonesOf(sim, player)
    const drops = [...new Set(zones.flatMap((zone) => dropsIn(sim, zone)))]
    outlets.push(...drops)
    mines.push(...drops)
    /** Откуда брать готовое для заявки зоны: её здания и дропы в ней; считается раз на зону. */
    const zoneSources = new Map<Zone, Entity[]>()
    const sourcesOf = (zone: Zone) => {
      let found = zoneSources.get(zone)
      if (!found) zoneSources.set(zone, (found = sourcesIn(sim, zone)))
      return found
    }
    // У шахты с привязанным грузовиком есть свой возчик: свободные её руду не трогают и занимаются готовым.
    const bound = new Set<Entity>()
    for (const [, hauler] of world.query(Hauler)) if (hauler.mine !== NONE) bound.add(hauler.mine as Entity)

    // Назначенные на здания грузовики берут груз в любой зоне игрока, а не только в зоне заказчика.
    let everywhere: Entity[] | undefined
    /** Заявки без учёта того, что уже везут: по ним видно, нужно ли ещё что-то тому, что велено обеспечить. */
    let needs: Request[] | undefined
    const anyZone = () => (everywhere ??= [...new Set([...zonesOf(sim, player).flatMap((zone) => zone.buildings), ...drops])])

    /** Есть ли в сети поставщик груза для заказчика to — тогда его везут трубы. */
    const hasLocal = (network: Zone, to: Entity, resource: Good, least: number, sale: boolean) =>
      offeringIn(network, resource).some((source) => !barred(sim, to, source) && givenBy(sim, source, resource, sale) >= least)
    /** Есть ли в сети хранилище, куда этот груз ещё помещается. */
    const hasStore = (network: Zone, resource: Good) => storesIn(network).some((store) => spaceFor(sim, NONE as Entity, store, resource, flows) > 1e-9)

    for (const truck of trucks) {
      const room = world.get(truck, Inventory)?.capacity ?? 0
      const hauler = world.get(truck, Hauler)!
      const { filter } = hauler
      const carries = (resource: Good) => !filter.length || filter.includes(resource)
      // Снесённые и потерянные здания из назначения выпадают; не осталось ни одного — грузовик снова общий.
      if (hauler.serve.length) hauler.serve = hauler.serve.filter((building) => isReady(sim, player, building as Entity) && world.has(building as Entity, Inventory))
      // Приказ обеспечить исполнен, когда у цели не осталось заявок, даже тех, что уже везут другие: грузовик свободен.
      if (hauler.supply !== NONE && !(needs ??= requestsOf(sim, player, { incoming: new Map(), outgoing: new Map() })).some((request) => request.to === hauler.supply)) hauler.supply = NONE
      const serving = hauler.supply !== NONE ? new Set([hauler.supply]) : hauler.serve.length ? new Set(hauler.serve) : undefined
      let best: (Job & { score: number }) | undefined
      for (const request of requests) {
        if (request.amount < MIN_JOB || !carries(request.resource)) continue
        if ((serving && !serving.has(request.to)) || unreachable(sim, request.to)) continue
        // Внутри сети груз идёт по трубам: сам грузовик везёт только то, чего в сети заказчика нет, — из других сетей.
        const network = serving ? undefined : networkOf(sim, request.to)
        if (network && hasLocal(network, request.to, request.resource, Math.min(request.amount, MIN_JOB), request.sale)) continue
        let found = false
        const look = (sources: readonly Entity[]) => {
          for (const source of sources) {
            if (source === request.to || !offersOf(sim, source).includes(request.resource) || unreachable(sim, source)) continue
            if ((network && networkOf(sim, source) === network) || barred(sim, request.to, source)) continue
            // Руду шахты, у которой уже есть привязанный грузовик, свободные не возят.
            if (request.source === 'mines' && bound.has(source)) continue
            const available = availableIn(sim, flows, source, request.resource, request.sale)
            if (available < Math.min(request.amount, MIN_JOB)) continue
            found = true
            const score = request.priority * PRIORITY_WEIGHT - distance(sim, truck, source) - distance(sim, source, request.to)
            if (best && score <= best.score) continue
            best = { from: source, to: request.to, resource: request.resource, amount: Math.min(request.amount, available, room), score }
          }
        }
        const local = request.source === 'zone' && !serving && !network
        look(request.source === 'mines' ? mines : local ? sourcesOf(request.zone) : anyZone())
        // В своей зоне взять нечего — груз везут из любой своей: иначе стройка в зоне без переработки, куда труб
        // ещё не дотянули, ждала бы металл вечно, а он лежал бы в плавильне соседней зоны.
        if (local && !found) look(anyZone())
      }
      if (!best && !serving) {
        // Заявок нет — увозит готовое из переработки и цехов; куда именно, решит, когда наберёт груз.
        for (const source of outlets) {
          if (unreachable(sim, source)) continue
          for (const resource of offersOf(sim, source)) {
            if (!carries(resource)) continue
            // Есть куда отправить по трубам своей сети — грузовик не нужен.
            const network = networkOf(sim, source)
            if (network && hasStore(network, resource)) continue
            // Станция запроса отдаёт в хранилища только лишнее сверх заказа.
            const requester = isRequester(sim, source)
            const available = requester ? Math.min(availableIn(sim, flows, source, resource), surplusOf(sim, source, resource)) : availableIn(sim, flows, source, resource)
            const inventory = world.get(source, Inventory)!
            const holds = Math.min(inventory.limits[resource] ?? inventory.capacity, inventory.capacity)
            // Остаток сырья прежнего рецепта цеху не нужен: его увозят весь, сколько бы его ни было.
            // Так же — готовое из вставшей переработки.
            // Как и закупку в космопорте: ей там не место, склад космопорта — под товар продажи. Даже крошки:
            // иначе остаток на пороге MIN_JOB так и лежал бы у цеха.
            const type = world.get(source, Building)?.type
            const spec: BuildingSpec = type === undefined ? {} as BuildingSpec : buildingSpec(type)
            // Переработка, которой нечего перерабатывать — руды нет и её не везут, — новое готовое не сделает:
            // остаток меньше порога иначе лежал бы в ней вечно.
            const stopped = !!spec.refines && amountOf(inventory, spec.refines) <= 1e-9 && !(flows.incoming.get(source)?.[spec.refines] ?? 0)
            // С дропа увозят всё до крошки.
            const leftover = (world.has(source, Assembly) && !isProduct(resource)) || !!spec.trades || !!spec.researches || stopped || world.has(source, Drop) || requester
            if (available < (leftover ? 1e-9 : Math.min(PUSH_MIN, room, holds * PUSH_SHARE))) continue
            const score = -distance(sim, truck, source)
            if (best && score <= best.score) continue
            // Груз, который некуда везти, не берут: куда — решается сразу, и берут не больше, чем там поместится.
            const to = deliveryFor(sim, truck, resource, source)
            if (to === NONE) continue
            const space = roomFor(world.get(to, Inventory)!, resource) - (flows.incoming.get(to)?.[resource] ?? 0)
            if (space <= 1e-9) continue
            best = { from: source, to, resource, amount: Math.min(available, room, space), score }
          }
        }
      }
      if (!best) continue
      hauler.from = best.from
      hauler.to = best.to
      hauler.resource = best.resource
      hauler.amount = best.amount
      hauler.full = false
      hauler.waiting = false
      add(flows.outgoing, best.from, best.resource, best.amount)
      if (best.to !== NONE) {
        add(flows.incoming, best.to, best.resource, best.amount)
        const request = requests.find((item) => item.to === best!.to && item.resource === best!.resource)
        if (request) request.amount -= best.amount
      }
    }
  }
}
