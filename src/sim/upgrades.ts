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
import { hasResearch, type Topic } from './topics'
import { networkOf } from './zones'

/**
 * Уровни зданий. Шахту, переработку, хранилище, космопорт и электростанцию поднимают до MAX_LEVEL: каждый уровень
 * прибавляет LEVEL_BONUS к тому, что здание делает (см. levelGain и levelFactor в buildings.ts). Цена — кредиты
 * и готовое: стройблоки, выше — ещё и компоненты. Списывается она сразу при заказе, и готовое берётся только
 * из сети труб самого здания: отрезанное от базы здание не улучшить, пока к нему не подведут трубу или не завезут
 * груз в хранилище его сети. Работу делают строители, как стройку; здание всё это время работает. Второй и третий
 * уровни открывают исследования, см. research.ts.
 */

/** Почему здание нельзя улучшить сейчас: уровень не исследован, не хватает кредитов или готового в его сети. */
export type UpgradeLack = 'research' | 'credits' | 'materials'

/** Какое исследование открывает подъём здания с уровня level. */
export const levelTopic = (level: number): Topic => (level >= 2 ? 'levels3' : 'levels2')

/** Сколько груза сеть здания может отдать на его нужды сразу: что отдают склады сети по заявкам, без резерва хранилищ. */
export function networkStock(sim: Sim, building: Entity, resource: Good) {
  let amount = 0
  for (const source of networkOf(sim, building)?.buildings ?? []) {
    if (offersOf(sim, source).includes(resource)) amount += givenBy(sim, source, resource)
  }
  return amount
}

/** Хватает ли в сети здания готового на эту цену. Долю единицы склады отдают неровно: недостачу меньше погрешности прощают, как стройке. */
export const networkHas = (sim: Sim, building: Entity, materials: Amounts) =>
  entriesOf(materials).every(([resource, amount]) => networkStock(sim, building, resource) >= amount - 1e-6)

/** Забирает готовое со складов сети здания, сначала из хранилищ. */
export function takeFromNetwork(sim: Sim, building: Entity, materials: Amounts) {
  const sources = [...(networkOf(sim, building)?.buildings ?? [])].sort((a, b) => Number(isStore(sim, b)) - Number(isStore(sim, a)))
  for (const [resource, amount] of entriesOf(materials)) {
    let left = amount
    for (const source of sources) {
      if (left <= 1e-9) break
      if (!offersOf(sim, source).includes(resource)) continue
      left -= take(sim.world.get(source, Inventory)!, resource, Math.min(left, givenBy(sim, source, resource)))
    }
  }
}

/** Возвращает готовое в хранилища сети здания, а чему там нет места — бросает на землю у здания. */
export function returnToNetwork(sim: Sim, building: Entity, materials: Amounts) {
  const { world } = sim
  const left: Amounts = {}
  const stores = (networkOf(sim, building)?.buildings ?? []).filter((entity) => isStore(sim, entity))
  for (const [resource, amount] of entriesOf(materials)) {
    let rest = amount
    for (const store of stores) rest -= put(world.get(store, Inventory)!, resource, rest)
    if (rest > 1e-9) left[resource] = rest
  }
  const { x, y } = world.get(building, Position)!
  const { width, height } = BUILDINGS[world.get(building, Building)!.type]
  dropItems(sim, x + Math.floor(width / 2), y + Math.floor(height / 2), left)
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
  if (!hasResearch(sim, player, levelTopic(built.level))) return 'research'
  const { credits, materials } = upgradeCost(built.type, built.level, sim.rules.upgradeScale)
  if (creditsOf(sim, player) < credits) return 'credits'
  return networkHas(sim, building, materials) ? undefined : 'materials'
}

/**
 * Заказывает следующий уровень своего готового здания и посылает к нему строителей. Кредиты и готовое списываются
 * сразу: готовое — со складов сети здания, сначала из хранилищ. Возвращает, принят ли заказ.
 */
export function orderUpgrade(sim: Sim, player: number, building: Entity, builders: Entity[]) {
  const { world } = sim
  if (!isUpgradable(sim, player, building) || upgradeLack(sim, player, building)) return false
  const { type, level } = world.get(building, Building)!
  const scale = sim.rules.upgradeScale
  const { credits, materials } = upgradeCost(type, level, scale)
  if (!pay(sim, player, credits)) return false
  takeFromNetwork(sim, building, materials)
  world.add(building, Upgrade({ scale }))
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
  const upgrade = world.get(building, Upgrade)
  if (!built || !upgrade || !isReady(sim, player, building)) return false
  const { credits, materials } = upgradeCost(built.type, built.level, upgrade.scale)
  world.remove(building, Upgrade)
  addCredits(sim, player, credits)
  returnToNetwork(sim, building, materials)
  return true
}
