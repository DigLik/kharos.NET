import type { Entity } from '@kharos/ecs'
import { BUILDINGS, CORE, canPlace, placeBuilding } from './buildings'
import { clearDrops } from './drops'
import { isOwn, onTurn, ownerOf } from './common'
import { Building, Converting, Deploys, Health, Path, Position, Producer, Unit } from './components'
import { reward } from './economy'
import type { Sim } from './sim'
import { clearGround, inBounds, orderMove, spawnUnit } from './units'
import { inForeignZone } from './zones'

/** Сколько секунд MCV разворачивается в главное здание и сколько здание сворачивается обратно. */
export const DEPLOY_SECONDS = 3
export const PACK_SECONDS = 10

/** Где встанет главное здание, если MCV развернётся на месте: левый верхний тайл основания. */
export function siteUnder(sim: Sim, entity: Entity) {
  const position = sim.world.get(entity, Position)!
  const { width, height } = BUILDINGS[CORE]
  return { x: Math.floor(position.x) - Math.floor(width / 2), y: Math.floor(position.y) - Math.floor(height / 2) }
}

/** Где встанет главное здание: куда MCV послан разворачиваться, а не послан — под ним. Левый верхний тайл основания. */
export function deploySite(sim: Sim, entity: Entity) {
  const target = sim.world.get(entity, Deploys)
  return target ? { x: target.x, y: target.y } : siteUnder(sim, entity)
}

/**
 * Может ли MCV развернуться главным зданием с левым верхним тайлом (x, y): это MCV игрока, он не разворачивается,
 * там свободная скала или фундамент и не чужая зона.
 */
export function canDeployAt(sim: Sim, player: number, entity: Entity, x: number, y: number) {
  if (!canSendDeploy(sim, player, entity) || !canPlace(sim, CORE, x, y)) return false
  return !inForeignZone(sim, player, x, y, BUILDINGS[CORE].width, BUILDINGS[CORE].height)
}

/** Может ли игрок развернуть этот MCV прямо на месте. */
export function canDeploy(sim: Sim, player: number, entity: Entity) {
  if (!canSendDeploy(sim, player, entity)) return false
  const site = siteUnder(sim, entity)
  return canDeployAt(sim, player, entity, site.x, site.y)
}

/** Можно ли послать этот MCV разворачиваться: это MCV игрока, и он ещё не разворачивается. */
export function canSendDeploy(sim: Sim, player: number, entity: Entity) {
  const { world } = sim
  return world.get(entity, Unit)?.type === 'mcv' && isOwn(sim, player, entity) && !world.has(entity, Converting)
}

/** Стоит ли MCV на основании будущего здания с левым верхним тайлом (x, y): дальше ехать незачем. */
function standsOn(sim: Sim, entity: Entity, x: number, y: number) {
  const position = sim.world.get(entity, Position)!
  const { width, height } = BUILDINGS[CORE]
  return position.x >= x && position.x < x + width && position.y >= y && position.y < y + height
}

/** Середина основания главного здания с левым верхним тайлом (x, y): туда едет MCV. */
const middleOf = (x: number, y: number) => ({ x: x + Math.floor(BUILDINGS[CORE].width / 2), y: y + Math.floor(BUILDINGS[CORE].height / 2) })

/**
 * Посылает MCV развернуться главным зданием с левым верхним тайлом (x, y): стоит на этом месте — разворачивается
 * сразу, иначе едет туда. Годится ли место, решается на месте: в тумане там может стоять чужое, о чём игрок не
 * знает, и отказ сразу выдал бы это. Возвращает, принят ли приказ.
 */
export function orderDeploy(sim: Sim, player: number, entity: Entity, x: number, y: number) {
  if (!canSendDeploy(sim, player, entity)) return false
  const { width, height } = BUILDINGS[CORE]
  if (!inBounds(sim, x, y) || !inBounds(sim, x + width - 1, y + height - 1)) return false
  const { world } = sim
  if (standsOn(sim, entity, x, y)) {
    if (!canDeployAt(sim, player, entity, x, y)) return false
    world.add(entity, Deploys({ x, y, misses: 0 }))
    startConverting(sim, entity, DEPLOY_SECONDS)
    return true
  }
  world.add(entity, Deploys({ x, y, misses: 0 }))
  const middle = middleOf(x, y)
  orderMove(sim, entity, middle.x, middle.y)
  return true
}

/** Раз во сколько тиков MCV, который не доехал до места, пробует снова; с каждым промахом — вдвое реже. */
const DEPLOY_RETRY_TICKS = 20
/** После скольких промахов подряд MCV бросает поездку: к месту не подъехать. */
const DEPLOY_MISSES = 5

/**
 * Раз в тик: посланные разворачиваться MCV, доехав, разворачиваются. Место оказалось негодным — приказ снимается,
 * MCV остаётся машиной. Не доехал — пробует снова, всё реже, и после DEPLOY_MISSES промахов бросает.
 */
export function deployOrders(sim: Sim) {
  const { world, time } = sim
  const arrived: Entity[] = []
  const late: Entity[] = []
  for (const [entity, deploys] of world.query(Deploys)) {
    if (world.has(entity, Converting) || world.has(entity, Path)) continue
    if (standsOn(sim, entity, deploys.x, deploys.y)) arrived.push(entity)
    else if (onTurn(time, entity, DEPLOY_RETRY_TICKS << deploys.misses)) late.push(entity)
  }
  // Состав компонентов меняется после обхода.
  for (const entity of arrived) {
    const { x, y } = world.get(entity, Deploys)!
    if (canDeployAt(sim, ownerOf(sim, entity), entity, x, y)) startConverting(sim, entity, DEPLOY_SECONDS)
    else world.remove(entity, Deploys)
  }
  for (const entity of late) {
    const deploys = world.get(entity, Deploys)!
    if (++deploys.misses > DEPLOY_MISSES) {
      world.remove(entity, Deploys)
      continue
    }
    const middle = middleOf(deploys.x, deploys.y)
    orderMove(sim, entity, middle.x, middle.y)
  }
}

/** Может ли игрок свернуть это здание: это его главное здание, и оно не сворачивается уже. */
export function canPack(sim: Sim, player: number, entity: Entity) {
  const { world } = sim
  if (world.get(entity, Building)?.type !== CORE || !isOwn(sim, player, entity)) return false
  return !world.has(entity, Converting)
}

/** Раз во сколько тиков разворачивающийся MCV просит своих юнитов уйти из-под будущего здания. */
const EVICT_TICKS = 20

/** Отменяет разворачивание или поездку на него: MCV остаётся машиной, стоит и снова слушается приказов. */
export function cancelDeploy(sim: Sim, player: number, entity: Entity) {
  const { world } = sim
  if (!world.has(entity, Unit) || !isOwn(sim, player, entity)) return false
  if (!world.has(entity, Converting) && !world.has(entity, Deploys)) return false
  if (!world.has(entity, Converting)) world.remove(entity, Path)
  world.remove(entity, Converting)
  world.remove(entity, Deploys)
  return true
}

/** Ждёт ли MCV, пока из-под будущего здания уйдут юниты: время разворачивания вышло, а место занято. */
export function isDeployBlocked(sim: Sim, entity: Entity) {
  const converting = sim.world.get(entity, Converting)
  return converting !== undefined && sim.world.has(entity, Unit) && converting.left <= 0
}

/**
 * Освобождает место под главное здание и говорит, свободно ли оно — правило то же, что у стройки.
 * Свои стоящие юниты с evict уходят сами; тех, кто едет, не трогают; чужих остаётся только ждать.
 */
function clearDeploySite(sim: Sim, mcv: Entity, evict: boolean) {
  const site = deploySite(sim, mcv)
  const { width, height } = BUILDINGS[CORE]
  return clearGround(sim, ownerOf(sim, mcv), site.x, site.y, width, height, evict, mcv)
}

/** Начинает превращение: оно закончится через seconds. */
export function startConverting(sim: Sim, entity: Entity, seconds: number) {
  const ticks = Math.round(seconds / sim.time.step)
  sim.world.remove(entity, Path)
  sim.world.add(entity, Converting({ left: ticks, total: ticks }))
}

/**
 * Раз в тик: продвигает превращения и завершает те, чьё время вышло. MCV, под которым стоят юниты,
 * зданием не становится: свои из-под него уходят, пока он разворачивается, а чужих он ждёт.
 */
export function convert(sim: Sim) {
  const { world, time } = sim
  const ready: Entity[] = []
  const deploying: Entity[] = []
  for (const [entity, converting] of world.query(Converting)) {
    converting.left = Math.max(0, converting.left - 1)
    if (world.has(entity, Unit)) deploying.push(entity)
    if (converting.left <= 0) ready.push(entity)
  }
  // Состав мира и приказы меняются после обхода. Выгонять пробуют не каждый тик: поиск пути недёшев.
  const blocked = new Set<Entity>()
  for (const entity of deploying) {
    if (!clearDeploySite(sim, entity, onTurn(time, entity, EVICT_TICKS))) blocked.add(entity)
  }
  const done = ready.filter((entity) => !blocked.has(entity))

  // Сущности заменяются после обхода: во время него состав мира менять нельзя.
  for (const entity of done) {
    world.remove(entity, Converting)
    const player = ownerOf(sim, entity)
    // Очередь производства переезжает вместе с игроком: заказы не теряются.
    const production = world.get(entity, Producer)
    // Прочность тоже: свернув разбитое здание, целую машину не получить.
    const health = world.get(entity, Health)?.value ?? 1

    if (world.has(entity, Unit)) {
      const site = deploySite(sim, entity)
      // За время разворачивания место могли занять. Тогда MCV остаётся машиной.
      if (!canPlace(sim, CORE, site.x, site.y)) {
        world.remove(entity, Deploys)
        continue
      }
      world.destroy(entity)
      clearDrops(sim, site.x, site.y, BUILDINGS[CORE].width, BUILDINGS[CORE].height)
      const core = placeBuilding(world, CORE, site.x, site.y, player)
      if (production) world.set(core, Producer, production)
      world.set(core, Health, { value: health })
      reward(sim, player, 'deploy')
    } else {
      const position = world.get(entity, Position)!
      const { width, height } = BUILDINGS[CORE]
      world.destroy(entity)
      const mcv = spawnUnit(sim, 'mcv', player, position.x + Math.floor(width / 2), position.y + Math.floor(height / 2))
      if (production) world.set(mcv, Producer, production)
      world.set(mcv, Health, { value: health })
    }
  }
}
