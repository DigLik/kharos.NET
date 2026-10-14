import { CORE, Owner, Unit, hasResearch, missingBuildings, rewardsOf, type BuildingType, type Sim, type Topic, type UnitType } from '../sim'

/**
 * Первые шаги новичка вместо туториала: список с галочками по настоящей цепочке экономики. Шаг отмечается сам, по миру,
 * а не по сценарию: есть готовое здание, открыта тема, получена награда. Поэтому переделка зданий ломает только
 * тексты, а не обучение. Цепочка: MCV разворачивается в главное здание, оно выпускает строителя; электростанция даёт
 * энергию, генератор материи — первый доход; шахта ставится на месторождение, лаборатория исследует его руду — тема
 * тратит руду из шахты; тогда открывается плавильня, первый металл открывает космопорт, а продажа — главный доход.
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

export type StepKey = 'deploy' | 'builder' | 'generator' | 'matter' | 'mine' | 'lab' | 'metalOre' | 'smelter' | 'spaceport' | 'sale'

export const STEPS: readonly StepSpec[] = [
  { key: 'deploy', deploy: true },
  { key: 'builder', unit: 'builder' },
  { key: 'generator', building: 'generator' },
  { key: 'matter', building: 'matter' },
  { key: 'mine', building: 'mine' },
  { key: 'lab', building: 'lab' },
  { key: 'metalOre', research: 'metalOre' },
  { key: 'smelter', building: 'smelter' },
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
    mine: rewards.includes('mine') || built('mine'),
    // Научный метод открывается, когда достроена лаборатория. Без дерева технологий темы открыты все — шаги о них
    // пройдены сразу.
    lab: built('lab') || hasResearch(sim, player, 'science'),
    metalOre: hasResearch(sim, player, 'metalOre'),
    // Торговля открывается первым готовым ресурсом — значит, плавильня уже поработала.
    smelter: built('smelter') || (sim.rules.techTree && hasResearch(sim, player, 'trade')),
    spaceport: built('spaceport'),
    sale: rewards.includes('sale'),
  }
  const done = STEPS.map(({ key }) => met[key])
  for (let i = done.length - 2; i >= 0; i--) done[i] ||= done[i + 1]
  return done
}

/** Текущий шаг — первый непройденный; undefined — пройдены все. */
export const currentStep = (done: readonly boolean[]) => STEPS[done.indexOf(false)] as StepSpec | undefined
