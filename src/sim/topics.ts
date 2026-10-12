import type { Entity } from '../ecs'
import { Player } from './components'
import { entriesOf, type Amounts } from './resources'
import type { Sim } from './sim'

/**
 * Темы исследований и то, что игрок уже открыл. Отдельно от research.ts: темы нужны и видам зданий и юнитов,
 * а само исследование зависит от них — так модули не замыкаются в круг.
 *
 * Тема тратит то, из чего делают открываемое: плавка — руду металла, стройблоки — металл и кремний, танк — металл
 * и компоненты; тема без такого сырья (казармы) — только кредиты. Чем глубже тема в дереве, тем она сложнее: ярус —
 * на единицу больше самого глубокого из её предшественников, и каждый ярус умножает и цену, и срок на
 * Rules.researchGrowth (см. topicPrice).
 *
 * Темы-открытия (trigger) лаборатории не ведут: они открываются сами, когда с игроком что-то случилось, — нашёл
 * месторождение, впервые переработал руду, остался без энергии (см. triggers в research.ts). Ярус они не добавляют.
 */

/**
 * Что открывает тему-открытие: у игрока достроена лаборатория (lab), он видит месторождение (любое — deposit, или своего вида), в его хранилище или
 * переработке появился готовый ресурс (refined), в его сети не хватило энергии (brownout).
 */
export type Trigger = 'lab' | 'deposit' | 'kharite' | 'lithium' | 'uranium' | 'refined' | 'brownout'
export interface TopicSpec {
  /** Темы, без которых эту не начать. Строки, а не Topic: темы ссылаются друг на друга; имена сверяет тест. */
  after?: readonly string[]
  /** Что тема тратит за всё исследование на первом ярусе: ярусом выше — больше. Без поля — только кредиты. */
  uses?: Amounts
  /** Тема-открытие: открывается сама, когда это случилось с игроком, и ничего не стоит. */
  trigger?: Trigger
}

export const RESEARCH = {
  // Открытия: научный метод — когда достроена лаборатория, шахта — когда найдено месторождение, редкие руды — когда найдено их месторождение, космопорт — когда
  // есть что продать, накопитель — когда энергии впервые не хватило.
  // Научный метод — когда достроена первая лаборатория: без него не начать ни одну тему.
  science: { trigger: 'lab' },
  prospecting: { trigger: 'deposit' },
  khariteFound: { trigger: 'kharite' },
  lithiumFound: { trigger: 'lithium' },
  uraniumFound: { trigger: 'uranium' },
  trade: { trigger: 'refined' },
  accumulators: { trigger: 'brownout' },
  // Ярус 1: трубы, стены, радар и дороги — за одни кредиты: с них начинается база. Трубы нужны раньше всего:
  // без сети лаборатория не получит руду из шахты.
  pipes: { after: ['science'] },
  walls: { after: ['science'] },
  radar: { after: ['science'] },
  roads: { after: ['science'] },
  // Ярус 1: переработка трёх обычных руд и казармы. Каждая переработка — исследование своей руды из шахты.
  metalOre: { after: ['science', 'prospecting'], uses: { metalOre: 10 } },
  siliconOre: { after: ['science', 'prospecting'], uses: { siliconOre: 10 } },
  fuelOre: { after: ['science', 'prospecting'], uses: { fuelOre: 10 } },
  infantry: { after: ['science'] },
  // Ярус 2: редкие руды, первые цеха и пехота с ракетами и огнём.
  khariteOre: { after: ['siliconOre', 'khariteFound'], uses: { khariteOre: 6 } },
  lithiumOre: { after: ['metalOre', 'lithiumFound'], uses: { lithiumOre: 6 } },
  blocks: { after: ['metalOre', 'siliconOre'], uses: { metal: 6, silicon: 3 } },
  ammo: { after: ['metalOre'], uses: { metal: 8 } },
  rockets: { after: ['infantry', 'fuelOre'], uses: { fuel: 6 } },
  gates: { after: ['walls', 'metalOre'], uses: { metal: 6 } },
  wells: { after: ['pipes', 'metalOre'], uses: { metal: 6 } },
  bridges: { after: ['roads', 'metalOre'], uses: { metal: 8 } },
  flamer: { after: ['infantry', 'fuelOre'], uses: { fuel: 8 } },
  // Ярус 3: уран, компоненты, машинный завод, аэродром и ремонт, второй уровень зданий.
  uraniumOre: { after: ['khariteOre', 'uraniumFound'], uses: { uraniumOre: 6 } },
  parts: { after: ['khariteOre', 'blocks'], uses: { silicon: 4, kharite: 2 } },
  vehicles: { after: ['blocks'], uses: { metal: 6, blocks: 3 } },
  aviation: { after: ['blocks', 'fuelOre'], uses: { metal: 4, blocks: 2, fuel: 4 } },
  levels2: { after: ['blocks'], uses: { blocks: 6 } },
  repair: { after: ['blocks'], uses: { blocks: 4 } },
  foundation: { after: ['blocks'], uses: { blocks: 4 } },
  // Ярус 4: тяжёлая техника, лазеры, стержни, третий уровень зданий.
  lasers: { after: ['vehicles'], uses: { silicon: 4, metal: 2 } },
  armor: { after: ['vehicles', 'parts'], uses: { metal: 6, parts: 1 } },
  gunship: { after: ['aviation'], uses: { metal: 4, silicon: 2, fuel: 3 } },
  bombing: { after: ['aviation', 'parts'], uses: { metal: 3, fuel: 4, parts: 1 } },
  levels3: { after: ['levels2', 'parts'], uses: { blocks: 4, parts: 1 } },
  rods: { after: ['uraniumOre', 'blocks'], uses: { uranium: 4, metal: 2 } },
  // Ярус 5: разряд, щит, осада, реактор.
  arcs: { after: ['lasers', 'parts'], uses: { silicon: 2, parts: 2 } },
  shields: { after: ['lasers', 'parts'], uses: { parts: 2 } },
  siege: { after: ['armor'], uses: { metal: 4, blocks: 1, parts: 1 } },
  atom: { after: ['rods', 'parts'], uses: { rods: 2, parts: 1 } },
  // Ярус 6: ракетная шахта.
  warheads: { after: ['atom'], uses: { rods: 3, parts: 2 } },
} satisfies Record<string, TopicSpec>

export type Topic = keyof typeof RESEARCH
export const TOPICS = Object.keys(RESEARCH) as Topic[]
export const topicSpec = (topic: Topic): TopicSpec => RESEARCH[topic]
export const isTopic = (topic: unknown): topic is Topic => typeof topic === 'string' && Object.hasOwn(RESEARCH, topic)
/** Предшественники темы: без них её не начать. */
export const topicsBefore = (topic: Topic) => (topicSpec(topic).after ?? []) as readonly Topic[]

/** Какое исследование открывает покрытие: фундамент, дорогу и мост — дорогу по болоту. */
export const PAVE_RESEARCH = { foundation: 'foundation', road: 'roads', bridge: 'bridges' } as const satisfies Record<string, Topic>

/** Тема-открытие ли это: её не исследуют, она открывается сама. */
export const isTrigger = (topic: Topic) => topicSpec(topic).trigger !== undefined

const tiers = new Map<Topic, number>()
/**
 * Ярус сложности темы: 1 — без предшественников, дальше — на единицу глубже самого глубокого из них. Открытия — ярус 1
 * и сложности не добавляют.
 */
export function tierOf(topic: Topic): number {
  let tier = tiers.get(topic)
  if (tier === undefined) tiers.set(topic, (tier = 1 + Math.max(0, ...topicsBefore(topic).filter((before) => !isTrigger(before)).map(tierOf))))
  return tier
}

/** Цена темы первого яруса в кредитах и её срок в секундах работы одной лаборатории при полной энергии. */
export const TIER_CREDITS = 100
export const TIER_SECONDS = 20

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

/** Цена темы целиком: кредиты, что она тратит, и секунды работы одной лаборатории при полной энергии. */
export interface TopicPrice {
  cost: number
  materials: Amounts
  seconds: number
}

/**
 * Что тема стоит целиком по правилам мира: ярус сложности умножает цену и срок на researchGrowth, researchScale множит
 * цену, researchSlow — срок. Платят не сразу, а по ходу работы: лаборатория тратит долю цены за долю исследования.
 */
export function priceOf(topic: Topic, { researchScale, researchGrowth, researchSlow }: Pick<Sim['rules'], 'researchScale' | 'researchGrowth' | 'researchSlow'>): TopicPrice {
  if (isTrigger(topic)) return { cost: 0, materials: {}, seconds: 0 }
  const factor = researchGrowth ** (tierOf(topic) - 1)
  const materials: Amounts = {}
  for (const [good, amount] of entriesOf(topicSpec(topic).uses ?? {})) materials[good] = Math.round(amount * factor * researchScale)
  return { cost: Math.round(TIER_CREDITS * factor * researchScale), materials, seconds: TIER_SECONDS * factor * researchSlow }
}

export const topicPrice = (sim: Sim, topic: Topic) => priceOf(topic, sim.rules)

/**
 * Темы, которые надо исследовать, чтобы открыть эту, вместе с ней самой, в порядке, в каком их можно вести:
 * предшественники раньше. Уже открытые пропускаются.
 */
export function pathTo(topic: Topic, done: readonly string[]): Topic[] {
  const path: Topic[] = []
  const visit = (next: Topic) => {
    if (done.includes(next) || path.includes(next)) return
    for (const before of topicsBefore(next)) visit(before)
    path.push(next)
  }
  visit(topic)
  return path
}

/**
 * Тема, которую лаборатории игрока ведут сейчас: первая в очереди, которой ещё нет в открытых и у которой открыты все
 * предшественники; undefined — вести нечего.
 */
export function currentTopic(sim: Sim, player: number): Topic | undefined {
  const entity = playerEntity(sim, player)
  if (entity === undefined) return undefined
  const { plan, research: known } = sim.world.get(entity, Player)!
  return plan.find((topic): topic is Topic => isTopic(topic) && !known.includes(topic) && topicsBefore(topic).every((before) => known.includes(before)))
}

/** Сколько чего ещё потратит нынешняя тема игрока до конца: её цена за вычетом сделанного. Пусто — темы нет. */
export function researchNeeds(sim: Sim, player: number): Amounts {
  const topic = currentTopic(sim, player)
  if (!topic) return {}
  const entity = playerEntity(sim, player)!
  const left = 1 - (sim.world.get(entity, Player)!.progress[topic] ?? 0)
  const needs: Amounts = {}
  for (const [good, amount] of entriesOf(topicPrice(sim, topic).materials)) needs[good] = amount * left
  return needs
}
