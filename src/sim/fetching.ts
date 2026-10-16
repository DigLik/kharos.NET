import type { DataOf, Entity } from '@kharos/ecs'
import { BUILDINGS } from './buildings'
import { NONE, onTurn } from './common'
import { Beam, Builds, Building, Converting, Fetch, Inventory, Owner, Path, Position, Repair, Site, Unit } from './components'
import { awaitsMaterials } from './construction'
import { amountOf, approach, reaches, roomFor, transfer } from './inventory'
import { flowsOf, givenBy, missingFor, offersOf, type Flows } from './logistics'
import { GOODS, entriesOf, type Amounts, type Good } from './resources'
import type { Sim } from './sim'
import { isStore } from './trade'

/**
 * Строители сами возят материалы. Стройка, которой не хватает материалов, встаёт, когда построено всё, что позволяет
 * привезённое; если недостающее никто не везёт — ни трубы, ни грузовики, ни другие строители, — её строитель едет
 * к ближайшему своему зданию, которое отдаёт нужное (хранилищу, переработке, главному зданию), берёт его своим лучом,
 * сколько поместится в кузов, и сгружает на площадку. Так первую базу можно построить без грузовиков и труб.
 *
 * Поручение (Fetch) живёт, пока строитель на этой стройке: увели — бросает, груз остаётся в кузове. Остаток в кузове
 * свободного строителя едет в ближайшее хранилище. Что строитель везёт, учитывают заявки (flowsOf в logistics.ts):
 * грузовики и трубы того же не повезут.
 */

type FetchData = DataOf<[typeof Fetch]>[0]

/** Дальше этого от стройки, в тайлах, строитель за материалом не едет: туда пусть возят грузовики. */
export const FETCH_REACH = 40
/** Меньше этого строитель не возит: крошку довезут вместе с остальным. */
const MIN_FETCH = 0.5
/** Раз во сколько тиков строитель ищет, где взять материал, и пробует подъехать снова. */
const RETRY_TICKS = 20
/** После скольких промахов подъезда подряд строитель бросает поручение. */
const GIVE_UP = 3
const EPSILON = 1e-9

/** Середина здания или площадки. */
function centerOf(sim: Sim, entity: Entity) {
  const position = sim.world.get(entity, Position)!
  const type = sim.world.get(entity, Building)?.type ?? sim.world.get(entity, Site)!.type
  return { x: position.x + BUILDINGS[type].width / 2, y: position.y + BUILDINGS[type].height / 2 }
}

/** Что в кузове: первый груз, которого там есть хоть сколько-то; undefined — пусто. */
export function cargoOf(sim: Sim, builder: Entity): Good | undefined {
  const inventory = sim.world.get(builder, Inventory)
  return inventory && GOODS.find((good) => amountOf(inventory, good) > EPSILON)
}

/** Чего стройке не хватает сверх того, что к ней уже едет по трубам, в грузовиках и у строителей. */
function shortOf(sim: Sim, site: Entity, flows: Flows): Amounts {
  const short: Amounts = {}
  const coming = flows.incoming.get(site) ?? {}
  for (const [good, amount] of entriesOf(missingFor(sim, site))) {
    const left = amount - (coming[good] ?? 0)
    if (left >= MIN_FETCH) short[good] = left
  }
  return short
}

/**
 * Где строителю взять материал для стройки: ближайшее к нему своё готовое здание, которое отдаёт недостающее, не дальше
 * FETCH_REACH от стройки. Хранилища отдают только сверх резерва. undefined — брать негде или везти нечего.
 */
export function fetchSource(sim: Sim, builder: Entity, site: Entity, flows: Flows = flowsOf(sim)) {
  const { world } = sim
  const player = world.get(builder, Owner)?.player
  const inventory = world.get(builder, Inventory)
  const position = world.get(builder, Position)
  if (player === undefined || !inventory || !position || !world.has(site, Site)) return undefined
  const short = shortOf(sim, site, flows)
  if (!Object.keys(short).length) return undefined
  const to = centerOf(sim, site)
  let best: { from: Entity; resource: Good; amount: number } | undefined
  let bestDistance = Infinity
  for (const [entity, , owner] of world.query(Building, Owner, Inventory)) {
    if (owner.player !== player || entity === site || world.has(entity, Site)) continue
    const at = centerOf(sim, entity)
    if (Math.hypot(at.x - to.x, at.y - to.y) > FETCH_REACH) continue
    const distance = Math.hypot(at.x - position.x, at.y - position.y)
    if (distance >= bestDistance) continue
    const offers = offersOf(sim, entity)
    for (const [good, need] of entriesOf(short)) {
      if (!offers.includes(good)) continue
      const spare = givenBy(sim, entity, good) - (flows.outgoing.get(entity)?.[good] ?? 0)
      const amount = Math.min(need, spare, roomFor(inventory, good))
      if (amount < MIN_FETCH) continue
      best = { from: entity, resource: good, amount }
      bestDistance = distance
      break
    }
  }
  return best
}

/** Ближайшее своё готовое хранилище, куда поместится этот груз: туда свободный строитель отвозит остаток. */
function storeFor(sim: Sim, builder: Entity, resource: Good): Entity | undefined {
  const { world } = sim
  const player = world.get(builder, Owner)?.player
  const position = world.get(builder, Position)!
  let best: Entity | undefined
  let bestDistance = Infinity
  for (const [entity, , owner, inventory] of world.query(Building, Owner, Inventory)) {
    if (owner.player !== player || world.has(entity, Site) || !isStore(sim, entity) || roomFor(inventory, resource) < MIN_FETCH) continue
    const at = centerOf(sim, entity)
    const distance = Math.hypot(at.x - position.x, at.y - position.y)
    if (distance < bestDistance) {
      best = entity
      bestDistance = distance
    }
  }
  return best
}

/** Может ли этот юнит возить материалы: ремонтник с кузовом и лучом. */
export const canFetch = (sim: Sim, unit: Entity) => sim.world.has(unit, Repair) && sim.world.has(unit, Inventory) && sim.world.has(unit, Beam) && sim.world.has(unit, Unit)

/**
 * Будет ли строитель возить материал на свою стройку, а не искать другую работу: стройка ждёт материалов, а взять их
 * есть где. Так считает и volunteer в construction.ts: такой строитель другой работы не берёт.
 */
export const willFetch = (sim: Sim, builder: Entity, site: Entity) => canFetch(sim, builder) && fetchSource(sim, builder, site) !== undefined

/** Подводит строителя к складу target на длину луча; не вышло GIVE_UP раз подряд — бросает поручение. */
function goTo(sim: Sim, builder: Entity, fetch: FetchData, target: Entity) {
  const { world, time } = sim
  if (world.has(builder, Path)) return
  if (fetch.misses && !onTurn(time, builder, RETRY_TICKS)) return
  fetch.misses = approach(sim, builder, target, world.get(builder, Beam)!.radius) ? 0 : fetch.misses + 1
  if (fetch.misses >= GIVE_UP) world.remove(builder, Fetch)
}

/** Один тик поручения: набрать груз у from, довезти и сгрузить на to. */
function run(sim: Sim, builder: Entity, fetch: FetchData) {
  const { world } = sim
  const to = fetch.to as Entity
  const toSite = world.has(to, Site)
  const builds = world.get(builder, Builds)
  // На стройку везут, пока строитель на ней; в хранилище — пока ему не дали работу.
  if (!world.alive(to) || !world.has(to, Inventory) || (toSite ? builds?.site !== to : builds !== undefined)) {
    world.remove(builder, Fetch)
    return
  }
  const cargo = world.get(builder, Inventory)!
  const have = amountOf(cargo, fetch.resource)
  if (!fetch.full) {
    const from = fetch.from as Entity
    const want = fetch.amount - have
    const gone = !world.alive(from) || !world.has(from, Inventory) || world.has(from, Site)
    if (!gone && want > EPSILON) {
      if (!reaches(sim, from, builder)) return goTo(sim, builder, fetch, from)
      const moved = transfer(sim, from, builder, fetch.resource, Math.min(want, givenBy(sim, from, fetch.resource)))
      if (moved > EPSILON) {
        world.remove(builder, Path)
        return
      }
    }
    // Набрал, сколько хотел, или больше не дают: едет с тем, что есть.
    if (have <= EPSILON) {
      world.remove(builder, Fetch)
      return
    }
    fetch.full = true
  }
  if (have <= EPSILON) {
    world.remove(builder, Fetch)
    return
  }
  if (!reaches(sim, builder, to)) return goTo(sim, builder, fetch, to)
  const need = toSite ? (missingFor(sim, to)[fetch.resource] ?? 0) : Infinity
  // Сгрузил всё, стройке больше не нужно или класть некуда — поручение кончено.
  if (need <= EPSILON || transfer(sim, builder, to, fetch.resource, need) <= EPSILON || amountOf(cargo, fetch.resource) <= EPSILON) world.remove(builder, Fetch)
}

/**
 * Раз в тик, после грузовиков: строители ведут свои поручения, а свободные от них — берут новые. Строитель на стройке,
 * которая ждёт материалов, едет за ними, если их никто не везёт; материал, нужный стройке, уже в кузове — везёт сразу.
 * Свободный строитель с грузом отвозит его в хранилище.
 */
export function fetchMaterials(sim: Sim) {
  const { world, time } = sim
  const builders: Entity[] = []
  for (const [entity] of world.query(Unit, Repair, Inventory, Beam)) if (!world.has(entity, Converting)) builders.push(entity)
  if (!builders.length) return
  let flows: Flows | undefined
  for (const builder of builders) {
    const fetch = world.get(builder, Fetch)
    if (fetch) {
      run(sim, builder, fetch)
      continue
    }
    const cargo = cargoOf(sim, builder)
    const builds = world.get(builder, Builds)
    const site = builds?.site as Entity | undefined
    if (site !== undefined && world.has(site, Site)) {
      if (cargo !== undefined) {
        if ((missingFor(sim, site)[cargo] ?? 0) > EPSILON) world.add(builder, Fetch({ from: NONE, to: site, resource: cargo, amount: amountOf(world.get(builder, Inventory)!, cargo), full: true }))
        continue
      }
      if (!onTurn(time, builder, RETRY_TICKS) || !awaitsMaterials(sim, site)) continue
      const source = fetchSource(sim, builder, site, (flows ??= flowsOf(sim)))
      if (!source) continue
      world.add(builder, Fetch({ from: source.from, to: site, resource: source.resource, amount: source.amount, full: false }))
      world.remove(builder, Path)
      // Следующему строителю в этот тик — с учётом того, что повезёт этот.
      flows = undefined
      continue
    }
    if (cargo === undefined || builds || world.has(builder, Path) || !onTurn(time, builder, RETRY_TICKS)) continue
    const store = storeFor(sim, builder, cargo)
    if (store !== undefined) world.add(builder, Fetch({ from: NONE, to: store, resource: cargo, amount: amountOf(world.get(builder, Inventory)!, cargo), full: true }))
  }
}
