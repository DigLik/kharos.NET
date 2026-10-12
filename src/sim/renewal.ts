import { hash } from '../map/terrain'
import { buildingSpec } from './buildings'
import { Building, Deposit, Position, Site } from './components'
import { DEPOSIT_CELL, DEPOSIT_SIZE, addDeposit, cellOf, depositAt, depositsIn, dropDeposit, knowsDeposit, placeIn, reserveLeft, seesDeposit, type DepositSpot } from './deposits'
import { hasMine } from './harvesting'
import type { Sim } from './sim'

/**
 * Смена месторождений (§9 п. 2 docs/design.md). Выработанное месторождение уходит с карты, как только на нём нет
 * шахты, а через rules.depositRenewal минут после выработки в случайной пустой клетке карты появляется новое — тем же
 * генератором: вид по биому, запас по виду. Месторождений на карте столько же, сколько было, но лежат они уже не там:
 * за ними приходится идти, и разведка не кончается.
 *
 * Туман: о новом месторождении знает только тот, кто видел его место после появления (DepositSpot.found), а ушедшее
 * помнят, как прежде, все, кто о нём знал, пока не увидят, что его нет (DepositLayer.ghosts).
 */

/** Раз в сколько тиков всё это проверяется: месторождения уходят и появляются за минуты, а не за тики. */
const CHECK_TICKS = 20
/** Не ближе скольких тайлов к чужим и своим зданиям и стройкам появляется новое месторождение: не посреди базы. */
const CLEARANCE = 12
/** Сколько клеток пробуется за проверку под одно новое месторождение; не нашлось — до следующей проверки. */
const TRIES = 24
/** Сдвиг сида: места новых месторождений не повторяют места генератора. */
const SALT = 40507

export function renewDeposits(sim: Sim) {
  if (sim.time.tick % CHECK_TICKS) return
  learn(sim)
  if (sim.rules.depositRenewal <= 0) return
  retire(sim)
  spawn(sim)
}

/** Игроки находят новые месторождения и забывают ушедшие, место которых видят. */
function learn(sim: Sim) {
  const layer = sim.deposits
  const players = sim.vision.players()
  let changed = false
  for (const spots of layer.cells.values()) {
    for (const spot of spots) {
      if (!spot.found) continue
      for (const player of players) {
        if (spot.found.includes(player) || !seesDeposit(sim, player, spot)) continue
        spot.found.push(player)
        changed = true
      }
    }
  }
  for (const ghost of layer.ghosts) {
    const left = ghost.players.filter((player) => !seesDeposit(sim, player, ghost))
    if (left.length === ghost.players.length) continue
    ghost.players = left
    changed = true
  }
  if (!changed) return
  layer.ghosts = layer.ghosts.filter((ghost) => ghost.players.length)
  layer.revision++
}

/** Выработанным месторождениям заказывает смену, а те, на которых нет шахты, убирает с карты. */
function retire(sim: Sim) {
  const { world, time, deposits: layer } = sim
  const gone: DepositSpot[] = []
  for (const [, position, deposit] of world.query(Position, Deposit)) {
    const spot = depositAt(sim, position.x, position.y)
    if (!spot || reserveLeft(sim, spot.x, spot.y) > 0) continue
    if (!deposit.spent) {
      deposit.spent = true
      layer.due.push(time.tick + Math.round((sim.rules.depositRenewal * 60) / time.step))
      layer.due.sort((a, b) => a - b)
    }
    if (!hasMine(sim, spot)) gone.push(spot)
  }
  for (const spot of gone) {
    const players = sim.vision.players().filter((player) => knowsDeposit(sim, player, spot) && !seesDeposit(sim, player, spot))
    if (players.length) layer.ghosts.push({ x: spot.x, y: spot.y, kind: spot.kind, reserve: spot.reserve, players })
    dropDeposit(sim, spot.x, spot.y)
  }
}

/** Кладёт новые месторождения, чей срок подошёл. */
function spawn(sim: Sim) {
  const layer = sim.deposits
  while (layer.due.length && layer.due[0] <= sim.time.tick) {
    const spot = renewedSpot(sim)
    if (!spot) return
    layer.due.shift()
    addDeposit(sim, spot)
  }
}

/**
 * Место под новое месторождение: случайная клетка карты, где месторождения нет и никто не помнит ушедшего; в клетке —
 * место, где можно строить, вдали от зданий и, если есть туман, не на глазах у игроков. Не нашлось за TRIES клеток —
 * null.
 */
function renewedSpot(sim: Sim): DepositSpot | null {
  const { bounds, world, deposits: layer } = sim
  const seed = sim.land.config.seed + SALT
  const left = Math.floor(bounds.left / DEPOSIT_CELL)
  const top = Math.floor(bounds.top / DEPOSIT_CELL)
  const columns = Math.floor((bounds.right - 1) / DEPOSIT_CELL) - left + 1
  const rows = Math.floor((bounds.bottom - 1) / DEPOSIT_CELL) - top + 1
  const fog = sim.options.fog !== false
  const players = sim.vision.players()
  // Основания зданий и строек: новое месторождение встаёт не ближе CLEARANCE к любому из них.
  const bases: { x: number; y: number; width: number; height: number }[] = []
  for (const [, building, position] of world.query(Building, Position)) bases.push({ ...position, ...buildingSpec(building.type) })
  for (const [, site, position] of world.query(Site, Position)) bases.push({ ...position, ...buildingSpec(site.type) })
  const clear = (x: number, y: number) =>
    bases.every((base) => {
      const dx = Math.max(base.x - (x + DEPOSIT_SIZE), x - (base.x + base.width), 0)
      const dy = Math.max(base.y - (y + DEPOSIT_SIZE), y - (base.y + base.height), 0)
      return Math.hypot(dx, dy) >= CLEARANCE
    })
  const unseen = (x: number, y: number) => !fog || players.every((player) => !seesDeposit(sim, player, { x, y }))
  for (let attempt = 0; attempt < TRIES; attempt++) {
    const n = layer.spawned++
    const cellX = left + Math.floor(hash(n, 0, seed) * columns)
    const cellY = top + Math.floor(hash(n, 1, seed) * rows)
    if (depositsIn(sim, cellX, cellY).length) continue
    const key = cellOf(cellX * DEPOSIT_CELL, cellY * DEPOSIT_CELL)
    if (layer.ghosts.some((ghost) => cellOf(ghost.x, ghost.y) === key)) continue
    const spot = placeIn(sim, cellX, cellY, (k) => hash(n, k + 2, seed), (x, y) => clear(x, y) && unseen(x, y))
    if (spot) return { ...spot, found: [] }
  }
  return null
}
