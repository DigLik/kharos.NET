import type { Entity } from '../ecs'
import { BUILDINGS, buildingSpec, levelFactor, type BuildingSpec, type BuildingType } from './buildings'
import { isOwn } from './common'
import { creditsOf } from './economy'
import { Accumulator, Assembly, Building, Busy, Health, Off, Player, Position, Site } from './components'
import { isBurning, meltdown } from './reactors'
import type { Sim } from './sim'
import { allGrids, type Zone } from './zones'

/** Хозяйство одной энергосети на этот тик (см. allGrids). Энергия у каждой своя: между ними она не передаётся. */
export interface Economy {
  /**
   * Сколько энергии дают здания зоны и сколько её просят потребители: работающий — всю свою, стоящий без дела —
   * меньше, см. idlePower. В produced входит и отданное накопителями.
   */
  produced: number
  demand: number
  /** Полная нагрузка: сколько попросили бы потребители, работай они все. По ней судят о перегрузе электростанций. */
  peak: number
  /** Сколько ещё энергии накопители зоны могли бы отдать в этот тик сверх отданного: запас под полную нагрузку. */
  spare: number
  /** Доход в кредитах в секунду с учётом нехватки энергии. */
  income: number
  /** Сколько в зоне зданий, которым тесно друг с другом (см. BuildingSpec.crowding). */
  crowd: number
  /** Сколько энергии в этот тик отдают зоне её накопители, см. storeEnergy. */
  stored: number
}

/**
 * Подмога главного здания: пока у игрока меньше RELIEF_BELOW кредитов, одно его главное здание даёт RELIEF_INCOME
 * в секунду вместо обычного дохода. 600 — строитель, электростанция и генератор материи: без денег и без зданий
 * игрок не в тупике, а дальше выбирается сам. Подмога одна на игрока, сколько бы главных зданий он ни поставил,
 * и меньше одного генератора материи — фермы из неё не выходит.
 */
export const RELIEF_BELOW = 600
export const RELIEF_INCOME = 1

/** Какую долю своей энергии просит потребитель, которому нечего делать. */
export const IDLE_SHARE = 1 / 3

/**
 * Сколько энергии просит без дела потребитель, которому в работе нужно power (меньше нуля): треть, целым числом
 * в большую сторону — 2 и 3 дают 1, 4, 5 и 6 — 2, 10 — 4.
 */
export const idlePower = (power: number) => -Math.ceil(-power * IDLE_SHARE)

/** Потребитель, у которого простоя нет: пока включён, он работает — генератор материи, радар. */
export const alwaysBusy = (spec: BuildingSpec) => !!(spec.income || spec.scan)

/** Есть ли в зоне главное здание: без него доход приносит только оно само, генераторы материи стоят. */
const hasCore = (sim: Sim, zone: Zone) => zone.buildings.some((entity) => sim.world.get(entity, Building)!.type === 'command')

/** Приносит ли здание доход только при главном здании в зоне: все доходные, кроме самого главного. */
const needsCore = (type: BuildingType) => type !== 'command' && !!buildingSpec(type).income

/**
 * Хозяйство зоны. Если энергии не хватает, потребители работают на ту долю, на которую её хватает. Потребитель
 * без дела (нет метки Busy, см. busy.ts) просит меньше — этим он освобождает энергию остальным; электростанции же
 * перегружаются по полной нагрузке (peak): то, что три завода разом взялись за работу, станции не ломает.
 * Доход с генераторов материи — только если в зоне есть главное здание; энергию они просят и без него.
 * stored — считать ли то, что отдают накопители: сами они решают, сколько отдать, по хозяйству без себя.
 */
function economyOfZone(sim: Sim, zone: Zone, stored = true): Economy {
  const economy: Economy = { produced: 0, demand: 0, peak: 0, spare: 0, income: 0, crowd: 0, stored: 0 }
  const core = hasCore(sim, zone)
  /** Доход потребителей при полной энергии. */
  let powered = 0
  for (const entity of zone.buildings) {
    const building = sim.world.get(entity, Building)!
    const spec: BuildingSpec = BUILDINGS[building.type]
    const battery = stored ? sim.world.get(entity, Accumulator) : undefined
    const flow = battery?.flow ?? 0
    if (flow > 0) economy.stored += flow
    // Заряженный накопитель мог бы отдать ещё: под полную нагрузку этот запас считается.
    if (battery) economy.spare += Math.max(0, Math.min(spec.battery!.rate - Math.max(0, flow), battery.charge / sim.time.step))
    const power = powerAt(sim, entity)
    const income = core || !needsCore(building.type) ? (spec.income ?? 0) : 0
    // Выключенный потребитель энергии не просит и ничего не даёт.
    if (power < 0 && switchedOff(sim, entity)) continue
    // Повреждённая электростанция даёт энергии во столько же раз меньше, во сколько упала её прочность.
    if (power > 0) economy.produced += power * (sim.world.get(entity, Health)?.value ?? 1)
    if (power < 0) {
      // Тесное здание просит тем больше, чем их уже в зоне: первое — одну норму, второе — две, третье — три.
      const norms = spec.crowding ? ++economy.crowd : 1
      economy.peak -= power * norms
      economy.demand -= (alwaysBusy(spec) || sim.world.has(entity, Busy) ? power : idlePower(power)) * norms
      powered += income
    } else {
      economy.income += income
    }
  }
  economy.produced += economy.stored
  if (powered) economy.income += powered * Math.min(1, economy.produced / economy.demand)
  return economy
}

/** Зона строительства вместе с её хозяйством. */
interface Book {
  player: number
  zone: Zone
  economy: Economy
}

/**
 * Хозяйства, посчитанные в идущем тике, и для каких энергосетей; supply — то же, что powerSupply. Нет записи — тик
 * не идёт, и хозяйства считаются заново при каждом вопросе: между тиками мир меняют команды, тесты и редактор.
 */
const ledgers = new WeakMap<Sim, { grids?: Map<number, Zone[]>; books?: Book[]; supply?: Map<Entity, number> }>()

/**
 * Тик начался: до closeBooks хозяйства зон считаются раз, а не каждым, кто спросил, — их спрашивают с десяток систем,
 * и на большой базе каждый раз это обход тысяч зданий. Открывается после накопителей: они решают, сколько отдают
 * зоне в этот тик. Перестроились сети — достроили или разрушили здание — хозяйства считаются заново; урон
 * электростанциям в бою скажется со следующего тика.
 */
export const openBooks = (sim: Sim) => void ledgers.set(sim, {})

/** Тик кончился, см. openBooks. */
export const closeBooks = (sim: Sim) => void ledgers.delete(sim)

/** Хозяйства идущего тика, если сети с тех пор не перестроились; undefined — считать заново. */
function ledgerOf(sim: Sim) {
  const ledger = ledgers.get(sim)
  if (!ledger) return undefined
  const grids = allGrids(sim)
  if (ledger.grids !== grids) {
    ledger.grids = grids
    ledger.books = ledger.supply = undefined
  }
  return ledger
}

/**
 * Зоны всех игроков с их хозяйствами, у каждого игрока — в порядке зон. Считаются только готовые здания в зонах:
 * свернул главное здание — встало всё.
 */
function books(sim: Sim): Book[] {
  const ledger = ledgerOf(sim)
  if (ledger?.books) return ledger.books
  const result: Book[] = []
  for (const [player, zones] of allGrids(sim)) {
    // Подмога — первой сети с главным зданием: они идут первыми, см. allGrids.
    let relief = creditsOf(sim, player) < RELIEF_BELOW ? RELIEF_INCOME - (BUILDINGS.command.income ?? 0) : 0
    for (const zone of zones) {
      const economy = economyOfZone(sim, zone)
      if (relief > 0 && hasCore(sim, zone)) {
        economy.income += relief
        relief = 0
      }
      result.push({ player, zone, economy })
    }
  }
  if (ledger) ledger.books = result
  return result
}

/** Здания всех игроков, которые принесли бы доход, но стоят в зоне без главного здания и поэтому не приносят. */
export function coreless(sim: Sim): Set<Entity> {
  const idle = new Set<Entity>()
  for (const { zone } of books(sim)) {
    if (hasCore(sim, zone)) continue
    for (const entity of zone.buildings) if (needsCore(sim.world.get(entity, Building)!.type)) idle.add(entity)
  }
  return idle
}

/** Сколько энергии даёт или просит здание: больше нуля — вырабатывает (с уровнем — больше), меньше — потребляет. */
function powerAt(sim: Sim, building: Entity) {
  const { type, level } = sim.world.get(building, Building)!
  const power = buildingSpec(type).power ?? 0
  // Реактор вырабатывает, только пока в нём горит стержень.
  if (buildingSpec(type).burns && !isBurning(sim, building)) return 0
  // Выработка растёт с уровнем; у радара — потребление: дальше разведывает — больше ест.
  return power > 0 || buildingSpec(type).scan ? power * levelFactor(level) : power
}
/** Потребитель, которого игрок выключил: энергии ему не нужно, и работать он не будет. У завода изделий — свой флаг. */
export function switchedOff(sim: Sim, building: Entity) {
  const assembly = sim.world.get(building, Assembly)
  return assembly ? !assembly.on : sim.world.has(building, Off)
}

/**
 * Своё готовое здание, которое игрок выключил: так оно и показано на карте — темнее и со значком. О чужих судить
 * не по чему: их выключатель видит только хозяин.
 */
export const shutDown = (sim: Sim, player: number, building: Entity) =>
  isOwn(sim, player, building) && !sim.world.has(building, Site) && switchedOff(sim, building)

/** Хозяйства зон одного игрока. */
export const zoneEconomies = (sim: Sim, player: number): readonly Economy[] =>
  books(sim).filter((book) => book.player === player).map((book) => book.economy)

/** Итог по всем зонам игрока. Для дохода это то, что он получает; энергию так складывать можно только для справки. */
export function economyOf(sim: Sim, player: number): Economy {
  const total: Economy = { produced: 0, demand: 0, peak: 0, spare: 0, income: 0, crowd: 0, stored: 0 }
  for (const economy of zoneEconomies(sim, player)) {
    total.produced += economy.produced
    total.demand += economy.demand
    total.peak += economy.peak
    total.spare += economy.spare
    total.income += economy.income
    total.crowd += economy.crowd
    total.stored += economy.stored
  }
  return total
}

/**
 * Как здание этого вида изменит баланс энергии зоны с хозяйством economy, если его там построить:
 * больше нуля — добавит выработки, меньше — попросит столько энергии.
 */
export function powerOf(type: BuildingType, economy: Economy) {
  const spec: BuildingSpec = BUILDINGS[type]
  const power = spec.power ?? 0
  return power < 0 && spec.crowding ? power * (economy.crowd + 1) : power
}

/** То же для здания, которому нечего делать: сколько оно просит без дела. У вида без простоя — столько же, сколько в работе. */
export function idlePowerOf(type: BuildingType, economy: Economy) {
  const spec: BuildingSpec = BUILDINGS[type]
  const power = spec.power ?? 0
  if (power >= 0 || alwaysBusy(spec)) return powerOf(type, economy)
  return idlePower(power) * (spec.crowding ? economy.crowd + 1 : 1)
}

/**
 * Какую долю прочности в секунду теряет электростанция, когда с зоны просят вдвое больше энергии, чем она даёт.
 * При меньшем перегрузе урон меньше во столько же раз, при большем — не растёт.
 */
export const OVERLOAD_DAMAGE = 0.01

/**
 * Насколько зона перегружена: 0 — энергии хватает и на полную нагрузку, 1 — полная нагрузка вдвое больше того, что
 * есть, и выше. Считается по полной нагрузке, а не по нынешней: перегруз — это то, что построено, а не то, что
 * сейчас включилось. В «есть» входит и то, что могли бы отдать заряженные накопители: пока заряда хватает, станции целы.
 */
export const overloadOf = (economy: Economy) => {
  const able = economy.produced + economy.spare
  return economy.produced > 0 && economy.peak > able ? Math.min(1, economy.peak / able - 1) : 0
}

/** Что нехватка энергии делает со зданием: электростанция перегружена и разрушается, потребитель работает медленнее. */
export type PowerState = 'overload' | 'starved'

/** Здания всех игроков, которым сейчас не хватает энергии. Тех, у кого всё в порядке, здесь нет. */
export function powerStates(sim: Sim): Map<Entity, PowerState> {
  const states = new Map<Entity, PowerState>()
  for (const { zone, economy } of books(sim)) {
    // Станции страдают от полной нагрузки, потребители — от нынешней нехватки: одно бывает без другого.
    const overloaded = overloadOf(economy) > 0
    const short = economy.demand > economy.produced
    if (!overloaded && !short) continue
    for (const entity of zone.buildings) {
      const power = powerAt(sim, entity)
      if (power > 0 && overloaded) states.set(entity, 'overload')
      else if (power < 0 && short && !switchedOff(sim, entity)) states.set(entity, 'starved')
    }
  }
  return states
}

/**
 * На какую долю от полной скорости работает каждый потребитель энергии: 1 — энергии хватает. Потребителя,
 * который не входит ни в одну зону строительства или выключен, здесь нет — он не работает вовсе.
 */
export function powerSupply(sim: Sim): ReadonlyMap<Entity, number> {
  const ledger = ledgerOf(sim)
  if (ledger?.supply) return ledger.supply
  const supply = new Map<Entity, number>()
  for (const { zone, economy } of books(sim)) {
    const share = economy.demand > 0 ? Math.min(1, economy.produced / economy.demand) : 1
    for (const entity of zone.buildings) if (powerAt(sim, entity) < 0 && !switchedOff(sim, entity)) supply.set(entity, share)
  }
  if (ledger) ledger.supply = supply
  return supply
}

/**
 * Электростанции, которым сейчас есть что питать: в каждой зоне — ровно столько, сколько нужно на её нынешний спрос
 * сверх отданного накопителями, начиная с ближайших к её потребителям; остальные стоят в запасе. Это только картинка:
 * энергия у зоны общая, и на счёт выбор станций не влияет.
 */
export function loadedPlants(sim: Sim): Set<Entity> {
  const { world } = sim
  const loaded = new Set<Entity>()
  const centerOf = (entity: Entity) => {
    const { width, height } = BUILDINGS[world.get(entity, Building)!.type]
    const position = world.get(entity, Position)
    return { x: (position?.x ?? 0) + width / 2, y: (position?.y ?? 0) + height / 2 }
  }
  for (const { zone, economy } of books(sim)) {
    let need = economy.demand - economy.stored
    if (need <= 1e-9) continue
    // Середина потребителей зоны: от неё и считается, какая станция ближе.
    let x = 0
    let y = 0
    let count = 0
    const plants: { entity: Entity; output: number; x: number; y: number }[] = []
    for (const entity of zone.buildings) {
      const power = powerAt(sim, entity)
      if (!power || (power < 0 && switchedOff(sim, entity))) continue
      const center = centerOf(entity)
      if (power > 0) {
        plants.push({ entity, output: power * (world.get(entity, Health)?.value ?? 1), ...center })
        continue
      }
      x += center.x
      y += center.y
      count++
    }
    if (!count) continue
    x /= count
    y /= count
    plants.sort((a, b) => Math.hypot(a.x - x, a.y - y) - Math.hypot(b.x - x, b.y - y) || a.entity - b.entity)
    for (const plant of plants) {
      if (need <= 1e-9) break
      loaded.add(plant.entity)
      need -= plant.output
    }
  }
  return loaded
}

/**
 * Электростанции зон, где энергии не хватило бы и целым станциям: потребителей там больше, чем станции тянут.
 * Чинить такие бесполезно, пока перегруз не снят. Станции, которым не хватает только из-за повреждений, сюда не входят.
 */
export function overbuiltPlants(sim: Sim): Set<Entity> {
  const plants = new Set<Entity>()
  for (const { zone, economy } of books(sim)) {
    const found = zone.buildings.filter((entity) => powerAt(sim, entity) > 0)
    const capacity = found.reduce((sum, entity) => sum + powerAt(sim, entity), 0)
    if (economy.peak > capacity + economy.spare) for (const entity of found) plants.add(entity)
  }
  return plants
}

/**
 * Раз в тик, до всех потребителей: накопители решают, сколько энергии отдают зоне в этот тик и сколько берут.
 * Не хватает выработки — отдают недостающее, сколько позволяют скорость и заряд; есть излишек — копят его. Заряжаются
 * только излишком: потребителей накопитель не объедает. Отданное входит в выработку зоны (Economy.stored), поэтому,
 * пока заряда хватает, потребители работают в полную силу и электростанции не перегружаются. Накопитель вне зоны
 * не отдаёт и не берёт.
 */
export function storeEnergy(sim: Sim) {
  const { world, time } = sim
  for (const [, battery] of world.query(Accumulator)) battery.flow = 0
  for (const [, zones] of allGrids(sim)) {
    for (const zone of zones) {
      const cells = zone.buildings.filter((entity) => world.has(entity, Accumulator))
      if (!cells.length) continue
      const economy = economyOfZone(sim, zone, false)
      let balance = economy.produced - economy.demand
      for (const entity of cells) {
        const battery = world.get(entity, Accumulator)!
        const { capacity, rate } = buildingSpec(world.get(entity, Building)!.type).battery!
        let flow = 0
        if (balance < 0 && battery.charge > 0) flow = Math.min(rate, -balance, battery.charge / time.step)
        else if (balance > 0 && battery.charge < capacity) flow = -Math.min(rate, balance, (capacity - battery.charge) / time.step)
        balance += flow
        battery.flow = flow
        battery.charge = Math.max(0, Math.min(capacity, battery.charge - flow * time.step))
      }
    }
  }
}

/**
 * Раз в тик: перегруженные электростанции теряют прочность и в нуле разрушаются.
 * Сами здания не восстанавливаются: их чинят строители.
 */
function wear(sim: Sim, all: Book[]) {
  const { world, time } = sim
  // Состав мира меняется после обхода.
  const ruined: Entity[] = []
  for (const { zone, economy } of all) {
    const overload = overloadOf(economy)
    if (!overload) continue
    for (const entity of zone.buildings) {
      if (powerAt(sim, entity) <= 0) continue
      const health = world.get(entity, Health)!
      health.value -= OVERLOAD_DAMAGE * overload * time.step
      if (health.value <= 0) ruined.push(entity)
    }
  }
  for (const entity of ruined) {
    meltdown(sim, entity)
    world.destroy(entity)
  }
}

/**
 * Раз в тик: начисляет игрокам доход и изнашивает перегруженные электростанции.
 * Доли кредита копятся в earned, на счёт попадают целые. Игроку не в сети — доля дохода, и за одно отсутствие
 * не больше потолка (Rules.offlineIncome, offlineMinutes): иначе за ночь копилось бы целое состояние.
 */
export function earn(sim: Sim) {
  const { world, time } = sim
  const all = books(sim)
  if (!all.length) return
  const incomes = new Map<number, number>()
  for (const { player, economy } of all) incomes.set(player, (incomes.get(player) ?? 0) + economy.income)
  const paid: { entity: Entity; credits: number; earned: number; away: number }[] = []
  const { online, rules } = sim
  for (const [entity, player] of world.query(Player)) {
    const here = !online || online.has(player.id)
    const income = incomes.get(player.id) ?? 0
    let gain = income * time.step
    if (!here) gain = Math.max(0, Math.min(gain * rules.offlineIncome, income * rules.offlineMinutes * 60 - player.away))
    // В сети — отсутствие кончилось, и потолок снова полный.
    const away = here ? 0 : player.away + gain
    if (gain <= 0 && away === player.away) continue
    const earned = player.earned + gain
    const whole = Math.floor(earned)
    paid.push({ entity, credits: player.credits + whole, earned: earned - whole, away })
  }
  for (const { entity, credits, earned, away } of paid) world.set(entity, Player, { credits, earned, away })
  // Износ — в самом конце: разрушенное здание исчезает из мира, а зоны этого тика о нём ещё помнят.
  wear(sim, all)
}
