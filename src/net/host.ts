import { applyEdit, type EditOp } from '../sim/editOps'
import type { Entity } from '../ecs'
import { Terrain, terrainAt, tileBytes } from '../map/terrain'
import { UNIT_TYPES, creditsOf, flies, fromClient, isDefeated, type UnitType, notWalledIn, isWalkable, openSpawn, protect, protectedPlayers, shownBy, sightingOf, spawnStartingUnits, wipePlayer, type Command, type Sim, type Sighting, type SimSave } from '../sim'
import { Armed, Attached, Blast, Building, Health, Owner, Path, Player, Position, Shot, Unit } from '../sim/components'
import { motionOf, pathOf, seenBy, sharedWireOf, type Wired } from './wire'
import { LAND, encodeDelta, type Motion } from './codec'
import { deflate } from '../save/file'
import { DEPOSIT_SIZE, fillDeposits, saveDeposits } from '../sim/deposits'
import { knownEdits, pristineLand, takeLearned } from '../sim/landMemory'
import type { ServerData } from './protocol'
import { TRACE_CELL, type Trace } from '../sim/traces'
import { createBucket } from './limits'
import { cleanChat, cleanName, type ChatLine, type PlayerInfo, type PlayerView, type ServerMessage, type ViewBox } from './protocol'
import { PLAYER_ARG, parseCommand, type CommandInfo } from './chatCommands'
import { hostText, type Text } from '../i18n'
import { erase, putUnit, setCredits, setHealth } from '../sim/editor'
import { freeTilesNear } from '../sim/units'

/**
 * Где появляется новый игрок: в случайной точке карты, на скале, где хватит места под базу, и не ближе SPAWN_APART
 * тайлов к чужим юнитам и зданиям. Не нашлось за SPAWN_TRIES попыток — требование к расстоянию слабеет вдвое.
 */
const SPAWN_APART = 96
const SPAWN_TRIES = 400
/** Сколько тайлов скалы должно быть в квадрате SPAWN_AREA вокруг точки появления: MCV есть где развернуться. */
const SPAWN_AREA = 6
const SPAWN_ROCK = 100
/** Отступ точки появления от края карты. */
const SPAWN_MARGIN = 16
/** Запасной круг: если случайной точки не нашлось совсем, игроки встают на нём вокруг начала мира. */
const SPAWN_RADIUS = 24
/** Раз во сколько тиков хост ищет для игроков старые следы, а не только новые. */
const SWEEP_TICKS = 5
/** Как часто, в мс, одно подключение может писать в чат. */
const CHAT_GAP = 500
/** Длиннее скольких символов сообщение клиента не читается: приказ всем своим юнитам в разы короче. */
const MESSAGE_MOST = 64 * 1024
/** Сколько сообщений в секунду подключение шлёт в среднем и сколько разом: что сверх — отбрасывается. */
const MESSAGE_RATE = 30
const MESSAGE_BURST = 120
/** Сколько отброшенных сообщений подключению прощается разом и сколько в секунду: дальше его отключают. */
const FLOOD_BURST = 300
const FLOOD_RATE = 1
/** Больше скольких юнитов /spawn за раз не ставит. */
const SPAWN_MOST = 50
/** Сколько мс после неверного пароля администратора подключение не может попробовать снова. */
const ADMIN_LOCK = 3000
/**
 * Чем хост подписывает то, что пишет сам: player — игрок без ника, с номером на месте «{number}»; server — имя, которым
 * подписаны сообщения администратора и консоли, /say. Это язык хоста: ник видят все игроки одинаково.
 */
export interface HostNames {
  player: string
  server: string
}
const DEFAULT_NAMES: HostNames = { player: 'Игрок {number}', server: '[Сервер]' }
/** Сколько игроков помещается на круге появления; следующие встают на круг шире. */
const SPAWN_SLOTS = 8

/** Сколько КБ в секунду сервер шлёт одному подключению, если в настройках не сказано иначе, см. HostOptions.budget. */
export const DEFAULT_SEND_RATE = 256
/** Сколько сущностей подключение сверяет за один заход круга, см. Host.advance. */
const BATCH = 24
/**
 * Какую долю тика хост может занять симуляцией и рассылкой вместе, если не сказано иначе, и сколько мс на очередь
 * рассылки есть всегда, даже когда симуляция съела всё.
 */
const TICK_SHARE = 0.8
const SEND_LEAST = 4
/**
 * Как делится байтовый бюджет подключения на тик. Следам — TRACE_SHARE. Новые выстрелы и взрывы уходят сразу, пока
 * кадр не набрал FLEETING_SHARE: в большом бою на экране их сотни за тик, и без предела они съедали бы канал
 * целиком. Очереди остаётся остальное и не меньше QUEUE_SHARE, сколько бы ни заняло обязательное: иначе юниты
 * на экране стояли бы.
 */
const TRACE_SHARE = 0.15
const FLEETING_SHARE = 0.35
const QUEUE_SHARE = 0.5
/** Во что обходится один след в сообщении traces, в байтах: для бюджета, на глаз. */
const TRACE_BYTES = 100
/** Веса в очереди рассылки, см. begin: на экране игрока, в запасе вокруг экрана, едет, в бою. */
const ON_SCREEN = 8
const NEAR_SCREEN = 3
const MOVING = 2
const FIGHTING = 2
/** Запас вокруг экрана — доля его стороны, но не меньше стольких тайлов: туда камера доедет раньше, чем придёт её новое место. */
const SCREEN_MARGIN = 0.25
const SCREEN_MARGIN_LEAST = 6
/** Сколько тиков после попадания сущность считается «в бою». */
const HOT_TICKS = 20
/** Корзин в очереди рассылки: по четыре на каждое удвоение приоритета, см. bucketOf. */
const BUCKETS = 64
/** Во что кадру обходится запись движения, обёртка записи set и номер в remove, в байтах: для бюджета, на глаз. */
const MOTION_BYTES = 13
const SET_BYTES = 10
const REMOVE_BYTES = 6
const PATH_KEY = JSON.stringify(Path.key)

/**
 * Корзина по приоритету: чем он выше, тем корзина старше. Внутри корзины порядок случайный, поэтому корзины узкие —
 * четверть удвоения; приоритет растёт с возрастом, и отложенное само переходит в корзину старше.
 */
const bucketOf = (priority: number) => {
  if (priority < 4) return priority
  const high = 31 - Math.clz32(priority)
  return Math.min(BUCKETS - 1, high * 4 + ((priority >> (high - 2)) & 3))
}

/** Что подключение знает о сущности: какой она ему ушла и когда хост последний раз сверял её с миром. */
interface Known {
  /** JSON компонентов по имени — как в Wired; путь — отдельно, он свой у каждого игрока. */
  parts: ReadonlyMap<string, string>
  path?: string
  motion?: Motion
  /** Тик последней сверки: возраст в очереди считается от него. */
  checked: number
  /** Проход рассылки, в котором сущность последний раз была в обзоре: не нынешний — её больше не видно. */
  seen: number
  /** Клиент о ней слышал: о пропаже такой ему надо сказать. */
  told: boolean
}
/** Мир, каким его знает подключение. */
type View = Map<number, Known>

/** Что о сущности нужно очереди рассылки — одно на всех игроков, раз за тик. */
interface Brief {
  motion?: Motion
  /** Есть ли у неё место на карте и где оно, в тайлах. */
  placed: boolean
  x: number
  y: number
  /** В бою: целится, недавно получила попадание или сама выстрел. */
  hot: boolean
  /** Чей это счёт игрока; 0 — это не счёт. */
  account: number
  /** Выстрел или взрыв: живёт несколько тиков. aim — в чьё оно летит; 0 — ни в чьё. */
  fleeting: boolean
  aim: number
}

/** Кадр изменений мира для одного подключения, пока он собирается. */
interface Frame {
  send: Send
  player: number
  god: boolean
  known: View
  pass: number
  set: string[]
  unset: [number, string[]][]
  remove: number[]
  motions: [number, Motion, number][]
  /** Очередь необязательного: номера сущностей по корзинам, см. bucketOf; bucket и at — докуда она разобрана. */
  queue: number[][]
  bucket: number
  at: number
  /** Сколько в очереди ещё не сверено. */
  queued: number
  /** Сколько байт кадр уже набрал, примерно, и больше скольких в него не кладётся необязательное. */
  bytes: number
  most: number
}
/** Как отправить сообщение подключению: изменения мира — двоичные, остальное — текст. */
type Send = (data: ServerData) => void

/** Подключённый игрок с точки зрения хоста. */
export interface Peer {
  readonly player: number
  /** Сообщение от клиента как пришло из сети. Негодное молча отбрасывается. */
  receive(text: string): void
  /** Клиент отключился. Его юниты и здания остаются в мире. */
  leave(): void
}

/** Кто зовёт команду: игрок из чата или консоль сервера (player нет). reply — ответ только ему. */
export interface Caller {
  player?: number
  admin: boolean
  /** Ответ: сообщение хоста ключом словаря (его читают на своём языке, см. textOf) или готовый текст. */
  reply(text: Text): void
}

/**
 * Команда хоста. admin — только администратору (консоль им считается всегда); game — только из игры, не из
 * консоли; console — только из консоли, клиенту её не видно. args — слова после команды, rest(n) — текст после n слов.
 */
export interface HostCommand extends Omit<CommandInfo, 'help'> {
  /** Что делает команда — для команд не из словаря; у своих команд хоста описание клиент берёт по имени. */
  help?: string
  admin?: boolean
  game?: boolean
  console?: boolean
  run(args: string[], rest: (skip: number) => string, caller: Caller): void
}

export interface HostOptions {
  /** Пароль режима администратора, /admin; пустой или нет — режима нет. В локальной игре он не нужен. */
  admin?: string
  /** Куда писать, что делают администраторы: консоль сервера. */
  log?(text: string): void
  /** Подписи хоста; нет — русские. */
  names?: HostNames
  /**
   * Бюджет рассылки мира, см. Host.advance: ms — сколько мс хост может занять за тик симуляцией и рассылкой вместе
   * (нет — TICK_SHARE тика); bytes — сколько байт в секунду уходит одному подключению (нет — без предела: локальной
   * игре канал не тесен). Что не влезло, ждёт следующих тиков. Обязательное в бюджет не смотрит.
   */
  budget?: { ms?: number; bytes?: number }
}

export interface Host {
  readonly sim: Sim
  /** Заменяет мир новым: все подключённые получают приветствие и новый мир, как при подключении. */
  replace(sim: Sim): void
  /** Сколько клиентов сейчас подключено. */
  readonly peers: number
  /** Размер самого большого из последних разосланных изменений мира в байтах. */
  readonly stateSize: number
  /**
   * Рассылка последнего тика: waited — сколько тиков ждала сверки самая залежавшаяся из сверенных сущностей (1 — никто
   * не ждал), deferred — сколько сверок бюджет отложил на следующие тики, по всем подключениям.
   */
  readonly sending: { readonly waited: number; readonly deferred: number }
  /**
   * Подключает клиента. send отправляет ему сообщение: текст или двоичный кадр изменений мира, см. codec.ts. id — то, по чему хост узнаёт вернувшегося игрока: его
   * выдаёт сам хост в приветствии; с прежним id клиент получает прежнего игрока, а не новый стартовый набор.
   * Неизвестный или пустой id — новый игрок с новым id. name — ник; пустой — «Игрок N».
   */
  join(send: Send, id?: string, name?: string, close?: () => void): Peer
  /** Выполняет команду (строку без «/» или с ним) от имени caller, см. HostCommand. */
  command(text: string, caller: Caller): void
  /** Добавляет команду: например, сохранение и остановку, которые умеет только сервер. */
  addCommand(command: HostCommand): void
  /** Пишет всем в чат от имени сервера. */
  say(text: string): void
  /**
   * Продвигает игру на seconds реального времени и рассылает мир, если прошёл хотя бы один тик. Рассылка — с бюджетом:
   * новое и пропавшее уходит каждому сразу, остальное сверяется по очереди, пока есть время и байты. Подключения
   * идут по кругу, каждому — пачка из верха его очереди (см. BATCH): у кого слать нечего, выпадает, и его доля достаётся
   * остальным. Хотя бы одну пачку за тик получает каждый.
   */
  advance(seconds: number): number
  /** Знает ли хост игрока с этим id; незнакомому при подключении заводится новый игрок. */
  knows(id?: string): boolean
  /** Мир вместе с тем, кого хост знает: по id игроки узнаются и после перезапуска сервера. */
  save(): HostSave
}

/** Сохранение сервера: мир, id игроков и их ники. */
export interface HostSave {
  sim: SimSave
  /** Номер игрока по выданному ему id. */
  players: Record<string, number>
  /** Ники по номеру игрока. */
  names: Record<string, string>
}

/**
 * Хост — симуляция плюс подключённые игроки. Про сокеты он не знает: ему дают функцию отправки и приносят
 * пришедший текст. Поэтому один и тот же хост работает на сервере за WebSocket и в воркере локальной игры
 * за postMessage. player — все подключения играют за этого игрока, и новых стартовых наборов нет: так вкладки
 * одной локальной игры показывают один мир. Без него каждый новый token — новый игрок.
 */
export function createHost(first: Sim, player?: number, saved?: Omit<HostSave, 'sim'>, options: HostOptions = {}): Host {
  let sim = first
  // Клиенты получают месторождения слоем целиком: генератор у них их не считает.
  fillDeposits(sim)
  /** Какую правку месторождений уже разослали. */
  let depositsSent = sim.deposits.revision
  /** Какой слой месторождений каждое подключение получило последним: шлётся заново, только если он поменялся. */
  const depositsShown = new Map<Send, string>()
  // На сервере до первого подключения не в сети никто.
  if (player === undefined) sim.online = new Set()
  /** Подключённые: как отправить и за кого играет. */
  const peers = new Map<Send, number>()
  /** Какие следы каждое подключение уже получило. */
  const shown = new Map<Send, Set<number>>()
  /** Ячейки следов, которые подключение видело при прошлом полном обходе, см. traces. */
  const swept = new Map<Send, ReadonlySet<number>>()
  /** Какой мир каждое подключение уже получило: по сущности — JSON каждого её компонента. */
  const sent = new Map<Send, View>()
  let sinceSweep = 0

  /**
   * Следы, которые подключение видит — они в обзоре его юнитов и зданий прямо сейчас, — а ещё не получало. Новые проверяются каждый тик, все — раз в SWEEP_TICKS:
   * так находятся старые следы там, куда игрок только что пришёл. Полный обход смотрит не все следы мира, а только
   * ячейки, где игрок видит хоть что-то сейчас или видел при прошлом обходе: в остальных ничего не поменялось.
   * most — сколько следов влезает в бюджет тика: за экраном игрока берётся не больше половины, а что не влезло,
   * найдётся при следующем обходе.
   */
  const traces = (send: Send, player: number, sweep: boolean, most = Infinity) => {
    const known = shown.get(send)!
    const found: Trace[] = []
    const near = nearOf(send)
    const check = (trace: Trace) => {
      const seen = sim.vision.sees(player, trace.x, trace.y)
      if (seen && !known.has(trace.id)) {
        if (found.length >= (near(trace.x, trace.y) ? most : most / 2)) return
        found.push(trace)
        known.add(trace.id)
      } else if (!seen && known.has(trace.id)) {
        // Ушёл из обзора — клиент его выбросил, а вернётся в обзор — получит снова.
        known.delete(trace.id)
      }
    }
    // Колею, оставленную на глазах у игрока, клиент кладёт сам: он видит того же юнита, см. traces.ts.
    for (const trace of sim.traces.fresh()) if (trace.kind === 'track' && sim.vision.sees(player, trace.x, trace.y)) known.add(trace.id)
    if (!sweep) {
      for (const trace of sim.traces.fresh()) check(trace)
      return found
    }
    const cells = sim.vision.seenCells(player, TRACE_CELL)
    for (const cell of cells) for (const trace of sim.traces.inCell(cell)) check(trace)
    for (const cell of swept.get(send) ?? []) if (!cells.has(cell)) for (const trace of sim.traces.inCell(cell)) check(trace)
    swept.set(send, cells)
    return found
  }
  /** Номер игрока по id, который ему выдал хост. */
  const players = new Map<string, number>(Object.entries(saved?.players ?? {}))
  let nextPlayer = Math.max(0, ...players.values()) + 1
  /** Ники игроков по номеру. */
  const names = new Map<number, string>(Object.entries(saved?.names ?? {}).map(([player, name]) => [Number(player), name]))
  /** Скрытые администраторы, /hide: для остальных их нет — ни в списке, ни в сообщениях о входе. До перезапуска. */
  const hidden = new Set<number>()
  /** Кто под защитой новичка, каким это знают клиенты: поменялось — список игроков рассылается заново. */
  let sheltered = protectedPlayers(sim)
  /** Кто играет — глазами viewer: скрытых он не видит, кроме самого себя. */
  const roster = (viewer?: number) => {
    const online = new Set(peers.values())
    const list: PlayerInfo[] = [...names].filter(([who]) => !hidden.has(who) || who === viewer).map(([player, name]) => ({ player, name, online: online.has(player), ...(sheltered.has(player) && { safe: true as const }) }))
    return JSON.stringify({ type: 'players', players: list } satisfies ServerMessage)
  }
  /** Сообщает всем подключённым, кто сейчас в игре. */
  const announce = () => {
    // Кто в сети, знает только сервер: доход игрока не в сети урезан, см. Rules.offlineIncome. Локальная игра
    // (player задан) идёт, только пока открыта, и там все в сети.
    if (player === undefined) sim.online = new Set(peers.values())
    const texts = new Map<number, string>()
    for (const [send, viewer] of peers) {
      let text = texts.get(viewer)
      if (text === undefined) texts.set(viewer, (text = roster(viewer)))
      send(text)
    }
  }
  let stateSize = 0
  /** Когда подключение писало в чат последний раз, в мс: чаще CHAT_GAP сообщения отбрасываются. */
  const lastChat = new Map<Send, number>()
  /** Счёт сообщений подключения: что слишком часто — отбрасывается, а кто не унимается — отключается. */
  const floods = new Map<Send, { messages: ReturnType<typeof createBucket>; strikes: ReturnType<typeof createBucket> }>()
  /** Рассылает сообщения чата всем подключённым. */
  const broadcast = (lines: ChatLine[]) => {
    const out = JSON.stringify({ type: 'chat', lines } satisfies ServerMessage)
    for (const peer of peers.keys()) peer(out)
  }
  /** Сообщение сервера о игроке: «<ник> text». */
  const notice = (about: number, text: Text) => broadcast([{ player: about, name: nameOf(about), text, system: true }])
  const online = (who: number) => [...peers.values()].includes(who)
  /** Подключения в режиме администратора. */
  const admins = new Set<Send>()
  /** Администраторы в редакторе: видят весь мир и правят его, см. EditOp. */
  const editing = new Set<Send>()
  /** Где камера у каждой вкладки, см. ViewBox: редактор показывает их на карте. */
  const views = new Map<Send, ViewBox>()
  /** Рядом ли точка с экраном подключения: на нём или в запасе вокруг, см. SCREEN_MARGIN. Камеры нет — рядом всё. */
  const nearOf = (send: Send) => {
    const box = views.get(send)
    if (!box) return () => true
    const marginX = Math.max(SCREEN_MARGIN_LEAST, (box.right - box.left) * SCREEN_MARGIN)
    const marginY = Math.max(SCREEN_MARGIN_LEAST, (box.bottom - box.top) * SCREEN_MARGIN)
    const left = box.left - marginX
    const top = box.top - marginY
    const right = box.right + marginX
    const bottom = box.bottom + marginY
    return (x: number, y: number) => x >= left && x <= right && y >= top && y <= bottom
  }
  /** Какие камеры каждый редактор получил последними: шлются заново, только если что-то сдвинулось. */
  const viewsShown = new Map<Send, string>()
  /** Шлёт редакторам камеры игроков — всех, кроме своих. */
  const sendViews = () => {
    for (const send of editing) {
      const own = peers.get(send)
      const list: PlayerView[] = []
      for (const [peer, box] of views) if (peers.get(peer) !== own && !hidden.has(peers.get(peer)!)) list.push({ player: peers.get(peer)!, name: nameOf(peers.get(peer)!), ...box })
      const text = JSON.stringify({ type: 'views', views: list } satisfies ServerMessage)
      if (viewsShown.get(send) === text) continue
      viewsShown.set(send, text)
      send(text)
    }
  }
  /** Когда подключению снова можно пробовать пароль администратора, в мс. */
  const adminLocked = new Map<Send, number>()
  /** Как закрыть подключение: им администратор выгоняет игрока. */
  const closers = new Map<Send, () => void>()
  /** Ответ на команду только этому подключению. */
  const reply = (send: Send) => (text: Text) => send(JSON.stringify({ type: 'chat', lines: [{ player: 0, name: '', text, system: true }] } satisfies ServerMessage))
  const defaultName = (who: number) => (options.names ?? DEFAULT_NAMES).player.replace('{number}', String(who))
  const nameOf = (who: number): string => names.get(who) ?? defaultName(who)
  /** Игрок по номеру или нику без учёта регистра; не нашёлся — undefined. */
  const findPlayer = (text: string | undefined, admin = true) => {
    if (!text) return undefined
    const found = findAny(text)
    return found !== undefined && (admin || !hidden.has(found)) ? found : undefined
  }
  const findAny = (text: string) => {
    const number = Number(text)
    if (Number.isInteger(number) && names.has(number)) return number
    const lower = text.toLowerCase()
    for (const [who, name] of names) if (name.toLowerCase() === lower) return who
    return undefined
  }
  const commands = new Map<string, HostCommand>()
  /** Команды, которые видит подключение: для дополнения у клиента. */
  const commandsFor = (admin: boolean): CommandInfo[] =>
    [...commands.values()]
      .filter((command) => !command.console && (admin || !command.admin))
      // Локальной игре пароль не нужен, и клиент не спросит его.
      .map(({ name, args, help = '', secret, choices }) => ({ name, args, help, ...(choices && { choices }), ...(secret && player === undefined && { secret }) }))
  const sendCommands = (send: Send) => send(JSON.stringify({ type: 'commands', commands: commandsFor(admins.has(send)), admin: admins.has(send) } satisfies ServerMessage))
  const serverName = (options.names ?? DEFAULT_NAMES).server
  const serverSay = (text: string) => broadcast([{ player: 0, name: serverName, text, system: true }])
  /** Подключение по его reply: команде из игры нужно знать, кто её прислал. */
  const callers = new WeakMap<Caller, Send>()
  const usage = (command: HostCommand) => hostText('usage', { command: `/${[command.name, ...command.args].join(' ')}` })
  const add = (command: HostCommand) => commands.set(command.name, command)

  add({
    name: 'help',
    args: [],
    run(_args, _rest, caller) {
      for (const command of commands.values()) {
        if (command.admin && !caller.admin) continue
        if (caller.player === undefined ? command.game : command.console) continue
        caller.reply(hostText('help', { command: `/${[command.name, ...command.args].join(' ')}`, name: command.name, help: command.help ?? '' }))
      }
    },
  })
  add({
    name: 'list',
    args: [],
    run(_args, _rest, caller) {
      const present = [...new Set(peers.values())]
      const here = present.filter((who) => !hidden.has(who)).map(nameOf)
      caller.reply(here.length ? hostText('list', { count: here.length, names: here.join(', ') }) : hostText('listEmpty'))
      // Скрытых видит только администратор.
      const unseen = present.filter((who) => hidden.has(who)).map(nameOf)
      if (caller.admin && unseen.length) caller.reply(hostText('listHidden', { names: unseen.join(', ') }))
    },
  })
  add({
    name: 'me',
    args: ['<action>'],
    game: true,
    run(_args, rest, caller) {
      const text = rest(0)
      if (text) broadcast([{ player: caller.player!, name: `* ${nameOf(caller.player!)}`, text, system: true }])
    },
  })
  add({
    name: 'msg',
    args: [PLAYER_ARG, '<text>'],
    game: true,
    run(args, rest, caller) {
      const to = findPlayer(args[0], caller.admin)
      const text = rest(1)
      if (to === undefined) return caller.reply(hostText('noPlayer', { name: args[0] ?? '' }))
      if (!text) return caller.reply(usage(this))
      const out = JSON.stringify({ type: 'chat', lines: [{ player: caller.player!, name: nameOf(caller.player!), text, whisper: nameOf(to) }] } satisfies ServerMessage)
      const self = callers.get(caller)
      for (const [peer, owner] of peers) if (owner === to || owner === caller.player || peer === self) peer(out)
      if (![...peers.values()].includes(to) || (hidden.has(to) && !caller.admin)) caller.reply(hostText('notOnline', { name: nameOf(to) }))
    },
  })
  add({
    name: 'admin',
    args: [],
    secret: true,
    game: true,
    run(args, _rest, caller) {
      const send = callers.get(caller)!
      // В локальной игре мир свой, и пароль не нужен.
      const local = player !== undefined
      if (!local && !options.admin) return caller.reply(hostText('noAdminMode'))
      if (admins.has(send)) return caller.reply(hostText('alreadyAdmin'))
      const now = Date.now()
      if (now < (adminLocked.get(send) ?? 0)) return caller.reply(hostText('wait'))
      if (!local && args.join(' ') !== options.admin) {
        adminLocked.set(send, now + ADMIN_LOCK)
        options.log?.(`! неверный пароль администратора от ${nameOf(caller.player!)}`)
        return caller.reply(hostText('wrongPassword'))
      }
      admins.add(send)
      sendCommands(send)
      options.log?.(`! ${nameOf(caller.player!)} — администратор`)
      caller.reply(hostText('adminOn'))
    },
  })
  add({
    name: 'logout',
    args: [],
    admin: true,
    game: true,
    run(_args, _rest, caller) {
      const send = callers.get(caller)!
      admins.delete(send)
      if (editing.delete(send)) resync(send, caller.player!, true)
      sendCommands(send)
      caller.reply(hostText('adminOff'))
    },
  })
  add({
    name: 'editor',
    args: [],
    admin: true,
    game: true,
    run(_args, _rest, caller) {
      const send = callers.get(caller)!
      const on = !editing.has(send)
      if (on) editing.add(send)
      else editing.delete(send)
      resync(send, caller.player!, true)
      caller.reply(hostText(on ? 'editorOn' : 'editorOff'))
      options.log?.(`! ${nameOf(caller.player!)} ${on ? 'открыл' : 'закрыл'} редактор`)
    },
  })
  add({
    name: 'hide',
    args: [],
    admin: true,
    game: true,
    run(_args, _rest, caller) {
      const who = caller.player!
      // Для остальных — как будто вышел или зашёл.
      if (hidden.delete(who)) {
        notice(who, hostText('joins'))
        caller.reply(hostText('shown'))
      } else {
        notice(who, hostText('leaves'))
        hidden.add(who)
        caller.reply(hostText('hidden'))
      }
      announce()
      options.log?.(`! ${nameOf(who)} ${hidden.has(who) ? 'скрылся' : 'появился'}`)
    },
  })
  add({
    name: 'say',
    args: ['<text>'],
    admin: true,
    run(_args, rest, caller) {
      const text = cleanChat(rest(0))
      if (!text) return caller.reply(usage(this))
      serverSay(text)
      options.log?.(`${serverName} ${text}`)
    },
  })
  add({
    name: 'kick',
    args: [PLAYER_ARG],
    admin: true,
    run(args, _rest, caller) {
      const who = findPlayer(args.join(' '))
      if (who === undefined) return caller.reply(hostText('noPlayer', { name: args.join(' ') }))
      const out = JSON.stringify({ type: 'refused', reason: hostText('kickedYou') } satisfies ServerMessage)
      let count = 0
      for (const [peer, owner] of [...peers]) {
        if (owner !== who) continue
        peer(out)
        closers.get(peer)?.()
        count++
      }
      if (!count) return caller.reply(hostText('notOnline', { name: nameOf(who) }))
      notice(who, hostText('kicked'))
      options.log?.(`! ${nameOf(who)} отключён`)
    },
  })
  add({
    name: 'credits',
    args: [PLAYER_ARG, '<amount>'],
    admin: true,
    run(args, _rest, caller) {
      const who = findPlayer(args[0])
      const amount = Number(args[1])
      if (who === undefined) return caller.reply(hostText('noPlayer', { name: args[0] ?? '' }))
      if (!Number.isFinite(amount)) return caller.reply(usage(this))
      const relative = /^[+-]/.test(args[1])
      setCredits(sim, who, relative ? creditsOf(sim, who) + amount : amount)
      caller.reply(hostText('creditsNow', { name: nameOf(who), credits: creditsOf(sim, who) }))
      options.log?.(`! кредиты ${nameOf(who)}: ${creditsOf(sim, who)}`)
    },
  })

  /**
   * Игрок, о котором команда: по нику или номеру, а без аргумента — тот, кто её позвал. Не нашёлся — ответ caller и
   * undefined.
   */
  const target = (arg: string | undefined, caller: Caller) => {
    const who = arg === undefined ? caller.player : findPlayer(arg)
    if (who === undefined) caller.reply(arg === undefined ? hostText('needPlayer') : hostText('noPlayer', { name: arg }))
    return who
  }
  /** Своё у игрока: юниты и здания, без турелей — они уходят вместе с носителем. */
  const ownedBy = (who: number) => {
    const units: Entity[] = []
    const buildings: Entity[] = []
    for (const [entity, owner] of sim.world.query(Owner)) {
      if (owner.player !== who || sim.world.has(entity, Attached)) continue
      if (sim.world.has(entity, Unit)) units.push(entity)
      else if (sim.world.has(entity, Building)) buildings.push(entity)
    }
    return { units, buildings }
  }
  /** Середина базы игрока: среднее его зданий, а без них — юнитов. */
  const baseOf = (who: number) => {
    const { units, buildings } = ownedBy(who)
    const points = (buildings.length ? buildings : units).map((entity) => sim.world.get(entity, Position)!)
    if (!points.length) return undefined
    return { x: Math.floor(points.reduce((sum, p) => sum + p.x, 0) / points.length), y: Math.floor(points.reduce((sum, p) => sum + p.y, 0) / points.length) }
  }

  add({
    name: 'players',
    args: [],
    admin: true,
    run(_args, _rest, caller) {
      const here = new Set(peers.values())
      for (const [who, name] of names) {
        const { units, buildings } = ownedBy(who)
        const state = isDefeated(sim, who) ? 'playerDefeated' : here.has(who) ? 'playerOnline' : 'playerOffline'
        caller.reply(hostText(state, { number: who, name, credits: creditsOf(sim, who), units: units.length, buildings: buildings.length }))
      }
    },
  })
  add({
    name: 'spawn',
    args: ['<unit>', '[count]', `[${PLAYER_ARG.slice(1, -1)}]`],
    choices: [UNIT_TYPES, null, null],
    admin: true,
    run(args, _rest, caller) {
      const type = args[0] as UnitType
      if (!UNIT_TYPES.includes(type)) return caller.reply(hostText('unitTypes', { list: UNIT_TYPES.join(', ') }))
      const count = Math.min(SPAWN_MOST, Math.max(1, Math.floor(Number(args[1] ?? 1)) || 1))
      const who = target(args[2], caller)
      if (who === undefined) return
      const base = baseOf(who)
      if (!base) return caller.reply(hostText('noBase', { name: nameOf(who), number: who }))
      const tiles = freeTilesNear(sim, base.x, base.y, count, 2, undefined, flies(type))
      let made = 0
      for (let i = 0; i + 1 < tiles.length; i += 2) if (putUnit(sim, type, tiles[i], tiles[i + 1], who)) made++
      caller.reply(`${nameOf(who)}: ${type} × ${made}`)
      options.log?.(`! spawn ${type} × ${made} → ${nameOf(who)}`)
    },
  })
  add({
    name: 'killunits',
    args: [`${PLAYER_ARG.slice(0, -1)}|all>`],
    admin: true,
    run(args, _rest, caller) {
      const all = args[0]?.toLowerCase() === 'all'
      const who = all ? undefined : target(args[0], caller)
      if (!all && who === undefined) return
      const units = all ? [...names.keys()].flatMap((each) => ownedBy(each).units) : ownedBy(who!).units
      for (const entity of units) erase(sim, entity)
      const count = units.length
      caller.reply(hostText('unitsRemoved', { count }))
      options.log?.(`! killunits ${all ? 'all' : nameOf(who!)}: ${count}`)
    },
  })
  add({
    name: 'heal',
    args: [PLAYER_ARG],
    admin: true,
    run(args, _rest, caller) {
      const who = target(args[0], caller)
      if (who === undefined) return
      const { units, buildings } = ownedBy(who)
      for (const entity of [...units, ...buildings]) setHealth(sim, entity, 1)
      caller.reply(hostText('healed', { name: nameOf(who), count: units.length + buildings.length }))
    },
  })
  add({
    name: 'reset',
    args: [PLAYER_ARG],
    admin: true,
    run(args, _rest, caller) {
      const who = target(args[0], caller)
      if (who === undefined) return
      restart(who)
      caller.reply(hostText('restarted', { name: nameOf(who) }))
      options.log?.(`! reset ${nameOf(who)}`)
    },
  })
  add({
    name: 'reveal',
    args: [PLAYER_ARG],
    admin: true,
    run(args, _rest, caller) {
      const who = target(args[0], caller)
      if (who === undefined) return
      // Один отрезок «разведано» длиннее любой карты: explore обрежет его по её краю.
      sim.vision.explore(who, [0, 2 ** 31])
      for (const [peer, owner] of peers) if (owner === who) peer(explored(who))
      caller.reply(hostText('revealed', { name: nameOf(who) }))
    },
  })

  const runCommand = (text: string, caller: Caller) => {
    const parsed = parseCommand(text.startsWith('/') ? text : `/${text}`)
    if (!parsed || !parsed.name) return
    const command = commands.get(parsed.name)
    const fromGame = caller.player !== undefined
    if (!command || (command.admin && !caller.admin) || (fromGame ? command.console : command.game)) {
      return caller.reply(hostText('noCommand', { name: parsed.name }))
    }
    command.run(parsed.args, parsed.rest, caller)
  }

  /** Игрок начинает заново: остатки базы исчезают, туман закрыт, новый стартовый набор в новом месте. */
  const restart = (who: number) => {
    wipePlayer(sim, who)
    defeated.delete(who)
    if (player === undefined && !hidden.has(who)) notice(who, hostText('restarts'))
    sim.vision.forget(who)
    place(who)
    // Вкладки игрока начинают как в новом мире: туман закрыт, камера встаёт на новый стартовый набор.
    for (const [peer, owner] of peers) if (owner === who) resync(peer, who)
  }

  /** Кто уже побеждён: о поражении сообщается один раз, когда оно случилось. */
  const defeated = new Set([...names.keys()].filter((who) => isDefeated(sim, who)))

  /**
   * Мир, разобранный один раз на всех подключённых: ids — сущности, остальное — по тому же месту в списке. sightings —
   * кому сущность видна, см. sightingOf; wired — какой она уходит в сеть, кроме пути, см. sharedWireOf; briefs — что
   * о ней нужно очереди, см. Brief. Последние два собираются, только когда до сущности дошли.
   */
  let scanned: { tick: number; ids: number[]; sightings: Sighting[]; wired: (Wired | undefined)[]; briefs: (Brief | undefined)[] }
  const scan = () => {
    const { world } = sim
    const ids: number[] = []
    const sightings: Sighting[] = []
    for (const entity of world.all) {
      ids.push(entity)
      sightings.push(sightingOf(world, entity))
    }
    scanned = { tick: sim.time.tick, ids, sightings, wired: [], briefs: [] }
  }
  const briefOf = (at: number) => {
    let brief = scanned.briefs[at]
    if (!brief) {
      const { world } = sim
      const entity = scanned.ids[at] as Entity
      const place = world.get(entity, Position)
      const armed = world.get(entity, Armed)
      const hit = world.get(entity, Health)?.hit ?? -1
      const shot = world.get(entity, Shot)
      const fleeting = shot !== undefined || world.has(entity, Blast)
      brief = {
        motion: motionOf(world, entity),
        placed: place !== undefined,
        x: place?.x ?? 0,
        y: place?.y ?? 0,
        hot: fleeting || (armed !== undefined && armed.target >= 0) || (hit >= 0 && scanned.tick - hit < HOT_TICKS),
        account: world.get(entity, Player)?.id ?? 0,
        fleeting,
        aim: shot ? (world.get(shot.target as Entity, Owner)?.player ?? 0) : 0,
      }
      scanned.briefs[at] = brief
    }
    return brief
  }

  /**
   * Что игрок видит: места сущностей в scanned. Вкладки одного игрока смотрят на одно и то же. В редакторе (god) —
   * весь мир, со складами и целями чужих: администратор правит всё. В кэше такой список — под номером -player.
   */
  const visible = (player: number, cache?: Map<number, number[]>, god = false) => {
    const slot = god ? -player : player
    let found = cache?.get(slot)
    if (found === undefined) {
      found = []
      const { sightings } = scanned
      const sees = sim.vision.seer(player)
      for (let at = 0; at < sightings.length; at++) if (god || shownBy(player, sees, sightings[at])) found.push(at)
      cache?.set(slot, found)
    }
    return found
  }

  let passes = 0
  /** Рассылка последнего тика, см. Host.sending. */
  const sending = { waited: 0, deferred: 0 }

  /**
   * Сверяет сущность (at — её место в scanned) с тем, что подключение о ней знает, и кладёт разницу в кадр:
   * сравнивается JSON компонентов, так что неподвижное здание в сеть не уходит. Текст собирается из готовых кусков
   * JSON, чтобы не сериализовать всё второй раз. Общее для всех собирается раз — у первого, кто до сущности дошёл;
   * путь — свой у каждого.
   */
  const check = (frame: Frame, at: number) => {
    const { known, player } = frame
    const { tick } = scanned
    const id = scanned.ids[at]
    const entity = id as Entity
    const shared = (scanned.wired[at] ??= sharedWireOf(sim.world, entity, tick))
    const { parts, motion } = frame.god ? shared : seenBy(shared, player)
    const path = pathOf(sim.world, entity, player)
    const old = known.get(id)
    let set = ''
    for (const [key, json] of parts) if (old?.parts.get(key) !== json) set += `${set && ','}${JSON.stringify(key)}:${json}`
    if (path !== undefined && path !== old?.path) set += `${set && ','}${PATH_KEY}:${path}`
    // Новая сущность получает движение, даже если ничего, кроме него, у неё нет: так клиент узнаёт о ней.
    const moved = motion !== undefined && (!old?.motion || motion[0] !== old.motion[0] || motion[1] !== old.motion[1] || motion[2] !== old.motion[2])
    if (set) {
      frame.set.push(`[${id},{${set}}]`)
      frame.bytes += set.length + SET_BYTES
    }
    if (moved) {
      // Срок — сколько тиков подключение не знало, где сущность: столько клиент и ведёт её к новому месту.
      frame.motions.push([id, motion, old ? tick - old.checked : 1])
      frame.bytes += MOTION_BYTES
    }
    if (!old) {
      // Сущность, которой в сети нечего показать, клиенту не называется — но помнится, чтобы не собирать её каждый тик.
      known.set(id, { parts, path, motion, checked: tick, seen: frame.pass, told: !!set || moved })
      return
    }
    let dropped: string[] | undefined
    for (const key of old.parts.keys()) if (!parts.has(key)) (dropped ??= []).push(key)
    if (old.path !== undefined && path === undefined) (dropped ??= []).push(Path.key)
    if (dropped) frame.unset.push([id, dropped])
    if (tick - old.checked > sending.waited) sending.waited = tick - old.checked
    old.parts = parts
    old.path = path
    old.motion = motion
    old.checked = tick
    old.told ||= !!set || moved
  }

  /**
   * Начинает кадр подключения. Обязательное кладётся сразу: сущности, которых подключение ещё не знает (появилось,
   * вошло в обзор, выстрел), те, которых больше не видно (гибель, ушло из обзора), и счёт самого игрока. Остальное
   * видимое только встаёт в очередь по приоритету — возраст × вес — и сверяется, пока хватает бюджета, см. advance.
   * Возраст — сколько тиков сущность не сверялась: отложенная тяжелеет с каждым тиком, и навсегда не застревает
   * никто. Вес: на экране игрока — ON_SCREEN, в запасе вокруг него — NEAR_SCREEN (камеру клиент присылает сам, см.
   * views; не прислал — все равны); едет — ×MOVING; в бою — ×FIGHTING. Очередь — корзины по приоритету, см.
   * bucketOf: полная сортировка стоила бы дороже самой рассылки.
   *
   * Выстрелы и взрывы — обязательное с оговорками: в большом бою их сотни за тик, а живут они несколько тиков. За
   * экраном игрок их не видит и не слышит — такие встают в очередь; на экране уходят сразу, пока кадр не набрал
   * fleeting байт, дальше — тоже в очередь, первыми. До кого бюджет не дошёл, тот просто не приходит. Выстрел
   * по своим считается как выстрел на экране, где бы ни был: по ним клиент узнаёт, что его атакуют, см. alerts.ts.
   */
  const begin = (send: Send, player: number, cache?: Map<number, number[]>, fleeting = Infinity): Frame => {
    const { tick, ids, sightings } = scanned
    const god = editing.has(send)
    const known = sent.get(send)!
    const frame: Frame = { send, player, god, known, pass: ++passes, set: [], unset: [], remove: [], motions: [], queue: [], bucket: BUCKETS - 1, at: 0, queued: 0, bytes: 0, most: Infinity }
    const box = views.get(send)
    const near = nearOf(send)
    const enqueue = (at: number, priority: number) => {
      ;(frame.queue[bucketOf(priority)] ??= []).push(at)
      frame.queued++
    }
    for (const at of visible(player, cache, god)) {
      const old = known.get(ids[at])
      if (!old) {
        const brief = briefOf(at)
        if (brief.fleeting) {
          let away = brief.aim !== player && !(brief.placed && near(brief.x, brief.y))
          const points = sightings[at].points ?? []
          for (let i = 0; away && i + 1 < points.length; i += 2) away = !near(points[i], points[i + 1])
          if (away || frame.bytes >= fleeting) {
            enqueue(at, away ? FIGHTING : FIGHTING * ON_SCREEN * MOVING * 2)
            continue
          }
        }
        check(frame, at)
        continue
      }
      old.seen = frame.pass
      const age = tick - old.checked
      // Сверена в этом же тике: мир подключению ушёл сразу при входе.
      if (age <= 0) continue
      const brief = briefOf(at)
      if (brief.account === player) {
        check(frame, at)
        continue
      }
      let weight = 1
      if (box) {
        const { x, y } = brief
        // Что без места на карте — заказы, потоки в трубах — идёт как ближнее.
        if (!brief.placed) weight = NEAR_SCREEN
        else if (x >= box.left && x <= box.right && y >= box.top && y <= box.bottom) weight = ON_SCREEN
        else if (near(x, y)) weight = NEAR_SCREEN
      }
      const { motion } = brief
      if (motion && old.motion && (motion[0] !== old.motion[0] || motion[1] !== old.motion[1] || motion[2] !== old.motion[2])) weight *= MOVING
      if (brief.hot) weight *= FIGHTING
      enqueue(at, age * weight)
    }
    for (const [id, old] of known) {
      if (old.seen === frame.pass) continue
      known.delete(id)
      if (!old.told) continue
      frame.remove.push(id)
      frame.bytes += REMOVE_BYTES
    }
    return frame
  }

  /** Сверяет до count сущностей из верха очереди кадра. */
  const take = (frame: Frame, count: number) => {
    while (count > 0 && frame.queued > 0) {
      const list = frame.queue[frame.bucket]
      if (!list || frame.at >= list.length) {
        frame.bucket--
        frame.at = 0
        continue
      }
      check(frame, list[frame.at++])
      frame.queued--
      count--
    }
  }

  /** Кадр, каким он уходит подключению. */
  const finish = ({ motions, set, unset, remove }: Frame) =>
    encodeDelta(sim.time.tick, motions, `{"set":[${set.join(',')}],"unset":${JSON.stringify(unset)},"remove":${JSON.stringify(remove)}}`)

  /**
   * Весь мир подключения, которое о нём ещё ничего не знает: при входе и когда мир начинается заново. cache — мир
   * уже разобран (scan) для нескольких подключений подряд.
   */
  const whole = (send: Send, player: number, cache?: Map<number, number[]>) => {
    if (!cache) scan()
    return finish(begin(send, player, cache))
  }

  /** Случайная точка появления: на скале, просторная и подальше от других игроков; undefined — не нашлась. */
  const spawnPoint = () => {
    const others: { x: number; y: number }[] = []
    for (const [, position, owner] of sim.world.query(Position, Owner)) if (owner.player) others.push(position)
    const { left, top, right, bottom } = sim.bounds
    const halfX = Math.max(0, (right - left) / 2 - SPAWN_MARGIN)
    const halfY = Math.max(0, (bottom - top) / 2 - SPAWN_MARGIN)
    for (let apart = SPAWN_APART; apart >= 8; apart /= 2) {
      for (let attempt = 0; attempt < SPAWN_TRIES; attempt++) {
        const x = Math.floor((left + right) / 2 + (Math.random() * 2 - 1) * halfX)
        const y = Math.floor((top + bottom) / 2 + (Math.random() * 2 - 1) * halfY)
        if (terrainAt(sim.land, x, y) !== Terrain.Rock || !isWalkable(sim, x, y)) continue
        if (others.some((other) => Math.hypot(other.x - x, other.y - y) < apart)) continue
        let rock = 0
        for (let dy = -SPAWN_AREA; dy <= SPAWN_AREA; dy++) {
          for (let dx = -SPAWN_AREA; dx <= SPAWN_AREA; dx++) if (terrainAt(sim.land, x + dx, y + dy) === Terrain.Rock) rock++
        }
        // Обрывы не должны запирать базу: технике надо куда-то выехать.
        if (rock >= SPAWN_ROCK && notWalledIn(sim, x, y)) return { x, y }
      }
    }
    return undefined
  }

  /** Новый игрок: стартовый набор в случайном месте, см. spawnPoint; не нашлось — на круге вокруг начала мира. */
  const addPlayer = () => place(nextPlayer++)

  /**
   * Ставит игроку стартовый набор: в случайном месте вдали от других, а не нашлось — на круге вокруг начала мира.
   * С ним игрок получает защиту новичка, см. Rules.protection.
   */
  const place = (player: number) => {
    const point = spawnPoint()
    if (point) {
      spawnStartingUnits(sim, player, point.x, point.y)
      protect(sim, player)
      return player
    }
    const slot = player - 1
    const radius = SPAWN_RADIUS * (1 + Math.floor(slot / SPAWN_SLOTS))
    const angle = (slot / SPAWN_SLOTS) * Math.PI * 2
    const { left, top, right, bottom } = sim.bounds
    const clamp = (value: number, from: number, to: number) => Math.max(from + 2, Math.min(to - 3, Math.round(value)))
    const spot = openSpawn(sim, clamp(Math.cos(angle) * radius, left, right), clamp(Math.sin(angle) * radius, top, bottom))
    spawnStartingUnits(sim, player, spot.x, spot.y)
    protect(sim, player)
    return player
  }

  /**
   * Сжатая карта мира, одна на всех: изменённые тайлы в ней исходные (см. pristineLand), а что из правок знает игрок,
   * он получает следом сообщением tiles. Сжимается заново, когда карта поменялась или мир начался заново.
   */
  let landCache: { sim: Sim; revision: number; frame: Promise<Uint8Array> } | null = null
  const landFrame = () => {
    if (landCache?.sim !== sim || landCache.revision !== sim.land.revision) {
      const frame = deflate(pristineLand(sim)).then((packed) => {
        const bytes = new Uint8Array(packed.length + 1)
        bytes[0] = LAND
        bytes.set(packed, 1)
        return bytes
      })
      landCache = { sim, revision: sim.land.revision, frame }
    }
    return landCache.frame
  }
  /**
   * Шлёт карту мира, если подключение ещё здесь, когда она сожмётся, и сразу — что игрок знает о правках. Правки
   * придут раньше карты, и клиент наложит их, когда она придёт.
   */
  const sendLand = (send: Send, player: number) => {
    void landFrame().then((frame) => {
      if (peers.has(send)) send(frame)
    })
    const edits = editing.has(send) ? currentEdits() : knownEdits(sim.landMemory, player)
    if (edits.length) send(JSON.stringify({ type: 'tiles', edits } satisfies ServerMessage))
    depositsShown.delete(send)
    sendDeposits(send, player)
  }
  /**
   * Месторождения, которые игрок знает: те, что на разведанной им земле. Остальные клиенту не шлются — иначе их видно
   * сквозь туман. cache — слой на игрока за один проход по подключениям.
   */
  const depositsMessage = (player: number, god = false) => {
    const { cells, spots } = saveDeposits(sim.deposits)
    const known: number[] = []
    for (let i = 0; i + 3 < spots.length; i += 4) {
      if (god || sim.vision.exploredIn(player, spots[i], spots[i + 1], DEPOSIT_SIZE, DEPOSIT_SIZE)) known.push(...spots.slice(i, i + 4))
    }
    return JSON.stringify({ type: 'deposits', deposits: { cells, spots: known } } satisfies ServerMessage)
  }
  /** Шлёт подключению слой месторождений, если игрок узнал новые или слой поменялся. */
  const sendDeposits = (send: Send, player: number, cache = new Map<number, string>()) => {
    const god = editing.has(send)
    const slot = god ? -player : player
    let text = cache.get(slot)
    if (text === undefined) cache.set(slot, (text = depositsMessage(player, god)))
    if (depositsShown.get(send) === text) return
    depositsShown.set(send, text)
    send(text)
  }

  /** Все изменённые тайлы, какие они сейчас: редактору правки видны без тумана. */
  const currentEdits = () => {
    const edits: number[] = []
    for (const { x, y } of sim.landMemory.original.values()) edits.push(x, y, ...tileBytes(sim.land, x, y))
    return edits
  }
  /** Какую версию карты получили редакторы: при правке им уходят все изменённые тайлы заново. */
  let landShown = sim.land.revision
  const explored = (player: number) => JSON.stringify({ type: 'explored', map: sim.vision.map(player) } satisfies ServerMessage)
  const welcome = (player: number, id?: string, god = false) =>
    JSON.stringify({ type: 'welcome', player, options: god ? { ...sim.options, fog: false } : sim.options, step: sim.time.step, id, ...(god && { editor: true }) } satisfies ServerMessage)
  /**
   * Подключение получает мир заново, как при входе: при новом стартовом наборе, при входе в редактор и выходе из него.
   * keep — камера остаётся, где была.
   */
  const resync = (send: Send, who: number, keep = false) => {
    const god = editing.has(send)
    send(god || keep ? JSON.stringify({ ...JSON.parse(welcome(who, undefined, god)), keep: true }) : welcome(who))
    sendLand(send, who)
    send(explored(who))
    sent.set(send, new Map())
    shown.set(send, new Set())
    send(whole(send, who))
  }

  return {
    get sim() {
      return sim
    },
    replace(next) {
      sim.destroy()
      sim = next
      fillDeposits(sim)
      depositsSent = sim.deposits.revision
      defeated.clear()
      for (const who of names.keys()) if (isDefeated(sim, who)) defeated.add(who)
      const cache = new Map<number, number[]>()
      scan()
      for (const [send, player] of peers) {
        send(welcome(player))
        sendLand(send, player)
        send(explored(player))
        sent.set(send, new Map())
        send(whole(send, player, cache))
        shown.set(send, new Set())
      }
      announce()
    },
    get peers() {
      return peers.size
    },
    get stateSize() {
      return stateSize
    },
    sending,
    knows: (id) => player !== undefined || (!!id && players.has(id)),
    command: runCommand,
    addCommand: add,
    say: serverSay,
    join(send, id, name, close) {
      if (close) closers.set(send, close)
      let own = player
      // У локальной игры игрок один, и узнавать его не нужно.
      if (own === undefined) {
        own = id ? players.get(id) : undefined
        if (own === undefined) {
          own = addPlayer()
          id = crypto.randomUUID()
          players.set(id, own)
        }
      } else id = undefined
      const joined = own
      const nick = cleanName(name ?? '')
      if (nick || !names.has(joined)) names.set(joined, nick || defaultName(joined))
      // О входе — только когда игрок появился: вторая вкладка того же игрока не в счёт. Локальной игре не нужно.
      if (player === undefined && !online(joined) && !hidden.has(joined)) notice(joined, hostText('joins'))
      peers.set(send, joined)
      // У локальной игры клиент свой, и считать его сообщения незачем.
      if (player === undefined) floods.set(send, { messages: createBucket(MESSAGE_RATE, MESSAGE_BURST), strikes: createBucket(FLOOD_RATE, FLOOD_BURST) })
      shown.set(send, new Set())
      sent.set(send, new Map())
      send(welcome(joined, id))
      sendLand(send, joined)
      send(explored(joined))
      // Мир сразу, не дожидаясь тика: иначе клиент начал бы с пустого экрана.
      send(whole(send, joined))
      sendCommands(send)
      announce()
      return {
        player: joined,
        receive(text) {
          // Администратора счёт не касается: кисть редактора шлёт правки потоком.
          const flood = floods.get(send)
          if (flood && !admins.has(send) && (text.length > MESSAGE_MOST || !flood.messages.take())) {
            if (flood.strikes.take()) return
            floods.delete(send)
            options.log?.(`! ${nameOf(joined)} отключён: слишком много сообщений`)
            send(JSON.stringify({ type: 'refused', reason: hostText('flood') } satisfies ServerMessage))
            closers.get(send)?.()
            return
          }
          let message: unknown
          try {
            message = JSON.parse(text)
          } catch {
            return
          }
          if (typeof message !== 'object' || message === null) return
          const { type, command, text: said } = message as { type?: unknown; command?: unknown; text?: unknown }
          if (type === 'chat') {
            const clean = typeof said === 'string' ? cleanChat(said) : ''
            const now = Date.now()
            if (!clean || now - (lastChat.get(send) ?? -Infinity) < CHAT_GAP) return
            lastChat.set(send, now)
            // Команда никому не уходит: пароль администратора не должен попасть в чат.
            if (clean.startsWith('/')) {
              const caller: Caller = { player: joined, admin: admins.has(send), reply: reply(send) }
              callers.set(caller, send)
              runCommand(clean, caller)
              return
            }
            const line: ChatLine = { player: joined, name: nameOf(joined), text: clean }
            broadcast([line])
            return
          }
          // Проигравший начинает заново: остатки его базы исчезают, а сам он получает новый стартовый набор.
          if (type === 'respawn') {
            if (isDefeated(sim, joined)) restart(joined)
            return
          }
          if (type === 'view') {
            const { left, top, right, bottom } = message as Record<string, unknown>
            const box = [left, top, right, bottom]
            if (!box.every((value) => typeof value === 'number' && Number.isFinite(value))) return
            views.set(send, { left: left as number, top: top as number, right: right as number, bottom: bottom as number })
            return
          }
          // Правка редактора — только от администратора в редакторе; что не удалось, он узнаёт ответом в чат.
          if (type === 'edit') {
            if (!editing.has(send)) return
            const { edit } = message as { edit?: EditOp }
            if (typeof edit !== 'object' || edit === null) return
            try {
              const failed = applyEdit(sim, edit)
              if (failed) reply(send)(hostText(failed))
            } catch {
              reply(send)(hostText('editFailed'))
            }
            return
          }
          if (type !== 'command' || typeof command !== 'object' || command === null) return
          // Цель по номеру сущности клиент не называет: приказ идёт точкой, а цель по ней выбирает симуляция.
          if (!fromClient(command as Command)) return
          // Что внутри команды, проверит сама симуляция: она не доверяет и локальному клиенту.
          sim.send(joined, command as Command)
        },
        leave() {
          peers.delete(send)
          shown.delete(send)
          swept.delete(send)
          depositsShown.delete(send)
          lastChat.delete(send)
          floods.delete(send)
          admins.delete(send)
          editing.delete(send)
          views.delete(send)
          viewsShown.delete(send)
          adminLocked.delete(send)
          closers.delete(send)
          sent.delete(send)
          if (player === undefined && !online(joined) && !hidden.has(joined)) notice(joined, hostText('leaves'))
          announce()
        },
      }
    },
    save() {
      return { sim: sim.save(), players: Object.fromEntries(players), names: Object.fromEntries(names) }
    },
    advance(seconds) {
      const began = performance.now()
      const ticks = sim.advance(seconds)
      // Правки карты — только тем, кто их увидел, см. landMemory.ts. Узнанное игроками без вкладок уже в их памяти.
      const learned = new Map<number, string>()
      for (const player of new Set(peers.values())) {
        const edits = takeLearned(sim.landMemory, player)
        if (edits.length) learned.set(player, JSON.stringify({ type: 'tiles', edits } satisfies ServerMessage))
      }
      sim.landMemory.learned.clear()
      // Месторождения поменялись — всем слой заново: он невелик, а меняется редко. Узнанные разведкой — при обходе ниже.
      if (sim.deposits.revision !== depositsSent) {
        depositsSent = sim.deposits.revision
        const cache = new Map<number, string>()
        for (const [send, player] of peers) sendDeposits(send, player, cache)
      }
      const landChanged = sim.land.revision !== landShown
      landShown = sim.land.revision
      for (const [send, player] of peers) {
        if (editing.has(send)) {
          if (landChanged) send(JSON.stringify({ type: 'tiles', edits: currentEdits() } satisfies ServerMessage))
          continue
        }
        const text = learned.get(player)
        if (text) send(text)
      }
      if (ticks && peers.size) {
        const cache = new Map<number, number[]>()
        const frames: Frame[] = []
        stateSize = 0
        sinceSweep += ticks
        const sweep = sinceSweep >= SWEEP_TICKS
        if (sweep) sinceSweep = 0
        // Защита новичка началась или кончилась — список игроков всем заново.
        const safe = protectedPlayers(sim)
        if (safe.size !== sheltered.size || [...safe].some((who) => !sheltered.has(who))) {
          sheltered = safe
          announce()
        }
        // Поражение — сообщение всем, один раз. Локальной игре не нужно: там игрок один.
        if (editing.size) sendViews()
        if (sweep && player === undefined) {
          for (const who of names.keys()) {
            if (defeated.has(who) || !isDefeated(sim, who)) continue
            defeated.add(who)
            if (!hidden.has(who)) notice(who, hostText('defeated'))
          }
        }
        const expired = sim.traces.expired()
        const deposits = new Map<number, string>()
        /** Сколько байт на подключение в этом тике, см. HostOptions.budget. */
        const most = (options.budget?.bytes ?? Infinity) * sim.time.step * ticks
        scan()
        for (const [send, player] of peers) {
          const known = shown.get(send)!
          for (const id of expired) known.delete(id)
          if (sweep) sendDeposits(send, player, deposits)
          const found = traces(send, player, sweep, Math.ceil((most * TRACE_SHARE) / TRACE_BYTES))
          if (found.length) send(JSON.stringify({ type: 'traces', traces: found } satisfies ServerMessage))
          const frame = begin(send, player, cache, most * FLEETING_SHARE)
          frame.most = Math.max(most * (1 - TRACE_SHARE), frame.bytes + most * QUEUE_SHARE)
          frames.push(frame)
        }
        // Обязательное уже в кадрах; остальное — по кругу, пока есть время и байты.
        const deadline = Math.max(began + (options.budget?.ms ?? sim.time.step * 1000 * TICK_SHARE), performance.now() + SEND_LEAST)
        sending.waited = 1
        let waiting = frames
        do {
          for (const frame of waiting) take(frame, BATCH)
          waiting = waiting.filter((frame) => frame.queued > 0 && frame.bytes < frame.most)
        } while (waiting.length && performance.now() < deadline)
        sending.deferred = 0
        for (const frame of frames) {
          sending.deferred += frame.queued
          const bytes = finish(frame)
          stateSize = Math.max(stateSize, bytes.length)
          frame.send(bytes)
        }
      }
      return ticks
    },
  }
}
