import type { World } from '../ecs'
import { Player } from './components'
import type { Sim } from './sim'
import { playerEntity } from './topics'

/**
 * Союзы игроков. Союз — пара: один предлагает, другой соглашается, выйти может любой и в любой момент. Союзников
 * не атакуют — ни сами, ни по приказу, — их ворота перед ними не закрываются, а разведка у них общая: каждый видит
 * всё, что видит другой (см. vision.ts), и хост шлёт ему базу союзника так же подробно, как хозяину (host.ts).
 * Союз троих — это три пары: союзник союзника сам по себе не союзник.
 *
 * Всё лежит в Player обоих: allies — с кем союз, offered — кому игрок предложил и ждёт ответа, asked — кто предложил
 * ему. Запись всегда парная: A в allies у B — значит, и B в allies у A.
 */

/** С кем у игрока союз: то же, что alliesOf, там, где есть только мир, — в обзоре и на клиенте. */
export function alliesIn(world: World, player: number): readonly number[] {
  for (const [, info] of world.query(Player)) if (info.id === player) return info.allies
  return []
}

/** С кем у игрока союз. */
export function alliesOf(sim: Sim, player: number): readonly number[] {
  return alliesIn(sim.world, player)
}

/** В союзе ли два игрока. */
export const allied = (sim: Sim, a: number, b: number) => a !== b && alliesOf(sim, a).includes(b)

/** Союзники каждого игрока разом: когда спрашивать приходится тысячи раз за тик. */
export function alliances(sim: Sim) {
  const all = new Map<number, readonly number[]>()
  for (const [, info] of sim.world.query(Player)) if (info.allies.length) all.set(info.id, info.allies)
  return all
}

const without = (list: readonly number[], player: number) => list.filter((other) => other !== player)

/** Меняет списки игрока в его Player. */
function update(sim: Sim, player: number, change: (info: { allies: number[]; offered: number[]; asked: number[] }) => Partial<{ allies: number[]; offered: number[]; asked: number[] }>) {
  const entity = playerEntity(sim, player)!
  sim.world.set(entity, Player, change(sim.world.get(entity, Player)!))
}

/** Чем кончилось предложение союза: заключён, предложен, уже есть, уже предложен и ждёт ответа — или предлагать некому. */
export type AllyResult = 'allied' | 'offered' | 'already' | 'pending' | 'refused'

/** Игрок from предлагает союз игроку to; если тот уже предлагал ему — это согласие, и союз заключён. */
export function offerAlliance(sim: Sim, from: number, to: number): AllyResult {
  const mine = playerEntity(sim, from)
  const theirs = playerEntity(sim, to)
  if (from === to || mine === undefined || theirs === undefined) return 'refused'
  const info = sim.world.get(mine, Player)!
  if (info.allies.includes(to)) return 'already'
  if (info.asked.includes(to)) {
    update(sim, from, ({ allies, asked, offered }) => ({ allies: [...allies, to], asked: without(asked, to), offered: without(offered, to) }))
    update(sim, to, ({ allies, asked, offered }) => ({ allies: [...allies, from], asked: without(asked, from), offered: without(offered, from) }))
    return 'allied'
  }
  if (info.offered.includes(to)) return 'pending'
  update(sim, from, ({ offered }) => ({ offered: [...offered, to] }))
  update(sim, to, ({ asked }) => ({ asked: [...asked, from] }))
  return 'offered'
}

/** Чем кончился разрыв: союз расторгнут, своё предложение отозвано, чужое отклонено — или рвать было нечего. */
export type BreakResult = 'left' | 'withdrawn' | 'declined' | 'nothing'

/** Игрок from рвёт с игроком to всё, что между ними есть: союз, своё предложение или его предложение. */
export function breakAlliance(sim: Sim, from: number, to: number): BreakResult {
  const mine = playerEntity(sim, from)
  if (mine === undefined) return 'nothing'
  const info = sim.world.get(mine, Player)!
  const result: BreakResult = info.allies.includes(to) ? 'left' : info.offered.includes(to) ? 'withdrawn' : info.asked.includes(to) ? 'declined' : 'nothing'
  if (result === 'nothing') return result
  for (const [a, b] of [[from, to], [to, from]]) {
    if (playerEntity(sim, a) === undefined) continue
    update(sim, a, ({ allies, asked, offered }) => ({ allies: without(allies, b), asked: without(asked, b), offered: without(offered, b) }))
  }
  return result
}

/** Игрок начинает заново: все его союзы и предложения пропадают. */
export function forgetAlliances(sim: Sim, player: number) {
  const entity = playerEntity(sim, player)
  if (entity === undefined) return
  const { allies, offered, asked } = sim.world.get(entity, Player)!
  for (const other of new Set([...allies, ...offered, ...asked])) breakAlliance(sim, player, other)
}
