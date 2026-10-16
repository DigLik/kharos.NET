import type { Entity } from '@kharos/ecs'
import { allied } from './allies'
import { BUILDINGS, CORE, buildingSpec, isReady } from './buildings'
import { Building, Inventory, Owner, Position, Trade, Transfer } from './components'
import { dropItems } from './drops'
import { addCredits, pay } from './economy'
import { put, take } from './inventory'
import { GOODS, type Amounts, type Good } from './resources'
import type { Sim } from './sim'
import { flightTicks, SELL_SECONDS } from './trade'
import { zonesOf } from './zones'

/**
 * Корабль космопорта с подарком союзнику: деньги и груз. Всё отдаётся при отправке — деньги списываются, груз
 * уходит из хранилищ, — и всё выгружается у главного здания получателя через TRANSFER_SECONDS, как рейс
 * космопорта (см. trade.ts). Пока корабль летит, космопорт занят: и продавать, и слать второй подарок нельзя.
 * Получатель без главного здания — тогда и выгружать некуда — принять подарок не может: отправка отклоняется.
 * Юнитов так не передают: их управление отдают союзнику сразу, см. gift.ts. Уведомления об отправке и прибытии
 * шлёт хост по самой сущности корабля, см. host.ts.
 */

/** Сколько секунд летит корабль с подарком: столько же, сколько рейс космопорта. */
export const TRANSFER_SECONDS = SELL_SECONDS

/** Наибольшее, что корабль берёт одного груза: трюм не бездонный. */
export const TRANSFER_CAPACITY = 500

/** Главное здание игрока: к нему летит подарок. Нет его — принять нечего. */
export function coreOf(sim: Sim, player: number): Entity | undefined {
  for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
    if (building.type === CORE && owner.player === player && sim.world.has(entity, Position)) return entity
  }
  return undefined
}

/** Готовый ли свой космопорт и свободен ли он: корабль у космопорта один — ни рейса, ни второго подарка. */
export function canFerry(sim: Sim, player: number, port: Entity) {
  const type = sim.world.get(port, Building)?.type
  return type !== undefined && !!buildingSpec(type).trades && isReady(sim, player, port) && !sim.world.has(port, Transfer) && !sim.world.has(port, Trade)
}

/** Кладёт груз в хранилища всех зон игрока. Возвращает, сколько вошло. */
function putIntoStores(sim: Sim, player: number, resource: Good, amount: number) {
  let left = amount
  for (const zone of zonesOf(sim, player)) {
    for (const entity of zone.buildings) {
      const inventory = sim.world.get(entity, Inventory)
      const type = sim.world.get(entity, Building)?.type
      if (!inventory || type === undefined || !buildingSpec(type).stores) continue
      left -= put(inventory, resource, left)
      if (left <= 0) return amount
    }
  }
  return amount - left
}

/** Берёт груз из хранилищ всех зон игрока, сколько есть. Возвращает, сколько взято. */
export function takeFromStores(sim: Sim, player: number, resource: Good, amount: number) {
  let left = amount
  for (const zone of zonesOf(sim, player)) {
    for (const entity of zone.buildings) {
      const inventory = sim.world.get(entity, Inventory)
      const type = sim.world.get(entity, Building)?.type
      if (!inventory || type === undefined || !buildingSpec(type).stores) continue
      left -= take(inventory, resource, left)
      if (left <= 0) return amount
    }
  }
  return amount - left
}

/**
 * Отправляет подарок союзнику. Всё, что летит, отдаётся сразу: деньги списываются, груз забирается из хранилищ
 * по всему запасу игрока. Возвращает, вышел ли корабль.
 */
export function sendFerry(sim: Sim, player: number, port: Entity, to: number, credits: number, goods: Amounts) {
  if (to === player || !allied(sim, player, to)) return false
  if (!canFerry(sim, player, port)) return false
  const core = coreOf(sim, to)
  if (core === undefined) return false
  if (!Number.isFinite(credits) || credits < 0) return false
  const asked = Math.floor(credits)
  const cargo: Amounts = {}
  for (const resource of GOODS) {
    const amount = goods[resource] ?? 0
    if (!Number.isFinite(amount) || amount < 0) return false
    if (amount >= 1) cargo[resource] = Math.min(Math.floor(amount), TRANSFER_CAPACITY)
  }
  if (!asked && !Object.keys(cargo).length) return false
  if (asked && !pay(sim, player, asked)) return false
  for (const [resource, amount] of Object.entries(cargo) as [Good, number][]) {
    const got = takeFromStores(sim, player, resource, amount)
    if (got >= 1) cargo[resource] = got
    else delete cargo[resource]
  }
  // Ничего не набралось: забирать было нечего, и корабль не выходит — деньги остаются у игрока.
  if (!asked && !Object.keys(cargo).length) return false
  // Высадка — у центра главного здания: там груз и падает, если в хранилищах не нашлось места.
  const at = sim.world.get(core, Position)!
  const { width, height } = BUILDINGS[CORE]
  const ticks = flightTicks(sim, port, TRANSFER_SECONDS)
  sim.world.add(
    port,
    Transfer({ to, x: at.x + Math.floor(width / 2), y: at.y + Math.floor(height / 2), credits: asked, goods: cargo, left: ticks, total: ticks }),
  )
  return true
}

/** Раз в тик: корабли с подарками долетают и выгружаются. */
export function ferry(sim: Sim) {
  const { world } = sim
  /** Что не вошло в хранилища: упадёт на землю после обхода — во время него мир не меняют, см. ниже. */
  const spills: { x: number; y: number; items: Amounts }[] = []
  for (const [, transfer] of world.query(Transfer)) {
    if (transfer.done) continue
    if (--transfer.left > 0) continue
    if (transfer.credits) addCredits(sim, transfer.to, transfer.credits)
    // Груз идёт в хранилища получателя; то, что не вошло, падает на землю у его главного здания.
    const spill: Amounts = {}
    for (const [resource, amount] of Object.entries(transfer.goods) as [Good, number][]) {
      const stored = putIntoStores(sim, transfer.to, resource, amount)
      if (amount - stored >= 1e-9) spill[resource] = amount - stored
    }
    if (Object.keys(spill).length) spills.push({ x: transfer.x, y: transfer.y, items: spill })
    transfer.credits = 0
    transfer.goods = {}
    transfer.done = true
  }
  // Дропы роняем после обхода: во время обхода сущность только заводится, а компоненты ей добавят после него.
  for (const { x, y, items } of spills) dropItems(sim, x, y, items)
}
