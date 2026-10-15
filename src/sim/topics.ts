import type { Entity, World } from '../ecs'
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
 * месторождение, впервые переработал руду (см. triggers в research.ts). Ярус они не добавляют.
 *
 * Тема не предлагается раньше, чем от неё будет толк: переработку руды не изучить, пока её месторождение не найдено,
 * трубы — пока нет металла, пехоту — пока нет патронов, накопитель — пока нет лития. Иначе зелёная плитка в начале
 * игры зовёт тратить кредиты на то, чем ещё нельзя пользоваться.
 */

/**
 * Что открывает тему-открытие: у игрока достроена лаборатория (lab), он видит месторождение (любое — deposit, или своего
 * вида) или поставил на него шахту, в его хранилище или переработке появился готовый ресурс (refined).
 */
export type Trigger = 'lab' | 'deposit' | 'metal' | 'silicon' | 'fuel' | 'kharite' | 'lithium' | 'uranium' | 'refined'
export interface TopicSpec {
  /** Темы, без которых эту не начать. Строки, а не Topic: темы ссылаются друг на друга; имена сверяет тест. */
  after?: readonly string[]
  /** Что тема тратит за всё исследование на первом ярусе: ярусом выше — больше. Без поля — только кредиты. */
  uses?: Amounts
  /** Тема-открытие: открывается сама, когда это случилось с игроком, и ничего не стоит. */
  trigger?: Trigger
}

export const RESEARCH = {
  // Открытия: научный метод — когда достроена первая лаборатория (без него не начать ни одну тему), шахта — когда
  // найдено любое месторождение, руда — когда найдено её месторождение, космопорт — когда есть что продать.
  science: { trigger: 'lab' },
  prospecting: { trigger: 'deposit' },
  metalFound: { trigger: 'metal' },
  siliconFound: { trigger: 'silicon' },
  fuelFound: { trigger: 'fuel' },
  khariteFound: { trigger: 'kharite' },
  lithiumFound: { trigger: 'lithium' },
  uraniumFound: { trigger: 'uranium' },
  trade: { trigger: 'refined' },
  // Ярус 1: стены, радар и дороги — за одни кредиты: с них начинается база. Здания вплотную и так одна сеть, а трубы
  // из металла — после плавки.
  walls: { after: ['science'] },
  radar: { after: ['science'] },
  roads: { after: ['science'] },
  // Ярус 1: переработка трёх обычных руд — каждая открывается, когда найдено её месторождение, и изучается на руде из шахты.
  metalOre: { after: ['science', 'metalFound'], uses: { metalOre: 10 } },
  siliconOre: { after: ['science', 'siliconFound'], uses: { siliconOre: 10 } },
  fuelOre: { after: ['science', 'fuelFound'], uses: { fuelOre: 10 } },
  // Ярус 2: трубы, редкие руды, первые цеха, оборона.
  pipes: { after: ['metalOre'], uses: { metal: 4 } },
  khariteOre: { after: ['siliconOre', 'khariteFound'], uses: { khariteOre: 6 } },
  lithiumOre: { after: ['metalOre', 'lithiumFound'], uses: { lithiumOre: 6 } },
  blocks: { after: ['metalOre', 'siliconOre'], uses: { metal: 6, silicon: 3 } },
  ammo: { after: ['metalOre'], uses: { metal: 8 } },
  gates: { after: ['walls', 'metalOre'], uses: { metal: 6 } },
  bridges: { after: ['roads', 'metalOre'], uses: { metal: 8 } },
  // Ярус 3: пехота — когда есть патроны: без них её магазины не пополнить; колодцы — после труб, накопитель — на литии.
  infantry: { after: ['ammo'] },
  wells: { after: ['pipes'], uses: { metal: 6 } },
  accumulators: { after: ['lithiumOre'], uses: { lithium: 6 } },
  // Ярус 4: ракеты и огонь пехоте.
  rockets: { after: ['infantry', 'fuelOre'], uses: { fuel: 6 } },
  flamer: { after: ['infantry', 'fuelOre'], uses: { fuel: 8 } },
  // Ярус 3: уран, компоненты, машинный завод, аэродром и ремонт, второй уровень зданий.
  uraniumOre: { after: ['khariteOre', 'uraniumFound'], uses: { uraniumOre: 6 } },
  parts: { after: ['khariteOre', 'blocks'], uses: { silicon: 4, kharite: 2 } },
  vehicles: { after: ['blocks'], uses: { metal: 6, blocks: 3 } },
  aviation: { after: ['blocks', 'fuelOre'], uses: { metal: 4, blocks: 2, fuel: 4 } },
  levels2: { after: ['blocks'], uses: { blocks: 6 } },
  repair: { after: ['blocks'], uses: { blocks: 4 } },
  foundation: { after: ['blocks'], uses: { blocks: 4 } },
  // Ярус 4: тяжёлая техника, лазеры, стержни, третий уровень зданий, землеройные работы.
  earthworks: { after: ['foundation'], uses: { metal: 6, blocks: 2 } },
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

/** Сущности игроков по номеру: о счёте игрока спрашивают тысячи раз за тик, и перебирать для этого мир дорого. */
const accounts = new WeakMap<World, Map<number, Entity>>()

/** Сущность игрока player со счётом, исследованиями и планом (Player); undefined — такого игрока нет. */
export function playerEntity(sim: Sim, player: number): Entity | undefined {
  let index = accounts.get(sim.world)
  if (!index) {
    const fresh = new Map<number, Entity>()
    sim.world.observe([Player], (entity, data) => {
      const { id } = data
      fresh.set(id, entity)
      return () => void (fresh.get(id) === entity && fresh.delete(id))
    })
    accounts.set(sim.world, (index = fresh))
  }
  return index.get(player)
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
 * Темы плана игрока, которые можно вести сейчас, в порядке плана: ещё не открытые, с открытыми предшественниками.
 * Первая — головная: за неё лаборатории берутся прежде остальных.
 */
export function openTopics(sim: Sim, player: number): Topic[] {
  const entity = playerEntity(sim, player)
  if (entity === undefined) return []
  const { plan, research: known } = sim.world.get(entity, Player)!
  return plan.filter((topic): topic is Topic => isTopic(topic) && !known.includes(topic) && topicsBefore(topic).every((before) => known.includes(before)))
}

/**
 * Какую из открытых тем плана лаборатории игрока ведут вместо головной, пока той не из чего работать: выбор делает
 * pickTopic в research.ts. Это не состояние мира, а вывод из него: не сохраняется и в сеть не идёт — и хост,
 * и клиент выводят его сами.
 */
const picks = new WeakMap<Sim, Map<number, Topic>>()

/** Запоминает выбор лабораторий игрока; undefined — ведут головную. */
export function setPick(sim: Sim, player: number, topic: Topic | undefined) {
  let byPlayer = picks.get(sim)
  if (!byPlayer) picks.set(sim, (byPlayer = new Map()))
  if (topic) byPlayer.set(player, topic)
  else byPlayer.delete(player)
}

/**
 * Тема, которую лаборатории игрока ведут сейчас: головная тема плана, а если она встала без сырья — следующая
 * открытая, у которой сырьё есть (см. pickTopic); undefined — вести нечего. Правило одно на всех: по нему работают
 * лаборатории, возят грузовики и показывает интерфейс.
 */
export function currentTopic(sim: Sim, player: number): Topic | undefined {
  const open = openTopics(sim, player)
  const pick = picks.get(sim)?.get(player)
  return pick !== undefined && open.includes(pick) ? pick : open[0]
}

/**
 * Сколько чего ещё потратят до конца нынешняя тема игрока и головная тема плана, если ведут не её: цена за вычетом
 * сделанного. Сырьё головной лаборатории ждут и держат и пока заняты другой: иначе грузовики увезли бы привезённое,
 * а вернуться к ней было бы не с чем. Пусто — темы нет.
 */
export function researchNeeds(sim: Sim, player: number): Amounts {
  const current = currentTopic(sim, player)
  if (!current) return {}
  const { progress } = sim.world.get(playerEntity(sim, player)!, Player)!
  const needs: Amounts = {}
  for (const topic of new Set([openTopics(sim, player)[0], current])) {
    const left = 1 - (progress[topic] ?? 0)
    for (const [good, amount] of entriesOf(topicPrice(sim, topic).materials)) needs[good] = (needs[good] ?? 0) + amount * left
  }
  return needs
}
