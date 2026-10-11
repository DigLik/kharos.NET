import type { Entity } from '../ecs'
import { Player } from './components'
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

/** Что тема стоит в готовом. */
export const topicMaterials = (topic: Topic): Amounts => ({ parts: RESEARCH[topic].parts })

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
