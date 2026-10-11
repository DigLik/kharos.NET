import type { Entity, Time } from '../ecs'
import { buildingSpec, isReady } from './buildings'
import { Building, Owner, Player, Research } from './components'
import { addCredits, creditsOf, pay } from './economy'
import { powerSupply } from './income'
import type { Sim } from './sim'
import { RESEARCH, playerEntity, researchOf, topicMaterials, topicSpec, type Topic } from './topics'
import { networkHas, returnToNetwork, takeFromNetwork } from './upgrades'

/**
 * Исследования. Их ведёт техцентр: тема стоит кредитов и компонентов и занимает время, а открытое остаётся
 * у игрока навсегда — и без техцентра. Темы открывают уровни зданий (см. upgrades.ts), технику второго тира
 * (UnitSpec.research) и лучевую оборону (RESEARCH_FOR в buildings.ts). Цена списывается сразу: компоненты — из сети
 * труб техцентра. Один техцентр ведёт одну тему; два техцентра — две разные темы сразу. При нехватке энергии
 * исследование идёт медленнее, в выключенном техцентре — стоит. Темы и то, что игрок открыл, — в topics.ts.
 */

/** Ведёт ли кто-то из техцентров игрока эту тему прямо сейчас. */
function inProgress(sim: Sim, player: number, topic: Topic) {
  for (const [entity, research] of sim.world.query(Research)) if (research.topic === topic && isReady(sim, player, entity)) return true
  return false
}

/** Почему тему нельзя начать: уже открыта, её ведут, нужна другая тема, не хватает кредитов или компонентов в сети. */
export type TopicLack = 'done' | 'running' | 'after' | 'credits' | 'materials'

/** Исследует ли это здание игрока: его готовый техцентр. */
export const isLab = (sim: Sim, player: number, building: Entity) => {
  const type = sim.world.get(building, Building)?.type
  return type !== undefined && !!buildingSpec(type).researches && isReady(sim, player, building)
}

/** Что мешает начать тему в этом техцентре; undefined — ничего. Занят ли сам техцентр, здесь не смотрят. */
export function topicLack(sim: Sim, player: number, lab: Entity, topic: Topic): TopicLack | undefined {
  const spec = topicSpec(topic)
  const done = researchOf(sim, player)
  if (done.includes(topic)) return 'done'
  if (inProgress(sim, player, topic)) return 'running'
  if (spec.after && !done.includes(spec.after)) return 'after'
  if (creditsOf(sim, player) < spec.cost) return 'credits'
  return networkHas(sim, lab, topicMaterials(topic)) ? undefined : 'materials'
}

/** Начинает исследование в своём свободном техцентре: кредиты и компоненты списываются сразу. */
export function startResearch(sim: Sim, player: number, lab: Entity, topic: Topic) {
  if (!Object.hasOwn(RESEARCH, topic) || !isLab(sim, player, lab) || sim.world.has(lab, Research)) return false
  if (topicLack(sim, player, lab, topic) || !pay(sim, player, topicSpec(topic).cost)) return false
  takeFromNetwork(sim, lab, topicMaterials(topic))
  sim.world.add(lab, Research({ topic }))
  return true
}

/** Бросает исследование: цена возвращается целиком, вложенное время пропадает. */
export function cancelResearch(sim: Sim, player: number, lab: Entity) {
  const research = sim.world.get(lab, Research)
  if (!research || !isReady(sim, player, lab)) return false
  const topic = research.topic as Topic
  sim.world.remove(lab, Research)
  addCredits(sim, player, topicSpec(topic).cost)
  returnToNetwork(sim, lab, topicMaterials(topic))
  return true
}

/** Сколько тиков идёт исследование темы при полной энергии. */
export const researchTicks = (topic: Topic, step: number) => Math.round(topicSpec(topic).seconds / step)

/** Раз в тик: техцентры ведут свои темы; законченная достаётся хозяину техцентра. */
export function research(sim: Sim, time: Time) {
  const { world } = sim
  if (!world.count(Research)) return
  // Считается недёшево, поэтому только когда что-то исследуют, и один раз за тик.
  const supply = powerSupply(sim)
  const done: { lab: Entity; topic: Topic }[] = []
  for (const [entity, running] of world.query(Research)) {
    running.progress += supply.get(entity) ?? 0
    if (running.progress >= researchTicks(running.topic as Topic, time.step)) done.push({ lab: entity, topic: running.topic as Topic })
  }
  for (const { lab, topic } of done) {
    world.remove(lab, Research)
    const owner = playerEntity(sim, world.get(lab, Owner)?.player ?? 0)
    if (owner === undefined) continue
    const { research: known } = world.get(owner, Player)!
    if (!known.includes(topic)) world.set(owner, Player, { research: [...known, topic] })
  }
}
