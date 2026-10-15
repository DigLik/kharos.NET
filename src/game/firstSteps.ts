import type { Entity } from '../ecs'
import { BUILDINGS, Building, CORE, DEPOSIT_CELL, DEPOSIT_SIZE, Owner, Position, Site, Unit, depositAt, depositsIn, hasResearch, missingBuildings, reserveLeft, rewardsOf, type BuildingType, type DepositKind, type Sim, type Topic, type UnitType } from '../sim'
import { isReady } from '../sim/buildings'
import { knowsDeposit } from '../sim/deposits'

/**
 * Первые шаги новичка вместо туториала: список с галочками по настоящей цепочке экономики. Шаг отмечается сам, по миру,
 * а не по сценарию: есть готовое здание, открыта тема, получена награда. Поэтому переделка зданий ломает только
 * тексты, а не обучение. Цепочка: MCV разворачивается в главное здание, оно выпускает строителя; электростанция даёт
 * энергию, генератор материи — первый доход; шахта ставится на металлическую руду, грузовик возит её руду в лабораторию
 * (здания вплотную связывает только одинаковые, а трубы — после плавки), лаборатория исследует плавку; тогда
 * открывается плавильня, металл из неё нужно куда-то складывать — иначе она забьётся и встанет; первый металл открывает
 * космопорт, а продажа — главный доход.
 *
 * У шага есть место на карте (stepTarget): где его делать — месторождение металла, шахта, рядом с которой ставить,
 * космопорт, который продаёт. Кнопка «Показать» ведёт туда камеру, а на карте оно обведено.
 */
export interface StepSpec {
  key: StepKey
  /** Что подсветить в сетке, пока шаг текущий: кнопку здания (и её раздел), заказ юнита или разворот MCV. */
  building?: BuildingType
  unit?: UnitType
  deploy?: true
  /** Кнопку окна исследований на верхней полосе. */
  research?: Topic
  /** Кнопку продажи у космопорта. */
  sell?: true
}

export type StepKey = 'deploy' | 'builder' | 'generator' | 'matter' | 'mine' | 'truck' | 'lab' | 'metalOre' | 'smelter' | 'metalYard' | 'spaceport' | 'sale'

export const STEPS: readonly StepSpec[] = [
  { key: 'deploy', deploy: true },
  { key: 'builder', unit: 'builder' },
  { key: 'generator', building: 'generator' },
  { key: 'matter', building: 'matter' },
  { key: 'mine', building: 'mine' },
  { key: 'truck', unit: 'truck' },
  { key: 'lab', building: 'lab' },
  { key: 'metalOre', research: 'metalOre' },
  { key: 'smelter', building: 'smelter' },
  { key: 'metalYard', building: 'metalYard' },
  { key: 'spaceport', building: 'spaceport' },
  { key: 'sale', sell: true },
]

/** Есть ли у игрока свой юнит этого вида. */
function hasUnit(sim: Sim, player: number, type: UnitType) {
  for (const [, unit, owner] of sim.world.query(Unit, Owner)) if (unit.type === type && owner.player === player) return true
  return false
}

/**
 * Какие шаги пройдены, по шагу. Пройденный дальше отмечает и все до него: строитель мог погибнуть, а главное здание —
 * свернуться в MCV, но новичку уже не надо учиться их заказывать и разворачивать.
 */
export function stepsDone(sim: Sim, player: number): boolean[] {
  const rewards = rewardsOf(sim, player)
  const built = (type: BuildingType) => missingBuildings(sim, player, [type]).length === 0
  const met: Record<StepKey, boolean> = {
    deploy: rewards.includes('deploy') || built(CORE),
    builder: hasUnit(sim, player, 'builder'),
    generator: rewards.includes('generator') || built('generator'),
    matter: rewards.includes('matter') || built('matter'),
    // Шахта нужна на металле: на другой руде плавку не изучить.
    mine: minesOn(sim, player, 'metal').length > 0,
    // Грузовик возит руду с шахты в лабораторию: вплотную связаны только одинаковые здания, а трубы — после плавки.
    truck: hasUnit(sim, player, 'truck'),
    // Научный метод открывается, когда достроена лаборатория. Без дерева технологий темы открыты все — шаги о них
    // пройдены сразу.
    lab: built('lab') || hasResearch(sim, player, 'science'),
    metalOre: hasResearch(sim, player, 'metalOre'),
    // Торговля открывается первым готовым ресурсом — значит, плавильня уже поработала.
    smelter: built('smelter') || (sim.rules.techTree && hasResearch(sim, player, 'trade')),
    metalYard: rewards.includes('metalYard') || built('metalYard'),
    spaceport: built('spaceport'),
    sale: rewards.includes('sale'),
  }
  const done = STEPS.map(({ key }) => met[key])
  for (let i = done.length - 2; i >= 0; i--) done[i] ||= done[i + 1]
  return done
}

/** Готовые шахты игрока на месторождениях этого вида. */
function minesOn(sim: Sim, player: number, kind: DepositKind): Entity[] {
  const found: Entity[] = []
  for (const [entity, building, position] of sim.world.query(Building, Position)) {
    if (building.type === 'mine' && isReady(sim, player, entity) && depositAt(sim, position.x, position.y)?.kind === kind) found.push(entity)
  }
  return found
}

/** Своё здание этого вида — готовое или на стройке; undefined — нет. */
function ownBuilding(sim: Sim, player: number, type: BuildingType): Entity | undefined {
  let site: Entity | undefined
  for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
    if (building.type !== type || owner.player !== player) continue
    if (!sim.world.has(entity, Site)) return entity
    site ??= entity
  }
  return site
}

/** Где на карте делать шаг: прямоугольник в тайлах. */
export interface StepTarget {
  x: number
  y: number
  width: number
  height: number
}

/** Основание здания как цель. */
function footprint(sim: Sim, entity: Entity | undefined): StepTarget | undefined {
  const position = entity === undefined ? undefined : sim.world.get(entity, Position)
  const type = entity === undefined ? undefined : sim.world.get(entity, Building)?.type
  return position && type !== undefined ? { x: position.x, y: position.y, width: BUILDINGS[type].width, height: BUILDINGS[type].height } : undefined
}

/** Свой юнит этого вида как цель: тайл под ним. */
function unitTarget(sim: Sim, player: number, type: UnitType): StepTarget | undefined {
  for (const [, unit, owner, position] of sim.world.query(Unit, Owner, Position)) {
    if (unit.type === type && owner.player === player) return { x: Math.floor(position.x) - 1, y: Math.floor(position.y) - 1, width: 3, height: 3 }
  }
  return undefined
}

/** Как далеко от базы искать месторождение для первой шахты, в тайлах. */
const DEPOSIT_SEARCH = 80

/** Ближайшее к базе известное игроку свободное месторождение этого вида, где ещё есть руда. */
function nearestDeposit(sim: Sim, player: number, kind: DepositKind, from: { x: number; y: number }): StepTarget | undefined {
  let best: StepTarget | undefined
  let bestDistance = Infinity
  const reach = Math.ceil(DEPOSIT_SEARCH / DEPOSIT_CELL)
  const cellX = Math.floor(from.x / DEPOSIT_CELL)
  const cellY = Math.floor(from.y / DEPOSIT_CELL)
  for (let y = cellY - reach; y <= cellY + reach; y++) {
    for (let x = cellX - reach; x <= cellX + reach; x++) {
      for (const spot of depositsIn(sim, x, y)) {
        if (spot.kind !== kind || !knowsDeposit(sim, player, spot) || sim.occupancy.at(spot.x, spot.y) !== undefined || reserveLeft(sim, spot.x, spot.y) <= 0) continue
        const distance = Math.hypot(spot.x + DEPOSIT_SIZE / 2 - from.x, spot.y + DEPOSIT_SIZE / 2 - from.y)
        if (distance > DEPOSIT_SEARCH || distance >= bestDistance) continue
        best = { x: spot.x, y: spot.y, width: DEPOSIT_SIZE, height: DEPOSIT_SIZE }
        bestDistance = distance
      }
    }
  }
  return best
}

/**
 * Где делать шаг: MCV — разворачивать, главное здание — заказывать строителя, грузовика и строиться рядом,
 * месторождение металла — ставить шахту, шахта на металле — ставить рядом лабораторию и плавильню (их кормят
 * грузовики), плавильня — склад металла, склад — космопорт, космопорт — продавать. undefined — показать нечего:
 * исследование, или нужное ещё не найдено.
 */
export function stepTarget(sim: Sim, player: number, key: StepKey): StepTarget | undefined {
  const core = footprint(sim, ownBuilding(sim, player, CORE))
  const metalMine = () => footprint(sim, minesOn(sim, player, 'metal')[0])
  switch (key) {
    case 'deploy':
      return unitTarget(sim, player, 'mcv')
    case 'builder':
    case 'truck':
    case 'generator':
    case 'matter':
      return core
    case 'mine': {
      const base = core ?? unitTarget(sim, player, 'mcv')
      return base && nearestDeposit(sim, player, 'metal', { x: base.x + base.width / 2, y: base.y + base.height / 2 })
    }
    case 'lab':
    case 'smelter':
      return metalMine()
    case 'metalOre':
      return footprint(sim, ownBuilding(sim, player, 'lab'))
    case 'metalYard':
      return footprint(sim, ownBuilding(sim, player, 'smelter'))
    case 'spaceport':
      return footprint(sim, ownBuilding(sim, player, 'metalYard')) ?? core
    case 'sale':
      return footprint(sim, ownBuilding(sim, player, 'spaceport'))
  }
}

/** Текущий шаг — первый непройденный; undefined — пройдены все. */
export const currentStep = (done: readonly boolean[]) => STEPS[done.indexOf(false)] as StepSpec | undefined
