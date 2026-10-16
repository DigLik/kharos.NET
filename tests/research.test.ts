import { expect, test } from 'bun:test'
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import {
  BUILDINGS, Batch, Inventory, Off, Player, RESEARCH, RESEARCH_FOR, Research, TIER_CREDITS, TIER_SECONDS, TOPICS, Upgrade, amountOf, canPlace, createSim, creditsOf, depositIn, hasResearch,
  STALL_SECONDS, isUnlocked, pickTopic, planOf, progressOf, researchOf, stockOf, tierOf, topicPrice, topicsBefore, upgradeCost, upgradeLack, upgradeTicks,
  type BuildingType, type Rules, type Sim, type Topic,
} from '@kharos/sim-ts'
import { STORES, placeBuilding, storeFor } from '@kharos/sim-ts/buildings'
import { PRODUCTS, RESOURCE_SPECS, isOre, resourceOf, type Amounts, type Good, type Ware } from '@kharos/sim-ts/resources'
import { addCredits } from '@kharos/sim-ts/economy'
import { UNIT_TYPES, unitSpec } from '@kharos/sim-ts/units'
import { adoptLabs, grantResearch, topicsUnder } from '@kharos/sim-ts/research'
import { isTrigger } from '@kharos/sim-ts/topics'
import { spawnUnit } from '@kharos/sim-ts/units'
import { requestsOf } from '@kharos/sim-ts/logistics'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Ставит здания в ряд на скале под одной трубой; null — ряда нет. first — где ряд начинается, если задано. */
function row(sim: Sim, layout: BuildingType[], at?: { x: number; y: number }) {
  const offsets: number[] = []
  let width = 0
  for (const type of layout) {
    offsets.push(width)
    width += BUILDINGS[type].width + 1
  }
  const fits = (x: number, y: number) => {
    if (!layout.every((type, i) => canPlace(sim, type, x + offsets[i], y))) return false
    for (let dx = 0; dx < width; dx++) if (!canPlace(sim, 'pipe', x + dx, y - 1)) return false
    return true
  }
  const place = (x: number, y: number) => {
    const buildings = layout.map((type, i) => placeBuilding(sim.world, type, x + offsets[i], y, 1))
    for (let dx = 0; dx < width; dx++) placeBuilding(sim.world, 'pipe', x + dx, y - 1, 1)
    return buildings
  }
  if (at) return fits(at.x, at.y) ? place(at.x, at.y) : null
  for (let y = -100; y < 100; y++) for (let x = -100; x < 100; x++) if (fits(x, y)) return place(x, y)
  return null
}

/**
 * База игрока 1 в одной сети труб: электростанции, здания из списка и все хранилища. Главного здания нет: его доход
 * сбивал бы счёт кредитов.
 */
function base(extra: BuildingType[], rules?: Partial<Rules>) {
  const sim = createSim({ ...options, rules })
  addCredits(sim, 1, 10000)
  const buildings = row(sim, ['generator', 'generator', ...extra, ...STORES])
  if (!buildings) throw new Error('Не нашлось места под базу')
  const shelves = buildings.slice(2 + extra.length)
  const stash = (items: Amounts) => {
    for (const [item, amount] of Object.entries(items) as [Ware, number][]) sim.world.get(shelves[STORES.indexOf(storeFor(item)!)], Inventory)!.items[item] = amount
  }
  return { sim, plants: buildings.slice(0, 2), buildings: buildings.slice(2, 2 + extra.length), stash }
}

const plan = (sim: Sim, topic: Topic, front = false) => {
  sim.send(1, { type: 'research', topic, front })
  sim.advance(TICK)
}

const learn = (sim: Sim, ...topics: Topic[]) => {
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { research: [...player.research, ...topics] })
}

test('темы ссылаются на существующие темы, а тратят то, что даёт открытое раньше', () => {
  for (const topic of TOPICS) for (const before of topicsBefore(topic)) expect(RESEARCH).toHaveProperty(before)
  for (const topic of Object.values(RESEARCH_FOR)) expect(RESEARCH).toHaveProperty(topic)
  for (const unit of UNIT_TYPES) if (unitSpec(unit).research) expect(RESEARCH).toHaveProperty(unitSpec(unit).research!)
  for (const spec of Object.values(RESOURCE_SPECS) as { research?: string }[]) if (spec.research) expect(RESEARCH).toHaveProperty(spec.research)
  // Где игрок возьмёт то, что тратит тема: руду дают шахты всегда, ресурс — переработка его руды, изделие — его цех.
  const makes = (good: Good): BuildingType | undefined =>
    isOre(good) ? undefined : (Object.keys(BUILDINGS) as BuildingType[]).find((type) => {
      const spec = BUILDINGS[type] as { refines?: string; assembles?: string }
      return (spec.refines && resourceOf(spec.refines as never) === good) || spec.assembles === good
    })
  const ancestors = (topic: Topic): Set<Topic> => new Set(topicsBefore(topic).flatMap((before) => [before, ...ancestors(before)]))
  for (const topic of TOPICS) {
    const known = ancestors(topic)
    for (const good of Object.keys(RESEARCH[topic as Topic] && (RESEARCH[topic] as { uses?: Amounts }).uses ? (RESEARCH[topic] as { uses: Amounts }).uses : {}) as Good[]) {
      const maker = makes(good)
      if (!maker) continue
      expect({ topic, good, open: known.has(RESEARCH_FOR[maker]!) }).toEqual({ topic, good, open: true })
    }
  }
  // Каждое изделие и каждая переработка открываются исследованием.
  for (const product of PRODUCTS) expect(Object.entries(RESEARCH_FOR).some(([type]) => (BUILDINGS[type as BuildingType] as { assembles?: string }).assembles === product)).toBe(true)
})

test('чем глубже тема, тем она дороже и дольше', () => {
  const sim = createSim(options)
  expect(tierOf('metalOre')).toBe(1)
  expect(tierOf('blocks')).toBe(2)
  expect(tierOf('warheads')).toBe(6)
  expect(topicPrice(sim, 'metalOre')).toEqual({ cost: TIER_CREDITS, materials: { metalOre: 10 }, seconds: TIER_SECONDS })
  const growth = sim.rules.researchGrowth
  expect(topicPrice(sim, 'blocks')).toEqual({ cost: Math.round(TIER_CREDITS * growth), materials: { metal: Math.round(6 * growth), silicon: Math.round(3 * growth) }, seconds: TIER_SECONDS * growth })
  for (const topic of TOPICS) for (const before of topicsBefore(topic)) {
    expect(topicPrice(sim, topic).cost).toBeGreaterThan(topicPrice(sim, before).cost)
    expect(topicPrice(sim, topic).seconds).toBeGreaterThan(topicPrice(sim, before).seconds)
  }
})

test('плавильню открывает исследование руды металла: лаборатория берёт руду из шахты своей сети и кредиты по ходу работы', () => {
  const sim = createSim(options)
  let found: { mine: Entity; lab: Entity } | undefined
  for (let cellY = -6; cellY < 6 && !found; cellY++) {
    for (let cellX = -6; cellX < 6 && !found; cellX++) {
      const spot = depositIn(sim, cellX, cellY)
      if (spot?.kind !== 'metal') continue
      const buildings = row(sim, ['mine', 'lab', 'generator'], spot)
      if (buildings) found = { mine: buildings[0], lab: buildings[1] }
    }
  }
  if (!found) throw new Error('Не нашлось месторождения металла с местом рядом')
  const { mine, lab } = found
  addCredits(sim, 1, 1000)
  expect(isUnlocked(sim, 1, 'smelter')).toBe(false)
  expect(isUnlocked(sim, 1, 'lab')).toBe(true)

  // Плавку не поставить в план, пока не случились открытия: научный метод — достроенная лаборатория, металл найден —
  // его месторождение в обзоре (здесь на нём стоит шахта). Их не исследуют, они открываются сами, проверка раз в секунду.
  plan(sim, 'metalOre')
  expect(planOf(sim, 1)).toEqual([])
  seconds(sim, 1)
  expect(hasResearch(sim, 1, 'science')).toBe(true)
  expect(hasResearch(sim, 1, 'prospecting')).toBe(true)
  expect(hasResearch(sim, 1, 'metalFound')).toBe(true)
  plan(sim, 'metalOre')
  expect(planOf(sim, 1)).toEqual(['metalOre'])
  // Шахта ещё не накопала руды: лаборатория стоит и ничего не тратит.
  const ore = sim.world.get(mine, Inventory)!
  ore.items.metalOre = 0
  sim.advance(TICK)
  expect(sim.world.get(lab, Research)).toMatchObject({ speed: 0, lack: 'metalOre' })
  ore.items.metalOre = 0
  const credits = creditsOf(sim, 1)
  sim.advance(TICK)
  expect(creditsOf(sim, 1)).toBe(credits)

  // Шахта копает быстрее, чем лаборатория тратит, и тема идёт без остановок; кредитов уходит ровно её цена.
  const { cost, seconds: time } = topicPrice(sim, 'metalOre')
  const start = creditsOf(sim, 1)
  for (let i = 0; i < Math.round((time + 1) / TICK) && !hasResearch(sim, 1, 'metalOre'); i++) sim.advance(TICK)
  expect(hasResearch(sim, 1, 'metalOre')).toBe(true)
  expect(start - creditsOf(sim, 1)).toBeGreaterThanOrEqual(cost - 1)
  expect(start - creditsOf(sim, 1)).toBeLessThanOrEqual(cost + 1)
  expect(isUnlocked(sim, 1, 'smelter')).toBe(true)
  expect(planOf(sim, 1)).toEqual([])
  sim.advance(TICK)
  expect(sim.world.get(lab, Research)!.lack).toBe('idle')
})

test('руду в лабораторию без труб до шахты везут грузовики, и она исследует плавку', () => {
  const sim = createSim(options)
  let found: { mine: Entity; lab: Entity; x: number; y: number } | undefined
  for (let cellY = -6; cellY < 6 && !found; cellY++) {
    for (let cellX = -6; cellX < 6 && !found; cellX++) {
      const spot = depositIn(sim, cellX, cellY)
      if (spot?.kind !== 'metal') continue
      const [mine] = row(sim, ['mine'], spot) ?? []
      // Лаборатория со своей электростанцией — в стороне, трубы до шахты нет.
      const base = mine && row(sim, ['generator', 'lab'], { x: spot.x + 5, y: spot.y })
      if (base) found = { mine, lab: base[1], x: spot.x + 3, y: spot.y + 3 }
    }
  }
  if (!found) throw new Error('Не нашлось месторождения металла с местом рядом')
  const { mine, lab, x, y } = found
  addCredits(sim, 1, 1000)
  spawnUnit(sim, 'truck', 1, x, y)
  grantResearch(sim, 1, ['science', 'prospecting', 'metalFound'])
  plan(sim, 'metalOre')
  // Лаборатория заказала руду из шахты: грузовик её везёт.
  sim.advance(TICK)
  expect(requestsOf(sim, 1).some((request) => request.to === lab && request.resource === 'metalOre' && request.source === 'mines')).toBe(true)
  seconds(sim, topicPrice(sim, 'metalOre').seconds + 40)
  expect(hasResearch(sim, 1, 'metalOre')).toBe(true)
  // Лишнего лаборатория не набрала: запас — не больше, чем теме оставалось потратить.
  expect(amountOf(sim.world.get(lab, Inventory)!, 'metalOre')).toBeLessThanOrEqual(10)
  void mine
})

test('руду из шахты грузовики везут во все лаборатории, а не в одну', () => {
  const sim = createSim(options)
  let found: { labs: Entity[]; x: number; y: number } | undefined
  for (let cellY = -6; cellY < 6 && !found; cellY++) {
    for (let cellX = -6; cellX < 6 && !found; cellX++) {
      const spot = depositIn(sim, cellX, cellY)
      if (spot?.kind !== 'metal') continue
      const [mine] = row(sim, ['mine'], spot) ?? []
      const base = mine && row(sim, ['generator', 'lab', 'lab'], { x: spot.x + 5, y: spot.y })
      if (base) found = { labs: base.slice(1), x: spot.x + 3, y: spot.y + 3 }
    }
  }
  if (!found) throw new Error('Не нашлось месторождения металла с местом рядом')
  const { labs, x, y } = found
  addCredits(sim, 1, 1000)
  spawnUnit(sim, 'truck', 1, x, y)
  spawnUnit(sim, 'truck', 1, x, y + 1)
  grantResearch(sim, 1, ['science', 'prospecting', 'metalFound'])
  plan(sim, 'metalOre')
  // Обе лаборатории в работе хоть раз: каждой привезли её долю руды.
  const worked = new Set<Entity>()
  for (let i = 0; i < Math.round(60 / TICK) && !hasResearch(sim, 1, 'metalOre'); i++) {
    sim.advance(TICK)
    for (const lab of labs) if (sim.world.get(lab, Research)!.speed > 0) worked.add(lab)
  }
  expect(hasResearch(sim, 1, 'metalOre')).toBe(true)
  expect(worked.size).toBe(2)
})

test('лаборатория тратит столько, сколько сделала; открытое остаётся у игрока и переживает сохранение', () => {
  const { sim, buildings: [lab], stash } = base(['lab'])
  learn(sim, 'metalOre', 'siliconOre')
  const { cost, materials, seconds: time } = topicPrice(sim, 'blocks')
  stash({ metal: 100, silicon: 100 })
  const credits = creditsOf(sim, 1)
  plan(sim, 'blocks')
  seconds(sim, time / 2)
  // Полпути — полцены.
  expect(progressOf(sim, 1, 'blocks')).toBeCloseTo(0.5, 1)
  expect(credits - creditsOf(sim, 1)).toBeGreaterThan(cost * 0.45)
  expect(credits - creditsOf(sim, 1)).toBeLessThan(cost * 0.55)
  // Потрачено — сколько нет ни в хранилищах, ни в запасе лаборатории, ни в трубах.
  const held = (good: Good) => {
    let amount = (stockOf(sim, 1).items[good as Ware] ?? 0) + amountOf(sim.world.get(lab, Inventory)!, good)
    for (const [, batch] of sim.world.query(Batch)) if (batch.resource === good) amount += batch.amount
    return amount
  }
  expect(100 - held('metal')).toBeCloseTo(materials.metal! * progressOf(sim, 1, 'blocks'), 3)
  // Запас лаборатории — не больше, чем теме осталось потратить.
  expect(amountOf(sim.world.get(lab, Inventory)!, 'metal')).toBeLessThanOrEqual(materials.metal! * (1 - progressOf(sim, 1, 'blocks')) + 1)
  seconds(sim, time / 2 + 1)
  expect(hasResearch(sim, 1, 'blocks')).toBe(true)
  expect(100 - held('metal')).toBeCloseTo(materials.metal!, 3)
  expect(100 - held('silicon')).toBeCloseTo(materials.silicon!, 3)
  // Остатки запаса трубы увозят обратно в хранилища.
  seconds(sim, 5)
  expect(amountOf(sim.world.get(lab, Inventory)!, 'metal')).toBeLessThan(1e-6)
  expect(100 - stockOf(sim, 1).items.metal!).toBeCloseTo(materials.metal!, 3)
  expect(Math.abs(credits - creditsOf(sim, 1) - cost)).toBeLessThanOrEqual(1)
  // Чужому игроку тема не открыта.
  expect(hasResearch(sim, 2, 'blocks')).toBe(false)

  const copy = createSim(throughJson(sim.save()))
  expect(researchOf(copy, 1)).toContain('blocks')
  // Снесли лабораторию — открытое остаётся.
  sim.world.destroy(lab)
  expect(hasResearch(sim, 1, 'blocks')).toBe(true)
})

test('несколько лабораторий складывают скорость там, где у них есть сырьё, и вместе тратят не больше цены', () => {
  const { sim, buildings: [first, second, third], stash } = base(['lab', 'lab', 'lab', 'generator'])
  learn(sim, 'metalOre')
  // Третья лаборатория — в своей сети без хранилищ: металла у неё нет, и она стоит.
  const lonely = row(sim, ['generator', 'lab'])!
  const { cost, materials, seconds: time } = topicPrice(sim, 'ammo')
  stash({ metal: 100 })
  plan(sim, 'ammo')
  seconds(sim, time / 6)
  expect(sim.world.get(first, Research)).toMatchObject({ speed: 1, lack: '' })
  expect(sim.world.get(third, Research)).toMatchObject({ speed: 1, lack: '' })
  expect(sim.world.get(lonely[1], Research)).toMatchObject({ speed: 0, lack: 'metal' })
  // Три лаборатории — втрое быстрее одной.
  expect(progressOf(sim, 1, 'ammo')).toBeCloseTo(0.5, 1)
  void second
  seconds(sim, time / 6 + 1)
  expect(hasResearch(sim, 1, 'ammo')).toBe(true)
  // Доля каждой лаборатории округляется вверх: неистраченное трубы увозят обратно.
  seconds(sim, 5)
  expect(100 - stockOf(sim, 1).items.metal!).toBeCloseTo(materials.metal!, 3)
  void cost
})

test('очередь ведёт тему вместе с предшественниками; брошенная тема помнит прогресс; без энергии лаборатория стоит', () => {
  const { sim, plants, buildings: [lab], stash } = base(['lab'])
  stash({ metal: 200, silicon: 200 })
  learn(sim, 'science', 'prospecting', 'metalFound', 'siliconFound')
  plan(sim, 'vehicles')
  expect(planOf(sim, 1)).toEqual(['metalOre', 'siliconOre', 'blocks', 'vehicles'])
  // Руды в сети нет: лаборатория ждёт её на первой теме.
  sim.advance(TICK)
  expect(sim.world.get(lab, Research)!.lack).toBe('metalOre')
  learn(sim, 'metalOre', 'siliconOre')
  seconds(sim, topicPrice(sim, 'blocks').seconds / 2)
  expect(planOf(sim, 1)).toEqual(['blocks', 'vehicles'])

  sim.send(1, { type: 'cancelResearch' })
  sim.advance(TICK)
  const half = progressOf(sim, 1, 'blocks')
  expect(half).toBeGreaterThan(0.3)
  seconds(sim, 2)
  expect(planOf(sim, 1)).toEqual([])
  expect(progressOf(sim, 1, 'blocks')).toBe(half)
  expect(sim.world.get(lab, Research)!.lack).toBe('idle')
  // Выключенная лаборатория стоит: пока собирается очередь, работа не идёт.
  sim.world.add(lab, Off)

  // Щелчок ставит в конец очереди, повтор ничего не меняет, Shift — в начало.
  plan(sim, 'ammo')
  plan(sim, 'blocks')
  plan(sim, 'blocks')
  expect(planOf(sim, 1)).toEqual(['ammo', 'blocks'])
  plan(sim, 'vehicles', true)
  expect(planOf(sim, 1)).toEqual(['blocks', 'vehicles', 'ammo'])
  // Убрать из очереди — вместе с тем, чему тема нужна.
  sim.send(1, { type: 'dequeue', topic: 'blocks' })
  sim.advance(TICK)
  expect(planOf(sim, 1)).toEqual(['ammo'])
  // Тему, которой не хватает неслучившегося открытия, в очередь не поставить.
  plan(sim, 'khariteOre')
  expect(planOf(sim, 1)).toEqual(['ammo'])

  plan(sim, 'blocks', true)
  seconds(sim, 2)
  expect(progressOf(sim, 1, 'blocks')).toBe(half)
  // Стоит, потому что выключена, а не от нехватки энергии.
  expect(sim.world.get(lab, Research)!.lack).toBe('off')
  sim.world.remove(lab, Off)
  // Без электростанций энергии нет вовсе.
  for (const plant of plants) sim.world.destroy(plant)
  seconds(sim, 2)
  expect(progressOf(sim, 1, 'blocks')).toBe(half)
  expect(sim.world.get(lab, Research)!.lack).toBe('power')
})

test('план не встаёт на теме без сырья: лаборатории берутся за следующую и возвращаются, когда сырьё появится', () => {
  const { sim, buildings: [lab], stash } = base(['lab'])
  learn(sim, 'metalOre', 'fuelOre', 'infantry')
  stash({ metal: 100 })
  plan(sim, 'rockets')
  plan(sim, 'ammo')
  expect(planOf(sim, 1)).toEqual(['rockets', 'ammo'])
  // Топлива нет нигде. Сразу план не двигается: грузовикам и трубам нужно время привезти.
  seconds(sim, STALL_SECONDS - 2)
  expect(pickTopic(sim, 1)).toBe('rockets')
  expect(sim.world.get(lab, Research)).toMatchObject({ speed: 0, lack: 'fuel' })
  expect(progressOf(sim, 1, 'ammo')).toBe(0)
  // Простояла — лаборатория ведёт следующую тему, у которой сырьё есть. План при этом тот же.
  seconds(sim, 4)
  expect(pickTopic(sim, 1)).toBe('ammo')
  expect(sim.world.get(lab, Research)).toMatchObject({ speed: 1, lack: '' })
  expect(progressOf(sim, 1, 'ammo')).toBeGreaterThan(0)
  expect(planOf(sim, 1)).toEqual(['rockets', 'ammo'])
  // Сырьё головной темы появилось — лаборатория возвращается к ней, а начатое в брошенной теме помнится.
  stash({ fuel: 100 })
  seconds(sim, 2)
  expect(pickTopic(sim, 1)).toBe('rockets')
  const paused = progressOf(sim, 1, 'ammo')
  expect(paused).toBeGreaterThan(0)
  seconds(sim, 3)
  expect(progressOf(sim, 1, 'rockets')).toBeGreaterThan(0)
  expect(progressOf(sim, 1, 'ammo')).toBe(paused)
  // Обе доводятся до конца, по очереди плана.
  seconds(sim, topicPrice(sim, 'rockets').seconds + topicPrice(sim, 'ammo').seconds + 5)
  expect(hasResearch(sim, 1, 'rockets')).toBe(true)
  expect(hasResearch(sim, 1, 'ammo')).toBe(true)
  expect(planOf(sim, 1)).toEqual([])
})

test('без сырья у всех тем плана лаборатории ждут головную, а нехватка кредитов план не двигает', () => {
  const { sim, buildings: [lab], stash } = base(['lab'])
  learn(sim, 'metalOre', 'fuelOre', 'infantry')
  plan(sim, 'rockets')
  plan(sim, 'ammo')
  // Ни топлива, ни металла: вести нечего, ждут первую.
  seconds(sim, STALL_SECONDS + 3)
  expect(pickTopic(sim, 1)).toBe('rockets')
  expect(sim.world.get(lab, Research)).toMatchObject({ speed: 0, lack: 'fuel' })
  // Сырьё есть, кредитов нет: следующая тема стоила бы тех же кредитов, и план стоит на первой.
  stash({ fuel: 100, metal: 100 })
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { credits: 0, earned: 0 })
  seconds(sim, STALL_SECONDS + 3)
  expect(pickTopic(sim, 1)).toBe('rockets')
  expect(sim.world.get(lab, Research)!.lack).toBe('credits')
  expect(progressOf(sim, 1, 'ammo')).toBe(0)
})

test('открытия случаются сами: шахта на месторождении — его руда найдена, переработанная руда — космопорт', () => {
  const { sim, stash } = base(['lab', 'smelter'])
  expect(isUnlocked(sim, 1, 'spaceport')).toBe(false)
  let spot: { x: number; y: number } | undefined
  for (let cellY = -6; cellY < 6 && !spot; cellY++) {
    for (let cellX = -6; cellX < 6 && !spot; cellX++) {
      const found = depositIn(sim, cellX, cellY)
      if (found?.kind === 'silicon' && canPlace(sim, 'mine', found.x, found.y)) spot = found
    }
  }
  if (!spot) throw new Error('Не нашлось месторождения кремния')
  seconds(sim, 1.1)
  expect(hasResearch(sim, 1, 'siliconFound')).toBe(false)
  // Переработку кремния не поставить в план, пока его руда не найдена.
  plan(sim, 'siliconOre')
  expect(planOf(sim, 1)).toEqual([])
  placeBuilding(sim.world, 'mine', spot.x, spot.y, 1)
  seconds(sim, 1.1)
  expect(hasResearch(sim, 1, 'siliconFound')).toBe(true)
  plan(sim, 'siliconOre')
  expect(planOf(sim, 1)).toEqual(['siliconOre'])
  expect(hasResearch(sim, 1, 'trade')).toBe(false)
  stash({ metal: 5 })
  seconds(sim, 1.1)
  expect(isUnlocked(sim, 1, 'spaceport')).toBe(true)
})

test('темы не зовут раньше, чем от них толк: трубы — после металла, пехота — после патронов, накопитель — после лития', () => {
  expect([...topicsUnder('pipes')]).toContain('metalOre')
  expect([...topicsUnder('infantry')]).toContain('ammo')
  expect([...topicsUnder('accumulators')]).toContain('lithiumOre')
  for (const ore of ['metalOre', 'siliconOre', 'fuelOre', 'khariteOre', 'lithiumOre', 'uraniumOre'] as const) {
    expect([...topicsUnder(ore)].some((before) => isTrigger(before) && before !== 'science')).toBe(true)
  }
})

test('кончились кредиты — лаборатория встаёт, не уходя в долг', () => {
  const { sim, buildings: [lab], stash } = base(['lab'])
  for (const [entity, player] of sim.world.query(Player)) if (player.id === 1) sim.world.set(entity, Player, { credits: 10, earned: 0 })
  stash({ metal: 100 })
  learn(sim, 'metalOre')
  plan(sim, 'ammo')
  seconds(sim, topicPrice(sim, 'ammo').seconds)
  expect(creditsOf(sim, 1)).toBe(0)
  expect(sim.world.get(lab, Research)!.lack).toBe('credits')
  const done = progressOf(sim, 1, 'ammo')
  expect(done).toBeCloseTo(10 / topicPrice(sim, 'ammo').cost, 2)
  addCredits(sim, 1, 1000)
  seconds(sim, topicPrice(sim, 'ammo').seconds)
  expect(hasResearch(sim, 1, 'ammo')).toBe(true)
})

test('исследования открывают армию и уровни зданий', () => {
  const { sim, plants: [plant], stash } = base(['lab'])
  placeBuilding(sim.world, 'radar', 60, 60, 1)
  stash({ blocks: 100 })
  expect(isUnlocked(sim, 1, 'barracks')).toBe(false)
  expect(isUnlocked(sim, 1, 'laserTurret')).toBe(false)
  expect(upgradeLack(sim, 1, plant)).toBe('research')
  learn(sim, 'infantry', 'lasers')
  expect(isUnlocked(sim, 1, 'laserTurret')).toBe(true)
  expect(isUnlocked(sim, 1, 'arcTurret')).toBe(false)
  learn(sim, 'levels2')
  expect(upgradeLack(sim, 1, plant)).toBeUndefined()
  sim.send(1, { type: 'upgrade', building: plant, builders: [] })
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(true)
})

test('правила темпа множат цену и срок, а уровни зданий дорожают своим правилом', () => {
  const { sim, buildings: [plant], stash } = base(['generator', 'lab'], { techTree: false, researchScale: 2, researchSlow: 3, researchGrowth: 2, upgradeScale: 3 })
  expect(topicPrice(sim, 'blocks')).toEqual({ cost: TIER_CREDITS * 2 * 2, materials: { metal: 6 * 2 * 2, silicon: 3 * 2 * 2 }, seconds: TIER_SECONDS * 2 * 3 })

  // Электростанция в матче — 150 кредитов и 10 стройблоков; здесь втрое.
  expect(upgradeCost('generator', 1, sim.rules.upgradeScale)).toEqual({ credits: 450, materials: { blocks: 30 } })
  stash({ blocks: 100 })
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'upgrade', building: plant, builders: [] })
  sim.advance(TICK)
  expect(sim.world.get(plant, Upgrade)).toMatchObject({ scale: 3 })
  expect(Math.round(credits - creditsOf(sim, 1))).toBe(450)
  expect(stockOf(sim, 1).items.blocks).toBe(70)
  expect(upgradeTicks('generator', 1, TICK, 3)).toBe(upgradeTicks('generator', 1, TICK) * 3)
  sim.rules.upgradeScale = 1
  sim.send(1, { type: 'cancelUpgrade', building: plant })
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(false)
  expect(Math.round(creditsOf(sim, 1) - credits)).toBe(0)
  expect(stockOf(sim, 1).items.blocks).toBe(100)
})

test('старое сохранение: техцентр становится лабораторией, игроку засчитано то, что у него стоит', () => {
  const { sim } = base(['smelter', 'factory'])
  const save = throughJson(sim.save())
  const world = save.world
  const [id, data] = world.entities.find(([, data]) => (data.Building as { type: string } | undefined)?.type === 'smelter')!
  void id
  // Подменяем завод на техцентр с начатым исследованием, как в версии 22.
  const factory = world.entities.find(([, data]) => (data.Building as { type: string } | undefined)?.type === 'factory')![1]
  ;(factory.Building as { type: string }).type = 'techCenter'
  factory.Research = { topic: 'armor', progress: 10, cost: 400, parts: 4, ticks: 800 }
  const player = world.entities.find(([, data]) => (data.Player as { id: number } | undefined)?.id === 1)![1].Player as { research: string[]; credits: number }
  player.research = ['beams']
  const credits = player.credits
  adoptLabs(world)
  expect((factory.Building as { type: string }).type).toBe('lab')
  expect(factory.Research).toBeUndefined()
  expect(player.credits).toBe(credits + 400)
  // Плавильня стоит — плавка засчитана; лучи — это лазеры и разряд со всеми предшественниками.
  expect(player.research).toContain('metalOre')
  expect(player.research).toEqual(expect.arrayContaining(['lasers', 'arcs', 'vehicles', 'parts', 'blocks', 'siliconOre', 'khariteOre']))
  expect(player.research).not.toContain('warheads')
  void data
  const copy = createSim(save)
  expect(hasResearch(copy, 1, 'metalOre')).toBe(true)
  expect(amountOf(sim.world.get([...sim.world.query(Inventory)][0][0], Inventory)!, 'metal')).toBeGreaterThanOrEqual(0)
})
