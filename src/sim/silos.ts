import type { Entity } from '../ecs'
import { BUILDINGS, buildingSpec, isReady } from './buildings'
import { rectDistance } from './common'
import { Building, Burst, Glare, Mark, Missile, Off, Owner, Position, Silo, Site } from './components'
import { addCredits, creditsOf, pay } from './economy'
import { powerSupply } from './income'
import { unprotect } from './shelter'
import type { Sim } from './sim'
import { inBounds } from './units'
import { networkHas, returnToNetwork, takeFromNetwork } from './upgrades'

/**
 * Ядерный удар (§9 п. 4 design.md). Ракетная шахта собирает боеголовку, как техцентр тему: цена — кредиты и готовое
 * из сети труб шахты — списывается сразу, сборка идёт при полной энергии BuildingSpec.warhead.seconds секунд,
 * при нехватке медленнее, в выключенной шахте стоит. Боеголовка одна за раз. Готовую игрок наводит на любую точку
 * карты: через launch секунд после приказа ракета стартует — до этого пуск можно отменить, — и шахта снова пуста.
 * О начале пуска хост пишет всем в чат, без места и цели (см. host.ts).
 *
 * Ракета летит по прямой speed тайлов в секунду; её видно, где у игрока обзор, и обычное ПВО её не бьёт. Со старта
 * место пуска на LAUNCH_MARK секунд отмечено у всех, у кого есть готовый радар; за WARNING секунд до удара точку
 * удара видят те, чьи здания или юниты в радиусе взрыва. Взрыв — Burst: бьёт всех без разбора, щит его не держит,
 * под защитой новичка урона нет (см. fight в combat.ts). Вспышка взрыва (Glare) на GLARE секунд снимает туман по всему
 * его кругу у всех игроков: виден и сам взрыв, и что от него осталось.
 */

/** Сколько секунд место пуска отмечено у игроков с радаром. */
export const LAUNCH_MARK = 60
/** За сколько секунд до удара его точку видят те, чьё под ней. */
export const WARNING = 15
/** Сколько секунд вспышка взрыва открывает всем его круг. */
export const GLARE = 5

/** Боеголовка шахты этого вида; у других зданий — undefined. */
const warheadOf = (sim: Sim, silo: Entity) => {
  const type = sim.world.get(silo, Building)?.type
  return type === undefined ? undefined : buildingSpec(type).warhead
}

/** Своя готовая шахта игрока: её компонент и боеголовка вида. */
function ownSilo(sim: Sim, player: number, silo: Entity) {
  const warhead = warheadOf(sim, silo)
  const state = sim.world.get(silo, Silo)
  if (!warhead || !state || !isReady(sim, player, silo)) return undefined
  return { warhead, state }
}

/** Почему сборку не начать: шахта уже собирает или держит боеголовку, не хватает кредитов или готового в сети. */
export type WarheadLack = 'busy' | 'credits' | 'materials'

/** Что мешает начать сборку боеголовки в своей шахте; undefined — ничего. */
export function warheadLack(sim: Sim, player: number, silo: Entity): WarheadLack | undefined {
  const own = ownSilo(sim, player, silo)
  if (!own) return 'busy'
  if (own.state.ticks > 0 || own.state.ready) return 'busy'
  if (creditsOf(sim, player) < own.warhead.cost) return 'credits'
  return networkHas(sim, silo, own.warhead.materials) ? undefined : 'materials'
}

/** Начинает сборку боеголовки в своей пустой шахте: кредиты и готовое списываются сразу. */
export function startWarhead(sim: Sim, player: number, silo: Entity) {
  const own = ownSilo(sim, player, silo)
  if (!own || warheadLack(sim, player, silo) || !pay(sim, player, own.warhead.cost)) return false
  takeFromNetwork(sim, silo, own.warhead.materials)
  Object.assign(own.state, { progress: 0, ticks: Math.round(own.warhead.seconds / sim.time.step), cost: own.warhead.cost })
  return true
}

/** Бросает сборку: цена возвращается целиком, вложенное время пропадает. Готовую боеголовку так не разобрать. */
export function cancelWarhead(sim: Sim, player: number, silo: Entity) {
  const own = ownSilo(sim, player, silo)
  if (!own || own.state.ticks <= 0 || own.state.ready) return false
  addCredits(sim, player, own.state.cost)
  returnToNetwork(sim, silo, own.warhead.materials)
  Object.assign(own.state, { progress: 0, ticks: 0, cost: 0 })
  return true
}

/**
 * Приказ о пуске готовой боеголовки в тайл (x, y) карты: ракета стартует через warhead.launch секунд. Наведённую
 * можно навести заново, пока она не стартовала. Пуск — нападение: он снимает с игрока защиту новичка.
 */
export function orderLaunch(sim: Sim, player: number, silo: Entity, x: number, y: number) {
  const own = ownSilo(sim, player, silo)
  if (!own || !own.state.ready || !inBounds(sim, x, y)) return false
  if (own.state.start < 0) own.state.start = sim.time.tick
  own.state.toX = x + 0.5
  own.state.toY = y + 0.5
  unprotect(sim, player)
  return true
}

/** Отменяет пуск, пока ракета не стартовала: боеголовка остаётся в шахте. */
export function abortLaunch(sim: Sim, player: number, silo: Entity) {
  const own = ownSilo(sim, player, silo)
  if (!own || own.state.start < 0) return false
  own.state.start = -1
  return true
}

/** Сколько тиков до старта ракеты у шахты в пуске; undefined — пуск не идёт. */
export function launchLeft(sim: Sim, silo: Entity) {
  const state = sim.world.get(silo, Silo)
  const warhead = warheadOf(sim, silo)
  if (!state || !warhead || state.start < 0) return undefined
  return Math.max(0, state.start + Math.round(warhead.launch / sim.time.step) - sim.time.tick)
}

/** Игроки, у которых есть готовый радар: им видно место пуска. */
function radarOwners(sim: Sim) {
  const players = new Set<number>()
  for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
    if (buildingSpec(building.type).scan && !sim.world.has(entity, Site)) players.add(owner.player)
  }
  players.delete(0)
  return [...players]
}

/** Игроки, чьи здания или юниты не дальше radius от точки (x, y): им видна точка удара. */
function playersNear(sim: Sim, x: number, y: number, radius: number) {
  const players = new Set<number>()
  for (const [entity, owner, position] of sim.world.query(Owner, Position)) {
    if (!owner.player || players.has(owner.player)) continue
    const type = sim.world.get(entity, Building)?.type
    const { width, height } = type === undefined ? { width: 0, height: 0 } : buildingSpec(type)
    if (rectDistance(position.x, position.y, width, height, x, y) <= radius) players.add(owner.player)
  }
  return [...players]
}

/** Сколько тиков ещё лететь ракете: по оставшемуся пути. */
const flightLeft = (sim: Sim, missile: { x: number; y: number; toX: number; toY: number }, speed: number) =>
  Math.ceil(Math.hypot(missile.toX - missile.x, missile.toY - missile.y) / (speed * sim.time.step))

/**
 * Раз в тик, до боя: шахты собирают боеголовки и пускают ракеты, ракеты летят и рвутся — их взрыв бьёт в этот же
 * бой; метки, чей срок вышел, пропадают.
 */
export function launchMissiles(sim: Sim) {
  const { world, time } = sim
  let supply: Map<Entity, number> | undefined
  const fired: { silo: Entity; player: number; x: number; y: number; toX: number; toY: number }[] = []
  for (const [entity, silo, building, position, owner] of world.query(Silo, Building, Position, Owner)) {
    if (silo.ticks > 0 && !silo.ready && !world.has(entity, Off)) {
      // Энергии не хватает — сборка идёт медленнее. Считается недёшево, поэтому только когда что-то собирают.
      supply ??= powerSupply(sim)
      silo.progress += supply.get(entity) ?? 0
      if (silo.progress >= silo.ticks) Object.assign(silo, { progress: 0, ticks: 0, cost: 0, ready: true })
    }
    const warhead = buildingSpec(building.type).warhead
    if (!warhead || silo.start < 0 || time.tick - silo.start < Math.round(warhead.launch / time.step)) continue
    const { width, height } = buildingSpec(building.type)
    fired.push({ silo: entity, player: owner.player, x: position.x + width / 2, y: position.y + height / 2, toX: silo.toX, toY: silo.toY })
  }
  // Ракета несёт боеголовку шахты: вид шахты один.
  const { speed, blast } = BUILDINGS.silo.warhead
  const arrived: Entity[] = []
  for (const [entity, missile] of world.query(Missile)) {
    const reach = Math.hypot(missile.toX - missile.x, missile.toY - missile.y)
    const step = speed * time.step
    if (reach <= step) {
      missile.x = missile.toX
      missile.y = missile.toY
      arrived.push(entity)
      continue
    }
    missile.x += ((missile.toX - missile.x) / reach) * step
    missile.y += ((missile.toY - missile.y) / reach) * step
    if (missile.mark >= 0) continue
    const left = flightLeft(sim, missile, speed)
    if (left > Math.round(WARNING / time.step)) continue
    const viewers = playersNear(sim, missile.toX, missile.toY, blast.radius).filter((viewer) => viewer !== missile.player)
    missile.mark = world.spawn(Position({ x: missile.toX, y: missile.toY }), Mark({ kind: 'strike', until: time.tick + left, player: missile.player, viewers }))
  }
  for (const entity of arrived) {
    const missile = world.get(entity, Missile)!
    if (missile.mark >= 0 && world.has(missile.mark as Entity, Mark)) world.destroy(missile.mark as Entity)
    world.spawn(Position({ x: missile.toX, y: missile.toY }), Burst({ radius: blast.radius, damage: blast.damage, core: blast.core }))
    world.spawn(Position({ x: missile.toX, y: missile.toY }), Glare({ radius: blast.radius, until: time.tick + Math.round(GLARE / time.step) }))
    world.destroy(entity)
  }

  // Стартовавшая ракета трогается со следующего тика: в тик born она над шахтой, так её ведёт и клиент.
  for (const { silo, player, x, y, toX, toY } of fired) {
    Object.assign(world.get(silo, Silo)!, { ready: false, start: -1 })
    world.spawn(Missile({ fromX: x, fromY: y, toX, toY, born: time.tick, x, y, player }))
    const viewers = radarOwners(sim).filter((viewer) => viewer !== player)
    world.spawn(Position({ x, y }), Mark({ kind: 'launch', until: time.tick + Math.round(LAUNCH_MARK / time.step), player, viewers }))
  }

  const expired: Entity[] = []
  for (const [entity, mark] of world.query(Mark)) if (time.tick >= mark.until) expired.push(entity)
  for (const [entity, glare] of world.query(Glare)) if (time.tick >= glare.until) expired.push(entity)
  for (const entity of expired) world.destroy(entity)
}
