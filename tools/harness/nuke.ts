/**
 * Раздел «nuke»: ядерный удар и защита от него (§9 п. 4 design.md).
 *
 *   1. Цена по описаниям: шахта и боеголовка против перехватчика и его ракет — в кредитах и в сырье по цене продажи.
 *   2. Что сносит взрыв: на каком расстоянии от точки удара здание каждой прочности гибнет и сколько теряет на краю.
 *   3. Удар по базе в симуляции: настоящий пуск по базе песочницы — сколько зданий и юнитов гибнет и во что это
 *      обошлось защищавшемуся.
 *   4. Залп: сколько ракет из залпа проходит мимо одного перехватчика с полным запасом.
 *
 * Раздел отвечает на вопрос: окупается ли удар и хватает ли одного перехватчика.
 */
import type { Entity } from '@kharos/ecs'
import {
  BUILDINGS, BUILDING_TYPES, Building, Health, Interceptor, Missile, Owner, PRODUCT_SPECS, RESOURCE_SPECS, Silo, UNITS, Unit, buildingHp, canPlace, createSim, entriesOf,
  type Amounts, type BuildingType, type Good, type Sim,
} from '../../src/sim'
import { placeBuilding } from '../../src/sim/buildings'
import { addCredits } from '../../src/sim/economy'
import { connectAll } from '../../src/sim/piping'
import { spawnSandbox } from '../../src/sim/sandbox'
import { options, placeNear, printTable, round, seconds, until } from './lib'

const { warhead } = BUILDINGS.silo
const { intercept } = BUILDINGS.interceptor
/** Время как «м:сс». */
const clock = (time: number) => `${Math.floor(time / 60)}:${String(Math.floor(time % 60)).padStart(2, '0')}`

/** Мир без защиты новичка: под ней удар урона не наносит. */
const RULES = { protection: 0, techTree: false }

/** Во что обходится груз сырьём по цене продажи: изделие — его рецепт, пересчитанный на штуку. */
function worth(good: Good): number {
  if (good in RESOURCE_SPECS) return RESOURCE_SPECS[good as keyof typeof RESOURCE_SPECS].price
  const spec = PRODUCT_SPECS[good as keyof typeof PRODUCT_SPECS]
  if (!spec) return 0
  return entriesOf(spec.recipe as Amounts).reduce((sum, [item, amount]) => sum + worth(item) * amount, 0) / spec.yield
}
const worthOf = (amounts: Amounts = {}) => entriesOf(amounts).reduce((sum, [good, amount]) => sum + worth(good) * amount, 0)
const listOf = (amounts: Amounts = {}) => entriesOf(amounts).map(([good, amount]) => `${amount} ${good}`).join(', ') || '—'

/** 1. Цена удара и защиты. */
function printPrices() {
  console.log('\nЯдерный удар и защита: цена по описаниям (сырьё — по цене продажи)')
  const rows = [
    ['Шахта', BUILDINGS.silo.cost, listOf(BUILDINGS.silo.materials), round(BUILDINGS.silo.cost + worthOf(BUILDINGS.silo.materials), 0)],
    ['Боеголовка', warhead.cost, listOf(warhead.materials), round(warhead.cost + worthOf(warhead.materials), 0)],
    ['Перехватчик', BUILDINGS.interceptor.cost, listOf(BUILDINGS.interceptor.materials), round(BUILDINGS.interceptor.cost + worthOf(BUILDINGS.interceptor.materials), 0)],
    ['Его ракета', intercept.cost, listOf(intercept.materials), round(intercept.cost + worthOf(intercept.materials), 0)],
  ]
  printTable(['', 'кредиты', 'материалы', 'всего'], rows, 14, 26)
  const strike = warhead.cost + worthOf(warhead.materials)
  const shot = intercept.cost + worthOf(intercept.materials)
  const dome = BUILDINGS.interceptor.cost + worthOf(BUILDINGS.interceptor.materials) + shot * intercept.capacity
  console.log(`  удар без шахты ${round(strike, 0)} против ${round(shot, 0)} за сбившую его ракету: защита дешевле в ${round(strike / shot, 1)} раза`)
  console.log(`  перехватчик с полным запасом (${intercept.capacity}) — ${round(dome, 0)}: как ${round(dome / strike, 1)} удара; круг ${intercept.radius} тайлов`)
  console.log(`  сроки: боеголовка ${clock(warhead.seconds)} при полной энергии (шахта просит ${-BUILDINGS.silo.power!}), пуск ${warhead.launch} с, полёт ${warhead.speed} тайлов/с — ${clock(1024 / warhead.speed)} через карту 1024; ракета перехватчика ${clock(intercept.seconds)}`)
}

/** 2. Что сносит взрыв. */
function printBlast() {
  const { radius, core, damage } = warhead.blast
  console.log(`\nВзрыв: радиус ${radius}, ядро ${core} (полный урон ${damage}), к краю урон падает до нуля; расстояние — до края здания`)
  /** До какого расстояния гибнет здание этой прочности. */
  const lethal = (hp: number) => (hp > damage ? -1 : core + (radius - core) * (1 - hp / damage))
  const groups = new Map<number, BuildingType[]>()
  for (const type of BUILDING_TYPES) {
    const hp = buildingHp(type)
    groups.set(hp, [...(groups.get(hp) ?? []), type])
  }
  const rows = [...groups].sort(([a], [b]) => a - b).map(([hp, types]) => {
    const reach = lethal(hp)
    const lost = (distance: number) => `${round(Math.min(1, (damage * (distance <= core ? 1 : (radius - distance) / (radius - core))) / hp) * 100, 0)}%`
    return [`${hp}`, reach < 0 ? 'не гибнет' : round(reach, 1), lost(8), lost(10), lost(11.5), types.slice(0, 4).join(', ') + (types.length > 4 ? ` и ещё ${types.length - 4}` : '')]
  })
  printTable(['прочность', 'гибнет до', 'на 8', 'на 10', 'на 11.5', 'здания'], rows, 10, 10)
  const tough = Math.max(...Object.values(UNITS).map((unit) => unit.hp))
  console.log(`  юниты: самый прочный — ${tough}, гибнет до ${round(lethal(tough), 1)} тайлов; площадь гибели здания на 1000 — ${round(Math.PI * lethal(1000) ** 2, 0)} тайлов²`)
}

/** База нападающего в ряд под общей трубой: главное здание, электростанции и шахты с готовыми боеголовками. */
function attackerBase(sim: Sim, player: number, nearX: number, nearY: number, silos: number) {
  const layout: [BuildingType, number][] = [['command', 0], ['generator', 4], ['generator', 7]]
  let at = 10
  for (let i = 0; i < silos; i++) {
    layout.push(['silo', at])
    at += BUILDINGS.silo.width + 1
  }
  for (let ring = 0; ring < 300; ring++) {
    for (let dy = -ring; dy <= ring; dy++) {
      for (let dx = -ring; dx <= ring; dx++) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) !== ring) continue
        const x = nearX + dx
        const y = nearY + dy
        if (!layout.every(([type, shift]) => canPlace(sim, type, x + shift, y))) continue
        if (!Array.from({ length: at }, (_, shift) => canPlace(sim, 'pipe', x + shift, y - 1)).every(Boolean)) continue
        const buildings = layout.map(([type, shift]) => placeBuilding(sim.world, type, x + shift, y, player))
        for (let shift = 0; shift < at; shift++) placeBuilding(sim.world, 'pipe', x + shift, y - 1, player)
        addCredits(sim, player, 10000)
        const found = buildings.slice(3)
        for (const silo of found) Object.assign(sim.world.get(silo, Silo)!, { ready: true })
        return found
      }
    }
  }
  throw new Error('Не нашлось места под базу нападающего')
}

/** Сколько у игрока зданий и юнитов и сколько они стоили. */
function census(sim: Sim, player: number) {
  let buildings = 0
  let units = 0
  let cost = 0
  for (const [, building, owner] of sim.world.query(Building, Owner)) {
    if (owner.player !== player) continue
    buildings++
    cost += BUILDINGS[building.type].cost
  }
  for (const [, unit, owner] of sim.world.query(Unit, Owner)) {
    if (owner.player !== player) continue
    units++
    cost += UNITS[unit.type].cost
  }
  return { buildings, units, cost }
}

const flying = (sim: Sim) => {
  let count = 0
  for (const _ of sim.world.query(Missile)) count++
  return count
}

/** Мир с базой песочницы игрока 1 и базой нападающего (игрок 2) в стороне; shield — у защищающегося перехватчик с полным запасом. */
function standoff(silos: number, shield: boolean) {
  const sim = createSim({ ...options, rules: RULES })
  const target = spawnSandbox(sim, 1)
  if (!target) throw new Error('База песочницы не встала')
  if (shield) {
    const dome = placeNear(sim, 'interceptor', target.x, target.y + 6, 1)
    if (dome === undefined) throw new Error('Перехватчику не нашлось места')
    connectAll(sim, 1)
    Object.assign(sim.world.get(dome, Interceptor)!, { missiles: intercept.capacity })
  }
  const found = attackerBase(sim, 2, Math.round(target.x) + 200, Math.round(target.y), silos)
  seconds(sim, 1)
  return { sim, target, silos: found }
}

/** Пускает все ракеты разом в точку и ждёт, пока долетят. Возвращает, сколько взорвалось у цели. */
function volley(sim: Sim, silos: Entity[], x: number, y: number) {
  let hits = 0
  for (const silo of silos) sim.send(2, { type: 'launch', silo, x: Math.round(x), y: Math.round(y) })
  seconds(sim, warhead.launch + 0.5)
  const before = census(sim, 1)
  let last = before.buildings + before.units
  until(sim, () => {
    const now = census(sim, 1)
    if (now.buildings + now.units < last) hits++
    last = now.buildings + now.units
    return flying(sim) === 0
  }, 600)
  seconds(sim, 2)
  return hits
}

/** 3. Удар по базе песочницы без защиты. */
function printStrike() {
  const { sim, target, silos } = standoff(1, false)
  const before = census(sim, 1)
  const start = sim.time.elapsed
  for (const silo of silos) sim.send(2, { type: 'launch', silo, x: Math.round(target.x), y: Math.round(target.y) })
  seconds(sim, warhead.launch + 0.5)
  until(sim, () => flying(sim) === 0, 600)
  const flight = sim.time.elapsed - start
  seconds(sim, 2)
  const after = census(sim, 1)
  let hurt = 0
  for (const [, , owner, health] of sim.world.query(Building, Owner, Health)) if (owner.player === 1 && health.value < 1) hurt++
  const strike = warhead.cost + worthOf(warhead.materials)
  console.log('\nУдар по базе песочницы без защиты: настоящий пуск за 200 тайлов, в середину базы')
  console.log(`  от приказа до взрыва   ${clock(flight)}`)
  console.log(`  здания                 ${before.buildings} → ${after.buildings}: погибло ${before.buildings - after.buildings}, ещё ${hurt} повреждено`)
  console.log(`  юниты                  ${before.units} → ${after.units}`)
  console.log(`  потеряно на            ${before.cost - after.cost} кр (без материалов и запасов) — удар стоит ${round(strike, 0)}: окупился в ${round((before.cost - after.cost) / strike, 1)} раза`)
}

/** 4. Залп против одного перехватчика с полным запасом. */
function printVolleys() {
  console.log(`\nЗалп против одного перехватчика с полным запасом (${intercept.capacity} ракеты): сколько боеголовок дошло`)
  const rows: (string | number)[][] = []
  for (const count of [1, intercept.capacity, intercept.capacity + 1, intercept.capacity + 2]) {
    const { sim, target, silos } = standoff(count, true)
    const before = census(sim, 1)
    volley(sim, silos, target.x, target.y)
    const after = census(sim, 1)
    let left = 0
    for (const [, state] of sim.world.query(Interceptor)) left += state.missiles
    const lost = before.buildings - after.buildings
    rows.push([count, lost ? 'да' : 'нет', lost, before.cost - after.cost, left])
  }
  printTable(['ракет', 'пробили', 'зданий', 'потеряно кр', 'осталось у перехв.'], rows, 6, 14)
  console.log(`  новую ракету перехватчик собирает ${clock(intercept.seconds)}: залпы чаще этого его истощают`)
}

export function runNuke() {
  printPrices()
  printBlast()
  printStrike()
  printVolleys()
}
