import type { Entity } from '../ecs'
import { Owner, Player, Research } from './components'
import type { Amounts } from './resources'
import type { Sim } from './sim'

/**
 * Темы исследований и то, что игрок уже открыл. Отдельно от research.ts: темы нужны и видам зданий и юнитов,
 * а само исследование зависит от них — так модули не замыкаются в круг.
 */
export interface TopicSpec {
  /** Цена в кредитах и компонентах. */
  cost: number
  parts: number
  /** Сколько секунд идёт исследование при полной энергии. */
  seconds: number
  /** Тема, без которой эту не начать. */
  after?: string
}

export const RESEARCH = {
  // Уровни зданий: второй — первое, на что хватает компонентов; третий — вдвое дороже и дольше.
  levels2: { cost: 300, parts: 3, seconds: 30 },
  levels3: { cost: 600, parts: 6, seconds: 60, after: 'levels2' },
  // Второй тир техники: тяжёлые, потом осадные; бомбардировщик — отдельной веткой.
  armor: { cost: 400, parts: 4, seconds: 40 },
  siege: { cost: 500, parts: 5, seconds: 50, after: 'armor' },
  bombing: { cost: 400, parts: 4, seconds: 40 },
  // Лазерная и разрядная турели.
  beams: { cost: 400, parts: 4, seconds: 40 },
} satisfies Record<string, TopicSpec>

export type Topic = keyof typeof RESEARCH
export const TOPICS = Object.keys(RESEARCH) as Topic[]
export const topicSpec = (topic: Topic): TopicSpec => RESEARCH[topic]


export function playerEntity(sim: Sim, player: number): Entity | undefined {
  for (const [entity, data] of sim.world.query(Player)) if (data.id === player) return entity
  return undefined
}

/** Что игрок уже исследовал, по порядку. */
export function researchOf(sim: Sim, player: number): readonly string[] {
  const entity = playerEntity(sim, player)
  return entity === undefined ? [] : sim.world.get(entity, Player)!.research
}

/** Открыта ли игроку тема. Без дерева технологий (Rules.techTree) открыто всё. */
export const hasResearch = (sim: Sim, player: number, topic: Topic | undefined) =>
  topic === undefined || !sim.rules.techTree || researchOf(sim, player).includes(topic)

/** Сколько тем игрок ведёт прямо сейчас во всех своих техцентрах. */
function runningTopics(sim: Sim, player: number) {
  let count = 0
  for (const [entity] of sim.world.query(Research)) if (sim.world.get(entity, Owner)?.player === player) count++
  return count
}

/** Цена темы: кредиты, готовое и секунды при полной энергии. */
export interface TopicPrice {
  cost: number
  materials: Amounts
  seconds: number
}

/**
 * Что тема стоит игроку сейчас, по правилам мира (Rules.researchScale, researchGrowth и researchSlow). Удорожают и открытые темы,
 * и начатые: иначе несколько техцентров разом начали бы всё дерево по цене первой темы. Порядок тем игрок выбирает
 * сам — и тем самым решает, что достанется дёшево.
 */
export function topicPrice(sim: Sim, player: number, topic: Topic): TopicPrice {
  return priceAfter(topic, sim.rules, researchOf(sim, player).length + runningTopics(sim, player))
}

/** Цена темы для того, у кого открыто или начато taken тем, по правилам темпа: так же считает и харнесс баланса. */
export function priceAfter(topic: Topic, { researchScale, researchGrowth, researchSlow }: Pick<Sim['rules'], 'researchScale' | 'researchGrowth' | 'researchSlow'>, taken: number): TopicPrice {
  const { cost, parts, seconds } = topicSpec(topic)
  const factor = researchScale * researchGrowth ** taken
  return { cost: Math.round(cost * factor), materials: { parts: Math.round(parts * factor) }, seconds: seconds * factor * researchSlow }
}
