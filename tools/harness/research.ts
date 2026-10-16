/**
 * Раздел «research»: компоненты и лестница исследований (этап 8.2 design.md).
 *
 *   1. Потолок по описаниям: что даёт одно месторождение харита и сколько кремния на него уходит.
 *   2. Карта: сколько харита лежит на карте каждого размера и сколько — в досягаемости одной базы.
 *   3. Живая линия в симуляции: шахта харита и шахта кремния, обогащение, цех компонентов и шкафы на одной сети труб —
 *      сколько компонентов в час выходит на самом деле и за сколько кончается месторождение.
 *   4. Лестница: цена каждой темы по правилам темпа (Rules.researchScale, researchGrowth, researchSlow), сколько
 *      месторождений харита она просит и когда открывается при разном размахе базы и числе лабораторий.
 *   5. Уровни зданий: во что обходится поднять типовую базу.
 *
 * Раздел отвечает на вопрос: на сколько хватает дерева исследований в мире, который живёт две недели.
 */
import {
  ASSEMBLE_RATE, BUILDINGS, DEPOSIT_KINDS, Inventory, MAX_LEVEL, PRODUCT_SPECS, REFINE_RATIO, RESOURCE_SPECS, TOPICS, amountOf, connectAll, createSim,
  cycleSeconds, depositIn, levelGain, priceOf, tierOf, upgradeCost, DEFAULT_RULES, type BuildingType, type DepositSpot, type Rules, type Sim, type Topic,
} from '@kharos/sim-ts'
import { setWorking } from '@kharos/sim-ts/assembly'
import { put } from '@kharos/sim-ts/inventory'
import { DEPOSIT_CELL, reserveLeft } from '@kharos/sim-ts/deposits'
import { placeBuilding } from '@kharos/sim-ts/buildings'
import { SESSION_RULES } from '../../server/settings'
import { placeNearMine } from './economy'
import { TICK, newSim, options, printTable, rings, round } from './lib'

const TICKS = Math.round(1 / TICK)
/** Харита на один компонент и кремния на единицу харита — по рецепту цеха. */
const KHARITE_PER_PART = PRODUCT_SPECS.parts.recipe.kharite / PRODUCT_SPECS.parts.yield
const SILICON_PER_PART = PRODUCT_SPECS.parts.recipe.silicon / PRODUCT_SPECS.parts.yield
/** Среднее месторождение харита в компонентах. */
const AVERAGE_DEPOSIT = (DEPOSIT_KINDS.kharite.min + DEPOSIT_KINDS.kharite.max) / 2 / REFINE_RATIO / KHARITE_PER_PART
/** Время как «м:сс» — минуты без выравнивания, в отличие от clock. */
const minutes = (time: number) => `${Math.floor(time / 60)}:${String(Math.floor(time % 60)).padStart(2, '0')}`
/** Время как «ч:мм». */
const hours = (time: number) => `${Math.floor(time / 3600)}:${String(Math.floor((time % 3600) / 60)).padStart(2, '0')}`

/** 1. Потолок по описаниям: месторождение харита в компонентах. */
function printSupply() {
  const { min, max, rate } = DEPOSIT_KINDS.kharite
  const parts = (reserve: number) => Math.floor(reserve / REFINE_RATIO / KHARITE_PER_PART)
  const perHour = (rate / REFINE_RATIO / KHARITE_PER_PART) * 3600
  console.log('\nКомпоненты: потолок по описаниям')
  console.log(`  рецепт                 ${PRODUCT_SPECS.parts.recipe.silicon} кремния + ${PRODUCT_SPECS.parts.recipe.kharite} харита → ${PRODUCT_SPECS.parts.yield}; цех собирает ${round((PRODUCT_SPECS.parts.yield / cycleSeconds('parts')) * 3600, 0)} в час (ASSEMBLE_RATE ${ASSEMBLE_RATE})`)
  console.log(`  шахта харита           ${rate} руды/с — ${round(perHour, 0)} компонентов в час, пока месторождение живо: узкое место — шахта, не цех`)
  console.log(`  месторождение          ${min}–${max} харита = ${parts(min)}–${parts(max)} компонентов; шахта выбирает его за ${minutes(min / rate)}–${minutes(max / rate)}`)
  console.log(`  кремний                ${round(rate * SILICON_PER_PART / KHARITE_PER_PART, 2)}/с на шахту харита: шахта кремния (${DEPOSIT_KINDS.silicon.rate}/с) кормит ${round(DEPOSIT_KINDS.silicon.rate / (rate * SILICON_PER_PART / KHARITE_PER_PART), 1)} шахты харита`)
  console.log(`  продать вместо этого   месторождение харита — ${min * RESOURCE_SPECS.kharite.price}–${max * RESOURCE_SPECS.kharite.price} кр, по ${RESOURCE_SPECS.kharite.price} за штуку`)
}

/** Все месторождения карты: генератор считает клетку при первом обращении. */
function depositsOf(sim: Sim) {
  const { left, top, right, bottom } = sim.bounds
  const spots: DepositSpot[] = []
  for (let cy = Math.floor(top / DEPOSIT_CELL); cy <= Math.floor((bottom - 1) / DEPOSIT_CELL); cy++) {
    for (let cx = Math.floor(left / DEPOSIT_CELL); cx <= Math.floor((right - 1) / DEPOSIT_CELL); cx++) {
      const spot = depositIn(sim, cx, cy)
      if (spot) spots.push(spot)
    }
  }
  return spots
}

/** 2. Карта: сколько харита на карте каждого размера и сколько — вокруг одной базы. */
function printMap() {
  console.log('\nХарит на карте: генератор по умолчанию, по три зерна на размер')
  const rows: (string | number)[][] = []
  const reach: number[][] = [[], [], []]
  const RADII = [100, 200, 300]
  for (const size of [256, 1024, 2048]) {
    const counts: number[] = []
    const totals: number[] = []
    let all = 0
    for (let n = 0; n < 3; n++) {
      const sim = createSim({ generator: { ...options.generator, seed: options.generator.seed + n * 7919 }, size })
      const spots = depositsOf(sim)
      const kharite = spots.filter((spot) => spot.kind === 'kharite')
      all += spots.length
      counts.push(kharite.length)
      totals.push(kharite.reduce((sum, spot) => sum + spot.reserve, 0))
      // Досягаемость меряем на карте, где есть куда расти: от каждого двадцатого месторождения как от базы.
      if (size === 2048) {
        const half = size / 2 - RADII[2]
        spots.forEach((base, i) => {
          if (i % 20 || Math.abs(base.x) > half || Math.abs(base.y) > half) return
          RADII.forEach((radius, r) => {
            reach[r].push(kharite.filter((spot) => Math.hypot(spot.x - base.x, spot.y - base.y) <= radius).reduce((sum, spot) => sum + spot.reserve, 0))
          })
        })
      }
      sim.destroy()
    }
    const mean = (list: number[]) => list.reduce((sum, value) => sum + value, 0) / list.length
    const parts = mean(totals) / REFINE_RATIO / KHARITE_PER_PART
    rows.push([`${size}×${size}`, Math.round(all / 3), `${Math.min(...counts)}–${Math.max(...counts)}`, Math.round(parts), round(parts / ladder(SESSION).parts, 1)])
  }
  printTable(['карта', 'месторожд.', 'из них харит', 'компонентов', 'деревьев'], rows, 12, 13)
  console.log('  компонентов — если весь харит карты пустить в цех; деревьев — сколько полных деревьев исследований')
  console.log(`  по правилам сервера (×${SESSION.researchScale}, ярус ×${SESSION.researchGrowth}) это кормит, если харит не продавать и не тратить на технику`)
  console.log('\nХарит вокруг базы (карта 2048, базы — вдали от края)')
  printTable(
    ['радиус', 'компонентов', 'от–до', 'месторожд.'],
    RADII.map((radius, r) => {
      const list = reach[r].map((kharite) => kharite / REFINE_RATIO / KHARITE_PER_PART)
      const mean = list.reduce((sum, value) => sum + value, 0) / list.length
      return [`${radius} тайлов`, Math.round(mean), `${Math.round(Math.min(...list))}–${Math.round(Math.max(...list))}`, round(mean / AVERAGE_DEPOSIT, 1)]
    }),
    12,
    13,
  )
}

/** Сколько компонентов в мире: в цехе и в шкафах. */
function partsIn(sim: Sim) {
  let total = 0
  for (const [, inventory] of sim.world.query(Inventory)) total += amountOf(inventory, 'parts')
  return total
}

/** Ближайшее к началу мира месторождение харита. */
function khariteSpot(sim: Sim) {
  for (const cell of rings(0, 0, 8)) {
    const spot = depositIn(sim, cell.x, cell.y)
    if (spot?.kind === 'kharite') return spot
  }
  throw new Error('Не нашлось харита рядом с началом мира')
}

/**
 * 3. Живая линия: готовая база у месторождения харита. Кремний лежит в хранилищах с запасом на всё месторождение:
 * его даёт любая шахта кремния, и меряется не он. Шкафов столько, чтобы все компоненты легли на склад: меряется
 * поток, а не теснота. Возвращает компонентов в секунду, пока шахта копает, и сколько секунд от запуска до первого.
 */
function measureLine() {
  const sim = newSim()
  const kharite = khariteSpot(sim)
  placeBuilding(sim.world, 'mine', kharite.x, kharite.y, 1)
  const parts = kharite.reserve / REFINE_RATIO / KHARITE_PER_PART
  const lockers = Math.ceil(parts / BUILDINGS.partsLocker.inventory) + 1
  const stores = Math.ceil((parts * SILICON_PER_PART) / BUILDINGS.siliconStore.inventory)
  const grid = (type: BuildingType, count: number, dx: number, dy: number, step: number) =>
    Array.from({ length: count }, (_, i): [BuildingType, number, number] => [type, dx + (i % 4) * step, dy + Math.floor(i / 4) * step])
  const base: [BuildingType, number, number][] = [
    ['command', 7, 0], ['enricher', 4, -4], ['partsPlant', 4, 5], ['generator', 6, -7], ['generator', 9, -7],
    ...grid('partsLocker', lockers, 8, 5, 2), ...grid('siliconStore', stores, -10, -4, 3),
  ]
  for (const [type, dx, dy] of base) {
    const entity = placeNearMine(sim, kharite, type, kharite.x + 1 + dx, kharite.y + 1 + dy)
    if (entity === undefined) throw new Error(`Нет места под «${type}» у харита`)
    if (type === 'siliconStore') put(sim.world.get(entity, Inventory)!, 'silicon', BUILDINGS.siliconStore.inventory)
    // Завод изделий ставится выключенным.
    if (type === 'partsPlant') setWorking(sim, 1, entity, true)
  }
  connectAll(sim, 1)

  const limit = 60 * 60
  let first = 0
  let dry = 0
  const marks: number[] = []
  for (let second = 1; second <= limit; second++) {
    for (let tick = 0; tick < TICKS; tick++) sim.advance(TICK)
    const parts = partsIn(sim)
    if (!first && parts >= 1) first = second
    if (!dry && reserveLeft(sim, kharite.x, kharite.y) <= 0) dry = second
    if (second % 60 === 0) marks.push(parts)
    // Месторождение выбрано и цех доел остатки.
    if (dry && second - dry > 120 && marks.length > 2 && marks[marks.length - 1] === marks[marks.length - 2]) break
  }
  const total = partsIn(sim)
  // Поток — между первым компонентом и концом месторождения: разгон и доедание остатков в него не входят.
  const steady = dry > first ? (marks[Math.floor(dry / 60) - 1] - marks[Math.ceil(first / 60)]) / ((Math.floor(dry / 60) - 1 - Math.ceil(first / 60)) * 60) : 0
  console.log('\nЛиния компонентов в симуляции: шахта харита, обогащение, цех и шкафы на одной сети труб; кремний — со склада')
  console.log(`  месторождение          ${kharite.reserve} харита`)
  console.log(`  первый компонент       ${first ? minutes(first) : 'не дождались'}`)
  console.log(`  поток                  ${round(steady * 3600, 0)} компонентов в час (по описаниям — ${round((DEPOSIT_KINDS.kharite.rate / REFINE_RATIO / KHARITE_PER_PART) * 3600, 0)})`)
  console.log(`  месторождение кончилось ${dry ? minutes(dry) : 'не кончилось за час'}`)
  console.log(`  всего компонентов      ${round(total, 0)} из ${Math.floor(kharite.reserve / REFINE_RATIO / KHARITE_PER_PART)} возможных`)
  sim.destroy()
  return { rate: steady, first }
}

/** Правила темпа исследований. */
type Pace = Pick<Rules, 'researchScale' | 'researchGrowth' | 'researchSlow'>
const SESSION: Rules = { ...DEFAULT_RULES, ...SESSION_RULES }
const pace = (researchScale: number, researchGrowth: number, researchSlow = 1): Pace => ({ researchScale, researchGrowth, researchSlow })

/** Типовой порядок тем: по ярусам сложности, в ярусе — как в описании. Цена от порядка не зависит. */
const typicalOrder = (): Topic[] => [...TOPICS].sort((a, b) => tierOf(a) - tierOf(b))

/** Дерево целиком по правилам темпа: ступени в типовом порядке и итог. Харит — в компонентах, которые тратит тема. */
function ladder(pace: Pace) {
  const steps = typicalOrder().map((topic) => {
    const { cost, materials, seconds } = priceOf(topic, pace)
    return { topic, cost, parts: materials.parts ?? 0, seconds }
  })
  return {
    steps,
    cost: steps.reduce((sum, step) => sum + step.cost, 0),
    parts: steps.reduce((sum, step) => sum + step.parts, 0),
    seconds: steps.reduce((sum, step) => sum + step.seconds, 0),
  }
}

/** Размах базы: сколько шахт харита копают без простоя и сколько кредитов в секунду база приносит. */
interface Scale {
  name: string
  mines: number
  income: number
}

/**
 * Когда открывается каждая ступень: labs лабораторий ведут темы подряд вместе; тема кончается не раньше, чем кончилась
 * прошлая и накоплены компоненты и кредиты на неё целиком. Компоненты идут потоком rate с шахты, но шахта копает
 * не всё время: между месторождениями — переезд (move секунд на каждое).
 */
function timeline(pace: Pace, scale: Scale, rate: number, move: number, labs = 1) {
  // Доля времени, когда шахта копает: месторождение выбирается за life, потом переезд.
  const life = AVERAGE_DEPOSIT / rate
  const parts = scale.mines * rate * (life / (life + move))
  let free = 0
  let spentParts = 0
  let spentCredits = 0
  return ladder(pace).steps.map((step) => {
    spentParts += step.parts
    spentCredits += step.cost
    const byParts = spentParts / parts
    const byCredits = spentCredits / scale.income
    const start = Math.max(free + step.seconds / labs, byParts, byCredits)
    free = start
    return { ...step, start, done: free, waits: start === byParts ? 'харит' : start === byCredits ? 'кредиты' : 'лаборатории' }
  })
}

/** 4. Лестница: цены ступеней, когда они открываются, и что будет при других правилах. */
function printLadder(rate: number, first: number) {
  const session = SESSION
  const whole = ladder(session)
  console.log(`\nЛестница исследований по правилам сервера: цена ×${session.researchScale}, каждый ярус ×${session.researchGrowth}, срок ещё ×${session.researchSlow}; порядок — по ярусам`)
  printTable(
    ['тема', 'кредиты', 'компон.', 'срок', 'месторожд.'],
    [
      ...whole.steps.map((step) => [step.topic, step.cost, step.parts, hours(step.seconds), round(step.parts / AVERAGE_DEPOSIT, 1)]),
      ['всё дерево', whole.cost, whole.parts, hours(whole.seconds), round(whole.parts / AVERAGE_DEPOSIT, 1)],
    ],
    12,
    11,
  )
  console.log(`  месторожд. — сколько средних месторождений харита (${AVERAGE_DEPOSIT} компонентов) уходит на тему`)

  // Переезд на новое месторождение: шахта, обогащение и труба — как первый компонент на готовой базе, плюс дорога.
  const move = first + 240
  const scales: Scale[] = [
    { name: 'малая', mines: 1, income: 12 },
    { name: 'средняя', mines: 2, income: 25 },
    { name: 'большая', mines: 4, income: 50 },
  ]
  console.log(`\nКогда открывается тема: часы работы базы от готовой лаборатории (ч:мм), одна лаборатория`)
  const lines = scales.map((scale) => timeline(session, scale, rate, move))
  printTable(
    ['тема', ...scales.flatMap((scale) => [scale.name, 'ждёт'])],
    whole.steps.map((step, i) => [step.topic, ...lines.flatMap((line) => [hours(line[i].done), line[i].waits])]),
    12,
    10,
  )
  for (const scale of scales) console.log(`  ${scale.name.padEnd(8)} — шахт харита без простоя: ${scale.mines}, доход ${scale.income} кр/с`)
  console.log(`  переезд на новое месторождение — ${minutes(move)}; ждёт — чего тема дожидалась последним`)

  console.log('\nТо же при других правилах: всё дерево')
  const paces: Pace[] = [DEFAULT_RULES, pace(1, 3, 6), session, pace(1, 3, 20), pace(2, 3, 10), pace(1, 3.5, 10), pace(10, 2, 12)]
  printTable(
    ['правила', 'кредиты', 'компон.', 'месторожд.', 'лаборат.', ...scales.map((scale) => scale.name), '4 лаб.', 'первая'],
    paces.map((pace) => {
      const tree = ladder(pace)
      return [
        `×${pace.researchScale}, ×${pace.researchGrowth}, ×${pace.researchSlow}`, tree.cost, tree.parts, round(tree.parts / AVERAGE_DEPOSIT, 1), hours(tree.seconds),
        ...scales.map((scale) => hours(timeline(pace, scale, rate, move).at(-1)!.done)),
        hours(timeline(pace, scales[1], rate, move, 4).at(-1)!.done),
        hours(timeline(pace, scales[0], rate, move)[0].done),
      ]
    }),
    12,
    11,
  )
  console.log('  лаборат. — сколько идут сами исследования одной лаборатории; малая, средняя, большая — когда открыто всё дерево')
  console.log('  с одной лабораторией; 4 лаб. — у средней базы с четырьмя; первая — первая тема у малой базы')
}

/** Типовая база серверной сессии: что из неё поднимается уровнями и в скольких штуках. */
const TYPICAL_BASE: [BuildingType, number][] = [
  ['generator', 4], ['mine', 4], ['smelter', 1], ['siliconWorks', 1], ['enricher', 1], ['metalYard', 2], ['siliconStore', 1], ['khariteVault', 1],
  ['blockYard', 1], ['ammoBunker', 1], ['partsLocker', 2], ['spaceport', 1],
]

/** 5. Уровни зданий: во что обходится поднять типовую базу и как это соотносится с деревом исследований. */
function printLevels() {
  const upgradable = TYPICAL_BASE.filter(([type]) => levelGain(type) !== undefined)
  const rows: (string | number)[][] = []
  const total = [0, 0, 0, 0, 0, 0]
  for (const [type, count] of upgradable) {
    const cells: number[] = []
    for (let level = 1; level < MAX_LEVEL; level++) {
      const { credits, materials } = upgradeCost(type, level, SESSION.upgradeScale)
      cells.push(credits * count, (materials.blocks ?? 0) * count, (materials.parts ?? 0) * count)
    }
    cells.forEach((value, i) => (total[i] += value))
    rows.push([`${type} ×${count}`, ...cells])
  }
  rows.push(['вся база', ...total])
  const tree = ladder(SESSION)
  console.log(`\nУровни зданий по правилам сервера (×${SESSION.upgradeScale}): поднять типовую базу (${upgradable.reduce((sum, [, count]) => sum + count, 0)} зданий) до второго и до третьего уровня`)
  printTable(['здание', 'кр → 2', 'блоков', 'компон.', 'кр → 3', 'блоков', 'компон.'], rows, 16, 9)
  console.log(`  обе ступени — ${total[0] + total[3]} кр, ${total[1] + total[4]} стройблоков и ${total[2] + total[5]} компонентов: ${round(((total[0] + total[3]) / tree.cost) * 100, 0)}% кредитов и ${round(((total[2] + total[5]) / tree.parts) * 100, 0)}% компонентов дерева исследований`)
  console.log(`  сами уровни открывают темы levels2 (${tree.steps.find((step) => step.topic === 'levels2')!.parts} компонентов) и levels3 (${tree.steps.find((step) => step.topic === 'levels3')!.parts})`)
}

export function runResearch() {
  printSupply()
  printMap()
  const { rate, first } = measureLine()
  printLadder(rate, first)
  printLevels()
}
