/**
 * Раздел «движение»: как юниты ездят строем, расходятся, окружают цель и подъезжают к стройке — и во что это
 * обходится. Нужен, чтобы правку движения мерить до и после, а не на глаз.
 *
 *   formation — марш строем, встречный обмен двух групп и атака толпой на ровном сухом поле. Меряется, сколько юнит
 *               стоит с приказом, крутится на месте и дёргается из стороны в сторону: по прямой в чистом поле
 *               поворачиваться почти незачем, и всё сверх нуля — вихляние;
 *   surround  — отряд атакует одну цель: сколько нападающих стреляют, как скоро стреляет весь отряд и какой дугой
 *               он обступает цель;
 *   builders  — строители едут на стройку: когда начинают работать, сколько проехали сверх нужного и где встали;
 *   load      — время тика: марш больших армий, толпа на одну цель, бой армий;
 *   crossing  — разношёрстная армия идёт через карту 512×512 по диагонали на нескольких картах: доезжают ли все
 *               и не стоит ли кто-то с приказом вечно. Долгий: около минуты.
 */
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Terrain, isCliffFoot, terrainAt } from '@kharos/shared/terrain'
import {
  BUILDINGS, Health, Path, Position, Repair, Shot, TRAINING_PLAYER, Unit, createSim, driveBattle, spawnBattle,
  type BuildingType, type Sim, type UnitType,
} from '@kharos/sim-ts'
import { orderAttack } from '@kharos/sim-ts/combat'
import { orderBuild } from '@kharos/sim-ts/construction'
import { searchedTiles } from '@kharos/sim-ts/path'
import { setStance } from '@kharos/sim-ts/tactics'
import { freeTilesNear, isWalkable, openSpawn, orderGroupMove, spawnStartingUnits, spawnUnit, terrainSpeed } from '@kharos/sim-ts/units'
import { TICK, newSim, options, printTable, round, seconds } from './lib'

/**
 * Левый верхний тайл площадки width×height, где наземная техника едет хотя бы на 85% скорости: без болота.
 * На болоте техника вязнет впятеро, и замер мерил бы болото, а не движение.
 */
function dryArena(sim: Sim, width: number, height: number) {
  for (let y = -300; y < 300; y += 3) {
    search: for (let x = -300; x < 300; x += 3) {
      for (let dy = 0; dy < height; dy++) {
        for (let dx = 0; dx < width; dx++) if (!isWalkable(sim, x + dx, y + dy) || terrainSpeed(sim, 'lancer', x + dx, y + dy) < 0.85) continue search
      }
      return { x, y }
    }
  }
  throw new Error(`Не нашлось сухой площадки ${width}×${height}`)
}

/** Ставит юнитов прямоугольником cols в ряд через gap тайлов от (x, y). */
function block(sim: Sim, type: UnitType, player: number, count: number, x: number, y: number, cols: number, gap = 1.5) {
  const units: Entity[] = []
  for (let i = 0; i < count; i++) units.push(spawnUnit(sim, type, player, Math.round(x + (i % cols) * gap), Math.round(y + Math.floor(i / cols) * gap)))
  return units
}

/** Кто выпустил выстрел в этот тик: у нового выстрела возраст ноль. Источник — носитель, а не турель. */
function shootersNow(sim: Sim) {
  const sources = new Set<Entity>()
  for (const [, shot] of sim.world.query(Shot)) if (shot.age === 0 && shot.source >= 0) sources.add(shot.source as Entity)
  return sources
}

/** Как юнит ехал: сколько тиков стоял с приказом, сколько крутился на месте, сколько раз сменил сторону поворота. */
interface Track {
  stood: number
  spun: number
  reversals: number
  turned: number
  lastTurn: number
  /** Когда приказ кончился, в секундах; −1 — ещё едет. */
  arrived: number
}

/** Крутит симуляцию до limit секунд или пока done, следя за юнитами units. */
function follow(sim: Sim, units: Entity[], limit: number, done = () => units.every((entity) => !sim.world.has(entity, Path)), each?: () => void) {
  const tracks = new Map<Entity, Track>(units.map((entity) => [entity, { stood: 0, spun: 0, reversals: 0, turned: 0, lastTurn: 0, arrived: -1 }]))
  let tick = 0
  for (; tick < limit / TICK && !done(); tick++) {
    const before = units.map((entity) => {
      const position = sim.world.get(entity, Position)
      return position && { entity, x: position.x, y: position.y, facing: sim.world.get(entity, Unit)!.facing, moving: sim.world.has(entity, Path) }
    })
    sim.advance(TICK)
    each?.()
    for (const was of before) {
      const position = was && sim.world.get(was.entity, Position)
      if (!was || !position) continue
      const track = tracks.get(was.entity)!
      const moved = Math.hypot(position.x - was.x, position.y - was.y)
      const turn = Math.atan2(Math.sin(sim.world.get(was.entity, Unit)!.facing - was.facing), Math.cos(sim.world.get(was.entity, Unit)!.facing - was.facing))
      track.turned += Math.abs(turn)
      if (was.moving && moved < 1e-4) {
        if (Math.abs(turn) < 1e-4) track.stood++
        else track.spun++
      }
      const side = Math.abs(turn) > 1e-3 ? Math.sign(turn) : 0
      if (side && track.lastTurn && side !== track.lastTurn) track.reversals++
      if (side) track.lastTurn = side
      if (was.moving && !sim.world.has(was.entity, Path) && track.arrived < 0) track.arrived = (tick + 1) * TICK
    }
  }
  return { tracks: [...tracks.values()], time: tick * TICK }
}

/** Строка таблицы строя: средние по юнитам. */
function formationRow(scene: string, type: UnitType, { tracks, time }: ReturnType<typeof follow>) {
  const mean = (value: (track: Track) => number) => round(tracks.reduce((sum, track) => sum + value(track), 0) / tracks.length, 1)
  return [scene, type, round(time, 1), mean((track) => track.stood * TICK), mean((track) => track.spun * TICK), mean((track) => track.reversals), mean((track) => track.turned)]
}

/** Строй: марш, встречный обмен и атака толпой — для каждого из типичных видов. */
export function printFormation() {
  console.log('\nСтрой на сухом ровном поле: средние на юнит. Поворот по прямой в чистом поле — почти ноль; всё сверх — вихляние')
  const rows: (string | number)[][] = []
  for (const type of ['infantry', 'lancer', 'tank'] as UnitType[]) {
    {
      // Марш строем 4×4 на ~38 тайлов.
      const sim = newSim()
      const at = dryArena(sim, 60, 30)
      const units = block(sim, type, 1, 16, at.x + 2, at.y + 10, 4)
      orderGroupMove(sim, units, at.x + 40, at.y + 12)
      rows.push(formationRow('марш 16', type, follow(sim, units, 60)))
    }
    {
      // Две группы по 12 меняются местами лоб в лоб.
      const sim = newSim()
      const at = dryArena(sim, 60, 30)
      const left = block(sim, type, 1, 12, at.x + 2, at.y + 8, 3)
      const right = block(sim, type, 1, 12, at.x + 50, at.y + 8, 3)
      orderGroupMove(sim, left, at.x + 52, at.y + 10)
      orderGroupMove(sim, right, at.x + 4, at.y + 10)
      rows.push(formationRow('встречные 12+12', type, follow(sim, [...left, ...right], 90)))
    }
    {
      // 12 атакуют одну цель среди десятка смирных пехотинцев.
      const sim = newSim()
      const at = dryArena(sim, 60, 30)
      const own = block(sim, type, 1, 12, at.x + 2, at.y + 8, 4)
      const foes = block(sim, 'infantry', TRAINING_PLAYER, 10, at.x + 40, at.y + 9, 5, 1)
      setStance(sim, TRAINING_PLAYER, foes, 'passive')
      orderAttack(sim, 1, own, foes[2])
      rows.push(formationRow('атака 12', type, follow(sim, own, 30, () => false)))
    }
  }
  printTable(['сцена', 'вид', 'время, с', 'стоял, с', 'крутился', 'смен пов.', 'поворот'], rows, 16, 10)
}

/** Один отряд атакует одну цель, которая не умирает и не отвечает. shift сдвигает цель: другая расстановка. */
function besiege(type: UnitType, count: number, cols: number, shift: number) {
  const sim = newSim()
  const at = dryArena(sim, 50, 34)
  const own = block(sim, type, 1, count, at.x + 2, at.y + 12, cols)
  const target = spawnUnit(sim, 'tank', TRAINING_PLAYER, at.x + 32 - Math.abs(shift), at.y + 17 + shift)
  setStance(sim, TRAINING_PLAYER, [target], 'passive')
  orderAttack(sim, 1, own, target)
  const LIMIT = 25
  /** Стреляли ли за последние 10 секунд: бой уже устоялся. */
  const lately = new Set<Entity>()
  const ever = new Set<Entity>()
  let all = -1
  for (let tick = 0; tick < LIMIT / TICK; tick++) {
    sim.advance(TICK)
    sim.world.get(target, Health)!.value = 1
    for (const source of shootersNow(sim)) {
      ever.add(source)
      if (tick * TICK > LIMIT - 10) lately.add(source)
    }
    if (all < 0 && own.every((entity) => ever.has(entity))) all = tick * TICK
  }
  // Дуга — сколько градусов круга вокруг цели занимают нападающие: круг минус самый большой пустой промежуток.
  const center = sim.world.get(target, Position)!
  const angles = own.map((entity) => {
    const position = sim.world.get(entity, Position)!
    return Math.atan2(position.y - center.y, position.x - center.x)
  }).sort((a, b) => a - b)
  let widest = 0
  for (let i = 0; i < angles.length; i++) widest = Math.max(widest, (angles[(i + 1) % angles.length] - angles[i] + 2 * Math.PI) % (2 * Math.PI) || 2 * Math.PI)
  return { lately: own.filter((entity) => lately.has(entity)).length, all, arc: ((2 * Math.PI - widest) * 180) / Math.PI }
}

/** Окружение: каждый отряд — в трёх расстановках. */
export function printSurround() {
  console.log('\nОкружение: отряд атакует одну цель — неубиваемый смирный танк; по три расстановки, через «/»')
  const rows: (string | number)[][] = []
  let fired = 0
  let total = 0
  for (const [type, count, cols] of [['lancer', 12, 4], ['tank', 10, 4], ['infantry', 20, 5], ['flamer', 16, 4], ['tesla', 8, 4], ['artillery', 8, 4], ['buggy', 12, 4]] as [UnitType, number, number][]) {
    const results = [-8, 0, 9].map((shift) => besiege(type, count, cols, shift))
    const lately = results.reduce((sum, result) => sum + result.lately, 0)
    fired += lately
    total += count * results.length
    rows.push([
      `${count} ${type}`,
      `${lately}/${count * results.length}`,
      results.map((result) => (result.all < 0 ? '—' : Math.round(result.all))).join('/'),
      results.map((result) => Math.round(result.arc)).join('/'),
    ])
  }
  printTable(['отряд', 'стреляли', 'все, с', 'дуга, °'], rows, 14, 16)
  console.log(`  «стреляли» — за последние 10 с из 25; «все» — когда выстрелил весь отряд, «—» — так и не выстрелил. Итого ${fired}/${total}`)
}

/** База игрока 1 на скале: развёрнутое главное здание; стартовые строители убраны. И место под стройку. */
function base() {
  const sim = createSim(options)
  const rock = (x: number, y: number) => {
    for (let tileY = y; tileY < y + 12; tileY++) {
      for (let tileX = x; tileX < x + 12; tileX++) if (terrainAt(sim.land, tileX, tileY) !== Terrain.Rock || isCliffFoot(sim.land, tileX, tileY)) return false
    }
    return true
  }
  for (let y = 0; y < 400; y++) {
    for (let x = 0; x < 400; x++) {
      if (!rock(x, y)) continue
      spawnStartingUnits(sim, 1, x + 2, y + 2)
      for (const [entity, unit] of sim.world.query(Unit)) if (unit.type === 'mcv') sim.send(1, { type: 'deploy', unit: entity })
      seconds(sim, 6)
      for (const [entity, unit] of [...sim.world.query(Unit)]) if (unit.type === 'builder') sim.world.destroy(entity)
      return { sim, siteX: x + 7, siteY: y + 4 }
    }
  }
  throw new Error('Не нашлось места под базу')
}

/** Строители: откуда едут — смещения от площадки — и что из этого вышло. */
export function printBuilders() {
  console.log('\nСтроители едут на стройку генератора (2×2); луч строителя — 5 тайлов. «минимум» — сколько ехать до края луча')
  const type: BuildingType = 'generator'
  const { width, height } = BUILDINGS[type]
  const rows: (string | number)[][] = []
  for (const [scene, offsets] of [
    ['один издалека', [[-12, 0]]],
    ['четверо кучкой', [[-12, 0], [-12, 2], [-14, 0], [-14, 2]]],
    ['уже в радиусе', [[-4, 0], [6, 1]]],
    ['на площадке', [[0, 0], [1, 1]]],
    ['с четырёх сторон', [[-12, 0], [13, 1], [1, -12], [1, 13]]],
  ] as [string, [number, number][]][]) {
    const { sim, siteX, siteY } = base()
    const edge = (x: number, y: number) => Math.hypot(Math.max(siteX - x, x - siteX - width, 0), Math.max(siteY - y, y - siteY - height, 0))
    const builders = offsets.map(([dx, dy]) => {
      const spot = openSpawn(sim, siteX + dx, siteY + dy, 4)
      return spawnUnit(sim, 'builder', 1, spot.x, spot.y)
    })
    const from = builders.map((entity) => ({ ...sim.world.get(entity, Position)! }))
    const site = orderBuild(sim, 1, type, siteX, siteY, builders)
    if (site === undefined) throw new Error(`Стройка не заложилась: ${scene}`)
    const started = new Map<Entity, number>()
    const driven = new Map<Entity, number>(builders.map((entity) => [entity, 0]))
    const last = builders.map((entity) => ({ ...sim.world.get(entity, Position)! }))
    for (let tick = 0; tick < 60 / TICK && started.size < builders.length; tick++) {
      sim.advance(TICK)
      builders.forEach((entity, i) => {
        const position = sim.world.get(entity, Position)!
        driven.set(entity, driven.get(entity)! + Math.hypot(position.x - last[i].x, position.y - last[i].y))
        last[i] = { ...position }
        if (!started.has(entity) && sim.world.get(entity, Repair)!.target === site) started.set(entity, (tick + 1) * TICK)
      })
    }
    builders.forEach((entity, i) => {
      const position = sim.world.get(entity, Position)!
      rows.push([
        i ? '' : scene,
        round(edge(from[i].x, from[i].y), 1),
        started.has(entity) ? round(started.get(entity)!, 1) : '—',
        round(driven.get(entity)!, 1),
        round(Math.max(0, edge(from[i].x, from[i].y) - 5), 1),
        round(edge(position.x, position.y), 1),
      ])
    })
  }
  printTable(['сцена', 'до края', 'работает', 'проехал', 'минимум', 'встал от'], rows, 18, 10)
}

/** Гоняет ticks тиков: среднее и худшее время тика в мс и сколько тайлов за тик осмотрел поиск пути. */
function measure(sim: Sim, ticks: number, each?: (tick: number) => boolean) {
  const searched = searchedTiles()
  let total = 0
  let worst = 0
  let tick = 0
  for (; tick < ticks; tick++) {
    if (each && !each(tick)) break
    const start = performance.now()
    sim.advance(TICK)
    const spent = performance.now() - start
    total += spent
    worst = Math.max(worst, spent)
  }
  return { mean: round(total / tick), worst: round(worst, 1), search: Math.round((searchedTiles() - searched) / tick) }
}

/** Нагрузка: во что обходится движение и погоня большим армиям. */
export function printLoad() {
  console.log('\nНагрузка: время тика в мс (бюджет — 50) и тайлов поиска пути за тик')
  const rows: (string | number)[][] = []
  for (const count of [200, 1000, 2000]) {
    // Армия лансеров стоит, потом идёт строем на 40 тайлов дальше своего края.
    const sim = newSim()
    const side = Math.ceil(Math.sqrt(count))
    let at: { x: number; y: number } | undefined
    search: for (let y = -400; y < 400 && !at; y += 4) {
      for (let x = -400; x < 400; x += 4) {
        let open = true
        for (let dy = 0; dy < side * 1.5 + 4 && open; dy += 2) for (let dx = 0; dx < side * 1.5 + 60 && open; dx += 2) open = isWalkable(sim, x + dx, y + dy)
        if (!open) continue
        at = { x, y }
        break search
      }
    }
    if (!at) throw new Error(`Не нашлось места под армию в ${count}`)
    const units = block(sim, 'lancer', 1, count, at.x, at.y, side)
    seconds(sim, 1)
    const idle = measure(sim, 100)
    orderGroupMove(sim, units, Math.round(at.x + side * 1.5 + 40), Math.round(at.y + side * 0.75))
    const moving = measure(sim, 200)
    rows.push([`марш ${count} лансеров`, idle.mean, moving.mean, moving.worst, moving.search])
  }
  {
    // Полторы сотни разных юнитов на одну цель: худший случай для мест вокруг цели.
    const sim = newSim()
    const at = dryArena(sim, 60, 40)
    const types: UnitType[] = ['infantry', 'lancer', 'tank', 'flamer', 'buggy']
    const own = Array.from({ length: 150 }, (_, i) => spawnUnit(sim, types[i % types.length], 1, at.x + 2 + (i % 10) * 2, at.y + 5 + Math.floor(i / 10) * 2))
    const target = spawnUnit(sim, 'tank', TRAINING_PLAYER, at.x + 50, at.y + 20)
    setStance(sim, TRAINING_PLAYER, [target], 'passive')
    orderAttack(sim, 1, own, target)
    const result = measure(sim, 30 / TICK, () => {
      sim.world.get(target, Health)!.value = 1
      return true
    })
    rows.push(['150 на одну цель', '', result.mean, result.worst, result.search])
  }
  {
    // Бой двух армий по сотне: как в показательном бою, приказы раз в четверть секунды.
    const sim = newSim()
    const at = dryArena(sim, 60, 40)
    const army = Array.from({ length: 100 }, (_, i) => (['infantry', 'lancer', 'tank', 'flamer', 'buggy', 'rocketeer', 'artillery'] as UnitType[])[i % 7])
    spawnBattle(sim, 1, at.x + 30, at.y + 20, army, army, 15)
    const result = measure(sim, 60 / TICK, (tick) => tick % 5 !== 0 || driveBattle(sim, 1))
    rows.push(['бой 100 на 100', '', result.mean, result.worst, result.search])
  }
  printTable(['сцена', 'стоят', 'идут', 'худший', 'поиск'], rows, 22, 9)
}

/** Состав армии перехода: пехота, техника всех весов и немного авиации. */
const CROSSING_ARMY: [UnitType, number][] = [
  ['infantry', 50], ['flamer', 20], ['rocketeer', 20], ['buggy', 25], ['lancer', 30], ['flak', 15], ['tank', 25],
  ['artillery', 15], ['tesla', 10], ['carrier', 8], ['mcv', 2], ['drone', 10], ['gunship', 6],
]
/** Ближе стольких тайлов к цели приказ, кончившийся у юнита, считается выполненным. */
const ARRIVED = 25
/** Сколько минут игры отводится переходу. */
const CROSSING_LIMIT = 20

/** Переход через карту 512×512 по диагонали на одной карте; seed — зерно карты, undefined — по умолчанию. */
function cross(seed: number | undefined) {
  const generator = seed === undefined ? DEFAULT_SETTINGS.generator : { ...DEFAULT_SETTINGS.generator, seed }
  const sim = createSim({ generator, size: 512 })
  const { bounds } = sim
  const total = CROSSING_ARMY.reduce((sum, [, count]) => sum + count, 0)
  /** Ближайший к (x, y) тайл, вокруг которого встанет вся армия. */
  const spot = (x: number, y: number) => {
    for (let ring = 0; ring < 60; ring++) {
      for (let dy = -ring; dy <= ring; dy++) {
        for (let dx = -ring; dx <= ring; dx++) {
          if (Math.max(Math.abs(dx), Math.abs(dy)) !== ring) continue
          if (isWalkable(sim, x + dx, y + dy) && freeTilesNear(sim, x + dx, y + dy, total).length >= total * 2) return { x: x + dx, y: y + dy }
        }
      }
    }
    throw new Error('Не нашлось места под армию')
  }
  const start = spot(bounds.left + 40, bounds.top + 40)
  const goal = spot(bounds.right - 40, bounds.bottom - 40)
  const tiles = freeTilesNear(sim, start.x, start.y, total)
  const units: { entity: Entity; type: UnitType }[] = []
  for (const [type, count] of CROSSING_ARMY) {
    for (let i = 0; i < count; i++) units.push({ entity: spawnUnit(sim, type, 1, tiles[units.length * 2], tiles[units.length * 2 + 1]), type })
  }
  orderGroupMove(sim, units.map((unit) => unit.entity), goal.x, goal.y)
  const state = new Map(units.map(({ entity }) => [entity, { arrived: -1, still: 0, longest: 0, x: 0, y: 0, moving: true }]))
  const searched = searchedTiles()
  const began = performance.now()
  let worst = 0
  let tick = 0
  for (; tick < (CROSSING_LIMIT * 60) / TICK; tick++) {
    for (const { entity } of units) Object.assign(state.get(entity)!, sim.world.get(entity, Position)!)
    const before = performance.now()
    sim.advance(TICK)
    worst = Math.max(worst, performance.now() - before)
    let moving = 0
    for (const { entity } of units) {
      const unit = state.get(entity)!
      const position = sim.world.get(entity, Position)!
      const now = sim.world.has(entity, Path)
      if (now) {
        moving++
        unit.still = Math.hypot(position.x - unit.x, position.y - unit.y) < 0.01 ? unit.still + 1 : 0
        unit.longest = Math.max(unit.longest, unit.still)
      }
      if (unit.moving && !now && unit.arrived < 0 && Math.hypot(position.x - goal.x, position.y - goal.y) <= ARRIVED) unit.arrived = tick * TICK
      unit.moving = now
    }
    if (!moving) break
  }
  const spent = performance.now() - began
  const stuck = units.filter(({ entity }) => state.get(entity)!.arrived < 0)
  const longest = units.reduce((best, unit) => (state.get(unit.entity)!.longest > state.get(best.entity)!.longest ? unit : best))
  return {
    seed: seed ?? 'обычная',
    arrived: `${units.length - stuck.length}/${units.length}`,
    last: Math.round(Math.max(...units.map(({ entity }) => state.get(entity)!.arrived))),
    longest: `${round(state.get(longest.entity)!.longest * TICK, 1)} ${longest.type}`,
    speed: `×${Math.round((tick * TICK * 1000) / spent)}`,
    worst: round(worst, 1),
    search: Math.round((searchedTiles() - searched) / tick),
    stuck,
  }
}

/** Переход армии через карту на нескольких картах. */
export function printCrossing() {
  console.log(`\nПереход: ${CROSSING_ARMY.reduce((sum, [, count]) => sum + count, 0)} разных юнитов через карту 512×512 по диагонали (~611 тайлов), до ${CROSSING_LIMIT} минут игры`)
  const rows: (string | number)[][] = []
  for (const seed of [undefined, 1, 2, 3, 4, 5, 6]) {
    const result = cross(seed)
    rows.push([result.seed, result.arrived, result.last, result.longest, result.speed, result.worst, result.search])
    for (const { type } of result.stuck) rows.push(['', `не доехал: ${type}`, '', '', '', '', ''])
  }
  printTable(['карта', 'доехали', 'последний', 'дольше всех стоял', 'скорость', 'худший', 'поиск'], rows, 10, 13)
  console.log('  «последний» — когда доехал последний, с; «дольше всех стоял» — подряд с приказом, с; «худший» — тик, мс')
}

/** Все замеры движения, кроме долгого перехода. */
export function runMovement() {
  printFormation()
  printSurround()
  printBuilders()
  printLoad()
}
