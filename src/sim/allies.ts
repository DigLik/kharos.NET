import type { World } from '../ecs'
import { Player } from './components'
import type { Sim } from './sim'
import { playerEntity } from './topics'

/**
 * Союзы игроков. Союз — группа: один предлагает, другой соглашается, и вступивший становится союзником всех её
 * участников — приняв союз у одного, получаешь и всех, с кем он в союзе. Союзников не атакуют — ни сами, ни по
 * приказу, — их ворота перед ними не закрываются, а разведка у них общая: каждый видит всё, что видит другой
 * (см. vision.ts), и хост шлёт ему базу союзника так же подробно, как хозяину (host.ts).
 *
 * Всё лежит в Player каждого: allies — с кем союз, offered — кому игрок предложил и ждёт ответа, asked — кто
 * предложил ему. Список держится полным: у всякого участника группы в allies все остальные её участники, поэтому
 * читателям (обзор, бой, ворота, хост) довольно готового массива — обходить связи им не приходится. Разрыв снимает
 * одну пару и только её: группа распадается не вся, а на две — как в RA2.
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
    merge(sim, from, to)
    return 'allied'
  }
  if (info.offered.includes(to)) return 'pending'
  update(sim, from, ({ offered }) => ({ offered: [...offered, to] }))
  update(sim, to, ({ asked }) => ({ asked: [...asked, from] }))
  return 'offered'
}

/**
 * Союз заключён: группы from и to сливаются, и каждый участник получает в allies всех остальных. Заодно из
 * предложений уходят записи о своих: предложение, ставшее союзом, ждать больше нечего.
 */
function merge(sim: Sim, from: number, to: number) {
  const group = new Set<number>([from, to])
  const mine = sim.world.get(playerEntity(sim, from)!, Player)!
  const theirs = sim.world.get(playerEntity(sim, to)!, Player)!
  for (const other of [...mine.allies, ...theirs.allies]) group.add(other)
  // Все друг другу: у каждого в allies группа без него самого.
  const members = [...group].sort((a, b) => a - b)
  for (const member of members) {
    if (playerEntity(sim, member) === undefined) continue
    update(sim, member, ({ offered, asked }) => ({
      allies: members.filter((other) => other !== member),
      offered: offered.filter((other) => !group.has(other)),
      asked: asked.filter((other) => !group.has(other)),
    }))
  }
}

/**
 * Списки союзов — полные: у всякого участника группы в allies все её участники. Сохранения прежних версий
 * держали связи парами (а союз троих — тремя парами), поэтому при загрузке пары достраиваются до групп. Пустой
 * работы не делает и повторно вызывать себя не боится.
 */
export function materializeAlliances(sim: Sim) {
  const neighbours = new Map<number, Set<number>>()
  const know = (a: number, b: number) => {
    if (!neighbours.has(a)) neighbours.set(a, new Set())
    neighbours.get(a)!.add(b)
  }
  for (const [, info] of sim.world.query(Player)) {
    know(info.id, info.id)
    // Ребро идёт в обе стороны, но старое сохранение могло донести только одну — связи от этого не теряются.
    for (const other of info.allies) {
      know(info.id, other)
      know(other, info.id)
    }
  }
  const groups: number[][] = []
  const seen = new Set<number>()
  for (const [player] of neighbours) {
    if (seen.has(player)) continue
    const group: number[] = []
    const queue = [player]
    seen.add(player)
    while (queue.length) {
      const current = queue.pop()!
      group.push(current)
      for (const other of neighbours.get(current)!) {
        if (!seen.has(other)) {
          seen.add(other)
          queue.push(other)
        }
      }
    }
    if (group.length > 1) groups.push(group.sort((a, b) => a - b))
  }
  for (const group of groups) {
    const members = new Set(group)
    for (const member of group) {
      if (playerEntity(sim, member) === undefined) continue
      update(sim, member, ({ offered, asked }) => ({
        allies: group.filter((other) => other !== member),
        offered: offered.filter((other) => members.has(other)),
        asked: asked.filter((other) => members.has(other)),
      }))
    }
  }
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
