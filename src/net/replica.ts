import type { EditOp } from '../sim/editOps'
import type { CommandInfo } from './chatCommands'
import { World, type Component, type Entity, type Time } from '../ecs'
import { applyEdits, areaOf, createLand, loadLand, saveLand } from '../map/terrain'
import { inflate } from '../save/file'
import { createDeposits, loadDeposits } from '../sim/deposits'
import { createLandMemory } from '../sim/landMemory'
import { DEFAULT_RULES, boundsOf, type Command, type Sim } from '../sim'
import { createOccupancy } from '../sim/buildings'
import { createPaving, loadPaving, savePaving } from '../sim/paved'
import { createPaveMemory } from '../sim/paveMemory'
import { createBuildMemory } from '../sim/buildMemory'
import { createReceivedTraces } from '../sim/traces'
import { createVision } from '../sim/vision'
import { BUILDINGS, type BuildingType } from '../sim/buildings'
import { Armed, Attached, Blast, Ghost, Position, SAVED, Shot, Turret, Unit } from '../sim/components'
import { placeTurret } from '../sim/turrets'
import { wrap } from '../sim/common'
import { MOTION_FIELDS } from './codec'
import { SAVE_VERSION, type SimSave } from '../sim/sim'
import type { ChatLine, ClientMessage, PlayerInfo, PlayerView, ServerMessage, ViewBox } from './protocol'
import { t, textOf } from '../i18n'

/** Из чего собираются призраки: сохраняемое и метка призрака. */
const REMEMBERED = [...SAVED, Ghost]
/**
 * Поля компонентов, которые приходят не в нём самом: поворот юнита — движением, см. codec.ts; прошлое место копия
 * помнит сама; возраст выстрела и взрыва считает по тику рождения, см. wire.ts.
 */
const KEPT: Record<string, string[]> = { Unit: ['facing', 'prevX', 'prevY', 'prevFacing'], Shot: ['prevX', 'prevY', 'age'], Blast: ['age'] }
/** Компоненты, которые присылает хост, по имени. */
const BY_KEY = new Map<string, Component<any>>(SAVED.map((component) => [component.key, component]))

type Delta = Extract<ServerMessage, { type: 'delta' }>

/** Дольше скольких тиков копия юнита к присланному месту не ведёт: секунда отставания — уже заметно. */
const GLIDE_MOST = 20

/** Сколько последних сообщений чата держит клиент. */
const CHAT_KEPT = 50

/** Копия чужой симуляции: выглядит как Sim, но сама игру не считает. */
export interface Replica extends Sim {
  /** Сообщение сервера. Мир из него применится в начале следующего кадра. */
  receive(message: ServerMessage): void
  /** Соединение пропало: следующий advance() бросит ошибку с этой причиной. */
  fail(reason: string): void
  /** Проигравший просит хост начать заново: новый стартовый набор в новом месте. */
  respawn(): void
  /** Растёт, когда хост начинает мир заново: по нему клиент сбрасывает выделение и камеру. */
  readonly generation: number
  /** Кто играет на хосте, как его прислал хост; пусто, пока не прислал. */
  readonly players: readonly PlayerInfo[]
  /** Чат: последние сообщения по порядку, at — когда клиент его получил, в мс по performance.now. */
  readonly chat: readonly (ChatLine & { at: number })[]
  /** Команды чата, которые хост разрешает этому игроку, и администратор ли он, см. chatCommands.ts. */
  readonly commands: readonly CommandInfo[]
  readonly admin: boolean
  /** Игрок в редакторе: мир весь, без тумана, и правки уходят хосту, см. edit. */
  readonly editor: boolean
  /** Последнее приветствие было тем же миром заново: камера остаётся, где была. */
  readonly kept: boolean
  /** Камеры других игроков: приходят только в редакторе. */
  readonly views: readonly PlayerView[]
  /** Сообщает хосту, где камера вкладки. */
  view(box: ViewBox): void
  /** Шлёт хосту правку редактора. */
  edit(edit: EditOp): void
  /** Написать в чат. */
  say(text: string): void
}

/**
 * Копия симуляции, которую считает сервер. Клиент читает её так же, как локальную: тот же мир, та же местность,
 * та же занятость тайлов. Разница в том, что send() уходит в сеть, а advance() не считает тики, а применяет
 * присланный мир и ведёт alpha, чтобы движение между тиками оставалось плавным.
 */
export function createReplica(welcome: Extract<ServerMessage, { type: 'welcome' }>, send: (text: string) => void, close: () => void): Replica {
  const world = new World()
  const time: Time = { tick: 0, step: welcome.step, elapsed: 0, delta: 0, alpha: 0 }
  /** Турели, которым место на носителе нашлось впервые: компонент добавляется после обхода. */
  const deferred: [Entity, { x: number; y: number }][] = []
  /** Пришедшие изменения мира, ещё не применённые: применяются все и по порядку. */
  let pending: Delta[] = []
  /** Что хост прислал и что игрок сейчас видит: сущность целиком, как её собрали из изменений. */
  const mirror = new Map<number, Record<string, object>>()
  /**
   * Юниты, которых хост шлёт не каждый тик (см. очередь рассылки в host.ts): куда каждый едет и сколько тиков ему
   * осталось. Место приходит раз в несколько тиков вместе со сроком — сколько тиков оно набегало; копия ведёт юнита
   * к нему столько же тиков, шаг за шагом, и общий alpha сглаживает каждый шаг, как у остальных. Юнит отстаёт от
   * хоста на свой срок, зато едет ровно, а не стоит и прыгает.
   */
  const glides = new Map<Entity, { x: number; y: number; facing: number; left: number }>()
  let failure: string | null = null
  let player = welcome.player
  /** Чужие здания и месторождения, увиденные раньше: какими их видели последний раз. */
  const memory = new Map<number, Record<string, object>>()
  /** Снесено ли запомненное: его место сейчас в обзоре, а хост его не прислал. */
  const gone = (data: Record<string, object>) => {
    const at = data.Position as { x: number; y: number } | undefined
    if (!at) return true
    const type = (data.Building as { type: BuildingType } | undefined)?.type
    const { width, height } = type ? BUILDINGS[type] : { width: 1, height: 1 }
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) if (replica.vision.sees(player, at.x + x, at.y + y)) return true
    return false
  }

  const occupancy = createOccupancy(world)
  const paving = createPaving(world)
  /** Какую карту ждём: растёт с каждым приветствием, и карта, пришедшая к прошлому, не ляжет на новый мир. */
  let landWait = 0
  /** Правки, пришедшие до карты: она снята раньше них и ляжет поверх, так что их накладываем после. null — карта пришла. */
  let earlyEdits: number[][] | null = []
  const receiveLand = async (data: Uint8Array) => {
    const wait = landWait
    const bytes = await inflate(data)
    if (wait !== landWait || !replica.options) return
    replica.land = loadLand(replica.options.generator, bytes)
    for (const edits of earlyEdits ?? []) applyEdits(replica.land, edits)
    earlyEdits = null
  }
  /** Мир по приветствию: при первом подключении и когда хост начинает мир заново. */
  const meet = ({ options, step, player: own, editor, keep }: Extract<ServerMessage, { type: 'welcome' }>) => {
    player = own
    replica.editor = !!editor
    replica.views = []
    replica.kept = !!keep
    memory.clear()
    replica.options = options
    replica.bounds = boundsOf(options.size, options.height)
    // Пока не пришла карта мира, местность — по генератору; правки, пришедшие раньше карты, ждут её.
    replica.land = createLand(options.generator, areaOf(replica.bounds))
    replica.deposits.cells.clear()
    // Покрытие придёт следом за приветствием: прежний мир его не знает.
    paving.clear()
    // Пусто в слое потому, что он в пути, а не потому, что игрок ничего не знает: уведомления по нему не судят.
    replica.deposits.pending = true
    replica.deposits.revision++
    landWait++
    earlyEdits = []
    replica.rules = { ...DEFAULT_RULES, ...options.rules }
    // Разведанное в новом мире ничего не значит.
    replica.vision = createVision(world, replica.bounds, () => time.tick, options.fog !== false)
    replica.traces = createReceivedTraces(world, () => time.tick, step, (x, y) => replica.vision.sees(player, x, y))
    world.clear()
    world.flush()
    mirror.clear()
    glides.clear()
    Object.assign(time, { tick: 0, step, elapsed: 0, delta: 0, alpha: 0 })
    pending = []
  }

  /** Накладывает изменения на мир на месте: незатронутые сущности и их наблюдатели не трогаются. */
  const apply = ({ set, unset, remove, motion }: Delta) => {
    for (const id of remove) {
      mirror.delete(id)
      // Запомненное здание остаётся и станет призраком, остальное исчезает.
      if (!memory.has(id)) world.destroy(id as Entity)
    }
    for (const [id, data] of set) {
      const entity = id as Entity
      let entry = mirror.get(id)
      if (!entry) {
        // Новая для игрока сущность приходит целиком — в том числе вместо своего призрака.
        world.destroy(entity)
        entry = { ...data }
        mirror.set(id, entry)
        world.insert(id, data, BY_KEY)
      } else {
        Object.assign(entry, data)
        for (const key in data) {
          const component = BY_KEY.get(key)
          if (!component) continue
          const current = world.get(entity, component)
          if (!current) {
            world.add(entity, component(data[key]))
            continue
          }
          // Компонент приходит целиком: поля, которых в нём больше нет, обнуляются. Кроме тех, что приходят иначе.
          const patch: Record<string, unknown> = { ...data[key] }
          const kept = KEPT[key]
          for (const field in current) if (!(field in patch) && !kept?.includes(field)) patch[field] = undefined
          world.set(entity, component, patch)
        }
      }
      const owner = (entry.Owner as { player: number } | undefined)?.player
      if (('Building' in entry || 'Deposit' in entry) && owner !== player) memory.set(id, entry)
    }
    // Движение — после set: новая сущность к этому времени уже есть.
    for (let i = 0; i < motion.length; i += MOTION_FIELDS) {
      const id = motion[i]
      const entity = id as Entity
      const x = motion[i + 1]
      const y = motion[i + 2]
      const facing = motion[i + 3]
      const span = motion[i + 4]
      if (!world.alive(entity)) world.insert(id, {}, BY_KEY)
      let entry = mirror.get(id)
      if (!entry) mirror.set(id, (entry = {}))
      entry.Position = { x, y }
      if (!Number.isNaN(facing) && entry.Unit) entry.Unit = { ...entry.Unit, facing }
      // Юнит, которого хост придержал на несколько тиков, едет к новому месту столько же, а не прыгает, см. glides.
      if (span > 1 && world.has(entity, Position) && world.has(entity, Unit)) {
        glides.set(entity, { x, y, facing, left: Math.min(span, GLIDE_MOST) })
        continue
      }
      glides.delete(entity)
      if (world.has(entity, Position)) world.set(entity, Position, { x, y })
      else world.add(entity, Position({ x, y }))
      if (!Number.isNaN(facing) && world.has(entity, Unit)) world.set(entity, Unit, { facing })
    }
    for (const [id, keys] of unset) {
      const entry = mirror.get(id)
      for (const key of keys) {
        if (entry) delete entry[key]
        const component = BY_KEY.get(key)
        if (component) world.remove(id as Entity, component)
      }
    }
  }
  const replica = {
    options: undefined as unknown as Replica['options'],
    bounds: undefined as unknown as Replica['bounds'],
    land: undefined as unknown as Replica['land'],
    // Знание о правках карты держит хост: клиенту приходит уже то, что он знает.
    landMemory: createLandMemory(),
    // События мира — для модов, а они живут на хосте.
    events: [],
    // Месторождения присылает хост слоем целиком; пока не прислал — их нет.
    deposits: createDeposits(undefined, true),
    rules: undefined as unknown as Replica['rules'],
    // Кто в сети, клиенту знать незачем: доход считает хост.
    online: null,
    generation: 0,
    players: [] as PlayerInfo[],
    chat: [] as (ChatLine & { at: number })[],
    commands: [] as CommandInfo[],
    admin: false,
    editor: false,
    views: [] as PlayerView[],
    kept: false,
    world,
    occupancy,
    paving,
    paveMemory: createPaveMemory(),
    // Чужие здания клиент помнит сам, см. memory; что он видел до входа, присылает хост.
    buildMemory: createBuildMemory(),
    vision: undefined as unknown as Replica['vision'],
    traces: undefined as unknown as ReturnType<typeof createReceivedTraces>,
    time,
    respawn() {
      send(JSON.stringify({ type: 'respawn' } satisfies ClientMessage))
    },
    view(box: ViewBox) {
      send(JSON.stringify({ type: 'view', ...box } satisfies ClientMessage))
    },
    edit(edit: EditOp) {
      send(JSON.stringify({ type: 'edit', edit } satisfies ClientMessage))
    },
    say(text: string) {
      send(JSON.stringify({ type: 'chat', text } satisfies ClientMessage))
    },
    send(_player: number, command: Command) {
      // Игрока сервер знает по соединению; номеру из сообщения он бы и не поверил.
      send(JSON.stringify({ type: 'command', command } satisfies ClientMessage))
    },
    advance(seconds: number) {
      if (failure !== null) throw new Error(failure)
      time.delta = seconds
      if (!pending.length) {
        // Следующий тик опаздывает: юниты доезжают до последнего известного места и ждут.
        time.alpha = Math.min(1, time.alpha + seconds / time.step)
        return 0
      }
      // Изменения идут одно за другим, поэтому применяются все пришедшие.
      const last = pending[pending.length - 1]!
      const ticks = last.tick - time.tick
      // Прошлое место и поворот юнитов хост не шлёт: это то, что было до пришедших изменений.
      const before = new Map<Entity, [number, number, number]>()
      for (const [entity, position, unit] of world.query(Position, Unit)) before.set(entity, [position.x, position.y, unit.facing])
      for (const [entity, position, turret] of world.query(Position, Turret)) before.set(entity, [position.x, position.y, turret.angle])
      for (const [entity, position] of world.query(Position, Shot)) before.set(entity, [position.x, position.y, 0])
      for (const delta of pending) apply(delta)
      for (const [entity, glide] of glides) {
        const position = world.get(entity, Position)
        const unit = world.get(entity, Unit)
        if (!position || !unit) {
          glides.delete(entity)
          continue
        }
        // Доля оставшегося пути, которую юнит проходит за пришедшие тики.
        const share = Math.min(1, ticks / glide.left)
        glide.left -= ticks
        world.set(entity, Position, { x: position.x + (glide.x - position.x) * share, y: position.y + (glide.y - position.y) * share })
        if (!Number.isNaN(glide.facing)) world.set(entity, Unit, { facing: share >= 1 ? glide.facing : wrap(unit.facing + wrap(glide.facing - unit.facing) * share) })
        if (glide.left <= 0) glides.delete(entity)
      }
      for (const [entity, position, unit] of world.query(Position, Unit)) {
        const [x, y, facing] = before.get(entity) ?? [position.x, position.y, unit.facing]
        unit.prevX = x
        unit.prevY = y
        unit.prevFacing = facing
      }
      // Место турели на носителе хост не шлёт: она встаёт на него здесь, после того как носитель сдвинулся.
      for (const [entity, attached, turret] of world.query(Attached, Turret)) {
        let position = world.get(entity, Position)
        if (!position) {
          position = { x: 0, y: 0 }
          if (!placeTurret(world, attached, position)) continue
          // Состав мира меняется после обхода: новое место ставится там же, где и у остальных, и сразу без сглаживания.
          deferred.push([entity, position])
          continue
        }
        placeTurret(world, attached, position)
        const [x, y, angle] = before.get(entity) ?? [position.x, position.y, turret.angle]
        turret.prevX = x
        turret.prevY = y
        turret.prevAngle = angle
      }
      for (const [entity, position] of deferred.splice(0)) {
        world.add(entity, Position(position))
        const turret = world.get(entity, Turret)!
        turret.prevX = position.x
        turret.prevY = position.y
        turret.prevAngle = turret.angle
      }
      // Возраст выстрела и взрыва хост шлёт тиком рождения, а где снаряд был тик назад, не шлёт вовсе, см. wire.ts.
      for (const [entity, shot, position] of world.query(Shot, Position)) {
        const [x, y] = before.get(entity) ?? [position.x, position.y]
        shot.prevX = x
        shot.prevY = y
        shot.age = last.tick - ((shot as typeof shot & { born?: number }).born ?? last.tick)
      }
      for (const [, blast] of world.query(Blast)) blast.age = last.tick - ((blast as typeof blast & { born?: number }).born ?? last.tick)
      // Перезарядку хост шлёт тиком её конца, см. wire.ts.
      for (const [, armed] of world.query(Armed)) {
        const wired = armed as typeof armed & { until?: number }
        armed.cooldown = Math.max(0, (wired.until ?? 0) - last.tick)
      }
      world.flush()
      time.tick = last.tick
      // Чужое здание, ушедшее в туман, остаётся на карте призраком, пока его место не окажется в обзоре.
      for (const [id, data] of memory) {
        if (mirror.has(id)) continue
        const entity = id as Entity
        if (gone(data)) {
          memory.delete(id)
          world.destroy(entity)
        } else if (!world.has(entity, Ghost)) {
          if (world.alive(entity)) world.add(entity, Ghost)
          else world.insert(id, { ...data, Ghost: {} }, REMEMBERED)
        }
      }
      world.flush()
      replica.traces.update()
      time.elapsed = time.tick * time.step
      time.alpha = 0
      pending = []
      return ticks
    },
    save: (): SimSave => ({ version: SAVE_VERSION, ...replica.options, tick: time.tick, world: world.snapshot(SAVED), land: saveLand(replica.land), paving: savePaving(paving) }),
    destroy() {
      close()
      occupancy.destroy()
      paving.destroy()
      world.clear()
    },
    receive(message: ServerMessage) {
      if (message.type === 'delta') pending.push(message)
      else if (message.type === 'explored') replica.vision.explore(player, message.map)
      // Виденное до входа: призраками станет в advance, как ушедшее в туман при нас.
      else if (message.type === 'remembered') for (const [id, data] of message.buildings) memory.set(id, data)
      else if (message.type === 'traces') replica.traces.receive(message.traces)
      else if (message.type === 'players') replica.players = message.players
      else if (message.type === 'views') replica.views = message.views
      else if (message.type === 'commands') {
        replica.commands = message.commands
        replica.admin = message.admin
      } else if (message.type === 'chat') {
        const at = performance.now()
        replica.chat = [...replica.chat, ...message.lines.map((line) => ({ ...line, at }))].slice(-CHAT_KEPT)
      }
      else if (message.type === 'land') receiveLand(message.data).catch((error) => replica.fail(t('errors.landFailed', { reason: error instanceof Error ? error.message : String(error) })))
      else if (message.type === 'deposits') loadDeposits(replica.deposits, message.deposits)
      else if (message.type === 'paving') {
        if (message.reset) paving.clear()
        loadPaving(paving, message.tiles)
      }
      else if (message.type === 'tiles') {
        if (earlyEdits) earlyEdits.push(message.edits)
        else applyEdits(replica.land, message.edits)
      }
      else if (message.type === 'refused') replica.fail(textOf(message.reason))
      else {
        meet(message)
        replica.generation++
      }
    },
    fail(reason: string) {
      failure ??= reason
    },
  }
  meet(welcome)
  return replica
}
