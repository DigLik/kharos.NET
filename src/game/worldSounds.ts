import type { Audio } from '../audio/audio'
import type { LoopName, SoundName } from '../audio/sounds'
import type { Entity } from '../ecs'
import {
  Accumulator, BUILDINGS, Batch, Beam, Building, Chaser, Converting, Deposit, Doomed, Drop, Glare, Hauler, Health, Mark, Missile, NO_PAVE, Off, Owner, Pave, Player, Position, Producer, Reactor,
  Shield, Silo, Site, Trade, Transfer, Turret, Unit, activeRepairs, buildingSpec, isBurning, isDefeated, powerStates, protectionLeft, wrap,
} from '../sim'
import { Gate, Scan } from '../sim/components'
import { nearness, sideOf } from './hearing'
import { chaserAt, missileAt } from './missiles'
import type { Scene } from './scene'

/** Раз во сколько секунд пересчитываются петли: они меняются плавно, и чаще не нужно. */
const PERIOD = 0.1
/** За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана. */
const HEARING = 0.6
/** Громкость петель при «полном» звучании. */
const LEVELS: Partial<Record<LoopName, number>> = {
  flow: 0.12, overload: 0.4, construct: 0.28, weld: 0.16, tractor: 0.1, servo: 0.12, reactor: 0.3, dome: 0.06, hydraulic: 0.3, steam: 0.4, rocket: 0.6, charge: 0.05,
}
/** Сколько вблизи звучащих набирают полную громкость петли: 1 − e^(−сумма/SATURATE). */
const SATURATE = 2
/** Громкость разовых звуков мира вблизи. */
const VOLUMES: Partial<Record<SoundName, number>> = {
  doorsLocked: 0.7, liftoff: 1.1, nukeBlast: 1.6, shieldHit: 0.35, shieldDown: 0.6, shieldUp: 0.35, gate: 0.45, built: 0.5, collapse: 0.45,
  deploy: 0.5, rollout: 0.45, crates: 0.35, paved: 0.25, radar: 0.1,
  shipLaunch: 0.8, shipLand: 0.8, depleted: 0.4, powerOn: 0.35, powerOff: 0.35, cargo: 0.3, surrender: 0.6,
}
/** Подобранный груз и снятое покрытие тише: это не событие, а уборка. */
const QUIET = 0.5
/** Уведомления слышны, где бы ни была камера. */
const NOTICE_VOLUME = 0.4
/** Чаще этого, в секундах, один звук не повторяется: ворота закрываются всей стеной, дороги ложатся пачкой. */
const MIN_GAP: Partial<Record<SoundName, number>> = {
  gate: 0.15, paved: 0.2, crates: 0.15, shieldHit: 0.08, built: 0.2, rollout: 0.3, radar: 15, brownout: 10, countdown: 0.5, unitReady: 0.5, researched: 1, upgraded: 0.5,
  gift: 1, surrender: 5, powerOn: 0.15, powerOff: 0.15, cargo: 0.2, chat: 0.15, chatSystem: 0.15, coins: 0.3, delivered: 0.5,
}
/** Строящееся работает, пока его прогресс менялся не раньше этого, в секундах: стройка без материалов молчит. */
const WORK_HOLD = 0.6
/** Доли пуска шахты: створки расходятся до DOORS, пар идёт с STEAM, двигатель ревёт с RISE. Как в отрисовке. */
const DOORS = 0.35
const STEAM = 0.45
const RISE = 0.6
/** Медленнее этого, в радианах за тик, башня не жужжит. */
const SERVO_MIN = 0.01
/** Ядерный взрыв слышен издалека: досягаемость во столько раз больше обычной. */
const NUKE_HEARING = 6
/** За сколько секунд до ядерного удара пищит отсчёт, раз в секунду. */
const COUNTDOWN = 10
/**
 * Скачок часов мира больше этого, в тиках, — новый мир или загрузка: всё, что появляется следующие SETTLE секунд, —
 * не события, а мир, который доходит по частям.
 */
const JUMP = 100
const SETTLE = 2

/** Что помнится о стройке: разбирают ли её, сколько сделано, до каких пор она звучит работой и где стоит. */
interface SiteMemory {
  demolish: boolean
  progress: number
  until: number
  at: { x: number; y: number } | null
}

/**
 * Звуки зданий и прочего мира. Петли: перегруз электростанций трещит, реактор пульсирует, стройка стучит, ремонт
 * варит, луч грузовика гудит, башни жужжат, купол щита поёт, накопитель свистит, шахта в пуске воет гидравликой,
 * парит и ревёт двигателем, ракета ревёт в полёте. Разовые — по сверке с прошлым кадром: створки встали, ракета
 * стартовала, ядерный взрыв, щит принял удар, пробит, поднялся, ворота, достроено, разобрано, MCV разворачивается,
 * машина выехала из завода, упал или подобран груз, легло или снято покрытие, импульс радара, корабль космопорта
 * взлетел или сел, рубильник здания, кузов полон или пуст, месторождение выработано, станция сгорела от перегруза,
 * капитуляция. Трубы журчат там, докуда дотёк груз. И уведомления игроку: машина готова, изучено, улучшено,
 * боеголовка готова, награда, деньги за продажу, груз доставлен, подарок, сеть перегружена, защита новичка кончилась,
 * отсчёт до удара, поражение, сообщение в чате.
 */
export function createWorldSounds(scene: Scene, audio: Audio) {
  let since = PERIOD
  let clock = 0
  let tick = -1
  let settled = 0
  const sum = new Map<LoopName, number>()
  const side = new Map<LoopName, number>()
  const last = new Map<SoundName, number>()

  // Что было в прошлом кадре: по этому видно, что изменилось.
  let silos = new Map<Entity, { start: number; ready: boolean; doors: boolean }>()
  let shields = new Map<Entity, { hit: number; up: boolean }>()
  let gates = new Map<Entity, boolean>()
  let sites = new Map<Entity, SiteMemory>()
    let units = new Set<Entity>()
  let laying = new Map<Entity, { x: number; y: number }>()
  let scans = new Map<Entity, number>()
  let levels = new Map<Entity, number>()
  let glares = new Set<Entity>()
  /** Ракеты-перехватчики в полёте: новая — старт со станка. */
  let chasers = new Set<Entity>()
  let research = -1
  let trades = new Map<Entity, { buy: boolean; flying: boolean; at: { x: number; y: number } }>()
  let transfers = new Map<Entity, { x: number; y: number; to: number }>()
  let owners = new Map<Entity, number>()
  let doomed = 0
  let spent = new Map<Entity, boolean>()
  let switched = new Map<Entity, boolean>()
  let loads = new Map<Entity, boolean>()
  let crates = new Map<Entity, { x: number; y: number }>()
  let removing = new Map<Entity, { x: number; y: number }>()
  let packing = new Map<Entity, { x: number; y: number }>()
  let burning = new Map<Entity, { x: number; y: number; health: number }>()
  let sheltered = false
  let states: ReturnType<typeof powerStates> = new Map()
  let statesTick = -1
  let lines = -1
  let lost = false
  let rewards = -1
  let overloaded = false
  let countdown = Infinity

  const add = (name: LoopName, x: number, y: number, weight = 1) => {
    const near = nearness(scene, x, y, HEARING) * weight
    if (near <= 0) return
    sum.set(name, (sum.get(name) ?? 0) + near)
    side.set(name, (side.get(name) ?? 0) + near * sideOf(scene, x))
  }
  const play = (name: SoundName, x: number, y: number, reach = HEARING, volume = 1) => {
    const level = (VOLUMES[name] ?? 0.4) * volume * nearness(scene, x, y, reach)
    if (level <= 0.01 || clock - (last.get(name) ?? -Infinity) < (MIN_GAP[name] ?? 0)) return
    last.set(name, clock)
    audio.play(name, level, sideOf(scene, x) * 0.8)
  }
  const notice = (name: SoundName, volume = NOTICE_VOLUME) => {
    if (clock - (last.get(name) ?? -Infinity) < (MIN_GAP[name] ?? 0)) return
    last.set(name, clock)
    audio.play(name, volume, 0)
  }
  const centerOf = (entity: Entity) => {
    const { world } = scene.sim
    const position = world.get(entity, Position)!
    const building = world.get(entity, Building)
    if (!building) return position
    const spec = buildingSpec(building.type)
    return { x: position.x + spec.width / 2, y: position.y + spec.height / 2 }
  }

  return {
    update(seconds: number) {
      clock += seconds
      const { sim, player } = scene
      const { world, time } = sim
      // Новый мир — всё, что в нём есть, уже было: молча запоминается.
      if (tick < 0 || Math.abs(time.tick - tick) > JUMP) settled = clock + SETTLE
      const fresh = clock < settled
      tick = time.tick
      const launchTicks = Math.round(BUILDINGS.silo.warhead.launch / time.step)
      const own = (entity: Entity) => world.get(entity, Owner)?.player === player

      // Шахты: створки, пар, двигатель, старт; своя боеголовка готова.
      const nextSilos = new Map<Entity, { start: number; ready: boolean; doors: boolean }>()
      for (const [entity, silo] of world.query(Silo)) {
        const { x, y } = centerOf(entity)
        const before = silos.get(entity)
        const share = silo.start >= 0 ? (time.tick + time.alpha - silo.start) / launchTicks : -1
        const doors = share >= DOORS
        nextSilos.set(entity, { start: silo.start, ready: silo.ready, doors })
        if (!fresh && before) {
          if (doors && !before.doors) play('doorsLocked', x, y)
          if (before.start >= 0 && silo.start < 0 && !silo.ready) play('liftoff', x, y, NUKE_HEARING / 2)
          if (silo.ready && !before.ready && own(entity)) notice('warheadReady')
        }
      }
      silos = nextSilos

      // Ядерный взрыв: вспышка в точке удара.
      const nextGlares = new Set<Entity>()
      for (const [entity, , position] of world.query(Glare, Position)) {
        nextGlares.add(entity)
        if (!fresh && !glares.has(entity)) play('nukeBlast', position.x, position.y, NUKE_HEARING)
      }
      glares = nextGlares

      // Перехватчик: старт ракеты со станка — тот же рёв, что у шахты, но короче слышен.
      const nextChasers = new Set<Entity>()
      for (const [entity, chaser] of world.query(Chaser)) {
        nextChasers.add(entity)
        if (!fresh && !chasers.has(entity)) play('liftoff', chaser.fromX, chaser.fromY, NUKE_HEARING / 3, 0.7)
      }
      chasers = nextChasers

      // Отсчёт перед ударом по своим: метка удара видна тем, чьё под ней.
      let left = Infinity
      for (const [, mark] of world.query(Mark)) {
        if (mark.kind === 'strike') left = Math.min(left, (mark.until - time.tick) * time.step)
      }
      const second = Math.ceil(left)
      if (second <= COUNTDOWN && second < countdown) notice('countdown')
      countdown = second

      // Щиты: принял удар, пробит, поднялся.
      const nextShields = new Map<Entity, { hit: number; up: boolean }>()
      for (const [entity, shield] of world.query(Shield)) {
        const up = shield.charge > 0
        nextShields.set(entity, { hit: shield.hit, up })
        const before = shields.get(entity)
        if (fresh || !before) continue
        const { x, y } = centerOf(entity)
        if (shield.hit !== before.hit && up) play('shieldHit', x, y)
        if (!up && before.up) play('shieldDown', x, y)
        if (up && !before.up) play('shieldUp', x, y)
      }
      shields = nextShields

      const nextGates = new Map<Entity, boolean>()
      for (const [entity, gate] of world.query(Gate)) {
        nextGates.set(entity, gate.open)
        const before = gates.get(entity)
        if (!fresh && before !== undefined && before !== gate.open) {
          const { x, y } = centerOf(entity)
          play('gate', x, y)
        }
      }
      gates = nextGates

      // Стройки: достроено — площадка ушла, здание осталось; разобрано — ушло всё, и где оно стояло, помнится заранее.
      const nextSites = new Map<Entity, SiteMemory>()
      for (const [entity, site] of world.query(Site)) {
        const before = sites.get(entity)
        const moved = !!before && Math.abs(before.progress - site.progress) > 1e-9
        nextSites.set(entity, { demolish: site.demolish, progress: site.progress, until: moved ? clock + WORK_HOLD : (before?.until ?? 0), at: world.has(entity, Position) ? centerOf(entity) : null })
      }
      if (!fresh) {
        for (const [entity, before] of sites) {
          if (nextSites.has(entity) || !before.at) continue
          if (before.demolish && !world.has(entity, Building)) play('collapse', before.at.x, before.at.y)
          else if (!before.demolish && world.has(entity, Building)) play('built', before.at.x, before.at.y)
        }
      }
      sites = nextSites

      // MCV разворачивается или сворачивается; кончил — новое на месте старого, и встаёт с лязгом.
      const nextPacking = new Map<Entity, { x: number; y: number }>()
      for (const [entity] of world.query(Converting, Position)) {
        const at = centerOf(entity)
        nextPacking.set(entity, at)
        if (!fresh && !packing.has(entity)) play('deploy', at.x, at.y)
      }
      if (!fresh) for (const [entity, at] of packing) if (!world.has(entity, Position)) play('built', at.x, at.y)
      // Сворачивание кончается тем, что здание пропадает, а машина появляется: смотреть надо на прошлый кадр.
      const unpacked = packing.size > 0
      packing = nextPacking

      // Новая своя машина у своего завода — выехала из него.
      const nextUnits = new Set<Entity>()
      for (const [entity, unit, position] of world.query(Unit, Position)) {
        nextUnits.add(entity)
        // Свёрнутое главное здание — тоже новая машина, но не из завода.
        if (fresh || units.has(entity) || !own(entity) || (unit.type === 'mcv' && unpacked)) continue
        let best: { x: number; y: number } | null = null
        let bestDistance = 5
        for (const [factory, , , , owner] of world.query(Producer, Building, Position, Owner)) {
          if (owner.player !== player) continue
          const center = centerOf(factory)
          const distance = Math.hypot(center.x - position.x, center.y - position.y)
          if (distance < bestDistance) {
            bestDistance = distance
            best = center
          }
        }
        if (best) {
          play('rollout', best.x, best.y)
          notice('unitReady')
        }
      }
      units = nextUnits

      // Груз: упал — удар ящиков; подобрали — тише.
      const nextCrates = new Map<Entity, { x: number; y: number }>()
      for (const [entity, , position] of world.query(Drop, Position)) {
        nextCrates.set(entity, { x: position.x, y: position.y })
        if (!fresh && !crates.has(entity)) play('crates', position.x, position.y)
      }
      if (!fresh) for (const [entity, at] of crates) if (!nextCrates.has(entity)) play('crates', at.x, at.y, HEARING, QUIET)
      crates = nextCrates

      // Покрытие: легло — шлепок; сняли — тише. Укладка и разбор — работа строителей, сущность на тайле: кончилась
      // укладка — тайл покрыт, кончился разбор — пуст. Работу, которую отменили, не слышно.
      const nextLaying = new Map<Entity, { x: number; y: number }>()
      const nextRemoving = new Map<Entity, { x: number; y: number }>()
      for (const [entity, pave, position] of world.query(Pave, Position)) {
        ;(pave.remove ? nextRemoving : nextLaying).set(entity, { x: position.x + 0.5, y: position.y + 0.5 })
      }
      if (!fresh) {
        const paved = (at: { x: number; y: number }) => sim.paving.laid(Math.floor(at.x), Math.floor(at.y)) !== NO_PAVE
        for (const [entity, at] of laying) if (!nextLaying.has(entity) && paved(at)) play('paved', at.x, at.y)
        for (const [entity, at] of removing) if (!nextRemoving.has(entity) && !paved(at)) play('paved', at.x, at.y, HEARING, QUIET)
      }
      laying = nextLaying
      removing = nextRemoving

      // Корабль космопорта: продажа улетает и приносит деньги, покупка садится с грузом.
      const nextTrades = new Map<Entity, { buy: boolean; flying: boolean; at: { x: number; y: number } }>()
      for (const [entity, trade] of world.query(Trade, Position)) {
        const flying = trade.buy || trade.total > 0
        const at = centerOf(entity)
        nextTrades.set(entity, { buy: trade.buy, flying, at })
        const before = trades.get(entity)
        if (!fresh && flying && !trade.buy && !before?.flying) play('shipLaunch', at.x, at.y)
      }
      if (!fresh) {
        for (const [entity, before] of trades) {
          if (nextTrades.has(entity) || !before.flying) continue
          if (before.buy) {
            play('shipLand', before.at.x, before.at.y)
            notice('delivered')
          } else notice('coins')
        }
      }
      trades = nextTrades

      // Корабль с подарком союзнику: взлетает из порта, садится у его главного здания.
      const nextTransfers = new Map<Entity, { x: number; y: number; to: number }>()
      for (const [entity, transfer] of world.query(Transfer, Position)) {
        nextTransfers.set(entity, { x: transfer.x, y: transfer.y, to: transfer.to })
        if (!fresh && !transfers.has(entity)) {
          const at = centerOf(entity)
          play('shipLaunch', at.x, at.y)
        }
      }
      if (!fresh) {
        for (const [entity, before] of transfers) {
          if (nextTransfers.has(entity)) continue
          play('shipLand', before.x, before.y)
          if (before.to === player) notice('gift')
        }
      }
      transfers = nextTransfers

      // Чужое стало своим: подарили юниты или здания.
      const nextOwners = new Map<Entity, number>()
      let given = false
      for (const [entity, owner] of world.query(Owner)) {
        nextOwners.set(entity, owner.player)
        const before = owners.get(entity)
        if (before !== undefined && before !== owner.player && owner.player === player) given = true
      }
      if (!fresh && given) notice('gift')
      owners = nextOwners

      // Капитуляция: база гибнет волной.
      let doom = 0
      let doomAt: { x: number; y: number } | null = null
      let ownDoom = false
      for (const [entity, fate, position] of world.query(Doomed, Position)) {
        // Юнит, который игрок уничтожил сам, — не капитуляция: слышен только его взрыв.
        if (fate.scuttled) continue
        doom++
        doomAt ??= position
        if (own(entity)) ownDoom = true
      }
      if (!fresh && doom && !doomed) {
        if (ownDoom) notice('surrender', NOTICE_VOLUME * 1.5)
        else if (doomAt) play('surrender', doomAt.x, doomAt.y, HEARING * 3)
      }
      doomed = doom

      // Месторождение выработано.
      const nextSpent = new Map<Entity, boolean>()
      for (const [entity, deposit, position] of world.query(Deposit, Position)) {
        nextSpent.set(entity, deposit.spent)
        if (!fresh && deposit.spent && spent.get(entity) === false) play('depleted', position.x, position.y)
      }
      spent = nextSpent

      // Рубильник: здание выключили или включили.
      const nextSwitched = new Map<Entity, boolean>()
      for (const [entity] of world.query(Building, Position)) {
        const off = world.has(entity, Off)
        nextSwitched.set(entity, off)
        const before = switched.get(entity)
        if (fresh || before === undefined || before === off) continue
        const { x, y } = centerOf(entity)
        play(off ? 'powerOff' : 'powerOn', x, y)
      }
      switched = nextSwitched

      // Свой грузовик загрузился доверху или опустел.
      const nextLoads = new Map<Entity, boolean>()
      for (const [entity, hauler, position] of world.query(Hauler, Position)) {
        nextLoads.set(entity, hauler.full)
        const before = loads.get(entity)
        if (!fresh && before !== undefined && before !== hauler.full) play('cargo', position.x, position.y)
      }
      loads = nextLoads

      // Станция сгорела от перегруза: пропала без взрыва, с прочностью на нуле.
      // Состояние сетей меняется только с тиком: считать его каждый кадр незачем.
      if (time.tick !== statesTick) {
        statesTick = time.tick
        states = powerStates(sim)
      }
      const nextBurning = new Map<Entity, { x: number; y: number; health: number }>()
      for (const [entity, state] of states) {
        const health = world.get(entity, Health)
        if (state === 'overload' && health && world.has(entity, Position)) nextBurning.set(entity, { ...centerOf(entity), health: health.value / health.max })
      }
      if (!fresh) for (const [entity, before] of burning) if (!world.has(entity, Position) && before.health < 0.05) play('collapse', before.x, before.y)
      burning = nextBurning

      // Своя защита новичка кончилась.
      const shelter = protectionLeft(sim, player) > 0
      if (!fresh && sheltered && !shelter) notice('protectionOver')
      sheltered = shelter

      // Поражение.
      const defeated = time.tick > 0 && isDefeated(sim, player)
      if (!fresh && defeated && !lost) notice('defeat', NOTICE_VOLUME * 1.5)
      lost = defeated

      // Чат: новое сообщение от других — звонок, от сервера — ниже.
      const chat = (sim as { chat?: readonly { player: number; system?: boolean }[] }).chat
      if (chat) {
        if (!fresh && lines >= 0 && chat.length > lines) {
          const fresher = chat.slice(lines - chat.length).filter((line) => line.system || line.player !== player)
          if (fresher.some((line) => !line.system)) notice('chat')
          else if (fresher.length) notice('chatSystem')
        }
        lines = chat.length
      }

      const nextScans = new Map<Entity, number>()
      for (const [entity, scan] of world.query(Scan)) {
        nextScans.set(entity, scan.tick)
        const before = scans.get(entity)
        if (!fresh && before !== undefined && before !== scan.tick && world.has(entity, Position)) {
          const { x, y } = centerOf(entity)
          play('radar', x, y)
        }
      }
      scans = nextScans

      // Своё здание стало на уровень выше.
      const nextLevels = new Map<Entity, number>()
      for (const [entity, building, owner] of world.query(Building, Owner)) {
        if (owner.player !== player) continue
        nextLevels.set(entity, building.level)
        const before = levels.get(entity)
        if (!fresh && before !== undefined && building.level > before) notice('upgraded')
      }
      levels = nextLevels

      for (const [, data] of world.query(Player)) {
        if (data.id !== player) continue
        if (!fresh && research >= 0 && data.research.length > research) notice('researched')
        if (!fresh && rewards >= 0 && data.rewards.length > rewards) notice('reward')
        research = data.research.length
        rewards = data.rewards.length
      }

      since += seconds
      if (since < PERIOD) return
      since = 0
      sum.clear()
      side.clear()

      // Перегруз: трещат перегруженные станции; своя сеть только что перегрузилась — сигнал.
      let overload = false
      for (const [entity, state] of states) {
        if (state !== 'overload' || !world.has(entity, Position)) continue
        const { x, y } = centerOf(entity)
        add('overload', x, y)
        if (own(entity)) overload = true
      }
      if (!fresh && overload && !overloaded) notice('brownout')
      overloaded = overload

      for (const [entity] of world.query(Reactor, Position)) {
        if (!isBurning(sim, entity)) continue
        const { x, y } = centerOf(entity)
        add('reactor', x, y)
      }
      for (const [entity, accumulator] of world.query(Accumulator, Position)) {
        if (Math.abs(accumulator.flow) < 1e-9) continue
        const { x, y } = centerOf(entity)
        add('charge', x, y)
      }
      for (const [entity, site] of sites) {
        if (clock < site.until && world.has(entity, Building)) {
          const { x, y } = centerOf(entity)
          add('construct', x, y)
        }
      }
      for (const link of activeRepairs(sim)) add('weld', link.toX, link.toY)
      for (const [, beam, position] of world.query(Beam, Position)) if (beam.links.length) add('tractor', position.x, position.y)
      for (const [, turret, position] of world.query(Turret, Position)) {
        if (Math.abs(wrap(turret.angle - turret.prevAngle)) > SERVO_MIN) add('servo', position.x, position.y)
      }
      for (const [entity, shield] of world.query(Shield, Position)) {
        if (shield.charge <= 0) continue
        const { x, y } = centerOf(entity)
        add('dome', x, y)
      }
      for (const [entity, silo] of world.query(Silo, Position)) {
        if (silo.start < 0) continue
        const share = (time.tick + time.alpha - silo.start) / launchTicks
        const { x, y } = centerOf(entity)
        if (share < DOORS) add('hydraulic', x, y)
        if (share >= STEAM) add('steam', x, y, (share - STEAM) / (1 - STEAM))
        if (share >= RISE) add('rocket', x, y, ((share - RISE) / (1 - RISE)) ** 2)
      }
      for (const [entity] of world.query(Converting, Position)) {
        const { x, y } = centerOf(entity)
        add('hydraulic', x, y, 0.6)
      }
      // Трубы: груз в пути звучит там, докуда дотёк.
      for (const [, batch] of world.query(Batch)) {
        const share = (time.tick + time.alpha - batch.sent) / Math.max(1, batch.arrive - batch.sent)
        if (share < 0 || share > 1) continue
        const points = batch.path.filter((node) => world.has(node as Entity, Position)).map((node) => centerOf(node as Entity))
        if (!points.length) continue
        const at = points[Math.min(points.length - 1, Math.floor(share * points.length))]
        add('flow', at.x, at.y)
      }
      for (const [, missile] of world.query(Missile)) {
        const at = missileAt(missile, time.tick + time.alpha, time.step)
        add('rocket', at.x, at.y)
      }
      for (const [, chaser] of world.query(Chaser)) {
        const at = chaserAt(chaser, time.tick + time.alpha)
        add('rocket', at.x, at.y, 0.5)
      }

      for (const [name, level] of Object.entries(LEVELS) as [LoopName, number][]) {
        const total = sum.get(name) ?? 0
        audio.loop(name, level * (1 - Math.exp(-total / SATURATE)), total ? (side.get(name) ?? 0) / total : 0)
      }
    },
  }
}
