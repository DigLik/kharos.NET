import type { Entity } from '@kharos/ecs'
import { BUILDINGS, buildingSpec, isReady } from './buildings'
import { rectDistance } from './common'
import { allied } from './allies'
import { Blast, Building, Burst, Chaser, Glare, Interceptor, Mark, Missile, Off, Owner, Position, Silo, Site } from './components'
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
 * Ракета летит по прямой speed тайлов в секунду; её весь полёт видят все игроки, где бы она ни была, — это событие
 * на весь мир, — а обычное ПВО её не бьёт. Со старта место пуска на LAUNCH_MARK секунд отмечено у всех, у кого есть
 * готовый радар. Точку удара весь полёт видят те, чьи здания или юниты в радиусе взрыва: список сверяется раз
 * в WATCH секунд — увёл всё из круга — метка пропала, завёл — появилась; за WARNING секунд до удара им звучит второй
 * сигнал (см. alerts.ts). Чужим ракета уходит без точки удара, только с курсом (wire.ts). Взрыв — Burst: бьёт всех без разбора, щит его не держит,
 * под защитой новичка урона нет (см. fight в combat.ts). Вспышка взрыва (Glare) на GLARE секунд снимает туман по всему
 * его кругу у всех игроков: виден и сам взрыв, и что от него осталось.
 *
 * Защита — перехватчик. Ракеты-перехватчики он собирает сам, по одной, пока включён и их меньше ёмкости. Каждой
 * сбивает одну чужую ракету (не свою и не союзника), чья точка удара в его круге, когда та в этот круг входит: решено сразу,
 * а ракета-перехватчик летит к точке встречи на курсе и успевает раньше удара всегда — и на краю круга. Чужим не видно,
 * сколько ракет у перехватчика: это знает только хозяин.
 */

/** Сколько секунд место пуска отмечено у игроков с радаром. */
export const LAUNCH_MARK = 60
/** За сколько секунд до удара тем, чьё под ним, звучит второй сигнал. */
export const WARNING = 15
/** Раз во сколько секунд сверяется, чьё под ударом: этим игрокам видна его точка. */
export const WATCH = 1
/** Сколько секунд вспышка взрыва открывает всем его круг. */
export const GLARE = 5

/**
 * Высота полёта ядерной ракеты над землёй, в тайлах, и за сколько секунд она её набирает после старта и теряет перед
 * ударом. На симуляцию высота не влияет, но по ней рисуются и ракета, и её встреча с перехватчиком, и вспышка сбитой.
 */
export const CRUISE = 2.5
export const CLIMB = 3

/** На какой высоте ракета, которая летит age секунд и которой осталось left тайлов до точки удара. */
export const missileLift = (age: number, left: number) => CRUISE * Math.max(0, Math.min(1, age / CLIMB, left / (BUILDINGS.silo.warhead.speed * CLIMB)))

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

/** Перехватчик этого вида; у других зданий — undefined. */
const interceptOf = (sim: Sim, entity: Entity) => {
  const type = sim.world.get(entity, Building)?.type
  return type === undefined ? undefined : buildingSpec(type).intercept
}

/** Почему перехватчик не собирает ракету: выключен, не хватает кредитов или готового в сети. */
export type InterceptorLack = 'off' | 'credits' | 'materials'

/** Что мешает перехватчику начать сборку следующей ракеты; undefined — ничего, он уже собирает или полон. */
export function interceptorLack(sim: Sim, entity: Entity): InterceptorLack | undefined {
  const spec = interceptOf(sim, entity)
  const state = sim.world.get(entity, Interceptor)
  const player = sim.world.get(entity, Owner)?.player ?? 0
  if (!spec || !state || state.ticks > 0 || state.missiles >= spec.capacity) return undefined
  if (sim.world.has(entity, Off)) return 'off'
  if (creditsOf(sim, player) < spec.cost) return 'credits'
  return networkHas(sim, entity, spec.materials) ? undefined : 'materials'
}

/** Сколько тиков ещё лететь ракете: по оставшемуся пути. */
const flightLeft = (sim: Sim, missile: { x: number; y: number; toX: number; toY: number }, speed: number) =>
  Math.ceil(Math.hypot(missile.toX - missile.x, missile.toY - missile.y) / (speed * sim.time.step))

/**
 * Через сколько тиков ракета-перехватчик со скоростью speed из (x, y) встретит ядерную ракету: в тик +k, до своего
 * шага, ракета пролетела k шагов, а на шаге left − 1 долетает. Не успевает по скорости — встреча в последний тик
 * до удара: перехват уже решён, скорость перехватчика только для картинки. Ноль — сбить сразу.
 */
function meetTicks(sim: Sim, missile: { x: number; y: number; toX: number; toY: number }, x: number, y: number, speed: number) {
  const { step } = sim.time
  const flight = BUILDINGS.silo.warhead.speed * step
  const reach = Math.hypot(missile.toX - missile.x, missile.toY - missile.y)
  const last = Math.ceil(reach / flight) - 1
  for (let k = 1; k < last; k++) {
    const flown = Math.min(reach, flight * k) / (reach || 1)
    const meetX = missile.x + (missile.toX - missile.x) * flown
    const meetY = missile.y + (missile.toY - missile.y) * flown
    if (Math.hypot(meetX - x, meetY - y) <= speed * step * k) return k
  }
  return Math.max(0, last)
}

/**
 * Перехватчики: включённый, у которого ракет меньше ёмкости, начинает сборку следующей, как только хватает кредитов
 * и готового в сети, — цена списывается сразу; сборка идёт долей энергии. С ракетами он стреляет по чужой ядерной,
 * которая вошла в его круг и метит в него же, — по ракете на каждую; из нескольких — ближайший к ней. power — доли
 * энергии, считаются по нужде.
 */
function intercept(sim: Sim, power: () => ReadonlyMap<Entity, number>) {
  const { world, time } = sim
  const armed: { entity: Entity; player: number; x: number; y: number; radius: number; speed: number }[] = []
  for (const [entity, state, building, position, owner] of world.query(Interceptor, Building, Position, Owner)) {
    const spec = buildingSpec(building.type)
    if (!spec.intercept || world.has(entity, Site) || world.has(entity, Off)) continue
    if (state.ticks > 0) {
      state.progress += power().get(entity) ?? 0
      if (state.progress >= state.ticks) Object.assign(state, { progress: 0, ticks: 0, missiles: state.missiles + 1 })
    } else if (!interceptorLack(sim, entity) && state.missiles < spec.intercept.capacity && pay(sim, owner.player, spec.intercept.cost)) {
      takeFromNetwork(sim, entity, spec.intercept.materials)
      Object.assign(state, { progress: 0, ticks: Math.round(spec.intercept.seconds / time.step) })
    }
    if (state.missiles > 0) armed.push({ entity, player: owner.player, x: position.x + spec.width / 2, y: position.y + spec.height / 2, ...spec.intercept })
  }
  if (!armed.length) return
  for (const [entity, missile] of world.query(Missile)) {
    if (missile.hunted >= 0) continue
    let best: (typeof armed)[number] | undefined
    let bestReach = Infinity
    for (const guard of armed) {
      if (guard.player === missile.player || allied(sim, guard.player, missile.player)) continue
      if (!world.get(guard.entity, Interceptor)?.missiles) continue
      const reach = Math.hypot(missile.x - guard.x, missile.y - guard.y)
      if (reach > guard.radius || reach >= bestReach || Math.hypot(missile.toX - guard.x, missile.toY - guard.y) > guard.radius) continue
      best = guard
      bestReach = reach
    }
    if (!best) continue
    world.get(best.entity, Interceptor)!.missiles--
    const k = meetTicks(sim, missile, best.x, best.y, best.speed)
    missile.hunted = time.tick + k
    if (k === 0) continue
    const reach = Math.hypot(missile.toX - missile.x, missile.toY - missile.y) || 1
    const ahead = Math.min(reach, BUILDINGS.silo.warhead.speed * time.step * k)
    world.spawn(
      Chaser({
        fromX: best.x,
        fromY: best.y,
        toX: missile.x + ((missile.toX - missile.x) * ahead) / reach,
        toY: missile.y + ((missile.toY - missile.y) * ahead) / reach,
        lift: missileLift((time.tick + k - missile.born) * time.step, reach - ahead),
        born: time.tick,
        arrive: time.tick + k,
        player: best.player,
        missile: entity,
      }),
    )
  }
}

/** Размер вспышки сбитой ракеты, в тайлах: картинка, урона нет. */
const AIRBURST = 1.5

/**
 * Раз в тик, до боя: шахты собирают боеголовки и пускают ракеты, перехватчики собирают свои и стреляют, ракеты
 * летят и рвутся — их взрыв бьёт в этот же бой, — или гибнут от перехватчика; метки, чей срок вышел, пропадают.
 */
export function launchMissiles(sim: Sim) {
  const { world, time } = sim
  let supply: ReadonlyMap<Entity, number> | undefined
  const power = () => (supply ??= powerSupply(sim))
  const fired: { silo: Entity; player: number; x: number; y: number; toX: number; toY: number }[] = []
  for (const [entity, silo, building, position, owner] of world.query(Silo, Building, Position, Owner)) {
    if (silo.ticks > 0 && !silo.ready && !world.has(entity, Off)) {
      // Энергии не хватает — сборка идёт медленнее. Считается недёшево, поэтому только когда что-то собирают.
      silo.progress += power().get(entity) ?? 0
      if (silo.progress >= silo.ticks) Object.assign(silo, { progress: 0, ticks: 0, cost: 0, ready: true })
    }
    const warhead = buildingSpec(building.type).warhead
    if (!warhead || silo.start < 0 || time.tick - silo.start < Math.round(warhead.launch / time.step)) continue
    const { width, height } = buildingSpec(building.type)
    fired.push({ silo: entity, player: owner.player, x: position.x + width / 2, y: position.y + height / 2, toX: silo.toX, toY: silo.toY })
  }
  intercept(sim, power)
  // Ракета несёт боеголовку шахты: вид шахты один.
  const { speed, blast } = BUILDINGS.silo.warhead
  const arrived: Entity[] = []
  const downed: Entity[] = []
  /** Игроки, чьё сейчас под ударом ракеты, кроме её хозяина: он точку удара знает и так. */
  const under = (missile: { toX: number; toY: number; player: number }) => playersNear(sim, missile.toX, missile.toY, blast.radius).filter((viewer) => viewer !== missile.player)
  for (const [entity, missile] of world.query(Missile)) {
    if (missile.hunted >= 0 && time.tick >= missile.hunted) {
      downed.push(entity)
      continue
    }
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
    // Под удар за время полёта входят и из-под него уходят.
    const mark = (time.tick - missile.born) % Math.round(WATCH / time.step) ? undefined : world.get(missile.mark as Entity, Mark)
    if (mark) mark.viewers = under(missile)
  }
  // Сбитая гибнет в воздухе: вспышка без урона, метка точки удара пропадает.
  for (const entity of downed) {
    const missile = world.get(entity, Missile)!
    if (missile.mark >= 0 && world.has(missile.mark as Entity, Mark)) world.destroy(missile.mark as Entity)
    const lift = missileLift((time.tick - missile.born) * time.step, Math.hypot(missile.toX - missile.x, missile.toY - missile.y))
    world.spawn(Position({ x: missile.x, y: missile.y }), Blast({ size: AIRBURST, lift, life: Math.round((0.25 + AIRBURST * 0.2) / time.step) }))
    world.destroy(entity)
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
    const missile = world.get(world.spawn(Missile({ fromX: x, fromY: y, toX, toY, born: time.tick, x, y, player })), Missile)!
    // Точка удара отмечена весь полёт; метку убирает сама ракета, срок — с запасом в тик.
    missile.mark = world.spawn(Position({ x: toX, y: toY }), Mark({ kind: 'strike', until: time.tick + flightLeft(sim, missile, speed) + 1, player, viewers: under(missile) }))
    const viewers = radarOwners(sim).filter((viewer) => viewer !== player)
    world.spawn(Position({ x, y }), Mark({ kind: 'launch', until: time.tick + Math.round(LAUNCH_MARK / time.step), player, viewers }))
  }

  const expired: Entity[] = []
  for (const [entity, mark] of world.query(Mark)) if (time.tick >= mark.until) expired.push(entity)
  for (const [entity, glare] of world.query(Glare)) if (time.tick >= glare.until) expired.push(entity)
  for (const [entity, chaser] of world.query(Chaser)) if (time.tick >= chaser.arrive) expired.push(entity)
  for (const entity of expired) world.destroy(entity)
}
