import { Player } from './components'
import type { Sim } from './sim'

/**
 * Защита новичка: первые минуты после появления (Rules.protection) игрока нельзя атаковать — ни по приказу, ни
 * самим прицелом, ни взрывом рядом, — и сам он не стреляет. Приказав атаковать, игрок снимает защиту досрочно.
 * Срок идёт по времени мира, в сети игрок или нет.
 */

/** Даёт игроку защиту новичка на срок из правил. Игрок уже должен быть в мире. */
export function protect(sim: Sim, player: number) {
  const ticks = Math.round((sim.rules.protection * 60) / sim.time.step)
  for (const [entity, info] of sim.world.query(Player)) {
    if (info.id === player) sim.world.set(entity, Player, { safe: ticks > 0 ? sim.time.tick + ticks : 0 })
  }
}

/** Снимает защиту новичка, если она есть. */
export function unprotect(sim: Sim, player: number) {
  for (const [entity, info] of sim.world.query(Player)) {
    if (info.id === player && info.safe > sim.time.tick) sim.world.set(entity, Player, { safe: 0 })
  }
}

/** Сколько тиков защиты новичка осталось у игрока; ноль — её нет. */
export function protectionLeft(sim: Sim, player: number) {
  for (const [, info] of sim.world.query(Player)) if (info.id === player) return Math.max(0, (info.safe ?? 0) - sim.time.tick)
  return 0
}

/** Игроки под защитой новичка. */
export function protectedPlayers(sim: Sim) {
  const safe = new Set<number>()
  for (const [, info] of sim.world.query(Player)) if (info.safe > sim.time.tick) safe.add(info.id)
  return safe
}
