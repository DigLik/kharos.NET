import type { Audio } from '../audio/audio'
import type { LoopName, SoundName } from '../audio/sounds'
import type { Entity } from '../ecs'
import {
  Accumulator, BUILDINGS, Beam, Building, Converting, Drop, Glare, Mark, Missile, Owner, Pave, Player, Position, Producer, Reactor, Shield, Silo, Site, Turret, Unit,
  activeRepairs, buildingSpec, isBurning, powerStates, wrap,
} from '../sim'
import { Gate, Scan } from '../sim/components'
import { nearness, sideOf } from './hearing'
import { missileAt } from './missiles'
import type { Scene } from './scene'

/** Раз во сколько секунд пересчитываются петли: они меняются плавно, и чаще не нужно. */
const PERIOD = 0.1
/** За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана. */
const HEARING = 0.6
/** Громкость петель при «полном» звучании. */
const LEVELS: Partial<Record<LoopName, number>> = {
  overload: 0.4, construct: 0.28, weld: 0.16, tractor: 0.1, servo: 0.12, reactor: 0.3, dome: 0.06, hydraulic: 0.3, steam: 0.4, rocket: 0.6, charge: 0.05,
}
/** Сколько вблизи звучащих набирают полную громкость петли: 1 − e^(−сумма/SATURATE). */
const SATURATE = 2
/** Громкость разовых звуков мира вблизи. */
const VOLUMES: Partial<Record<SoundName, number>> = {
  doorsLocked: 0.7, liftoff: 1.1, nukeBlast: 1.6, shieldHit: 0.35, shieldDown: 0.6, shieldUp: 0.35, gate: 0.45, built: 0.5, collapse: 0.45,
  deploy: 0.5, rollout: 0.45, crates: 0.35, paved: 0.25, radar: 0.1,
}
/** Уведомления слышны, где бы ни была камера. */
const NOTICE_VOLUME = 0.4
/** Чаще этого, в секундах, один звук не повторяется: ворота закрываются всей стеной, дороги ложатся пачкой. */
const MIN_GAP: Partial<Record<SoundName, number>> = {
  gate: 0.15, paved: 0.2, crates: 0.15, shieldHit: 0.08, built: 0.2, rollout: 0.3, radar: 15, brownout: 10, countdown: 0.5, unitReady: 0.5, researched: 1, upgraded: 0.5,
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
 * машина выехала из завода, упал груз, легло покрытие, импульс радара. И уведомления игроку: машина готова, изучено,
 * улучшено, боеголовка готова, награда, сеть перегружена, отсчёт до удара.
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
  let converting = new Set<Entity>()
  let units = new Set<Entity>()
  let drops = new Set<Entity>()
  let paves = new Map<Entity, boolean>()
  let scans = new Map<Entity, number>()
  let levels = new Map<Entity, number>()
  let glares = new Set<Entity>()
  let research = -1
  let rewards = -1
  let overloaded = false
  let countdown = Infinity

  const add = (name: LoopName, x: number, y: number, weight = 1) => {
    const near = nearness(scene, x, y, HEARING) * weight
    if (near <= 0) return
    sum.set(name, (sum.get(name) ?? 0) + near)
    side.set(name, (side.get(name) ?? 0) + near * sideOf(scene, x))
  }
  const play = (name: SoundName, x: number, y: number, reach = HEARING) => {
    const level = (VOLUMES[name] ?? 0.4) * nearness(scene, x, y, reach)
    if (level <= 0.01 || clock - (last.get(name) ?? -Infinity) < (MIN_GAP[name] ?? 0)) return
    last.set(name, clock)
    audio.play(name, level, sideOf(scene, x) * 0.8)
  }
  const notice = (name: SoundName) => {
    if (clock - (last.get(name) ?? -Infinity) < (MIN_GAP[name] ?? 0)) return
    last.set(name, clock)
    audio.play(name, NOTICE_VOLUME, 0)
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

      // MCV разворачивается или сворачивается.
      const nextConverting = new Set<Entity>()
      for (const [entity] of world.query(Converting)) {
        nextConverting.add(entity)
        if (!fresh && !converting.has(entity) && world.has(entity, Position)) {
          const { x, y } = centerOf(entity)
          play('deploy', x, y)
        }
      }
      // Сворачивание кончается тем, что здание пропадает, а машина появляется: смотреть надо на прошлый кадр.
      const packing = converting.size > 0
      converting = nextConverting

      // Новая своя машина у своего завода — выехала из него.
      const nextUnits = new Set<Entity>()
      for (const [entity, unit, position] of world.query(Unit, Position)) {
        nextUnits.add(entity)
        // Свёрнутое главное здание — тоже новая машина, но не из завода.
        if (fresh || units.has(entity) || !own(entity) || (unit.type === 'mcv' && packing)) continue
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

      const nextDrops = new Set<Entity>()
      for (const [entity, , position] of world.query(Drop, Position)) {
        nextDrops.add(entity)
        if (!fresh && !drops.has(entity)) play('crates', position.x, position.y)
      }
      drops = nextDrops

      const nextPaves = new Map<Entity, boolean>()
      for (const [entity, pave, position] of world.query(Pave, Position)) {
        nextPaves.set(entity, pave.done)
        if (!fresh && pave.done && paves.get(entity) === false) play('paved', position.x + 0.5, position.y + 0.5)
      }
      paves = nextPaves

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
      for (const [entity, state] of powerStates(sim)) {
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
      for (const [, missile] of world.query(Missile)) {
        const at = missileAt(missile, time.tick + time.alpha, time.step)
        add('rocket', at.x, at.y)
      }

      for (const [name, level] of Object.entries(LEVELS) as [LoopName, number][]) {
        const total = sum.get(name) ?? 0
        audio.loop(name, level * (1 - Math.exp(-total / SATURATE)), total ? (side.get(name) ?? 0) / total : 0)
      }
    },
  }
}
