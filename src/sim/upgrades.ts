import type { Entity } from '../ecs'
import { BUILDINGS, MAX_LEVEL, isReady, levelGain, upgradeCost } from './buildings'
import { Building, Converting, Inventory, Position, Upgrade } from './components'
import { assignBuilders } from './construction'
import { dropItems } from './drops'
import { addCredits, creditsOf, pay } from './economy'
import { put, take } from './inventory'
import { givenBy, offersOf } from './logistics'
import { entriesOf, type Amounts, type Good } from './resources'
import type { Sim } from './sim'
import { isStore } from './trade'
import { networkOf } from './zones'

/**
 * Уровни зданий. Шахту, переработку, хранилище, космопорт и электростанцию поднимают до MAX_LEVEL: каждый уровень
 * прибавляет LEVEL_BONUS к тому, что здание делает (см. levelGain и levelFactor в buildings.ts). Цена — кредиты
 * и готовое: стройблоки, выше — ещё и компоненты. Списывается она сразу при заказе, и готовое берётся только
 * из сети труб самого здания: отрезанное от базы здание не улучшить, пока к нему не подведут трубу или не завезут
 * груз в хранилище его сети. Работу делают строители, как стройку; здание всё это время работает.
 */

/** Почему здание нельзя улучшить сейчас: не хватает кредитов или готового в его сети. */
export type UpgradeLack = 'credits' | 'materials'

/** Сколько груза сеть здания может отдать на его улучшение: что отдают склады сети по заявкам, без резерва хранилищ. */
function stockFor(sim: Sim, building: Entity, resource: Good) {
  let amount = 0
  for (const source of networkOf(sim, building)?.buildings ?? []) {
    if (offersOf(sim, source).includes(resource)) amount += givenBy(sim, source, resource)
  }
  return amount
}

/** Поднимают ли это здание игрока сейчас: своё, готовое, из тех, что улучшают, не на верхнем уровне и не в работе. */
export function isUpgradable(sim: Sim, player: number, building: Entity) {
  const { world } = sim
  const built = world.get(building, Building)
  if (!built || !isReady(sim, player, building) || world.has(building, Converting) || world.has(building, Upgrade)) return false
  return levelGain(built.type) !== undefined && built.level < MAX_LEVEL
}

/** Чего не хватает на следующий уровень здания; undefined — всего хватает. */
export function upgradeLack(sim: Sim, player: number, building: Entity): UpgradeLack | undefined {
  const built = sim.world.get(building, Building)
  if (!built) return undefined
  const { credits, materials } = upgradeCost(built.type, built.level)
  if (creditsOf(sim, player) < credits) return 'credits'
  // Долю единицы склады отдают неровно: недостачу меньше погрешности прощают, как стройке.
  return entriesOf(materials).some(([resource, amount]) => stockFor(sim, building, resource) < amount - 1e-6) ? 'materials' : undefined
}

/**
 * Заказывает следующий уровень своего готового здания и посылает к нему строителей. Кредиты и готовое списываются
 * сразу: готовое — со складов сети здания, сначала из хранилищ. Возвращает, принят ли заказ.
 */
export function orderUpgrade(sim: Sim, player: number, building: Entity, builders: Entity[]) {
  const { world } = sim
  if (!isUpgradable(sim, player, building) || upgradeLack(sim, player, building)) return false
  const { type, level } = world.get(building, Building)!
  const { credits, materials } = upgradeCost(type, level)
  if (!pay(sim, player, credits)) return false
  const sources = [...(networkOf(sim, building)?.buildings ?? [])].sort((a, b) => Number(isStore(sim, b)) - Number(isStore(sim, a)))
  for (const [resource, amount] of entriesOf(materials)) {
    let left = amount
    for (const source of sources) {
      if (left <= 1e-9) break
      if (!offersOf(sim, source).includes(resource)) continue
      left -= take(world.get(source, Inventory)!, resource, Math.min(left, givenBy(sim, source, resource)))
    }
  }
  world.add(building, Upgrade())
  assignBuilders(sim, player, building, builders)
  return true
}

/**
 * Отменяет улучшение: кредиты возвращаются целиком, готовое — в хранилища сети здания, а чему там нет места —
 * на землю у здания. Вложенная работа пропадает.
 */
export function cancelUpgrade(sim: Sim, player: number, building: Entity) {
  const { world } = sim
  const built = world.get(building, Building)
  if (!built || !world.has(building, Upgrade) || !isReady(sim, player, building)) return false
  const { credits, materials } = upgradeCost(built.type, built.level)
  world.remove(building, Upgrade)
  addCredits(sim, player, credits)
  const left: Amounts = {}
  const stores = (networkOf(sim, building)?.buildings ?? []).filter((entity) => isStore(sim, entity))
  for (const [resource, amount] of entriesOf(materials)) {
    let rest = amount
    for (const store of stores) rest -= put(world.get(store, Inventory)!, resource, rest)
    if (rest > 1e-9) left[resource] = rest
  }
  const { x, y } = world.get(building, Position)!
  dropItems(sim, x + Math.floor(BUILDINGS[built.type].width / 2), y + Math.floor(BUILDINGS[built.type].height / 2), left)
  return true
}
