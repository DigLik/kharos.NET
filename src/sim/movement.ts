import type { DataOf, Entity, Time } from '../ecs'
import { tileKey } from '../map/terrain'
import { allied } from './allies'
import { onTurn, ownerOf, turnToward, wrap } from './common'
import { Converting, Path, Position, Unit } from './components'
import { SEARCH_LIMIT, isClear, searchCut, searchedTiles, withSearchQuota } from './path'
import type { Sim } from './sim'
import { JAM_TRIES, UNITS, canStand, fastestOf, flies, onFoot, orderMove, roadInSight, terrainSpeed } from './units'

/**
 * Сколько тиков юнит может не подбираться к точке пути ближе, чем подобрался, прежде чем проложить путь заново.
 * Считается не «сдвинулся ли», а «приблизился ли»: ёрзающий на месте в толпе тоже застрял.
 */
const WAIT_TICKS = 30
/**
 * Сколько тиков подряд ехавший напрямую может упираться в местность или здание — скользить вдоль или стоять, — прежде
 * чем попросить путь. Скольжение почти всегда чуть приближает к точке и сошло бы за продвижение: юнит тёрся бы о стену
 * до угла. Ждать WAIT_TICKS тут незачем — местность сама не разойдётся, в отличие от толпы.
 */
const SLIDE_TICKS = 4
/** На сколько тайлов надо подобраться к точке пути ближе прежнего, чтобы это считалось продвижением. */
const PROGRESS = 0.05
/** Сколько раз путь к одной точке прокладывается заново, прежде чем юнит сдастся и встанет. */
const MAX_TRIES = JAM_TRIES
/**
 * Ближе скольких тайлов к цели юнит, исчерпав попытки, сдаётся и встаёт: у цели тесно, и это значит «приехал».
 * Дальше он цель не бросает — приказ не отменяется оттого, что по дороге мешали, — а пробует снова раз в WAIT_TICKS.
 */
const GIVE_UP_REACH = 4
/**
 * Промежуточная точка пути засчитывается, когда юнит подошёл к ней на столько тайлов. Объезжая соседей, он редко
 * проходит точно через точку: требуй точности — кружил бы вокруг неё.
 */
const REACHED = 0.6
/**
 * Последняя точка пути занята другим юнитом: тогда юнит, подъехав к ней на столько тайлов сверх их радиусов,
 * считает, что приехал. Иначе двое с одной целью толкались бы за неё без конца.
 */
const SETTLE = 1
/** Во сколько раз круг разворота считается шире настоящего, когда решается, попадёт ли юнит в точку на ходу. */
const ORBIT_MARGIN = 1.3
/**
 * На сколько секунд вперёд юнит предвидит сближение с соседями. Дальше — рано сворачивать: сосед успеет уйти сам;
 * ближе — поздно: тяжёлая машина не успеет довернуть.
 */
const HORIZON = 1.2
/** Насколько, в радианах, курс может отличаться от направления на точку, чтобы считалось, что юнит едет прямо к ней. */
const AIMED = 0.05
/** Сколько тайлов зазора юнит держит до соседей сверх их радиусов, объезжая их. */
const GAP = 0.1
/** Насколько сильно предвиденное сближение уводит юнит в сторону, против желания ехать к точке. */
const AVOIDANCE = 2
/** Сколько раз за тик разводятся зашедшие друг в друга: одного прохода мало, когда расталкивают цепочкой. */
const SEPARATIONS = 2
/** Сколько юнитов за тик прокладывают путь заново; остальные ждут следующего тика. */
const LOST_SEARCHES = 8
/** Сколько тайлов за тик вместе осматривают перепрокладывающие путь; остальные ждут следующего тика. */
const LOST_TILES = 8000
/** Раз во сколько тиков идущий юнит смотрит, не показалась ли в обзоре дорога. */
const ROAD_LOOK_TICKS = 20
/**
 * На сколько тайлов дальше суммы радиусов юнит замечает соседей: самый быстрый проезжает за HORIZON больше тайла,
 * а встречный — столько же навстречу.
 */
const SENSE = 2.5
/** Сторона ячейки сетки, по которой ищутся соседи, в тайлах. Чем мельче, тем меньше в толпе лишних проверок. */
const CELL = 2
/** На сколько тайлов тело могло сдвинуться за тик от места, по которому строилась сетка: ходом и расталкиванием. */
const DRIFT = 1
/** Юниты крупнее этого радиуса — редкость: в сетку они не кладутся, их проверяет каждый. */
const LARGE = 0.9

interface Body {
  entity: Entity
  position: { x: number; y: number }
  radius: number
  /** Масса для расталкивания: крупный сдвигает мелкого сильнее, чем тот его. */
  mass: number
  /** Скорость за прошлый тик, тайлов в секунду: по ней соседи предвидят, где он будет. */
  vx: number
  vy: number
  /** Летающий: с наземными он не сталкивается. */
  air: boolean
  /** Пехота: ей можно к подножию обрыва, см. isWalkable. */
  foot: boolean
  /** Едет куда-то: стоящего своего едущий сдвигает с дороги. */
  moving: boolean
  /** Разворачивается в здание: с места его не сдвинуть. */
  rooted: boolean
  owner: number
}

/**
 * Раз в тик: двигает юниты по их путям. Юнит едет к точке пути, предвидя сближение с соседями по их скорости и
 * сворачивая заранее; союзник, что едет рядом в ту же сторону, ему не помеха. Поворачивает он не мгновенно, а со
 * своей скоростью поворота, и едет туда, куда смотрит, — поэтому на поворотах описывает дугу. В стену не упирается,
 * а скользит вдоль неё. Зашедших друг в друга после хода расталкивает: едущий сдвигает стоящего своего, тяжёлый —
 * лёгкого; стоящего чужого не сдвинуть. Летающие летят по прямой над всем и мешают только друг другу.
 */
export function moveUnits(sim: Sim, time: Time) {
  const { world } = sim
  const cells = new Map<number, Body[]>()
  const large: Body[] = []
  const bodies = new Map<Entity, Body>()
  // Клиент рисует юнит между прошлым и нынешним положением, поэтому прошлое запоминается у всех, даже у стоящих.
  for (const [entity, position, unit] of world.query(Position, Unit)) {
    const { radius, speed } = UNITS[unit.type]
    // Скорость за прошлый тик — по тому, где юнит был тогда. Телепорт (выселение из-под здания) скоростью не считается.
    let vx = (position.x - unit.prevX) / time.step
    let vy = (position.y - unit.prevY) / time.step
    if (Math.hypot(vx, vy) > speed * 1.5) vx = vy = 0
    unit.prevX = position.x
    unit.prevY = position.y
    unit.prevFacing = unit.facing
    const body: Body = {
      entity, position, radius, mass: radius * radius, vx, vy, air: flies(unit.type), foot: onFoot(unit.type),
      moving: world.has(entity, Path), rooted: world.has(entity, Converting), owner: ownerOf(sim, entity),
    }
    bodies.set(entity, body)
    if (radius > LARGE) {
      large.push(body)
      continue
    }
    // Сетка строится по местам на начало тика; за тик юнит сдвигается куда меньше, чем на ячейку.
    const key = tileKey(Math.floor(position.x / CELL), Math.floor(position.y / CELL))
    const cell = cells.get(key)
    if (cell) cell.push(body)
    else cells.set(key, [body])
  }

  /** Соседи тела, чьи края ближе reach тайлов к его краю. Массив один на все поиски — обходить его сразу. */
  const neighbours: Body[] = []
  const gather = (self: Body, reach: number) => {
    neighbours.length = 0
    const { x, y } = self.position
    const check = (other: Body) => {
      if (other === self || other.air !== self.air) return
      const far = self.radius + other.radius + reach
      const offsetX = other.position.x - x
      const offsetY = other.position.y - y
      if (offsetX * offsetX + offsetY * offsetY < far * far) neighbours.push(other)
    }
    // Ячейки — те, что накрывает круг досягаемости, с запасом на то, что за тик тела сдвинулись от мест, по которым
    // строилась сетка.
    const far = self.radius + LARGE + reach + DRIFT
    const right = Math.floor((x + far) / CELL)
    const bottom = Math.floor((y + far) / CELL)
    for (let cy = Math.floor((y - far) / CELL); cy <= bottom; cy++) {
      for (let cx = Math.floor((x - far) / CELL); cx <= right; cx++) {
        const cell = cells.get(tileKey(cx, cy))
        if (cell) for (const other of cell) check(other)
      }
    }
    for (const other of large) check(other)
  }

  /**
   * Куда юниту ехать, чтобы не столкнуться: направление к точке (dirX, dirY) со скоростью speed, отведённое от соседей,
   * с которыми он сблизится за HORIZON. Сближение считается по скорости соседа: идущий впереди в ту же сторону не
   * мешает, встречного объезжают заранее. Лоб в лоб оба берут вправо — и расходятся, а не зеркалят друг друга.
   */
  const steer = (self: Body, dirX: number, dirY: number, speed: number) => {
    gather(self, SENSE)
    let awayX = 0
    let awayY = 0
    const ownX = dirX * speed
    const ownY = dirY * speed
    for (const other of neighbours) {
      const offsetX = other.position.x - self.position.x
      const offsetY = other.position.y - self.position.y
      // Скорость юнита относительно соседа: как сосед видит его приближение.
      const relativeX = ownX - other.vx
      const relativeY = ownY - other.vy
      const closing = relativeX * relativeX + relativeY * relativeY
      const reach = self.radius + other.radius + GAP
      // Когда они будут ближе всего — и насколько.
      const when = closing > 1e-6 ? (offsetX * relativeX + offsetY * relativeY) / closing : 0
      // Расходятся — не мешает; зашедших друг в друга разведёт расталкивание.
      if (when <= 0 || when > HORIZON) continue
      const missX = offsetX - relativeX * when
      const missY = offsetY - relativeY * when
      const miss = Math.hypot(missX, missY)
      if (miss >= reach) continue
      // Уходит от того места, где был бы сосед; прямо на него — вправо от сближения.
      const length = Math.sqrt(closing)
      const sideX = miss > 1e-3 ? -missX / miss : -relativeY / length
      const sideY = miss > 1e-3 ? -missY / miss : relativeX / length
      const urgency = (1 - when / HORIZON) * ((reach - miss) / reach)
      awayX += sideX * urgency
      awayY += sideY * urgency
    }
    return Math.atan2(dirY + awayY * AVOIDANCE, dirX + awayX * AVOIDANCE)
  }

  /** Можно ли телу встать в точку: местность пускает, или оно уже внутри здания и выходит из него. */
  const open = (body: Body, x: number, y: number, inside: boolean) => inside || canStand(sim, body.air, Math.floor(x), Math.floor(y), body.foot)

  // Ход. Пути меняются после всех проходов: во время них состав мира трогать нельзя.
  const movers: { entity: Entity; body: Body; path: DataOf<[typeof Path]>[0]; walled: boolean; slid: boolean; inside: boolean }[] = []
  /** Увидевшие дорогу на ходу: перестраивают путь с ней. */
  const roadward: { entity: Entity; x: number; y: number; near: number }[] = []
  for (const [entity, position, unit, path] of world.query(Position, Unit, Path)) {
    const body = bodies.get(entity)!
    const { points } = path
    const { speed, turn, radius } = UNITS[unit.type]
    // Путь проложен без дороги, а она показалась в обзоре — юнит прикинет путь заново. Смотрит не каждый тик.
    if (!path.roads && fastestOf(unit.type) > 1 && onTurn(time, entity, ROAD_LOOK_TICKS) && roadInSight(sim, entity)) {
      roadward.push({ entity, x: path.goalX, y: path.goalY, near: path.near })
    }
    // Юнит, под которым выросло здание, выходит из него: внутри здания тайлы ему не преграда.
    const inside = !canStand(sim, body.air, Math.floor(position.x), Math.floor(position.y), body.foot)
    const mover = { entity, body, path, walled: false, slid: false, inside }
    movers.push(mover)
    if (body.rooted) continue
    const dx = points[0] - position.x
    const dy = points[1] - position.y
    const distance = Math.hypot(dx, dy)
    const wanted = distance ? Math.atan2(dy, dx) : unit.facing
    const last = points.length === 2
    // К последней точке юнит подъезжает прямо: объезжая соседей у самой цели, он вертелся бы вокруг неё.
    const heading = distance && !(last && distance < radius + SETTLE) ? steer(body, dx / distance, dy / distance, speed) : wanted

    unit.facing = turnToward(unit.facing, heading, turn * time.step)
    // Чем сильнее юнит смотрит в сторону, тем медленнее едет; развернувшись больше чем на четверть оборота, крутится на месте.
    const askew = Math.abs(wrap(heading - unit.facing))
    // Песок и болото замедляют наземных: см. terrainSpeed.
    let move = speed * terrainSpeed(sim, unit.type, Math.floor(position.x), Math.floor(position.y)) * time.step * Math.max(0, Math.cos(askew))
    // Точка внутри круга, который юнит описывает на полном ходу: в неё не попасть, сколько ни кружи.
    // Тогда он доворачивает на месте. Запас — на то, что поворот идёт шагами. Объезжающий соседа едет не к точке,
    // а мимо него: стой он доворачивать к точке, от которой его отводят, — не тронулся бы никогда.
    const off = Math.abs(wrap(wanted - unit.facing))
    const avoiding = Math.abs(wrap(heading - wanted)) > AIMED
    if (!avoiding && distance < ((2 * speed) / turn) * Math.sin(Math.min(off, Math.PI / 2)) * ORBIT_MARGIN) move = 0
    if (!move) continue
    // Глядя точно на точку пути, юнит приходит в неё ровно, без перелёта.
    if (unit.facing === wanted && move >= distance) {
      if (open(body, points[0], points[1], inside)) {
        position.x = points[0]
        position.y = points[1]
      } else mover.walled = true
      continue
    }
    const stepX = Math.cos(unit.facing) * move
    const stepY = Math.sin(unit.facing) * move
    if (open(body, position.x + stepX, position.y + stepY, inside)) {
      position.x += stepX
      position.y += stepY
      continue
    }
    // Впереди непроходимое — скользит вдоль него: по той оси, по которой можно и которая ближе к точке.
    const alongX = open(body, position.x + stepX, position.y, inside) ? stepX * dx : -Infinity
    const alongY = open(body, position.x, position.y + stepY, inside) ? stepY * dy : -Infinity
    if (alongX > 0 && alongX >= alongY) position.x += stepX
    else if (alongY > 0) position.y += stepY
    else mover.walled = true
    mover.slid = true
  }

  // Расталкивание: зашедших друг в друга разводят. Начинают те, кто ехал, — стоящие, которых сдвинули, продолжают
  // цепочку. Стоящих вдали никто не трогает: армия без дела ничего не стоит.
  const pushed = new Set<Body>(movers.map((mover) => mover.body))
  /** Сдвигается ли тело, когда в него въехал by: своё — да, стоящее чужое — нет, как и разворачивающееся. */
  const yields = (body: Body, by: Body) => !body.rooted && (body.moving || body.owner === by.owner || allied(sim, body.owner, by.owner))
  for (let pass = 0, moved = true; pass < SEPARATIONS && moved; pass++) {
    moved = false
    for (const body of [...pushed]) {
      gather(body, 0)
      for (const other of neighbours) {
        let normalX = other.position.x - body.position.x
        let normalY = other.position.y - body.position.y
        const distance = Math.hypot(normalX, normalY)
        const overlap = body.radius + other.radius - distance
        if (overlap <= 0) continue
        if (distance > 1e-6) {
          normalX /= distance
          normalY /= distance
        } else {
          // Стоят точка в точку: расходятся в сторону, заданную номерами, — одинаково при каждом прогоне.
          const angle = (body.entity * 7 + other.entity * 13) % 360
          normalX = Math.cos(angle)
          normalY = Math.sin(angle)
        }
        // Доли сдвига: едущий стоящего своего сдвигает целиком, в остальных случаях — по массе.
        const bodyFree = yields(body, other)
        const otherFree = yields(other, body)
        let bodyShare = !bodyFree ? 0 : !otherFree ? 1 : other.mass / (body.mass + other.mass)
        if (bodyFree && otherFree && body.moving !== other.moving) bodyShare = body.moving ? 0 : 1
        let otherShare = otherFree ? 1 - bodyShare : 0
        const otherX = other.position.x + normalX * overlap * otherShare
        const otherY = other.position.y + normalY * overlap * otherShare
        // Не пускает местность — сдвигается другой.
        if (otherShare && !open(other, otherX, otherY, false)) {
          otherShare = 0
          bodyShare = bodyFree ? 1 : 0
        }
        const bodyX = body.position.x - normalX * overlap * bodyShare
        const bodyY = body.position.y - normalY * overlap * bodyShare
        if (bodyShare && open(body, bodyX, bodyY, false)) {
          body.position.x = bodyX
          body.position.y = bodyY
          moved = true
        }
        if (otherShare) {
          moved = true
          other.position.x = otherX
          other.position.y = otherY
          pushed.add(other)
        }
      }
    }
  }

  const stopped: Entity[] = []
  const lost: { entity: Entity; x: number; y: number; tries: number; near: number }[] = []
  /**
   * Дошедшие до конца пути, не дойдя до цели: ищут путь дальше. blocked — тайл последней точки, в которую не встать:
   * её занял сосед.
   */
  const further: { entity: Entity; x: number; y: number; tries: number; near: number; blocked?: [number, number] }[] = []
  /** Юнит не может идти дальше: прокладывает путь заново или, если уже пробовал, встаёт. */
  const giveUp = (entity: Entity, path: { goalX: number; goalY: number; tries: number; near: number; wait: number; direct: boolean; stuck: boolean }, walled: boolean) => {
    const position = world.get(entity, Position)!
    const close = Math.hypot(position.x - path.goalX - 0.5, position.y - path.goalY - 0.5) <= path.near + GIVE_UP_REACH
    // Ехал напрямую: упёрся в местность или здание — путь ему проложит planPaths, по очереди с другими упёршимися.
    // Мешают только юниты — толпа обычно разъезжается сама, искать путь незачем. Не разъехалась за MAX_TRIES
    // ожиданий — это затор: передние упёрлись в местность, а задние за ними её не видят и давят дальше. Тогда путь
    // нужен и ему.
    if (path.direct) {
      path.wait = 0
      if (walled || ++path.tries >= MAX_TRIES) path.stuck = true
    }
    else if (path.tries >= MAX_TRIES && close) stopped.push(entity)
    // Сверх нормы — юнит просто ждёт дальше и попробует в следующий тик.
    else if (lost.length < LOST_SEARCHES) lost.push({ entity, x: path.goalX, y: path.goalY, tries: Math.min(path.tries + 1, MAX_TRIES), near: path.near })
  }
  /** Кто стоит в точке (x, y), не давая юниту в неё встать: тот, чей край ближе края юнита. */
  const occupant = (self: Body, x: number, y: number) => {
    gather(self, SENSE)
    return neighbours.find((other) => Math.hypot(other.position.x - x, other.position.y - y) < self.radius + other.radius)
  }

  for (const { entity, body, path, walled, slid } of movers) {
    const { points } = path
    const { position, radius, air } = body
    const left = Math.hypot(points[0] - position.x, points[1] - position.y)
    const last = points.length === 2
    const blocker = left < radius + LARGE + SETTLE ? occupant(body, points[0], points[1]) : undefined
    // Промежуточную точку занял сосед, а юнит уже рядом: точка пропускается — он едет к следующей. Иначе крутился бы
    // у занятой точки, которую не засчитать. Пропускается, только если следующая видна отсюда по прямой: путь спрямлён
    // от точки к точке, и напрямик мимо пропущенной может оказаться скала.
    if (blocker && !last && left < radius + blocker.radius + SETTLE) {
      if (air || isClear((x, y) => canStand(sim, air, x, y, body.foot), position.x, position.y, points[2], points[3])) {
        points.splice(0, 2)
        path.best = -1
        path.wait = 0
        continue
      }
    }
    // Последнюю точку занял тот, кого не сдвинуть, — чужой или разворачивающийся, — или свой, что едет туда же: ближе
    // не подъехать, юнит встаёт, где стоит. Иначе толкался бы за неё без конца. Стоящего своего он сдвинет плечом.
    const settled = blocker && last && left < radius + blocker.radius + SETTLE && (!yields(blocker, body) || (blocker.moving && blocker.entity < entity))
    if (left === 0 || settled || (!last && left < REACHED)) {
      points.splice(0, 2)
      path.tries = 0
      path.best = -1
      path.wait = 0
      if (points.length) continue
      // Путь к далёкой цели кончается раньше неё: тогда это не приезд, и юнит ищет путь дальше отсюда.
      const reached = path.near
        ? (position.x - path.goalX - 0.5) ** 2 + (position.y - path.goalY - 0.5) ** 2 <= (path.near + 1) ** 2
        : (Math.floor(position.x) === path.goalX && Math.floor(position.y) === path.goalY) || (!!settled && Math.hypot(position.x - path.goalX - 0.5, position.y - path.goalY - 0.5) < radius + SETTLE + 1)
      if (reached || air) stopped.push(entity)
      else {
        // Вернуть точку: пока путь ищется (а норма тика могла кончиться), юнит едет к ней же.
        points.push(position.x, position.y)
        further.push({ entity, x: path.goalX, y: path.goalY, tries: 0, near: path.near, blocked: settled ? [Math.floor(blocker!.position.x), Math.floor(blocker!.position.y)] : undefined })
      }
      continue
    }
    // Ехал напрямую и упёрся в местность или здание: путь нужен сразу, продвижение вдоль стены не в счёт.
    if (path.direct && (slid || walled)) {
      if (++path.wait >= SLIDE_TICKS) {
        path.stuck = true
        path.wait = 0
        path.best = -1
      }
      continue
    }
    // Продвижение: подобрался к точке ближе прежнего — ждать нечего. Иначе считает тики, а не дождавшись — ищет путь.
    if (path.best < 0 || left < path.best - PROGRESS) {
      path.best = left
      path.wait = 0
    } else if (++path.wait >= WAIT_TICKS) {
      giveUp(entity, path, walled)
      path.wait = 0
      path.best = -1
    }
  }

  for (const entity of stopped) world.remove(entity, Path)
  const searchedBefore = searchedTiles()
  // Новые поиски начинаются, пока не вышла норма; а все вместе они не осмотрят больше одного обычного поиска сверх неё:
  // первый в тике получает полный поиск, а долгий повторный (см. orderMove) норму не перешагнёт.
  withSearchQuota(LOST_TILES + SEARCH_LIMIT, () => {
    for (const { entity, x, y, near, blocked } of further) {
      // Поиски сверх нормы тика отложены: юнит постоит у последней точки и поищет в следующий тик.
      if (searchedTiles() - searchedBefore >= LOST_TILES) continue
      const position = world.get(entity, Position)!
      const fromX = Math.floor(position.x)
      const fromY = Math.floor(position.y)
      orderMove(sim, entity, x, y, undefined, 0, near)
      // Ближе к цели не подойти — встаёт: иначе искал бы на месте каждый тик. Так и когда новый путь кончается там же,
      // где стоит сосед, не дававший встать в прежний конец: юнит упирался бы в него и искал бы путь каждый тик.
      const next = world.get(entity, Path)
      const end = next?.points.length ? [Math.floor(next.points.at(-2)!), Math.floor(next.points.at(-1)!)] : undefined
      if (end && ((end[0] === fromX && end[1] === fromY) || (end[0] === blocked?.[0] && end[1] === blocked[1]))) world.remove(entity, Path)
    }
    for (const { entity, x, y, near } of roadward) {
      if (searchedTiles() - searchedBefore >= LOST_TILES) break
      if (!world.has(entity, Path)) continue
      // Новый путь помнит, что дорогу уже учёл, и второй раз её не ищет.
      orderMove(sim, entity, x, y, undefined, 0, near)
    }
    for (const { entity, x, y, tries, near } of lost) {
      // Не уложившиеся в норму ждут дальше: их путь цел, и после следующего ожидания они попробуют снова.
      if (searchedTiles() - searchedBefore >= LOST_TILES) break
      const before = world.get(entity, Path)?.points
      orderMove(sim, entity, x, y, undefined, tries, near)
      // Нового пути нет — не из-за нормы, а потому, что юнита обступили со всех сторон. Обступили те, кто уже стоит
      // без приказа, — значит, он упёрся в доехавших и сам доехал: встаёт, исчерпав попытки. С приказом он не отвечал
      // бы на огонь. Обступили такие же застрявшие — затор разойдётся: юнит ждёт до следующей попытки.
      const path = world.get(entity, Path)
      if (!path || path.points !== before || searchCut()) continue
      const body = bodies.get(entity)!
      gather(body, GAP)
      if (tries >= MAX_TRIES && neighbours.length > 0 && neighbours.every((other) => !other.moving)) world.remove(entity, Path)
    }
  })
}

/** Сколько тайлов за тик осматривают поиски пути упёршихся юнитов, ехавших напрямую. */
const PLAN_TILES = 20000

/**
 * Раз в тик, до движения: прокладывает пути юнитам, которые ехали к цели напрямую и упёрлись (Path.stuck), пока
 * не кончится норма тика. Первый в очереди получает путь всегда. Кто не дождался, ждёт следующего тика.
 */
export function planPaths(sim: Sim) {
  const { world } = sim
  const waiting: Entity[] = []
  for (const [entity, path] of world.query(Path)) if (path.direct && path.stuck) waiting.push(entity)
  const searchedBefore = searchedTiles()
  withSearchQuota(PLAN_TILES, () => {
    for (const entity of waiting) {
      const path = world.get(entity, Path)
      if (!path?.stuck) continue
      orderMove(sim, entity, path.goalX, path.goalY, undefined, 0, path.near)
      if (searchedTiles() - searchedBefore >= PLAN_TILES) break
    }
  })
}
