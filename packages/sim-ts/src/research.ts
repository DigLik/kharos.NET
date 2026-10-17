import type { Entity, Time, WorldSnapshot } from '@kharos/ecs'
import { BUILDING_TYPES, RESEARCH_FOR, buildingSpec, isReady } from './buildings'
import { Building, Inventory, Off, Owner, Player, Position, Research, Site } from './components'
import { DEPOSIT_CELL, depositAt, depositsIn } from './deposits'
import { amountOf, take } from './inventory'
import { spend } from './economy'
import { powerSupply } from './income'
import { networkOf } from './zones'
import { RESOURCES, entriesOf, type Amounts, type Good } from './resources'
import type { Sim } from './sim'
import { PAVE_RESEARCH, TOPICS, currentTopic, isTopic, isTrigger, openTopics, pathTo, playerEntity, setPick, topicPrice, topicSpec, topicsBefore, type Topic } from './topics'
import { UNIT_TYPES, unitSpec } from './units'
import { networkStock, takeFromNetwork } from './upgrades'

/**
 * Исследования. Их ведут лаборатории, а план и прогресс — у игрока (Player.plan и Player.progress): все его
 * лаборатории работают над одной темой, и их скорость складывается. Открытое остаётся у игрока навсегда — и без
 * лабораторий. Темы открывают здания (RESEARCH_FOR в buildings.ts), юнитов (UnitSpec.research) и уровни зданий
 * (см. upgrades.ts); сами темы — в topics.ts.
 *
 * Цену темы платят по ходу работы: за каждую долю исследования лаборатория берёт ту же долю кредитов со счёта
 * и сырья — со складов своей сети труб. Чего в её сети нет, без того она стоит, а другие лаборатории, у которых
 * есть, работают: так лабораторий ставят несколько, и каждая тратит ровно на свою долю — вместе они тратят столько
 * же, сколько одна, только быстрее. При нехватке энергии лаборатория работает и тратит медленнее, выключенная стоит.
 * Брошенная тема помнит свой прогресс: уплаченное за него не пропадает, и к ней можно вернуться.
 *
 * План не встаёт на теме без сырья: если головной теме STALL_SECONDS не из чего работать, лаборатории берутся за
 * следующую открытую тему плана, у которой сырьё под рукой, и возвращаются к головной, как только её сырьё появится,
 * см. pickTopic. Нехватка кредитов и энергии план не двигает: они тормозят любую тему одинаково.
 */

/** Почему лаборатория стоит: нет темы, нет энергии, не хватает кредитов или этого груза в её сети. */
export type LabLack = 'idle' | 'off' | 'power' | 'credits' | Good

/** Ведёт ли это здание исследования: готовая лаборатория игрока. */
export const isLab = (sim: Sim, player: number, building: Entity) => {
  const type = sim.world.get(building, Building)?.type
  return type !== undefined && !!buildingSpec(type).researches && isReady(sim, player, building)
}

/** Сколько темы игрок уже исследовал: доля от 0 до 1. */
export function progressOf(sim: Sim, player: number, topic: Topic) {
  const entity = playerEntity(sim, player)
  return entity === undefined ? 0 : (sim.world.get(entity, Player)!.progress[topic] ?? 0)
}

/** План исследований игрока: какие темы его лаборатории ведут по очереди, первая — нынешняя. */
export function planOf(sim: Sim, player: number): readonly Topic[] {
  const entity = playerEntity(sim, player)
  return entity === undefined ? [] : (sim.world.get(entity, Player)!.plan as Topic[])
}

/**
 * Ставит тему в план исследований вместе с её неоткрытыми предшественниками, в том порядке, в каком их можно вести:
 * в конец плана, а front — в начало, чтобы лаборатории взялись за неё сразу. Тему, которой не хватает
 * неслучившегося открытия, в план не поставить: лаборатории его не исследуют. Прогресс начатых тем не пропадает.
 */
export function planResearch(sim: Sim, player: number, topic: Topic, front = false) {
  const entity = playerEntity(sim, player)
  if (!isTopic(topic) || entity === undefined) return false
  const { research, plan } = sim.world.get(entity, Player)!
  const path = pathTo(topic, research)
  if (!path.length || path.some(isTrigger)) return false
  const next = front ? [...path, ...plan.filter((queued) => !path.includes(queued as Topic))] : [...plan, ...path.filter((step) => !plan.includes(step))]
  if (next.length === plan.length && next.every((queued, i) => queued === plan[i])) return false
  sim.world.set(entity, Player, { plan: next })
  return true
}

/** Все темы, которые ведут к этой: её предшественники, их предшественники и так далее. */
export function topicsUnder(topic: Topic): Set<Topic> {
  const found = new Set<Topic>()
  const visit = (next: Topic) => {
    for (const before of topicsBefore(next)) {
      if (found.has(before)) continue
      found.add(before)
      visit(before)
    }
  }
  visit(topic)
  return found
}

/** Убирает тему из плана вместе с темами плана, которым она нужна: без неё их не начать. */
export function dequeueResearch(sim: Sim, player: number, topic: Topic) {
  const entity = playerEntity(sim, player)
  if (!isTopic(topic) || entity === undefined) return false
  const { plan } = sim.world.get(entity, Player)!
  const next = plan.filter((queued) => queued !== topic && !(isTopic(queued) && topicsUnder(queued).has(topic)))
  if (next.length === plan.length) return false
  sim.world.set(entity, Player, { plan: next })
  return true
}

/** Открывает игроку темы без исследования: для сценок и тестов. Без списка — все. */
export function grantResearch(sim: Sim, player: number, topics: readonly Topic[] = TOPICS) {
  const entity = playerEntity(sim, player)
  if (entity === undefined) return
  const { research: known } = sim.world.get(entity, Player)!
  sim.world.set(entity, Player, { research: [...known, ...topics.filter((topic) => !known.includes(topic))] })
}

/** Очищает план исследований: лаборатории встают, начатые темы помнят свой прогресс. */
export function cancelResearch(sim: Sim, player: number) {
  const entity = playerEntity(sim, player)
  if (entity === undefined || !sim.world.get(entity, Player)!.plan.length) return false
  sim.world.set(entity, Player, { plan: [] })
  return true
}

/** Добавляет игроку открытую тему, если её ещё нет. */
function learn(sim: Sim, entity: Entity, topic: Topic) {
  const { research: known } = sim.world.get(entity, Player)!
  if (!known.includes(topic)) sim.world.set(entity, Player, { research: [...known, topic] })
}

/** Виды месторождений, которые игрок видит сейчас: по ячейкам, где ему виден хоть один тайл. */
function depositsSeen(sim: Sim, player: number) {
  const kinds = new Set<string>()
  for (const key of sim.vision.seenCells(player, DEPOSIT_CELL)) {
    const cellX = (key % 65536) - 32768
    const cellY = Math.floor(key / 65536) - 32768
    for (const spot of depositsIn(sim, cellX, cellY)) if (sim.vision.sees(player, spot.x, spot.y)) kinds.add(spot.kind)
  }
  return kinds
}

/** Виды месторождений под шахтами игрока, в том числе недостроенными: на чём шахта, то найдено. */
function depositsMined(sim: Sim, player: number) {
  const kinds = new Set<string>()
  for (const [, position, building, owner] of sim.world.query(Position, Building, Owner)) {
    if (owner.player !== player || !buildingSpec(building.type).extract) continue
    const kind = depositAt(sim, position.x, position.y)?.kind
    if (kind) kinds.add(kind)
  }
  return kinds
}

/** Есть ли у игрока достроенная лаборатория. */
function hasLab(sim: Sim, player: number) {
  for (const [entity] of sim.world.query(Building)) if (isLab(sim, player, entity)) return true
  return false
}

/** Есть ли у игрока готовый ресурс в хранилище или переработке: первая переработанная руда. */
function hasRefined(sim: Sim, player: number) {
  for (const [entity, building, inventory] of sim.world.query(Building, Inventory)) {
    const spec = buildingSpec(building.type)
    if (!(spec.stores || spec.refines) || sim.world.get(entity, Owner)?.player !== player) continue
    if (RESOURCES.some((resource) => (inventory.items[resource] ?? 0) > 0)) return true
  }
  return false
}

/**
 * Раз в секунду: темы-открытия, у которых случилось условие, открываются сами — игрок достроил лабораторию, нашёл
 * месторождение (увидел его или поставил на него шахту), впервые переработал руду. Проверяются только у тех, у кого
 * они ещё не открыты.
 */
export function triggers(sim: Sim, time: Time) {
  if (time.tick % Math.max(1, Math.round(1 / time.step))) return
  const waiting = TOPICS.filter(isTrigger)
  for (const [entity, player] of sim.world.query(Player)) {
    const open = waiting.filter((topic) => !player.research.includes(topic))
    if (!open.length || !player.id) continue
    let found: Set<string> | undefined
    for (const topic of open) {
      const trigger = topicSpec(topic).trigger!
      const happened =
        trigger === 'lab' ? hasLab(sim, player.id)
        : trigger === 'refined' ? hasRefined(sim, player.id)
        : (found ??= new Set([...depositsSeen(sim, player.id), ...depositsMined(sim, player.id)])).size > 0 && (trigger === 'deposit' || found.has(trigger))
      if (happened) learn(sim, entity, topic)
    }
  }
}

/** Ставит лаборатории её состояние, только если оно изменилось: компонент уходит в сеть. */
function report(sim: Sim, lab: Entity, speed: number, lack: LabLack | '') {
  const rounded = Math.round(speed * 100) / 100
  const current = sim.world.get(lab, Research)
  if (!current) sim.world.add(lab, Research({ speed: rounded, lack }))
  else if (current.speed !== rounded || current.lack !== lack) sim.world.set(lab, Research, { speed: rounded, lack })
}

/** Доля погрешности: столько от темы считается сделанным, а сырья — найденным. */
const EPSILON = 1e-9

/** Сколько секунд головная тема плана стоит без сырья, прежде чем лаборатории возьмутся за следующую. */
export const STALL_SECONDS = 10
/** Как часто сверяется, есть ли темам сырьё, в секундах: реже — и выбор не мечется, и склады сетей считаются нечасто. */
const PICK_SECONDS = 1

/** Как давно головная тема игрока без сырья: head — о какой теме речь, since — с какого тика (-1 — сырьё есть), checked — когда сверяли. */
const stalls = new WeakMap<Sim, Map<number, { head: Topic; since: number; checked: number }>>()

/** Ставит простой головной темы игрока, посчитанный другой стороной — Rust, см. docs/boundary.md; undefined — снять. */
export function setStall(sim: Sim, player: number, stall: { head: Topic; since: number; checked: number } | undefined) {
  let byPlayer = stalls.get(sim)
  if (!byPlayer) stalls.set(sim, (byPlayer = new Map()))
  if (stall) byPlayer.set(player, stall)
  else byPlayer.delete(player)
}

/** Есть ли под рукой хоть у одной из лабораторий всё сырьё темы: на её складе или в её сети труб. Теме без сырья оно не нужно. */
function supplied(sim: Sim, topic: Topic, labs: readonly Entity[]) {
  const goods = entriesOf(topicPrice(sim, topic).materials).map(([good]) => good)
  if (!goods.length) return true
  return labs.some((lab) => {
    const stock = sim.world.get(lab, Inventory)
    return goods.every((good) => (stock ? amountOf(stock, good) : 0) + networkStock(sim, lab, good) > EPSILON)
  })
}

/**
 * Выбирает тему, которую лаборатории игрока ведут сейчас, и запоминает выбор для currentTopic. Головная тема плана
 * уступает, только простояв без сырья STALL_SECONDS: грузовикам нужно время его привезти. Тогда берётся следующая
 * открытая тема плана, у которой сырьё под рукой; нет такой — ждут головную. Сверяется раз в PICK_SECONDS; тему
 * плана сменили — сразу. Зовут лаборатории каждый тик и интерфейс: у клиента мир — копия, и выбор он выводит сам.
 */
export function pickTopic(sim: Sim, player: number): Topic | undefined {
  const open = openTopics(sim, player)
  const head = open[0]
  let byPlayer = stalls.get(sim)
  if (!byPlayer) stalls.set(sim, (byPlayer = new Map()))
  if (!head) {
    byPlayer.delete(player)
    setPick(sim, player, undefined)
    return undefined
  }
  const { tick, step } = sim.time
  const last = byPlayer.get(player)
  const same = last?.head === head
  if (same && tick >= last.checked && (tick - last.checked) * step < PICK_SECONDS) return currentTopic(sim, player)
  const labs: Entity[] = []
  for (const [entity] of sim.world.query(Building)) if (isLab(sim, player, entity) && !sim.world.has(entity, Off)) labs.push(entity)
  const since = supplied(sim, head, labs) ? -1 : same && last.since >= 0 && last.since <= tick ? last.since : tick
  byPlayer.set(player, { head, since, checked: tick })
  const stalled = since >= 0 && (tick - since) * step >= STALL_SECONDS
  setPick(sim, player, stalled ? open.slice(1).find((topic) => supplied(sim, topic, labs)) : undefined)
  return currentTopic(sim, player)
}

/** Раз в тик: лаборатории ведут нынешнюю тему своего хозяина — головную тему плана или ту, что её подменяет, — и платят за сделанное. */
export function research(sim: Sim, time: Time) {
  const { world } = sim
  const labs = new Map<number, Entity[]>()
  for (const [entity, building, owner] of world.query(Building, Owner)) {
    if (!buildingSpec(building.type).researches || world.has(entity, Site)) continue
    const list = labs.get(owner.player)
    if (list) list.push(entity)
    else labs.set(owner.player, [entity])
  }
  if (!labs.size) return
  // Считается недёшево, поэтому только когда лаборатории есть, и один раз за тик.
  let supply: ReadonlyMap<Entity, number> | undefined
  // Что отдают склады сети лаборатории: лаборатории одной сети спрашивают одно и то же, а в сети большой базы — тысячи
  // зданий. Считается раз на сеть и груз; взятое лабораторией вычитается.
  const stocks = new Map<object, Partial<Record<Good, number>>>()
  const stockOf = (lab: Entity) => {
    const key = networkOf(sim, lab) ?? { lab }
    let stock = stocks.get(key)
    if (!stock) stocks.set(key, (stock = {}))
    return stock
  }
  for (const [player, list] of labs) {
    const entity = playerEntity(sim, player)
    // План сначала сбрасывает то, что вести нельзя; какую из оставшихся тем вести, решает pickTopic.
    const topic = entity === undefined || nextTopic(sim, entity) === undefined ? undefined : pickTopic(sim, player)
    if (entity === undefined || topic === undefined) {
      for (const lab of list) report(sim, lab, 0, 'idle')
      continue
    }
    supply ??= powerSupply(sim)
    const price = topicPrice(sim, topic)
    const ticks = Math.max(1, price.seconds / time.step)
    let done = world.get(entity, Player)!.progress[topic] ?? 0
    for (const lab of list) {
      // Выключенная энергию не берёт, но стоит не от нехватки: игрок видит «выключена», а не «нет энергии».
      if (world.has(lab, Off)) {
        report(sim, lab, 0, 'off')
        continue
      }
      const power = supply.get(lab) ?? 0
      const share = Math.min(power / ticks, 1 - done)
      if (share <= EPSILON) {
        report(sim, lab, 0, power > 0 ? 'idle' : 'power')
        continue
      }
      // Какую долю своей работы лаборатория может оплатить: всю или сколько хватает самого скудного.
      let part = 1
      let lack: LabLack | '' = power < 1 ? 'power' : ''
      const { credits, earned } = world.get(entity, Player)!
      const cost = price.cost * share
      if (cost > EPSILON && credits + earned < cost) {
        part = Math.max(0, (credits + earned) / cost)
        lack = 'credits'
      }
      const needs: Amounts = {}
      const stock = world.get(lab, Inventory)
      const network = stockOf(lab)
      for (const [good, amount] of entriesOf(price.materials)) {
        const need = amount * share
        // Сначала — привезённое на склад лаборатории, потом — что отдают склады её сети.
        const have = (stock ? amountOf(stock, good) : 0) + (network[good] ??= networkStock(sim, lab, good))
        if (have + EPSILON < need * part) {
          part = Math.max(0, have / need)
          lack = good
        }
        needs[good] = need
      }
      if (part <= EPSILON) {
        report(sim, lab, 0, lack || 'credits')
        continue
      }
      for (const good of Object.keys(needs) as Good[]) needs[good]! *= part
      if (cost > EPSILON && !spend(sim, player, Math.min(cost * part, credits + earned))) {
        report(sim, lab, 0, 'credits')
        continue
      }
      if (stock) for (const good of Object.keys(needs) as Good[]) needs[good]! -= take(stock, good, needs[good]!)
      takeFromNetwork(sim, lab, needs)
      for (const good of Object.keys(needs) as Good[]) if (network[good] !== undefined) network[good] = Math.max(0, network[good] - needs[good]!)
      done += share * part
      report(sim, lab, power * part, lack)
    }
    finish(sim, entity, topic, done)
  }
}

/**
 * Первая тема плана, которую можно вести: открытые уже темы и темы без открытых предшественников план сбрасывает —
 * вторые бывают, когда план собран при выключенном дереве технологий.
 */
function nextTopic(sim: Sim, entity: Entity): Topic | undefined {
  const { plan, research: known } = sim.world.get(entity, Player)!
  let skip = 0
  while (skip < plan.length && (!isTopic(plan[skip]) || known.includes(plan[skip]) || !topicsBefore(plan[skip] as Topic).every((before) => known.includes(before)))) skip++
  if (skip) sim.world.set(entity, Player, { plan: plan.slice(skip) })
  return skip < plan.length ? (plan[skip] as Topic) : undefined
}

/** Записывает прогресс темы; законченная переходит в открытые и уходит из плана. */
function finish(sim: Sim, entity: Entity, topic: Topic, done: number) {
  const { progress, research: known, plan } = sim.world.get(entity, Player)!
  if (done < 1 - EPSILON) {
    if (progress[topic] !== done) sim.world.set(entity, Player, { progress: { ...progress, [topic]: done } })
    return
  }
  const { [topic]: _, ...rest } = progress
  sim.world.set(entity, Player, { progress: rest, research: [...known, topic], plan: plan.filter((next) => next !== topic) })
}

/** Что открывает тема: виды зданий и юнитов. */
export const unlockedBy = (topic: Topic): string[] => [
  ...BUILDING_TYPES.filter((type) => RESEARCH_FOR[type] === topic),
  ...UNIT_TYPES.filter((type) => unitSpec(type).research === topic),
]

/**
 * Переводит мир из сохранения версии 22, где исследования вёл техцентр: техцентры и их стройки становятся
 * лабораториями, начатое исследование бросается с возвратом кредитов (компоненты уже потрачены), а игроку
 * засчитываются темы всего, что у него уже стоит или ездит, вместе с предшественниками: построенная плавильня значит
 * открытую плавку. Прежние «Лучи» — это нынешние лазеры и разряд.
 */
export function adoptLabs(snapshot: WorldSnapshot) {
  type Named = { type: string }
  const owned = new Map<number, Set<string>>()
  const refunds = new Map<number, number>()
  for (const [, data] of snapshot.entities) {
    const building = data.Building as Named | undefined
    const site = data.Site as Named | undefined
    if (building?.type === 'techCenter') building.type = 'lab'
    if (site?.type === 'techCenter') site.type = 'lab'
    const player = (data.Owner as { player: number } | undefined)?.player ?? 0
    const research = data.Research as { cost?: number } | undefined
    if (research) {
      refunds.set(player, (refunds.get(player) ?? 0) + (research.cost ?? 0))
      delete data.Research
    }
    // Уложенное покрытие засчитывает свою тему, как здание: дороги — «Дороги», фундамент — «Фундамент».
    const pave = (data.Pave as { kind: 'foundation' | 'road' } | undefined)?.kind
    const type = pave ? `pave:${pave}` : site ? undefined : (building ?? (data.Unit as Named | undefined))?.type
    if (player && type) {
      const types = owned.get(player) ?? new Set()
      owned.set(player, types.add(type))
    }
  }
  for (const [, data] of snapshot.entities) {
    const player = data.Player as { id: number; credits: number; research: string[] } | undefined
    if (!player) continue
    const known = player.research.flatMap((topic) => (topic === 'beams' ? ['lasers', 'arcs'] : [topic])).filter(isTopic)
    const types = owned.get(player.id)
    for (const topic of TOPICS) if (unlockedBy(topic).some((type) => types?.has(type))) known.push(topic)
    if (types?.has('pave:foundation')) known.push(PAVE_RESEARCH.foundation)
    if (types?.has('pave:road')) known.push(PAVE_RESEARCH.road)
    const research: Topic[] = []
    for (const topic of known) research.push(...pathTo(topic, research))
    player.research = research
    player.credits += refunds.get(player.id) ?? 0
  }
}
