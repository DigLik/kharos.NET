import type { World } from '@kharos/ecs'
import { Player } from './components'
import type { Sim } from './sim'
import { playerEntity } from './topics'

/**
 * Союзы игроков. Союз — группа во главе с игроком: глава его предлагает и принимает, он же исключает рядовых и
 * распускает союз целиком; рядовой может только выйти сам. Кто предложил — тот и глава: у двух одиночек главой
 * становится предложивший, а входящий в готовую группу ей и подчиняется. Приняв союз у одного, игрок становится
 * союзником всех участников группы. Союзников не атакуют — ни сами, ни по приказу, — их ворота перед ними не
 * закрываются, а разведка у них общая: каждый видит всё, что видит другой (см. vision.ts), и хост шлёт ему базу
 * союзника так же подробно, как хозяину (host.ts).
 *
 * Всё лежит в Player каждого: allies — с кем союз, head — номер главы группы (-1 — ни с кем), offered — кому игрок
 * предложил и ждёт ответа, asked — кто предложил ему. Список держится полным: у всякого участника группы в allies
 * все остальные её участники, поэтому читателям (обзор, бой, ворота, хост) довольно готового массива — обходить
 * связи им не приходится. Исключённый и вышедший покидают союз целиком: связи с ним рвутся все разом, а у
 * оставшихся связи друг с другом целы.
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

/** Номер главы союза игрока: он сам, если он глава, его глава, если рядовой, и -1, если игрок ни с кем. */
export function allyHeadIn(world: World, player: number): number {
  for (const [, info] of world.query(Player)) if (info.id === player) return info.head
  return -1
}

/** Номер главы союза игрока, см. allyHeadIn. */
export const allyHeadOf = (sim: Sim, player: number) => allyHeadIn(sim.world, player)

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
function update(sim: Sim, player: number, change: (info: { allies: number[]; head: number; offered: number[]; asked: number[] }) => Partial<{ allies: number[]; head: number; offered: number[]; asked: number[] }>) {
  const entity = playerEntity(sim, player)!
  sim.world.set(entity, Player, change(sim.world.get(entity, Player)!))
}

/** Чем кончилось предложение союза: заключён, предложен, уже есть, уже предложен и ждёт ответа — или предлагать некому. */
export type AllyResult = 'allied' | 'offered' | 'already' | 'pending' | 'refused' | 'fromMember' | 'toMember'

/**
 * Игрок from предлагает союз игроку to; если тот уже предлагал ему — это согласие, и союз заключён. Предлагают и
 * принимают только одиночки и главы: рядовой союзник союзом не распоряжается — ни своим, ни чужим.
 */
export function offerAlliance(sim: Sim, from: number, to: number): AllyResult {
  const mine = playerEntity(sim, from)
  const theirs = playerEntity(sim, to)
  if (from === to || mine === undefined || theirs === undefined) return 'refused'
  const info = sim.world.get(mine, Player)!
  if (info.allies.includes(to)) return 'already'
  if (info.head >= 0 && info.head !== from) return 'fromMember'
  if (info.asked.includes(to)) {
    merge(sim, from, to)
    return 'allied'
  }
  const theirInfo = sim.world.get(theirs, Player)!
  if (theirInfo.head >= 0 && theirInfo.head !== to) return 'toMember'
  if (info.offered.includes(to)) return 'pending'
  update(sim, from, ({ offered }) => ({ offered: [...offered, to] }))
  update(sim, to, ({ asked }) => ({ asked: [...asked, from] }))
  return 'offered'
}

/**
 * Союз заключён: группы from и to сливаются, и каждый участник получает в allies всех остальных. Главой остаётся
 * глава принявшей стороны — у двух одиночек это предложивший, у готовой группы её глава. Заодно из предложений
 * уходят записи о своих: предложение, ставшее союзом, ждать больше нечего.
 */
function merge(sim: Sim, from: number, to: number) {
  const mine = sim.world.get(playerEntity(sim, from)!, Player)!
  const theirs = sim.world.get(playerEntity(sim, to)!, Player)!
  const group = new Set<number>([from, to])
  for (const other of [...mine.allies, ...theirs.allies]) group.add(other)
  const head = theirs.allies.length ? theirs.head : mine.allies.length ? mine.head : to
  const members = [...group].sort((a, b) => a - b)
  for (const member of members) {
    if (playerEntity(sim, member) === undefined) continue
    update(sim, member, ({ offered, asked }) => ({
      allies: members.filter((other) => other !== member),
      head,
      offered: offered.filter((other) => !group.has(other)),
      asked: asked.filter((other) => !group.has(other)),
    }))
  }
}

/** Рядовой покидает группу: с ним рвутся все его связи, а у оставшихся связи друг с другом целы. */
function detach(sim: Sim, member: number) {
  const mine = sim.world.get(playerEntity(sim, member)!, Player)!
  for (const other of mine.allies) {
    if (playerEntity(sim, other) === undefined) continue
    update(sim, other, ({ allies }) => {
      const rest = without(allies, member)
      // Оставшемуся одному глава больше не нужен: он снова одиночка.
      return rest.length ? { allies: rest } : { allies: rest, head: -1 }
    })
  }
  update(sim, member, () => ({ allies: [], head: -1 }))
}

/** Союз распущен: у всех участников связей не остаётся. */
function dissolve(sim: Sim, group: readonly number[]) {
  for (const member of group) {
    if (playerEntity(sim, member) === undefined) continue
    update(sim, member, () => ({ allies: [], head: -1 }))
  }
}

/** Чем кончился выход: рядовой вышел, глава распустил союз — или игрок и так ни с кем. */
export type ExitResult = 'left' | 'dissolved' | 'nothing'

/** Игрок покидает союз сам: рядовой выходит, а глава распускает союз — без главы союз не живёт. */
export function exitAlliance(sim: Sim, player: number): ExitResult {
  const entity = playerEntity(sim, player)
  if (entity === undefined) return 'nothing'
  const info = sim.world.get(entity, Player)!
  if (!info.allies.length) return 'nothing'
  if (info.head === player) {
    dissolve(sim, [player, ...info.allies])
    return 'dissolved'
  }
  detach(sim, player)
  return 'left'
}

/** Чем кончился разрыв: глава исключил рядового, своё предложение отозвано, чужое отклонено — или рвать нечего. */
export type BreakResult = 'kicked' | 'withdrawn' | 'declined' | 'notHead' | 'nothing'

/**
 * Игрок from рвёт с игроком to всё, что между ними есть: союз, своё предложение или его предложение. Союзом
 * распоряжается глава: он исключает рядового, и тот покидает союз целиком; рядовому исключать некем — ему
 * отвечают notHead, а выйти самому можно через exitAlliance.
 */
export function breakAlliance(sim: Sim, from: number, to: number): BreakResult {
  const mine = playerEntity(sim, from)
  if (mine === undefined) return 'nothing'
  const info = sim.world.get(mine, Player)!
  if (info.allies.includes(to)) {
    if (info.head !== from) return 'notHead'
    detach(sim, to)
    return 'kicked'
  }
  const result: BreakResult = info.offered.includes(to) ? 'withdrawn' : info.asked.includes(to) ? 'declined' : 'nothing'
  if (result === 'nothing') return result
  for (const [a, b] of [[from, to], [to, from]]) {
    if (playerEntity(sim, a) === undefined) continue
    update(sim, a, ({ allies, asked, offered }) => ({ allies: without(allies, b), asked: without(asked, b), offered: without(offered, b) }))
  }
  return result
}

/** Игрок начинает заново: все его союзы и предложения пропадают. Глава, начавший заново, распускает союз. */
export function forgetAlliances(sim: Sim, player: number) {
  const entity = playerEntity(sim, player)
  if (entity === undefined) return
  const { offered, asked } = sim.world.get(entity, Player)!
  exitAlliance(sim, player)
  for (const other of new Set([...offered, ...asked])) breakAlliance(sim, player, other)
}

/**
 * Списки союзов — полные: у всякого участника группы в allies все её участники. Сохранения прежних версий
 * держали связи парами (а союз троих — тремя парами) и главы не знали, поэтому при загрузке пары достраиваются до
 * групп, а главой записанной группы без главы становится младший номер. Записанный глава в своей группе
 * остаётся. Пустой работы не делает и повторно вызывать себя не боится.
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
      update(sim, member, ({ head, offered, asked }) => ({
        allies: group.filter((other) => other !== member),
        head: members.has(head) ? head : group[0],
        offered: offered.filter((other) => !members.has(other)),
        asked: asked.filter((other) => !members.has(other)),
      }))
    }
  }
}
