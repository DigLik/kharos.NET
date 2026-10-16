/**
 * Раздел «economy»: рудник, производство и разгон.
 *
 *   1. Потолок по описаниям: сколько даёт шахта, во что обходится линия и когда она окупается.
 *   2. Замер живой линии в симуляции: шахта, переработка, хранилище и космопорт на одной сети труб и продажа —
 *      сколько кредитов в секунду выходит на самом деле, с доставкой по трубам и полётом корабля.
 *   3. Производство: сколько кредитов и материалов в секунду тратит здание и сколько линий его кормят.
 *   4. Разгон настоящими командами: типовой порядок нового игрока от стартового набора — разворот MCV, строители,
 *      лаборатория и первые темы, линия металла и космопорт — до второго генератора материи: когда появляются первые
 *      деньги и на что их хватает. Застрял — значит, застрянет и новичок.
 *
 * Раздел отвечает на главный вопрос стабилизации: сколько линий добычи кормит одну производственную.
 */
import type { Entity } from '@kharos/ecs'
import {
  BUILDINGS, BUILDING_TYPES, Builds, Building, DEPOSIT_KINDS, DEPOSIT_TYPES, Inventory, Owner, PRODUCTS, PRODUCT_SPECS, REFINE_RATE, RESOURCE_SPECS,
  REWARDS, Site, TRUCK_CAPACITY, TURRETS, Trade, UNITS, Unit, WEAPONS, amountOf, buildingSpec, canBuild, canPlace, canSell, creditsOf, cycleSeconds,
  depositIn, economyOf, freeCredits, hasResearch, isTrigger, topicsBefore, networkOf, pipeRoute, Position, entriesOf, isProduct, rewardsOf, stockOf, totalOf, unitSpec,
  type Topic,
  type Amounts, type BuildingType, type DepositSpot, type Resource, type Sim, type TurretSpec, type UnitType, type WeaponSpec,
} from '@kharos/sim-ts'
import { placeBuilding } from '@kharos/sim-ts/buildings'
import { connectAll } from '@kharos/sim-ts/piping'
import { spawnStartingUnits } from '@kharos/sim-ts/units'
import { TICK, clock, newSim, placeableNear, printTable, rectOf, rectsApart, rings, round, spotNear } from './lib'

/** Тиков в секунду: внутренние циклы идут ровно по секундам. */
const TICKS = Math.round(1 / TICK)

/** Цена линии добычи: шахта на месторождении и переработка, которая делает из руды товар. Трубы — отдельно, по длине. */
const LINE_COST = BUILDINGS.mine.cost + BUILDINGS.smelter.cost

/** 1. Потолок по описаниям: добыча месторождения, цена продажи, окупаемость линии и сколько шахт кормит завод. */
function printLineMath() {
  console.log('\nДобыча и продажа: потолок по описаниям, без дороги и полёта корабля')
  printTable(
    ['ресурс', 'добыча/с', 'цена', 'выручка/с', 'шахт на завод', 'окупаемость', 'запас'],
    DEPOSIT_TYPES.map((kind) => {
      const { rate, min, max } = DEPOSIT_KINDS[kind]
      const revenue = rate * RESOURCE_SPECS[kind].price
      return [
        kind, round(rate, 1), RESOURCE_SPECS[kind].price, round(revenue, 1), round(REFINE_RATE / rate, 1),
        `${Math.round(LINE_COST / revenue)} с`, `${min}–${max}`,
      ]
    }),
  )
  console.log(`  линия — шахта (${BUILDINGS.mine.cost}) и переработка (${BUILDINGS.smelter.cost}) = ${LINE_COST} кр; трубы — ${BUILDINGS.pipe.cost} кр за тайл сверх того`)
  console.log(`  переработка принимает ${REFINE_RATE} руды/с: столько шахт она держит; вторая шахта на том же заводе — ${BUILDINGS.mine.cost} кр`)
  console.log('  запас — сколько даёт одно месторождение')
}

/** Рудник в симуляции: шахта, переработка, база рядом и космопорт — всё на одной сети труб. */
interface Mine {
  sim: Sim
  port: Entity
  /** Сколько тайлов трубы ушло на то, чтобы связать рудник в одну сеть. */
  pipes: number
}

/** Ставит готовое здание у месторождения, оставляя вокруг шахты место под трубы. */
export function placeNearMine(sim: Sim, spot: DepositSpot, type: BuildingType, x: number, y: number) {
  const ok = (tx: number, ty: number) =>
    placeableNear(sim, type, tx, ty, 1, 0) !== undefined && rectsApart(rectOf(type, tx, ty), rectOf('mine', spot.x, spot.y), 2)
  const tile = spotNear(x, y, ok, 14)
  return tile ? placeBuilding(sim.world, type, tile.x, tile.y, 1) : undefined
}

/**
 * База у ближайшего к началу мира месторождения металла: всё готовое — меряется поток, а не стройка.
 * Энергии две электростанции — на переработку, космопорт и генератор материи. Грузовиков нет: внутри сети
 * руду и металл носят трубы.
 */
function mineScene(): Mine {
  const sim = newSim()
  for (const cell of rings(0, 0, 6)) {
    const spot = depositIn(sim, cell.x, cell.y)
    if (!spot || spot.kind !== 'metal') continue
    placeBuilding(sim.world, 'mine', spot.x, spot.y, 1)
    const cx = spot.x + 1
    const cy = spot.y + 1
    const core = placeNearMine(sim, spot, 'command', cx + 7, cy)
    const silo = placeNearMine(sim, spot, 'metalYard', cx + 4, cy + 6)
    const refinery = placeNearMine(sim, spot, 'smelter', cx + 4, cy - 4)
    const port = placeNearMine(sim, spot, 'spaceport', cx + 8, cy + 5)
    const generator = placeNearMine(sim, spot, 'generator', cx + 6, cy - 5)
    const generator2 = placeNearMine(sim, spot, 'generator', cx + 8, cy - 5)
    const matter = placeNearMine(sim, spot, 'matter', cx + 3, cy - 5)
    if (!core || !silo || !refinery || !port || !generator || !generator2 || !matter) {
      sim.destroy()
      continue
    }
    connectAll(sim, 1)
    return { sim, port, pipes: countBuildings(sim, 'pipe') }
  }
  throw new Error('Не нашлось места под рудник у месторождения металла')
}

/**
 * Гоняет рудник time секунд, держа заявку на продажу металла открытой, как это делает игрок.
 * Возвращает, на какой секунде пришла первая продажа: 0 — не дождались. Продажа — это когда у космопорта
 * пропадает заявка: корабль улетел и кредиты начислены.
 */
function runMine(scene: Mine, time: number, firstSale = 0) {
  const { sim, port } = scene
  let sale = firstSale
  for (let second = 0; second < time; second++) {
    for (let tick = 0; tick < TICKS; tick++) {
      if (canSell(sim, 1, port)) {
        const stock = Math.floor(stockOf(sim, 1).items.metal ?? 0)
        if (stock >= TRUCK_CAPACITY) sim.send(1, { type: 'sell', port, resource: 'metal', amount: stock })
      }
      const ordered = sim.world.has(port, Trade)
      sim.advance(TICK)
      if (sale === 0 && ordered && !sim.world.has(port, Trade)) sale = second
    }
  }
  return sale
}

/** Сколько металла в мире: в хранилищах, на заводе и в трюме космопорта. */
function metalInWorld(sim: Sim) {
  let total = 0
  for (const [, inventory] of sim.world.query(Inventory)) total += amountOf(inventory, 'metal')
  return total
}

/** 2. Замер живой линии: пять минут рудника с продажей. Возвращает выручку линии в кредитах в секунду. */
function measureMine(): number {
  const scene = mineScene()
  const { sim, pipes } = scene
  const warmup = 120
  const firstSale = runMine(scene, warmup)
  const before = creditsOf(sim, 1)
  const held = metalInWorld(sim)
  const passive = economyOf(sim, 1).income
  runMine(scene, 180, firstSale)
  const measured = (creditsOf(sim, 1) - before) / 180
  // Продажа идёт партиями: что из окна замера осталось на складах или пришло в него с прогрева — тоже выручка линии.
  const line = Math.max(0, measured - passive + ((metalInWorld(sim) - held) * RESOURCE_SPECS.metal.price) / 180)
  const piping = pipes * BUILDINGS.pipe.cost
  const invested = BUILDINGS.mine.cost + piping + BUILDINGS.metalYard.cost + BUILDINGS.smelter.cost + BUILDINGS.spaceport.cost + 2 * BUILDINGS.generator.cost

  console.log(`\nРудник в симуляции: шахта, переработка, хранилище и космопорт на одной сети труб; продажа металла, 5 минут`)
  console.log(`  труб                   ${pipes} тайлов, ${piping} кр`)
  console.log(`  первая продажа         ${firstSale ? clock(firstSale) : 'не дождались'}`)
  console.log(`  доход всего            ${round(measured, 1)} кр/с`)
  console.log(`  пассивка               ${round(passive, 1)} кр/с (главное здание и генератор материи)`)
  console.log(`  выручка линии          ${round(line, 1)} кр/с (с металлом, который за замер лёг на склады)`)
  console.log(`  через переработку      ${round(line / RESOURCE_SPECS.metal.price, 1)} металла/с (шахта даёт 1 руды/с, завод принимает ${REFINE_RATE}/с)`)
  if (line > 0) {
    console.log(`  окупаемость линии      ${Math.round(LINE_COST / line)} с (шахта и переработка, ${LINE_COST} кр)`)
    console.log(`  вложено в рудник       ${invested} кр, окупается за ${Math.round(invested / line)} с`)
  }
  sim.destroy()
  return line
}

/** Материалы в сырых ресурсах: изделия раскладываются по рецепту цеха на то, из чего их собирают. */
function rawOf(materials: Amounts): Partial<Record<Resource, number>> {
  const raw: Partial<Record<Resource, number>> = {}
  for (const [good, amount] of entriesOf(materials)) {
    if (isProduct(good)) {
      const { recipe, yield: made } = PRODUCT_SPECS[good]
      for (const [resource, need] of entriesOf(recipe)) raw[resource as Resource] = (raw[resource as Resource] ?? 0) + (need * amount) / made
    } else raw[good as Resource] = (raw[good as Resource] ?? 0) + amount
  }
  return raw
}

/** 3. Что тратит производство и сколько линий его кормят. */
function printProduction(lineRate: number) {
  const feed = Math.max(lineRate, 0.1)
  console.log('\nПроизводство: что здание тратит в секунду, если работает без остановки')
  const rows: (string | number)[][] = []
  for (const type of BUILDING_TYPES) {
    for (const unit of buildingSpec(type).produces ?? []) {
      const info = unitSpec(unit)
      const burn = info.cost / info.buildTime
      const raw = rawOf(info.materials ?? {})
      const share = (resource: Resource) => (raw[resource] ?? 0) / info.buildTime
      const lines = (['metal', 'silicon', 'fuel', 'kharite'] as Resource[]).reduce(
        (sum, resource) => sum + share(resource) / DEPOSIT_KINDS[resource].rate,
        0,
      )
      const show = (value: number) => (value > 0 ? round(value, 2) : '—')
      // Материалы по цене продажи: столько кредитов игрок не выручил, пустив сырьё на юнит.
      const value = entriesOf(raw).reduce((sum, [resource, amount]) => sum + amount * RESOURCE_SPECS[resource as Resource].price, 0)
      rows.push([
        type, unit, info.cost, round(info.buildTime, 0), round(burn, 1),
        show(share('metal')), show(share('silicon')), show(share('fuel')), show(share('kharite')),
        round(lines, 1), round(burn / feed, 1), value ? `${Math.round((value / (value + info.cost)) * 100)}%` : '—',
      ])
    }
  }
  printTable(['здание', 'юнит', 'цена', 'время', 'кр/с', 'м/с', 'к/с', 'т/с', 'х/с', 'линий', 'по деньгам', 'материалы'], rows, 12, 8)
  console.log('  кр/с — кредитов в секунду; м/к/т/х — материалов в секунду, изделия разложены по рецепту цеха;')
  console.log('  материалы — доля сырья по цене продажи в полной цене юнита (кредиты плюс сырьё), цель 25–40%;')
  console.log(`  линий — столько шахт нужно на материалы, и каждая кормится переработкой (завод держит ${REFINE_RATE} руды/с);`)
  console.log(`  по деньгам — на кредиты при выручке ${round(feed, 1)} кр/с`)
}

/** Сколько боеприпасов в секунду тратит непрерывно стреляющая турель каждого вида. */
function ammoBurn() {
  return BUILDING_TYPES.filter((type) => buildingSpec(type).ammo).map((type) => {
    const turret: TurretSpec = TURRETS[buildingSpec(type).mounts![0].turret]
    const weapon: WeaponSpec = WEAPONS[turret.weapon!]
    return { type, burn: (weapon.ammo ?? 0) / weapon.reload, capacity: buildingSpec(type).inventory ?? 0 }
  })
}

/**
 * 4. Производные: что ест цех на каждом рецепте, сколько линий его кормят и сколько он даёт; сколько турелей
 * держит одна линия металла через цех на патронах (§3.7 design.md: цель — 2–4 турели под огнём).
 */
function printProducts() {
  console.log('\nЗаводы изделий: рецепт, что завод ест и что даёт, работая без остановки')
  const rows: (string | number)[][] = []
  for (const product of PRODUCTS) {
    const { recipe, yield: made } = PRODUCT_SPECS[product]
    const plant = BUILDING_TYPES.find((type) => buildingSpec(type).assembles === product)
    const seconds = cycleSeconds(product)
    const show = (resource: Resource) => ((recipe as Partial<Record<Resource, number>>)[resource] ? round((recipe as Record<Resource, number>)[resource] / seconds, 2) : '—')
    const lines = entriesOf(recipe).reduce((sum, [resource, need]) => sum + need / seconds / DEPOSIT_KINDS[resource as Resource].rate, 0)
    rows.push([product, round(made / seconds, 2), show('metal'), show('silicon'), show('kharite'), round(lines, 2), (plant && buildingSpec(plant).limits?.[product]) ?? '—'])
  }
  printTable(['изделие', 'штук/с', 'м/с', 'к/с', 'х/с', 'линий', 'склад'], rows)
  console.log('  линий — сколько линий добычи завод съедает, работая без остановки; склад — сколько готового он держит у себя:')
  console.log('  набрал и сеть не забирает — встаёт')

  const ammoPerLine = (PRODUCT_SPECS.ammo.yield / totalOf(PRODUCT_SPECS.ammo.recipe)) * DEPOSIT_KINDS.metal.rate
  console.log('\nБоеприпасы: что тратит турель под непрерывным огнём и сколько их держит одна линия металла')
  printTable(
    ['турель', 'патр/с', 'запас', 'хватает', 'на линию'],
    ammoBurn().map(({ type, burn, capacity }) => [type, round(burn, 2), capacity, `${Math.round(capacity / burn)} с`, round(ammoPerLine / burn, 1)]),
    14,
    10,
  )
  console.log(`  линия металла через цех даёт ${round(ammoPerLine, 1)} патронов/с; хватает — сколько турель стреляет на своём запасе`)
}

/** Состояние разгона: что уже построено и кто свободен. */
interface Opening {
  mcv: Entity
  core?: Entity
  mine?: Entity
  port?: Entity
  builders: Entity[]
}

/** Сколько готовых зданий этого вида у игрока; sites — считать и заложенные. */
function countBuildings(sim: Sim, type: BuildingType, sites = false) {
  let count = 0
  for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
    if (owner.player === 1 && building.type === type && (sites || !sim.world.has(entity, Site))) count++
  }
  return count
}

/** Сколько юнитов этого вида у игрока. */
function countUnits(sim: Sim, type: UnitType) {
  let count = 0
  for (const [, unit, owner] of sim.world.query(Unit, Owner)) if (owner.player === 1 && unit.type === type) count++
  return count
}

/** Шаг разгона: что игрок делает, когда на это хватает денег. */
interface Step {
  title: string
  /** Сколько кредитов нужно, чтобы шаг начался. */
  cost: number
  started: boolean
  run: (sim: Sim, opening: Opening) => boolean
  done: (sim: Sim, opening: Opening) => boolean
}

/** Свободный строитель: не занят стройкой. */
const freeBuilder = (sim: Sim, opening: Opening) => opening.builders.find((builder) => !sim.world.has(builder, Builds))

/** Порядок разгона: типовой старт игрока — от разворота MCV до второго генератора материи. */
function makeSteps(opening: Opening, core: { x: number; y: number }, deposit: DepositSpot): Step[] {
  const mine = rectOf('mine', deposit.x, deposit.y)
  /**
   * Шаг стройки: место ищется в момент, когда до шага дошли деньги, — соседние здания уже стоят.
   * Кроме шахты, все здания ставятся в стороне от месторождения: займи они его, шахту будет не поставить.
   */
  const build = (title: string, type: BuildingType, count: number, x: number, y: number): Step => ({
    title,
    cost: BUILDINGS[type].cost,
    started: false,
    run(sim, opening) {
      const builder = freeBuilder(sim, opening)
      if (builder === undefined) return false
      const suit = (tx: number, ty: number) =>
        canBuild(sim, 1, type, tx, ty) && (type === 'mine' || rectsApart(rectOf(type, tx, ty), mine, 1))
      const tile = spotNear(x, y, suit, 20)
      if (!tile) return false
      sim.send(1, { type: 'build', building: type, x: tile.x, y: tile.y, builders: [builder] })
      return true
    },
    // Заложено: дальше к площадке тянут трубу, по ней придут материалы стройки.
    done: (sim) => countBuildings(sim, type, true) >= count,
  })
  /** Готово: здание достроено. */
  const ready = (title: string, type: BuildingType, count: number): Step => ({
    title,
    cost: 0,
    started: false,
    run: () => true,
    done: (sim) => countBuildings(sim, type) >= count,
  })
  /**
   * Подключение трубой: каждое здание этого вида, готовое или заложенное, — к сети главного здания. Труба — протяжкой, как игрок:
   * трасса от основания до ближайшей своей сети (pipeRoute), заказ — командой «pipes».
   */
  const connect = (type: BuildingType): Step => {
    const loose = (sim: Sim, opening: Opening) => {
      const home = opening.core === undefined ? undefined : networkOf(sim, opening.core)
      for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
        if (owner.player === 1 && building.type === type && networkOf(sim, entity) !== home) return entity
      }
      return undefined
    }
    return {
      title: `труба к зданию «${type}»`,
      cost: 0,
      started: false,
      run(sim, opening) {
        const entity = loose(sim, opening)
        const builder = freeBuilder(sim, opening)
        if (entity === undefined || builder === undefined || opening.core === undefined) return entity === undefined
        const home = networkOf(sim, opening.core)
        const position = sim.world.get(entity, Position)!
        const tiles = pipeRoute(sim, 1, position.x, position.y, BUILDINGS[type].width, BUILDINGS[type].height, (zone) => zone === home)
        if (!tiles?.length) return false
        sim.send(1, { type: 'pipes', tiles, builders: [builder] })
        return true
      },
      done: (sim, opening) => loose(sim, opening) === undefined,
    }
  }
  const produce = (title: string, unit: UnitType, count: number): Step => ({
    title,
    cost: UNITS[unit].cost * count,
    started: false,
    run(sim, opening) {
      if (opening.core === undefined) return false
      for (let i = countUnits(sim, unit); i < count; i++) sim.send(1, { type: 'produce', producer: opening.core, unit })
      return true
    },
    done: (sim) => countUnits(sim, unit) >= count,
  })
  /** Тема исследования: игрок ставит её в очередь, лаборатории ведут её сами; готово — когда тема изучена. */
  const research = (topic: Topic): Step => ({
    title: `исследовано: ${topic}`,
    cost: 0,
    started: false,
    run(sim) {
      // Открытия (наука, месторождение в обзоре) засчитываются сами и на тик позже события: до них тему не поставить,
      // как не поставит её и игрок — в окне исследований она ещё красная.
      if (!topicsBefore(topic).filter(isTrigger).every((before) => hasResearch(sim, 1, before))) return false
      sim.send(1, { type: 'research', topic })
      return true
    },
    done: (sim) => hasResearch(sim, 1, topic),
  })
  return [
    {
      title: 'разворот MCV',
      cost: 0,
      started: false,
      run(sim) {
        sim.send(1, { type: 'deploy', unit: opening.mcv })
        return true
      },
      done: (_sim, opening) => opening.core !== undefined,
    },
    // Строителей в стартовом наборе нет: их заказывают в развёрнутом главном здании.
    produce('строители', 'builder', 2),
    // Электростанция у главного здания: энергия общая на зону, трубы ей не нужны — да их ещё и не исследовали.
    build('заложено: электростанция', 'generator', 1, core.x + 5, core.y - 4),
    ready('электростанция', 'generator', 1),
    // Лаборатория открывает «Научный метод», с ним — трубы; руду металла она изучает из своей шахты по трубам.
    build('заложено: лаборатория', 'lab', 1, core.x - 4, core.y - 4),
    ready('лаборатория', 'lab', 1),
    research('pipes'),
    connect('lab'),
    // Генератор материи — первый доход, пока нет продажи: ему нужна сеть главного здания, то есть трубы.
    build('заложено: генератор материи', 'matter', 1, core.x + 2, core.y - 4),
    connect('matter'),
    ready('генератор материи', 'matter', 1),
    build('заложено: шахта', 'mine', 1, deposit.x, deposit.y),
    connect('mine'),
    ready('шахта', 'mine', 1),
    research('metalOre'),
    // Переработка до хранилища: хранилище металла открывается ею, и без неё продавать нечего.
    build('заложено: переработка', 'smelter', 1, core.x + 5, core.y + 4),
    connect('smelter'),
    ready('переработка', 'smelter', 1),
    build('заложено: хранилище', 'metalYard', 1, core.x + 4, core.y + 7),
    connect('metalYard'),
    ready('хранилище', 'metalYard', 1),
    build('заложено: космопорт', 'spaceport', 1, core.x + 8, core.y + 4),
    connect('spaceport'),
    ready('космопорт', 'spaceport', 1),
    build('заложено: вторая электростанция', 'generator', 2, core.x - 5, core.y - 4),
    ready('вторая электростанция', 'generator', 2),
    build('заложено: второй генератор материи', 'matter', 2, core.x - 2, core.y - 4),
    connect('matter'),
    ready('второй генератор материи', 'matter', 2),
  ]
}

/** Обновляет, что уже построено и кто свободен. */
function refresh(sim: Sim, opening: Opening) {
  for (const [entity, building, owner] of sim.world.query(Building, Owner)) {
    if (owner.player !== 1 || sim.world.has(entity, Site)) continue
    if (building.type === 'command') opening.core = entity
    if (building.type === 'mine') opening.mine = entity
    if (building.type === 'spaceport') opening.port = entity
  }
  opening.builders = []
  for (const [entity, unit, owner] of sim.world.query(Unit, Owner)) {
    if (owner.player !== 1) continue
    if (unit.type === 'builder') opening.builders.push(entity)
  }
}

/** Стартовая точка разгона: месторождение металла и место под главное здание рядом. */
function startSpot(sim: Sim) {
  for (const cell of rings(0, 0, 6)) {
    const spot = depositIn(sim, cell.x, cell.y)
    if (!spot || spot.kind !== 'metal') continue
    // Главное здание не строят: его разворачивает MCV на своё место, поэтому проверяется только место.
    // От месторождения оно стоит в стороне: займи оно его, шахту будет не поставить.
    const suit = (x: number, y: number) => canPlace(sim, 'command', x, y, 2) && rectsApart(rectOf('command', x, y), rectOf('mine', spot.x, spot.y), 1)
    const core = spotNear(spot.x + 1, spot.y + 1, suit, 12)
    if (core) return { spot, core }
  }
  throw new Error('Не нашлось места под старт у месторождения металла')
}

/** Русские имена наград: в таблице наград они названы по-английски, игроку они знакомы по-русски. */
const REWARD_NAMES: Record<string, string> = {
  deploy: 'разворот MCV', unit: 'первый юнит', generator: 'электростанция',
  matter: 'генератор материи', mine: 'шахта', metalYard: 'хранилище',
}

/** 4. Разгон настоящими командами: от разворота MCV до второго генератора материи. */
function printOpening() {
  const sim = newSim()
  const start = startSpot(sim)
  const center = { x: start.core.x + 1, y: start.core.y + 1 }
  // Стартовый набор настоящего игрока: MCV, бойцы и кредиты — MCV встаёт на место главного здания.
  const units = spawnStartingUnits(sim, 1, center.x, center.y)
  const mcv = units.find((unit) => sim.world.get(unit, Unit)?.type === 'mcv')!
  const position = sim.world.get(mcv, Position)!
  Object.assign(position, { x: center.x, y: center.y })
  const opening: Opening = { mcv, builders: [] }
  const steps = makeSteps(opening, center, start.spot)

  const limit = 600
  const events: { time: number; text: string }[] = []
  const curve: (string | number)[][] = []
  const known = new Set(rewardsOf(sim, 1))
  let index = 0
  let sales = 0
  let minute = creditsOf(sim, 1)
  let minuteRewards = 0
  let minuteSpend = 0

  for (let tick = 0; tick < Math.round(limit / TICK); tick++) {
    const time = tick * TICK
    // Продажа: игрок держит заявку открытой, как только есть что везти.
    const port = opening.port
    if (port !== undefined && canSell(sim, 1, port)) {
      const stock = Math.floor(stockOf(sim, 1).items.metal ?? 0)
      if (stock >= TRUCK_CAPACITY) sim.send(1, { type: 'sell', port, resource: 'metal', amount: stock })
    }
    const step = index < steps.length ? steps[index] : undefined
    // Стройку закладывают на свободные кредиты: за вычетом обещанного начатым, см. freeCredits.
    if (step && !step.started && freeCredits(sim, 1) >= step.cost && step.run(sim, opening)) {
      step.started = true
      minuteSpend += step.cost
    }
    const ordered = port !== undefined && sim.world.has(port, Trade)
    sim.advance(TICK)
    refresh(sim, opening)
    // Корабль улетел: заявка пропала, кредиты начислены.
    if (ordered && !sim.world.has(port!, Trade)) {
      sales++
      if (sales === 1) events.push({ time, text: 'первая продажа металла' })
    }
    // Награды за вехи — это тоже доход, но разовый: в доходе на графике их не считаем.
    for (const key of rewardsOf(sim, 1)) {
      if (known.has(key)) continue
      known.add(key)
      const amount = REWARDS[key as keyof typeof REWARDS] ?? 0
      minuteRewards += amount
      events.push({ time, text: `награда: ${REWARD_NAMES[key] ?? key} +${amount}` })
    }
    if (step?.started && step.done(sim, opening)) {
      events.push({ time, text: step.title })
      index++
    }
    if ((tick + 1) % TICKS === 0) {
      const second = (tick + 1) / TICKS
      const credits = creditsOf(sim, 1)
      if (second % 60 === 0) {
        // Доход — это всё, что пришло не от разовых наград: продажи и пассивка, за вычетом потраченного.
        const income = (credits - minute - minuteRewards + minuteSpend) / 60
        curve.push([clock(second), credits, round(income, 1), Math.floor(stockOf(sim, 1).items.metal ?? 0)])
        minute = credits
        minuteRewards = 0
        minuteSpend = 0
      }
    }
  }

  events.sort((a, b) => a.time - b.time)
  console.log('\nРазгон настоящими командами: от стартового набора до второго генератора материи')
  for (const event of events) console.log(`  ${clock(event.time).padStart(5)}  ${event.text}`)
  if (index < steps.length) console.log(`  застряли на шаге «${steps[index].title}»: не хватило места, денег или строителя`)
  printTable(['минута', 'кредиты', 'доход/с', 'металл'], curve)
  console.log(`  продаж за прогон: ${sales}; доход/с — продажи и пассивка, без разовых наград за вехи`)
  sim.destroy()
}

/** Раздел целиком: сначала рудник, потом производство по его выручке, потом разгон. */
export function runEconomy() {
  printLineMath()
  const lineRate = measureMine()
  printProduction(lineRate)
  printProducts()
  printOpening()
}
