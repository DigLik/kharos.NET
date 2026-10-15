import { allied, alliances } from './allies'
import type { Entity } from '../ecs'
import { Biome, Terrain, biomeAt, terrainAt, tileKey } from '../map/terrain'
import { isPaved } from './paved'
import { dropCargo } from './drops'
import { BUILDINGS, buildingSpec, isWall, type BuildingType } from './buildings'
import { NONE, isOwn, onTurn, ownerOf, rectDistance, turnToward, wrap } from './common'
import { Armed, Blast, Building, Burst, Converting, Doomed, Health, Inventory, Off, Owner, Path, Pave, Position, Shot, Site, Tactics, Turret, Unit } from './components'
import { releaseHauler } from './hauling'
import { powerSupply } from './income'
import { repairStations } from './construction'
import { absorb, domes, stopShot, stopper } from './shields'
import { amountOf, take } from './inventory'
import { searchCut, searchedTiles, withSearchQuota } from './path'
import { isEmpty, resupplying, spendAmmo, unitHasAmmo } from './supply'
import { meltdown } from './reactors'
import { protectedPlayers, protectionLeft, unprotect } from './shelter'
import type { Sim } from './sim'
import { TURRETS, carrierOf, turnerOf, turretsOf } from './turrets'
import { UNITS, flies, isWalkable, onFoot, orderMove, unitSpec, type UnitSpec } from './units'
import { WEAPONS, type Armor, type ShotKind, type WeaponSpec, type WeaponType } from './weapons'
import { LEASH, clearTactics, leaveHome, stanceOf } from './tactics'
import { unitSight } from './vision'

/** Сколько единиц прочности у здания на кредит его цены. */
export const BUILDING_HP = 2
/** Раз во сколько тиков свободный юнит высматривает врага, а гонящийся за целью прокладывает путь заново. */
const SCAN_TICKS = 5
/** На столько тайлов дальше считается цель, которая стрелку навредить не может: сперва бьют тех, кто опасен. */
const HARMLESS = 1000
const CHASE_TICKS = 20
/** На сколько тайлов ближе дальности выстрела подходит гонящийся: с запасом на то, что и он, и цель стоят не в центрах тайлов. */
const CHASE_MARGIN = 1.5
/**
 * Насколько дальше бьёт оружие, стоящее на здании: турель выше и устойчивее машины. Без надбавки враг с тем же
 * оружием бьёт здание рядом с турелью с дальности, считанной от края здания, и оказывается вне её досягаемости.
 */
export const TOWER_RANGE = 2
/** Сколько гонящихся за тик прокладывают путь и сколько тайлов они на это вместе осматривают; остальные ждут следующего раза. */
const CHASE_SEARCHES = 16
const CHASE_TILES = 15000
/** На сколько стрелков сдвигается начало обхода с каждым тиком. */
const SHOOTER_STRIDE = 17
/** Не нашедший пути гонящийся пробует снова вдвое реже с каждой неудачей, но не больше стольких удвоений. */
const STUCK_LIMIT = 3
/** Насколько точно юнит должен смотреть на цель, чтобы выстрелить, в радианах. */
const AIM = 0.12
/** Сколько тиков виден след лазера и разряда. */
const BEAM_TICKS = 3
/** На каком расстоянии разряд перескакивает на следующую цель, в тайлах, и какая доля урона остаётся после прыжка. */
const CHAIN_RANGE = 2.5
const CHAIN_DECAY = 0.6
/** Во сколько раз дальше дальности стрельбы наводящийся снаряд летит за целью, прежде чем сгореть. */
const OVERFLY = 1.5
/** У края взрыва достаётся такая доля урона. */
const SPLASH_EDGE = 0.5
/**
 * Что стена останавливает. Ракета летит поверх неё, разряд бьёт через неё: от электричества и ракет стена не укрывает.
 * Остальное — пули, ядро, лазер — в стену и попадает, а не в того, кто за ней.
 */
const STOPS_AT_WALL: Record<ShotKind, boolean> = { bullet: true, shell: true, laser: true, rocket: false, arc: false, flame: true }
/** Учебный противник: игрок, за которого никто не играет. Его юнитов создаёт отладочная команда spawnUnit. */
export const TRAINING_PLAYER = 9999

/** Сколько урона выдерживает здание этого вида. */
export const buildingHp = (type: BuildingType) => buildingSpec(type).hp ?? BUILDINGS[type].cost * BUILDING_HP

/** Враги ли игроки. Игрок 0 — ничей: он ни с кем не воюет. */
export const hostile = (a: number, b: number) => a !== b && a !== 0 && b !== 0

/** Всё, во что можно попасть: юнит или здание. Собирается раз в тик. */
interface Mark {
  entity: Entity
  player: number
  /** Центр. */
  x: number
  y: number
  /** Прямоугольник основания у здания; у юнита — нулевой, в его центре. */
  left: number
  top: number
  width: number
  height: number
  /** Радиус юнита; у здания ноль. */
  radius: number
  air: boolean
  armor: Armor
  /** Стена: остановленный ею выстрел достаётся только стенам. */
  wall: boolean
  hp: number
  /** Компонент прочности: урон пишется прямо в него, туда же — тик попадания. */
  health: { value: number; hit: number }
  /** Чем вооружена, считая турели: бит 1 — бьёт по земле, бит 2 — по воздуху. Считается при первой надобности. */
  arms?: number
  /** Номер последнего поиска по сетке, нашедшего метку: здание лежит в нескольких ячейках, а найтись должно раз. */
  seen: number
}

/** Сторона ячейки сетки меток, в тайлах. */
const MARK_CELL = 8
/** Самый большой радиус юнита: на столько метка может выступать из своей ячейки. */
const MARK_PAD = 2

/**
 * Метки по ячейкам: стрелку, взрыву и разряду нужны только те, что рядом, а перебирать на каждого все метки мира —
 * квадрат от численности армий. Здание лежит во всех ячейках, которые накрывает.
 */
function gridOf(marks: Map<Entity, Mark>) {
  const cells = new Map<number, Mark[]>()
  const put = (key: number, mark: Mark) => {
    const cell = cells.get(key)
    if (cell) cell.push(mark)
    else cells.set(key, [mark])
  }
  for (const mark of marks.values()) {
    const left = Math.floor(mark.left / MARK_CELL)
    const top = Math.floor(mark.top / MARK_CELL)
    const right = Math.floor((mark.left + mark.width) / MARK_CELL)
    const bottom = Math.floor((mark.top + mark.height) / MARK_CELL)
    for (let y = top; y <= bottom; y++) for (let x = left; x <= right; x++) put(tileKey(x, y), mark)
  }
  let query = 0
  const found: Mark[] = []
  /**
   * Метки, край которых может оказаться не дальше radius от точки: каждая по разу, в порядке ячеек. Массив один
   * на все поиски — его обходят сразу, до следующего поиска.
   */
  return function near(x: number, y: number, radius: number): readonly Mark[] {
    const current = ++query
    found.length = 0
    const reach = radius + MARK_PAD
    const bottom = Math.floor((y + reach) / MARK_CELL)
    const right = Math.floor((x + reach) / MARK_CELL)
    for (let cellY = Math.floor((y - reach) / MARK_CELL); cellY <= bottom; cellY++) {
      for (let cellX = Math.floor((x - reach) / MARK_CELL); cellX <= right; cellX++) {
        const cell = cells.get(tileKey(cellX, cellY))
        if (!cell) continue
        for (const mark of cell) {
          if (mark.seen === current) continue
          mark.seen = current
          found.push(mark)
        }
      }
    }
    return found
  }
}

/** Расстояние от точки до края цели в тайлах. */
function distanceTo(mark: Mark, x: number, y: number) {
  return Math.max(0, rectDistance(mark.left, mark.top, mark.width, mark.height, x, y) - mark.radius)
}

/**
 * На сколько тайлов ближе дальности выстрела место у цели. Меньше, чем CHASE_MARGIN: стреляющий корпусом по дороге к
 * месту молчит, и место должно быть там, где он и так встал бы, — у края выстрела, а не глубже.
 */
const SLOT_MARGIN = 0.5
/** Сколько тайлов оставляют между собой соседи на местах вокруг цели, сверх своих радиусов. */
const SLOT_GAP = 0.4
/** Ближе стольких тайлов к своему месту гонящийся, уже достающий до цели, встаёт стрелять. */
const SLOT_REACH = 0.75
/** Сколько тиков подряд гонящийся, уже достающий до цели, может простоять в заторе по пути к месту: дальше он стреляет, где стоит. */
const SLOT_PATIENCE = 10
/** Ближе стольких тайлов к своему месту гонящийся прокладывает путь к нему заново при каждой проверке погони. */
const SLOT_REPLAN = 4
/** Сторона ячейки, по которой slotsAround ищет занятые места рядом: не меньше, чем два самых крупных радиуса с зазором. */
const SLOT_CELL = 2.5
/** Ближе стольких тайлов к месту за целью не должно быть других врагов: иначе место — в строю чужой армии, см. slotsAround. */
const GUARD_RANGE = 4
/** Насколько кольца мест у разных нападающих могут разниться, чтобы считаться одним: см. slotsAround. */
const FULL_RING = 0.25

/** Гонящийся за целью: носитель, на сколько ему подойти к краю цели и стоит ли он уже на выстреле. */
interface Pursuer {
  entity: Entity
  x: number
  y: number
  radius: number
  reach: number
  foot: boolean
  /** Уже стреляет с места: его место — где стоит. */
  settled: boolean
  player: number
}

/**
 * Места вокруг цели для гонящихся за ней: каждому — точка на расстоянии его выстрела от края цели, как можно ближе
 * к той стороне, с которой он подходит, и не ближе SLOT_GAP к соседям. Немногие с дальним оружием встают дугой со
 * своей стороны, а когда на дуге тесно — лишние занимают места дальше по кругу, и дуга замыкается в кольцо.
 * Без мест все ехали бы в одну ближайшую точку: передние вставали бы стрелять, задние упирались бы в них.
 * За цель — дальше четверти круга от своей стороны — встают, только если там нет других врагов (guarded): одинокую цель
 * толпа обступает, а цель в строю чужой армии — нет, иначе лишние шли бы на места сквозь этот строй.
 * Сначала места занимают уже стреляющие, потом гонящиеся — от ближних к дальним. Кому места не нашлось, в ответе нет.
 */
function slotsAround(sim: Sim, target: Mark, pursuers: readonly Pursuer[], guarded: (x: number, y: number, player: number) => boolean) {
  const slots = new Map<Entity, { x: number; y: number }>()
  // Занятые места — по ячейкам SLOT_CELL: проверять каждое место против всех занятых — квадрат от числа нападающих.
  const taken = new Map<number, { x: number; y: number; radius: number }[]>()
  const take = (x: number, y: number, radius: number) => {
    const key = tileKey(Math.floor(x / SLOT_CELL), Math.floor(y / SLOT_CELL))
    const cell = taken.get(key)
    if (cell) cell.push({ x, y, radius })
    else taken.set(key, [{ x, y, radius }])
  }
  const free = (x: number, y: number, radius: number) => {
    const cellX = Math.floor(x / SLOT_CELL)
    const cellY = Math.floor(y / SLOT_CELL)
    for (let dy = -1; dy <= 1; dy++) {
      for (let dx = -1; dx <= 1; dx++) {
        for (const other of taken.get(tileKey(cellX + dx, cellY + dy)) ?? []) {
          if (Math.hypot(other.x - x, other.y - y) < other.radius + radius + SLOT_GAP) return false
        }
      }
    }
    return true
  }
  const centerX = target.left + target.width / 2
  const centerY = target.top + target.height / 2
  const order = [...pursuers].sort((a, b) => Number(b.settled) - Number(a.settled) || Math.hypot(a.x - centerX, a.y - centerY) - Math.hypot(b.x - centerX, b.y - centerY))
  // Кольца, на которых места уже не нашлось: занятых мест только прибавляется, и такому же или крупнее там его не найти.
  // Без этого в толпе каждый следующий перебирал бы весь круг впустую.
  const full: { ring: number; radius: number }[] = []
  for (const pursuer of order) {
    if (pursuer.settled) {
      take(pursuer.x, pursuer.y, pursuer.radius)
      continue
    }
    const bearing = Math.atan2(pursuer.y - centerY, pursuer.x - centerX)
    const ring = pursuer.reach + target.radius + Math.min(target.width, target.height) / 2
    if (full.some((other) => Math.abs(other.ring - ring) < FULL_RING && other.radius <= pursuer.radius)) continue
    // Шаг по кругу — полместа: так соседи встают плотно, но друг в друга не заходят.
    const step = (pursuer.radius + SLOT_GAP / 2) / Math.max(1, ring)
    for (let k = 0; k * step <= Math.PI; k++) {
      let found: { x: number; y: number } | undefined
      for (const angle of k ? [bearing + k * step, bearing - k * step] : [bearing]) {
        const dx = Math.cos(angle)
        const dy = Math.sin(angle)
        // Точка, где луч из центра выходит из основания цели, и от неё — на выстрел наружу.
        const edge = Math.min(dx ? target.width / 2 / Math.abs(dx) : Infinity, dy ? target.height / 2 / Math.abs(dy) : Infinity)
        const out = (Number.isFinite(edge) ? edge : 0) + target.radius + pursuer.reach
        const x = centerX + dx * out
        const y = centerY + dy * out
        if (!isWalkable(sim, Math.floor(x), Math.floor(y), pursuer.foot) || !free(x, y, pursuer.radius)) continue
        if (k * step > Math.PI / 2 && guarded(x, y, pursuer.player)) continue
        found = { x, y }
        break
      }
      if (!found) continue
      slots.set(pursuer.entity, found)
      take(found.x, found.y, pursuer.radius)
      break
    }
    if (!slots.has(pursuer.entity)) full.push({ ring, radius: pursuer.radius })
  }
  return slots
}

function collectMarks(sim: Sim) {
  const marks = new Map<Entity, Mark>()
  for (const [entity, position, unit, owner, health] of sim.world.query(Position, Unit, Owner, Health)) {
    const spec: UnitSpec = UNITS[unit.type]
    marks.set(entity, {
      entity, player: owner.player, x: position.x, y: position.y, left: position.x, top: position.y, width: 0, height: 0,
      radius: spec.radius, air: spec.kind === 'air', armor: spec.kind, wall: false, hp: spec.hp, health, seen: 0,
    })
  }
  for (const [entity, position, building, owner, health] of sim.world.query(Position, Building, Owner, Health)) {
    const { width, height } = BUILDINGS[building.type]
    marks.set(entity, {
      entity, player: owner.player, x: position.x + width / 2, y: position.y + height / 2, left: position.x, top: position.y, width, height,
      radius: 0, air: false, armor: 'building', wall: isWall(sim, entity), hp: buildingHp(building.type), health, seen: 0,
    })
  }
  return marks
}

/**
 * Первая чужая стена на отрезке выстрела и точка, где снаряд в неё входит; undefined — путь свободен.
 * Своя стена не мешает: через неё стреляют, как через бруствер. Стена союзника — тоже своя, см. allies.ts:
 * friends — союзники стрелка, уже собранные на этот тик. Отрезок задан в тайлах, от стрелка к цели.
 */
function wallOnPath(sim: Sim, player: number, x0: number, y0: number, x1: number, y1: number, friends: readonly number[] | undefined) {
  const dx = x1 - x0
  const dy = y1 - y0
  if (!dx && !dy) return undefined
  const lastX = Math.floor(x1)
  const lastY = Math.floor(y1)
  const stepX = dx > 0 ? 1 : -1
  const stepY = dy > 0 ? 1 : -1
  /** Через сколько долей отрезка снаряд пересекает следующую границу тайла по этой оси. */
  const deltaX = dx ? Math.abs(1 / dx) : Infinity
  const deltaY = dy ? Math.abs(1 / dy) : Infinity
  let tileX = Math.floor(x0)
  let tileY = Math.floor(y0)
  let crossX = dx ? (dx > 0 ? tileX + 1 - x0 : x0 - tileX) / Math.abs(dx) : Infinity
  let crossY = dy ? (dy > 0 ? tileY + 1 - y0 : y0 - tileY) / Math.abs(dy) : Infinity
  /** Доля отрезка, на которой снаряд вошёл в текущий тайл. */
  let t = 0
  // Отрезок короткий: снаряд за тик пролетает тайл-другой. Запас нужен лишь на вырожденный случай.
  for (let tile = 0; tile < 64; tile++) {
    const building = sim.occupancy.at(tileX, tileY)
    if (building !== undefined && isWall(sim, building) && !isOwn(sim, player, building) && !friends?.includes(sim.world.get(building, Owner)?.player ?? 0)) {
      return { wall: building, x: x0 + dx * t, y: y0 + dy * t }
    }
    if (tileX === lastX && tileY === lastY) return undefined
    if (crossX < crossY) {
      t = crossX
      tileX += stepX
      crossX += deltaX
    } else {
      t = crossY
      tileY += stepY
      crossY += deltaY
    }
  }
  return undefined
}

/** Во сколько раз быстрее поправляется сама пехота у своей работающей ремонтной станции (с полной энергией). */
export const STATION_REGEN = 4

/**
 * Раз в тик: те, чья прочность восстанавливается сама (Health.regen), понемногу поправляются — кроме задетых в этот тик.
 * У своей ремонтной станции — быстрее, на долю её энергии.
 */
export function recover(sim: Sim) {
  const { world } = sim
  const stations = repairStations(sim)
  for (const [entity, health] of world.query(Health)) {
    if (health.regen <= 0 || health.hit === sim.time.tick || health.value >= health.max) continue
    let boost = 1
    if (stations.length) {
      const position = world.get(entity, Position)
      const player = ownerOf(sim, entity)
      for (const station of stations) {
        if (position && station.player === player && Math.hypot(position.x - station.x, position.y - station.y) <= station.radius) boost = Math.max(boost, 1 + (STATION_REGEN - 1) * station.share)
      }
    }
    health.value = Math.min(health.max, health.value + health.regen * boost * sim.time.step)
  }
}

/**
 * Хватит ли носителю боеприпасов на выстрел этим оружием. Турели зданий с BuildingSpec.ammo стреляют со склада,
 * юниты — из магазина (см. supply.ts); прочим зданиям хватает всегда.
 */
export function hasAmmo(sim: Sim, carrier: Entity, weapon: WeaponSpec) {
  const unit = sim.world.get(carrier, Unit)
  if (unit) return unitHasAmmo(sim, carrier, unit.type, weapon)
  const type = sim.world.get(carrier, Building)?.type
  if (type === undefined || !buildingSpec(type).ammo || !weapon.ammo) return true
  const store = sim.world.get(carrier, Inventory)
  return !!store && amountOf(store, 'ammo') >= weapon.ammo - 1e-9
}

/** Оружие юнита или турели; undefined — безоружен. */
export const weaponOf = (sim: Sim, entity: Entity): WeaponType | undefined => {
  const type = sim.world.get(entity, Unit)?.type
  if (type) return unitSpec(type).weapon
  const turret = sim.world.get(entity, Turret)?.type
  return turret && (TURRETS[turret] as { weapon?: WeaponType }).weapon
}

/** Может ли игрок приказать своим юнитам атаковать это: чужой юнит или чужое здание — не союзника, см. allies.ts. */
export function canAttack(sim: Sim, player: number, target: Entity) {
  const { world } = sim
  if (!world.has(target, Position) || (!world.has(target, Unit) && !world.has(target, Building))) return false
  const owner = ownerOf(sim, target)
  // Новичка под защитой не атакуют, см. shelter.ts.
  return hostile(player, owner) && !allied(sim, player, owner) && protectionLeft(sim, owner) === 0
}

/**
 * Приказывает юнитам игрока атаковать цель: они подходят на выстрел и бьют, пока цель жива. Турели носителя
 * берут цель вместе с ним, а сам носитель подъезжает к ней на выстрел турелей.
 * Чужие, безоружные и те, чьё оружие до цели не достаёт (по летающим), из списка выбрасываются.
 */
export function orderAttack(sim: Sim, player: number, units: Entity[], target: Entity) {
  const { world } = sim
  if (!canAttack(sim, player, target)) return false
  // Сам приказал атаковать — защита новичка снята.
  unprotect(sim, player)
  const unit = world.get(target, Unit)
  const air = !!unit && flies(unit.type)
  /** Берёт ли цель этот стрелок; возвращает дальность его оружия или 0. */
  const aim = (entity: Entity) => {
    const armed = world.get(entity, Armed)
    const weapon = weaponOf(sim, entity)
    const spec: WeaponSpec | undefined = weapon && WEAPONS[weapon]
    if (!armed || !spec || (air ? !spec.air : spec.onlyAir)) return 0
    armed.target = target
    armed.chase = true
    armed.ordered = true
    armed.stuck = 0
    return spec.range
  }
  let ordered = false
  for (const entity of new Set(units)) {
    if (!isOwn(sim, player, entity)) continue
    // Приказ атаки снимает патруль и возвращение на место: юнит идёт за целью, куда велели.
    clearTactics(sim, entity)
    if (aim(entity)) {
      // Прежний путь больше не нужен: к цели юнит тронется сам в ближайший тик.
      world.remove(entity, Path)
      ordered = true
      continue
    }
    // Безоружный носитель подвозит турели на выстрел: путь к цели ему проложит бой, как гонящемуся.
    if (!turretsOf(sim, entity).map(aim).some(Boolean)) continue
    world.remove(entity, Path)
    ordered = true
  }
  return ordered
}

/** Снимает с юнита и его турелей цель: они снова бьют только тех, до кого достают с места. */
export function stopAttack(sim: Sim, entity: Entity) {
  for (const gunner of [entity, ...turretsOf(sim, entity)]) {
    const armed = sim.world.get(gunner, Armed)
    if (!armed) continue
    armed.target = NONE
    armed.chase = false
    armed.ordered = false
  }
}

/**
 * Раз в тик: бой. Вооружённые юниты бьют врагов — чужих юнитов и здания. Свободный юнит сам стреляет в тех, до кого
 * достаёт с места; получив приказ атаковать или попав под огонь, гонится за целью. Стреляет он только стоя
 * и повернувшись к цели. Снаряды летят и бьют, долетев; прочность, упавшая до нуля, — гибель.
 */
export function fight(sim: Sim) {
  const { world, time } = sim
  const marks = collectMarks(sim)
  const near = gridOf(marks)
  const dead = new Set<Entity>()
  /** Взрывы тика. ground — пришёлся в землю, а не в летающего: такой оставляет гарь. */
  const blasts: { x: number; y: number; size: number; ground: boolean }[] = []

  /** Игроки под защитой новичка: в них не попасть, и сами они не стреляют. */
  const safe = protectedPlayers(sim)
  /** Союзников не бьют — ни прицелом, ни взрывом рядом. */
  const friends = alliances(sim)
  const canHit = (weapon: WeaponSpec, player: number, mark: Mark) =>
    hostile(player, mark.player) && !friends.get(player)?.includes(mark.player) && !safe.has(player) && !safe.has(mark.player) && (mark.air ? weapon.air : !weapon.onlyAir) && !dead.has(mark.entity)

  /** Может ли mark навредить такому стрелку: есть ли у неё или у её турелей оружие, которое по нему бьёт. */
  const threatens = (mark: Mark, air: boolean) => {
    if (mark.arms === undefined) {
      mark.arms = 0
      for (const gunner of [mark.entity, ...turretsOf(sim, mark.entity)]) {
        const own = weaponOf(sim, gunner)
        if (!own || !world.has(gunner, Armed)) continue
        const spec: WeaponSpec = WEAPONS[own]
        mark.arms |= (spec.onlyAir ? 0 : 1) | (spec.air ? 2 : 0)
      }
    }
    return (mark.arms & (air ? 2 : 1)) !== 0
  }

  /** Доля энергии, которая достаётся потребителям каждой зоны; считается, когда выстрелит первый из них. */
  let supply: ReadonlyMap<Entity, number> | undefined
  /**
   * На какую долю скорострельности хватает энергии носителю. Здание-потребитель стреляет энергией зоны: не хватает —
   * стреляет реже, нет вовсе или стоит вне зоны — молчит. Остальным энергия не нужна.
   */
  const powerFor = (carrier: Entity) => {
    const type = world.get(carrier, Building)?.type
    if (type === undefined || (buildingSpec(type).power ?? 0) >= 0) return 1
    supply ??= powerSupply(sim)
    return supply.get(carrier) ?? 0
  }

  /** Купола щитов на этот тик; гасят урон, см. shields.ts. */
  const shields = domes(sim)
  /**
   * Наносит урон. source — кто стрелял: уцелевший свободный юнит отвечает ему огнём; (fromX, fromY) — откуда:
   * щит гасит только пришедшее из-за купола.
   */
  const hit = (mark: Mark, amount: number, weapon: WeaponSpec, source: Entity, fromX: number, fromY: number) => {
    if (dead.has(mark.entity)) return
    // Тик попадания помнит ремонт: под огнём не чинят, см. REPAIR_PAUSE.
    mark.health.hit = time.tick
    let damage = amount * (weapon.vs?.[mark.armor] ?? 1)
    if (shields.length) damage = absorb(sim, shields, mark.player, mark.x, mark.y, damage, fromX, fromY, !!marks.get(source)?.air)
    mark.health.value -= damage / mark.hp
    if (mark.health.value <= 0) {
      dead.add(mark.entity)
      return
    }
    const from = marks.get(source)
    // Идущий по приказу не отвлекается; патрульный — отвечает.
    if (!from || (world.has(mark.entity, Path) && !world.get(mark.entity, Tactics)?.patrol.length)) return
    const unit = world.has(mark.entity, Unit)
    const stance = unit ? stanceOf(sim, mark.entity) : 'defensive'
    // Кому велено не стрелять, тот не отвечает.
    if (stance === 'passive') return
    // Отвечают и сам юнит, и его турели. Занятый целью, которую выбрал сам и которая ему не опасна, бросает её.
    for (const gunner of [mark.entity, ...turretsOf(sim, mark.entity)]) {
      const armed = world.get(gunner, Armed)
      const own = weaponOf(sim, gunner)
      if (!armed || !own || !canHit(WEAPONS[own], mark.player, from)) continue
      const busy = armed.target === NONE ? undefined : marks.get(armed.target as Entity)
      if (armed.target !== NONE && (armed.ordered || !busy || threatens(busy, mark.air))) continue
      armed.target = source
      // Здание отвечает, только если само достаёт до стрелка: гнаться за ним может лишь носитель-юнит. Держащий
      // позицию тоже не гонится.
      armed.chase = unit && stance !== 'hold'
      armed.ordered = false
      armed.stuck = 0
      if (armed.chase) leaveHome(sim, mark.entity)
    }
  }

  /**
   * Попадание в точку: урон цели или всем врагам в радиусе взрыва. blocked — выстрел остановила стена:
   * тогда взрыв достаётся только стенам, а всё, что за ними, укрыто.
   */
  const impact = (weapon: WeaponSpec, player: number, source: Entity, target: Entity, x: number, y: number, fromX: number, fromY: number, blocked = false) => {
    if (!weapon.splash) {
      const mark = marks.get(target)
      if (mark && canHit(weapon, player, mark)) hit(mark, weapon.damage, weapon, source, fromX, fromY)
      return
    }
    for (const mark of near(x, y, weapon.splash)) {
      if (blocked && !mark.wall) continue
      if (!canHit(weapon, player, mark)) continue
      const distance = distanceTo(mark, x, y)
      if (distance > weapon.splash) continue
      hit(mark, weapon.damage * (1 - (1 - SPLASH_EDGE) * (distance / weapon.splash)), weapon, source, fromX, fromY)
    }
  }

  /** Списывает боеприпасы на выстрел: со склада турели или из магазина юнита. false — стрелять нечем. */
  const spend = (carrier: Entity, weapon: WeaponSpec) => {
    if (!hasAmmo(sim, carrier, weapon)) return false
    const unit = world.get(carrier, Unit)
    if (unit) {
      spendAmmo(sim, carrier, unit.type, weapon)
      return true
    }
    const store = world.get(carrier, Inventory)
    if (store && weapon.ammo) take(store, 'ammo', weapon.ammo)
    return true
  }

  // Взрывы и следы лучей доживают свой срок.
  const gone: Entity[] = []
  for (const [entity, blast] of world.query(Blast)) if (++blast.age >= blast.life) gone.push(entity)

  // Снаряды летят. Попадания — после обхода.
  const landed: { entity: Entity; weapon: WeaponSpec; player: number; source: Entity; target: Entity; x: number; y: number; fromX: number; fromY: number; blocked: boolean; air: boolean }[] = []
  for (const [entity, shot, position] of world.query(Shot, Position)) {
    const weapon: WeaponSpec = WEAPONS[shot.weapon]
    shot.age++
    if (!weapon.speed) {
      if (shot.age >= shot.life) gone.push(entity)
      continue
    }
    shot.prevX = position.x
    shot.prevY = position.y
    // Пуля и ракета следят за целью, пока она жива; ядро летит, куда выпущено.
    const mark = weapon.shot === 'shell' ? undefined : marks.get(shot.target as Entity)
    if (mark) {
      shot.toX = mark.x
      shot.toY = mark.y
    }
    const dx = shot.toX - position.x
    const dy = shot.toY - position.y
    const distance = Math.hypot(dx, dy)
    const move = weapon.speed * time.step
    // Купол чужого щита на пути: снаряд рвётся на его краю, урон берёт щит, внутрь ничего не долетает.
    if (shields.length) {
      const share = distance > 0 ? Math.min(1, move / distance) : 1
      const sourceUnit = world.get(shot.source as Entity, Unit)
      const fromAir = !!sourceUnit && flies(sourceUnit.type)
      const stop = stopper(sim, shields, shot.player, position.x, position.y, position.x + dx * share, position.y + dy * share, fromAir)
      if (stop) {
        stopShot(sim, stop.dome, weapon.damage)
        position.x = stop.x
        position.y = stop.y
        blasts.push({ x: stop.x, y: stop.y, size: weapon.splash ?? 0.2, ground: false })
        gone.push(entity)
        continue
      }
    }
    if (distance <= move || shot.age >= shot.life) {
      // Не догнавший цель снаряд сгорает там, где оказался.
      const reached = distance <= move
      if (reached) {
        position.x = shot.toX
        position.y = shot.toY
      }
      if (reached || weapon.splash) {
        landed.push({ entity, weapon, player: shot.player, source: shot.source as Entity, target: shot.target as Entity, x: position.x, y: position.y, fromX: shot.fromX, fromY: shot.fromY, blocked: shot.blocked, air: shot.air })
      } else gone.push(entity)
      continue
    }
    position.x += (dx / distance) * move
    position.y += (dy / distance) * move
  }
  for (const { entity, weapon, player, source, target, x, y, fromX, fromY, blocked, air } of landed) {
    impact(weapon, player, source, target, x, y, fromX, fromY, blocked)
    // Попадание в летающего земли не касается, даже если его сбили, пока снаряд летел.
    blasts.push({ x, y, size: weapon.splash ?? 0.2, ground: !air })
    gone.push(entity)
  }
  for (const entity of gone) world.destroy(entity)

  // Стрелки. Список собирается заранее: дальше мир и обходится заново, и меняется.
  const shooters: { entity: Entity; armed: { target: number; chase: boolean; cooldown: number; stuck: number; ordered: boolean } }[] = []
  for (const [entity, armed] of world.query(Armed)) shooters.push({ entity, armed })

  /**
   * Кто за какой целью гонится — наземные носители, на начало тика: им раздаются места вокруг цели, см. slotsAround.
   * У носителя с несколькими турелями подход — по самой короткой из погонь: на месте должны доставать все.
   */
  const pursuers = new Map<Entity, Map<Entity, Pursuer>>()
  for (const { entity, armed } of shooters) {
    if (!armed.chase || armed.target === NONE) continue
    const carrier = carrierOf(sim, entity)
    const unit = world.get(carrier, Unit)
    const position = world.get(carrier, Position)
    const weapon = weaponOf(sim, entity)
    if (!unit || !position || !weapon || flies(unit.type)) continue
    const spec: WeaponSpec = WEAPONS[weapon]
    const reach = Math.max((spec.minRange ?? 0) + SLOT_MARGIN, spec.range - SLOT_MARGIN)
    const target = marks.get(armed.target as Entity)
    let around = pursuers.get(armed.target as Entity)
    if (!around) pursuers.set(armed.target as Entity, (around = new Map()))
    const known = around.get(carrier)
    if (known && known.reach <= reach) continue
    const settled = !world.has(carrier, Path) && !!target && distanceTo(target, position.x, position.y) <= spec.range
    around.set(carrier, { entity: carrier, x: position.x, y: position.y, radius: UNITS[unit.type].radius, reach, foot: onFoot(unit.type), settled, player: ownerOf(sim, carrier) })
  }
  /** Места вокруг целей; раздаются, когда за местом впервые в тике обращаются. */
  const slots = new Map<Entity, Map<Entity, { x: number; y: number }>>()
  const slotOf = (target: Mark, carrier: Entity) => {
    let around = slots.get(target.entity)
    if (!around) {
      // Есть ли у места другие враги, кроме самой цели.
      const guarded = (x: number, y: number, player: number) =>
        near(x, y, GUARD_RANGE).some((mark) => mark.entity !== target.entity && hostile(player, mark.player) && !friends.get(player)?.includes(mark.player) && !dead.has(mark.entity) && distanceTo(mark, x, y) <= GUARD_RANGE)
      slots.set(target.entity, (around = slotsAround(sim, target, [...(pursuers.get(target.entity)?.values() ?? [])], guarded)))
    }
    return around.get(carrier)
  }

  /** Гонится ли носитель или одна из его турелей за целью: тогда его путь проложил бой, а не игрок. */
  const inChase = (carrier: Entity) => [carrier, ...turretsOf(sim, carrier)].some((gunner) => {
    const armed = world.get(gunner, Armed)
    return !!armed && armed.chase && armed.target !== NONE
  })

  let searches = 0
  /** Носители, которым в этот тик уже проложен путь к цели. */
  const chasing = new Set<Entity>()
  const searchedBefore = searchedTiles()
  // Обход каждый тик начинается с нового места: иначе норма поисков пути всегда доставалась бы одним и тем же.
  const first = shooters.length ? (time.tick * SHOOTER_STRIDE) % shooters.length : 0
  for (let index = 0; index < shooters.length; index++) {
    const { entity, armed } = shooters[(first + index) % shooters.length]
    if (armed.cooldown > 0) armed.cooldown--
    // Турель стреляет с носителя: её место — своё, а чей выстрел и кому отвечать огнём — носителя.
    const carrier = carrierOf(sim, entity)
    const mounted = carrier !== entity
    const body = marks.get(carrier)
    const position = world.get(entity, Position)
    const turner = turnerOf(sim, entity)
    const weaponType = weaponOf(sim, entity)
    if (!body || !position || !turner || !weaponType || world.has(carrier, Converting) || world.has(carrier, Site) || world.has(carrier, Off)) continue
    const self = { x: position.x, y: position.y, player: body.player }
    const weapon: WeaponSpec = WEAPONS[weaponType]
    const range = weapon.range + (world.has(carrier, Building) ? TOWER_RANGE : 0)

    let target = marks.get(armed.target as Entity)
    if (target && !canHit(weapon, self.player, target)) target = undefined
    // Гонится за целью тот, кто ездит: юнит сам, турель — на своём носителе. Стреляет турель и на ходу.
    const mover = carrier
    const moving = world.has(mover, Path)
    const unitCarrier = world.get(carrier, Unit)
    // Расстрелявший магазин молчит: цель бросает и новой не ищет, пока не пополнится, см. supply.ts.
    if (unitCarrier && isEmpty(sim, carrier)) {
      // Путь погони — боя, а не игрока: гнаться больше не за чем.
      if (armed.chase) world.remove(mover, Path)
      Object.assign(armed, { target: NONE, chase: false, ordered: false })
      continue
    }
    // Идущий за боеприпасами в погоню не уходит: бьёт, до кого достаёт, как держащий позицию.
    const stance = unitCarrier ? (resupplying(sim, carrier) ? 'hold' : stanceOf(sim, carrier)) : 'defensive'
    // Агрессивный в походе — идёт, куда послали, или возвращается после погони — высматривает врага, как патрульный:
    // бьёт всё, что увидел по дороге, и идёт дальше. Приказ атаковать цель — не поход: с ним юнит на других не смотрит.
    const tactics = world.get(carrier, Tactics)
    const patrolling = !!tactics?.patrol.length || (stance === 'aggressive' && !!tactics?.away)
    // Агрессивный высматривает на всю дальность обзора и гонится за найденным. Турель носителя, едущего по приказу
    // игрока, — только на выстрел: стреляет на ходу, но носитель за врагом не уводит.
    const led = moving && !patrolling && !inChase(carrier)
    const lookout = stance === 'aggressive' && unitCarrier && !led ? Math.max(range, unitSight(unitCarrier.type)) : range
    /**
     * Ближайший враг в пределах обзора: сперва те, кто стрелку опасен, потом юниты, потом здания. dangerous —
     * только опасные.
     */
    const look = (dangerous: boolean) => {
      let found: Mark | undefined
      let best = Infinity
      for (const mark of near(self.x, self.y, lookout)) {
        if (!canHit(weapon, self.player, mark)) continue
        const danger = threatens(mark, body.air)
        if (dangerous && !danger) continue
        const distance = distanceTo(mark, self.x, self.y)
        if (distance > lookout || distance < (weapon.minRange ?? 0)) continue
        const order = distance + (mark.armor === 'building' ? range : 0) + (danger ? 0 : HARMLESS)
        if (order >= best) continue
        best = order
        found = mark
      }
      return found
    }
    // Занятый целью, которую выбрал сам и которая ему не опасна, бросает её ради того, кто опасен.
    if (target && !armed.ordered && stance !== 'passive' && onTurn(time, entity, SCAN_TICKS) && !threatens(target, body.air)) {
      const danger = look(true)
      if (danger) {
        target = danger
        armed.target = danger.entity
        armed.stuck = 0
        if (distanceTo(danger, self.x, self.y) > range && unitCarrier) {
          armed.chase = true
          leaveHome(sim, carrier)
        }
      }
    }
    if (!target) {
      armed.target = NONE
      armed.chase = false
      armed.ordered = false
      // Свободный юнит высматривает врага в пределах выстрела: сперва юнитов, потом здания. Идущий по приказу не
      // высматривает, патрульный — высматривает. Кому велено не стрелять — не высматривает вовсе.
      if (stance === 'passive' || (moving && !mounted && !patrolling) || !onTurn(time, entity, SCAN_TICKS)) continue
      // В походе и по дороге назад агрессивный отвлекается только на тех, кто ему опасен: безобидное приказу идти
      // не помеха — иначе от чужих построек его было бы не увести.
      target = look(moving && !tactics?.patrol.length && !!tactics?.away)
      if (!target) continue
      armed.target = target.entity
      if (distanceTo(target, self.x, self.y) > range) {
        armed.chase = true
        leaveHome(sim, carrier)
      }
    }

    // Юнит в обороне, убежавший за врагом дальше поводка от места, где стоял, бросает погоню и вернётся туда.
    if (!armed.ordered && armed.chase && stance === 'defensive' && tactics?.away && Math.hypot(self.x - tactics.homeX - 0.5, self.y - tactics.homeY - 0.5) > LEASH) {
      armed.target = NONE
      armed.chase = false
      continue
    }

    if (distanceTo(target, self.x, self.y) > range) {
      if (!armed.chase || !world.has(mover, Unit)) armed.target = NONE
      // Носитель везёт к цели одна его турель за тик: остальным ехать с ним же.
      else if (mounted && chasing.has(mover)) continue
      // Вставший трогается снова не каждый тик, а не нашедший пути — всё реже: искать его без конца слишком дорого.
      else if (onTurn(time, entity, moving ? CHASE_TICKS : SCAN_TICKS << Math.min(armed.stuck, STUCK_LIMIT))) {
        // Едет на своё место вокруг цели; не нашлось места — просто на выстрел от неё.
        const slot = slotOf(target, mover)
        const goalX = Math.floor(slot ? slot.x : target.x)
        const goalY = Math.floor(slot ? slot.y : target.y)
        // Цель с места не сошла — прежний путь годится. Но у самого места путь к нему прокладывается заново: проложенный,
        // пока соседи ещё ехали, ведёт сквозь тех, кто с тех пор встал стрелять.
        const path = world.get(mover, Path)
        const carrierAt = world.get(mover, Position)!
        const close = !!slot && Math.hypot(carrierAt.x - slot.x, carrierAt.y - slot.y) < SLOT_REPLAN
        if (path && !close && path.slot === !!slot && Math.abs(path.goalX - goalX) <= 1 && Math.abs(path.goalY - goalY) <= 1) continue
        // Остальные дождутся своей очереди: сотня поисков пути за тик — заметная запинка.
        if (searches++ >= CHASE_SEARCHES || searchedTiles() - searchedBefore >= CHASE_TILES) continue
        // Идти надо не в саму цель, а на выстрел от неё: цель занята, а к зданию или в гущу врагов и не подойти.
        const near = slot ? 0 : Math.max(0, weapon.range - CHASE_MARGIN) + Math.max(target.width, target.height) / 2
        chasing.add(mover)
        withSearchQuota(CHASE_TILES - (searchedTiles() - searchedBefore), () => {
          orderMove(sim, mover, goalX, goalY, undefined, 0, near)
          const next = world.get(mover, Path)
          if (next && slot) next.slot = true
          // Не нашедший пути ищет всё реже; тот, чей поиск оборвала норма, — не в счёт: путь у него, может, и есть.
          if (next) armed.stuck = 0
          else if (!searchCut()) armed.stuck++
        })
      }
      continue
    }
    // В мёртвой зоне оружие молчит. Сам выбранную цель стрелок бросает: найдёт другую, подальше.
    if (distanceTo(target, self.x, self.y) < (weapon.minRange ?? 0)) {
      if (!armed.chase) armed.target = NONE
      continue
    }
    // На выстреле: гнавшийся встаёт. Идущий по приказу игрока не стреляет — цели у него нет; турель на едущем
    // по приказу носителе стреляет, но носитель не останавливает.
    // Патрульный встаёт драться и продолжит обход, когда бой кончится.
    // Гонящийся, который едет на своё место у цели, на выстрел попадает раньше, чем на место: он едет дальше, освобождая
    // подход задним, а турель на нём стреляет на ходу. Застрял в заторе по пути — стреляет, где стоит.
    const path = moving ? world.get(mover, Path) : undefined
    const carrierAt = world.get(mover, Position)!
    const toSlot = !!path?.slot && armed.chase && path.wait < SLOT_PATIENCE && Math.hypot(carrierAt.x - path.goalX - 0.5, carrierAt.y - path.goalY - 0.5) > SLOT_REACH
    if (toSlot && !mounted) continue
    if (moving && !toSlot && (!mounted || armed.chase || patrolling)) world.remove(mover, Path)

    const wanted = Math.atan2(target.y - self.y, target.x - self.x)
    const { body: aimer } = turner
    aimer.facing = turnToward(aimer.facing, wanted, turner.turn * time.step)
    if (armed.cooldown > 0 || Math.abs(wrap(wanted - aimer.facing)) > AIM) continue
    // Турель здания стреляет боеприпасами с его склада: кончились — молчит, пока не подвезут. Та, что на энергии, —
    // энергией зоны: при нехватке перезаряжается дольше, без энергии молчит.
    const power = powerFor(carrier)
    if (!power || !spend(carrier, weapon)) continue
    armed.cooldown = Math.max(1, Math.round(weapon.reload / time.step / power))

    // Выстрел — из точки перед стрелком.
    const fromX = self.x + Math.cos(aimer.facing) * turner.radius
    const fromY = self.y + Math.sin(aimer.facing) * turner.radius
    const shot = { weapon: weaponType, player: self.player, source: carrier, fromX, fromY, prevX: fromX, prevY: fromY }
    // Стена на линии огня принимает выстрел на себя: за ней укрытие. Турели зданий стоят выше стены и бьют поверх;
    // летающего стена не укрывает, и летающий стреляет поверх неё.
    const overWall = world.has(carrier, Building) || body.air || target.air || !STOPS_AT_WALL[weapon.shot] || !!weapon.lob
    const wall = overWall ? undefined : wallOnPath(sim, self.player, self.x, self.y, target.x, target.y, friends.get(self.player))
    const hitX = wall ? wall.x : target.x
    const hitY = wall ? wall.y : target.y
    if (weapon.speed) {
      // Ядру отпущено время до точки падения, наводящимся — пока не улетят слишком далеко.
      const reach = weapon.shot === 'shell' ? Math.hypot(hitX - fromX, hitY - fromY) : range * OVERFLY
      const life = Math.max(1, Math.ceil(reach / weapon.speed / time.step))
      const targetEntity = wall ? wall.wall : target.entity
      world.spawn(Position({ x: fromX, y: fromY }), Shot({ ...shot, target: targetEntity, toX: hitX, toY: hitY, blocked: wall !== undefined, air: !wall && target.air, life }))
      continue
    }
    // Луч стены не обходит: лазер бьёт в неё, а не в цель за ней. Разряду стена не помеха — он бьёт через неё.
    if (wall) {
      world.spawn(Position({ x: fromX, y: fromY }), Shot({ ...shot, target: wall.wall, toX: hitX, toY: hitY, life: BEAM_TICKS }))
      sim.traces.add({ kind: 'burn', x: hitX, y: hitY })
      const mark = marks.get(wall.wall)
      if (mark) hit(mark, weapon.damage, weapon, carrier, fromX, fromY)
      continue
    }
    // Лазер, разряд и огонь бьют сразу; разряд и огонь перескакивают дальше на ближайших врагов.
    let damage = weapon.damage
    let from = { x: fromX, y: fromY }
    let next: Mark | undefined = target
    const struck = new Set<Entity>()
    for (let jump = 0; next && jump <= (weapon.chain ?? 0); jump++) {
      const current: Mark = next
      struck.add(current.entity)
      world.spawn(
        Position({ x: from.x, y: from.y }),
        Shot({ ...shot, fromX: from.x, fromY: from.y, target: current.entity, toX: current.x, toY: current.y, life: BEAM_TICKS }),
      )
      // Разряд, перескочивший под куполом, всё равно пришёл снаружи: щит судит по стрелку.
      hit(current, damage, weapon, carrier, fromX, fromY)
      // Луч прожигает землю там, куда попал.
      if (!current.air) sim.traces.add({ kind: 'burn', x: current.x, y: current.y })
      damage *= CHAIN_DECAY
      from = current
      next = undefined
      let nearest = CHAIN_RANGE
      for (const mark of near(current.x, current.y, CHAIN_RANGE)) {
        if (struck.has(mark.entity) || !canHit(weapon, self.player, mark)) continue
        const distance = distanceTo(mark, current.x, current.y)
        if (distance >= nearest) continue
        nearest = distance
        next = mark
      }
    }
  }

  // Сдавшийся игрок взрывает своё: пришёл черёд — гибель, как от выстрела.
  const vanished: Entity[] = []
  for (const [entity, doomed] of world.query(Doomed)) {
    if (--doomed.left > 0) continue
    if (marks.has(entity)) dead.add(entity)
    else vanished.push(entity)
  }
  for (const entity of vanished) world.destroy(entity)

  // Аварии и ядерные удары бьют всех вокруг без разбора — своих и союзников тоже; щит их не держит. Под защитой
  // новичка урона нет.
  const bursts: Entity[] = []
  for (const [entity, burst, position] of world.query(Burst, Position)) {
    bursts.push(entity)
    for (const mark of near(position.x, position.y, burst.radius)) {
      if (dead.has(mark.entity) || safe.has(mark.player)) continue
      const distance = distanceTo(mark, position.x, position.y)
      if (distance > burst.radius) continue
      mark.health.hit = time.tick
      const share = distance <= burst.core ? 1 : (burst.radius - distance) / (burst.radius - burst.core)
      mark.health.value -= (burst.damage * share) / mark.hp
      if (mark.health.value <= 0) dead.add(mark.entity)
    }
    blasts.push({ x: position.x, y: position.y, size: burst.radius, ground: true })
  }
  for (const entity of bursts) world.destroy(entity)

  // Болото красных пустошей едкое: жжёт стоящих в нём наземных юнитов.
  const burn = sim.rules.toxicSwamp * time.step
  if (burn > 0) {
    for (const mark of marks.values()) {
      if (mark.air || mark.radius === 0 || dead.has(mark.entity)) continue
      const x = Math.floor(mark.x)
      const y = Math.floor(mark.y)
      if (terrainAt(sim.land, x, y) !== Terrain.Swamp || biomeAt(sim.land, x, y) !== Biome.RedWastes) continue
      // Мост над болотом не жжёт.
      if (isPaved(sim, 'road', x, y)) continue
      // Отметка попадания: пока стоит в болоте, не чинится и сам не залечивается.
      mark.health.hit = time.tick
      mark.health.value -= burn
      if (mark.health.value <= 0) dead.add(mark.entity)
    }
  }

  for (const entity of dead) {
    const mark = marks.get(entity)!
    blasts.push({ x: mark.x, y: mark.y, size: mark.radius ? mark.radius * 2 : Math.max(mark.width, mark.height) * 0.7, ground: !mark.air })
    // От техники остаётся остов, от пехоты — только гарь.
    const unit = world.get(entity, Unit)
    if (unit && !mark.air && unitSpec(unit.type).kind !== 'infantry') {
      sim.traces.add({ kind: 'wreck', type: unit.type, facing: unit.facing, x: mark.x, y: mark.y })
    }
    releaseHauler(sim, entity)
    // Реактор с топливом взрывается в следующем бою; топливо сгорает в аварии и на землю не падает.
    meltdown(sim, entity)
    // Груз со склада здания и из кузова остаётся на земле.
    dropCargo(sim, entity)
    world.destroy(entity)
  }
  for (const { x, y, size, ground } of blasts) {
    world.spawn(Position({ x, y }), Blast({ size, life: Math.round((0.25 + size * 0.2) / time.step) }))
    if (ground) sim.traces.add({ kind: 'scar', x, y, size })
    if (ground) breakSlabs(sim, x, y, size)
  }
}

/** Взрыв меньше этого — пуля: фундамент от неё не трескается. */
const SLAB_BLAST = 0.5

/** Наземный взрыв разбивает фундамент: плиты, чьи центры в его радиусе, и ту, на которую он пришёлся. Пуля — нет. */
export function breakSlabs(sim: Sim, x: number, y: number, size: number) {
  if (size < SLAB_BLAST) return
  const radius = size / 2
  const broken: Entity[] = []
  for (let tileY = Math.floor(y - radius); tileY <= Math.floor(y + radius); tileY++) {
    for (let tileX = Math.floor(x - radius); tileX <= Math.floor(x + radius); tileX++) {
      const hit = tileX === Math.floor(x) && tileY === Math.floor(y)
      if (!hit && Math.hypot(tileX + 0.5 - x, tileY + 0.5 - y) > radius) continue
      // Готовая плита уходит со слоя, а с ней и работа над ней: укладка или разбор.
      if (isPaved(sim, 'foundation', tileX, tileY)) sim.paving.lift(tileX, tileY)
      const entity = sim.paving.at(tileX, tileY)
      if (entity !== undefined && sim.world.get(entity, Pave)?.kind === 'foundation') broken.push(entity)
    }
  }
  for (const entity of broken) sim.world.destroy(entity)
}
