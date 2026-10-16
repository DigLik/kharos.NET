import type { Audio } from '../audio/audio'
import type { SoundName } from '../audio/sounds'
import type { Entity } from '@kharos/ecs'
import { Building, Chaser, Health, Mark, Missile, Owner, Position, Shot, Unit, WARNING, alliesIn, buildingSpec } from '../sim'
import { missileAt } from './missiles'
import { tileKey } from '../map/terrain'
import { DEPOSIT_SIZE } from '../sim/deposits'
import type { Scene } from './scene'

/** Не чаще этого, в секундах, игрока предупреждают, что замечен противник. */
const SPOTTED_GAP = 12
/** Не чаще этого, в секундах, игроку сообщают о найденном месторождении: армия в походе находит их пачками. */
const FOUND_GAP = 6
/** Сколько секунд строка уведомления висит в ленте и метка мигает на мини-карте. */
export const ALERT_SECONDS = 8
/** Больше строк лента не держит: старые уходят. */
const MAX_ALERTS = 4
/**
 * Атака — это урон, рядом с которым упал чужой выстрел: не дальше HIT_REACH тайлов от края и не раньше HIT_MEMORY
 * секунд назад. Прочность падает и без врага — у электростанции без энергии, от непогоды, — это не атака.
 */
const HIT_REACH = 3
const HIT_MEMORY = 1.5
/** Своя ракета пропала дальше этого, в тайлах, от точки удара — значит, сбита, а не долетела. */
const DOWNED_REACH = 2
/** Громкость сигналов: они слышны всегда, где бы ни была камера. */
const VOLUME = 0.7

export type AlertKind = 'attacked' | 'spotted' | 'found' | 'nuke' | 'intercepted' | 'ping'

/** Что сказать об уведомлении: ключ строки в разделе hud.alerts словаря. */
export type AlertText = 'spotted' | 'baseAttacked' | 'unitsAttacked' | 'oreFound' | 'nukeLaunched' | 'nukeIncoming' | 'nukeImminent' | 'nukeIntercepted' | 'nukeDowned' | 'allyPing' | 'ownPing'

export interface Alert {
  kind: AlertKind
  text: AlertText
  /** Где это случилось, в тайлах. */
  x: number
  y: number
  /** Когда, в секундах часов уведомлений. */
  at: number
}

/**
 * Уведомления игрока. Раз в кадр смотрит на копию мира: у своего юнита или здания убыло прочности, а рядом упал
 * чужой выстрел — «атакованы»;
 * в виду появился чужой юнит — «замечен противник». Хост шлёт только то, что игрок видит, поэтому чужие юниты
 * в копии мира — это враги в виду. Об атаке предупреждают раз: снова — когда враги пропали из виду и напали опять.
 * О замеченном — не чаще SPOTTED_GAP. Игрок узнал о новом месторождении — разведал его юнитом или радаром — «найдено
 * месторождение», не чаще FOUND_GAP. Чужая ядерная ракета взлетела — «ядерный пуск»: её видят все, и сирена звучит
 * всем. Чужая метка точки удара (её видят те, чьё под ней) — «ядерный удар по нам», а за WARNING секунд до удара —
 * второй сигнал, громче и выше: «удар через 15 секунд». Свой или союзный перехватчик выпустил
 * ракету — «ракета перехвачена»; своя ядерная пропала, не долетев, — «нашу ракету сбили». Метка-сигнал — свой или
 * союзника (см. pings.ts) — «сигнал союзника». Каждое уведомление — строка
 * в ленте, сигнал и метка на мини-карте.
 */
export function createAlerts(scene: Scene, audio: Audio | null) {
  let clock = 0
  let health = new Map<Entity, number>()
  let foes = new Set<Entity>()
  /** Об атаке уже предупредили: снова — после того как враги пропадут из виду. */
  let warned = false
  let spottedAt = -Infinity
  const alerts: Alert[] = []
  /** Куда недавно падали чужие выстрелы: номер выстрела — место и когда его заметили. */
  const impacts = new Map<Entity, { x: number; y: number; at: number }>()
  /** Какие месторождения игрок уже знал: по номеру правки слоя и по миру, с которым они сверены. */
  let deposits: { sim: unknown; generation: number | undefined; revision: number; known: Set<number> } | undefined
  let foundAt = -Infinity
  /** Метки ядерного удара и сигналы, о которых уже сказали. */
  let nukes = new Set<Entity>()
  /** Точки удара, о близости которых уже сказали, и чужие ракеты, о пуске которых сказали. */
  let imminent = new Set<Entity>()
  let launches = new Set<Entity>()
  let chasers = new Set<Entity>()
  /** Свои ядерные ракеты в полёте: где были в прошлый кадр и куда летят. */
  let flights = new Map<Entity, { x: number; y: number; toX: number; toY: number }>()
  let flightsOf: unknown

  const raise = (kind: AlertKind, text: AlertText, x: number, y: number, sound: SoundName = kind, volume = VOLUME) => {
    alerts.push({ kind, text, x, y, at: clock })
    if (alerts.length > MAX_ALERTS) alerts.shift()
    audio?.play(sound, volume, 0)
  }

  /** Середина сущности: у здания — центр основания. */
  const centerOf = (entity: Entity, position: { x: number; y: number }) => {
    const type = scene.sim.world.get(entity, Building)?.type
    if (type === undefined) return position
    const { width, height } = buildingSpec(type)
    return { x: position.x + width / 2, y: position.y + height / 2 }
  }

  return {
    update(seconds: number) {
      clock += seconds
      const { world } = scene.sim
      const player = scene.player

      const nextFoes = new Set<Entity>()
      let spotted: Entity | undefined
      // Союзник — не противник: о его юнитах не предупреждают, см. allies.ts.
      const allies = alliesIn(world, player)
      for (const [entity, , owner] of world.query(Unit, Owner)) {
        if (owner.player === player || owner.player === 0 || allies.includes(owner.player)) continue
        nextFoes.add(entity)
        if (!foes.has(entity)) spotted ??= entity
      }
      foes = nextFoes
      if (!foes.size) warned = false

      const nextNukes = new Set<Entity>()
      const nextImminent = new Set<Entity>()
      for (const [entity, mark, position] of world.query(Mark, Position)) {
        nextNukes.add(entity)
        // Сигнал на карте — свой или союзника: чужих хост не шлёт.
        if (mark.kind === 'ping') {
          if (!nukes.has(entity)) raise('ping', mark.player === player ? 'ownPing' : 'allyPing', position.x, position.y)
          continue
        }
        // Место пуска — только метка на карте: о самом пуске говорит взлетевшая ракета, её видят все.
        if (mark.kind !== 'strike' || mark.player === player || allies.includes(mark.player)) continue
        // Точка удара: первый сигнал — когда узнали о ней, второй, громче, — когда до удара осталось мало. Узнали
        // уже под конец — только второй.
        const close = (mark.until - scene.sim.time.tick) * scene.sim.time.step <= WARNING
        if (close) {
          nextImminent.add(entity)
          if (!imminent.has(entity)) raise('nuke', 'nukeImminent', position.x, position.y, 'nukeClose', 1)
        } else if (!nukes.has(entity)) raise('nuke', 'nukeIncoming', position.x, position.y)
      }
      nukes = nextNukes
      imminent = nextImminent

      const nextChasers = new Set<Entity>()
      for (const [entity, chaser] of world.query(Chaser)) {
        nextChasers.add(entity)
        if (!chasers.has(entity) && (chaser.player === player || allies.includes(chaser.player))) raise('intercepted', 'nukeIntercepted', chaser.toX, chaser.toY)
      }
      chasers = nextChasers
      const { time } = scene.sim
      // Другой мир — перезаход или новая партия: пропавшие ракеты прежнего не сбиты.
      if (flightsOf !== scene.sim) flights.clear()
      flightsOf = scene.sim
      const nextFlights = new Map<Entity, { x: number; y: number; toX: number; toY: number }>()
      const nextLaunches = new Set<Entity>()
      for (const [entity, missile] of world.query(Missile)) {
        // Чужая ракета (у неё нет точки удара, см. wire.ts) в воздухе: сирена всем, строка ведёт к месту старта.
        if (missile.blind) {
          nextLaunches.add(entity)
          if (!launches.has(entity)) raise('nuke', 'nukeLaunched', missile.fromX, missile.fromY)
          continue
        }
        const { x, y } = missileAt(missile, time.tick + time.alpha, time.step)
        nextFlights.set(entity, { x, y, toX: missile.toX, toY: missile.toY })
      }
      // Своя ракета пропала далеко от точки удара: её сбили. Долетевшая пропадает в самой точке.
      for (const [entity, flight] of flights) {
        if (!nextFlights.has(entity) && Math.hypot(flight.toX - flight.x, flight.toY - flight.y) > DOWNED_REACH) raise('nuke', 'nukeDowned', flight.x, flight.y)
      }
      flights = nextFlights
      launches = nextLaunches
      if (spotted !== undefined && clock - spottedAt >= SPOTTED_GAP) {
        spottedAt = clock
        const { x, y } = world.get(spotted, Position)!
        raise('spotted', 'spotted', x, y)
      }

      // Месторождения игрок знает только разведанные: хост шлёт слой заново, когда он узнал новые.
      const layer = scene.sim.deposits
      const generation = (scene.sim as { generation?: number }).generation
      // Слой в пути (мир только принят, хост ещё шлёт) пуст не по незнанию игрока: судить по нему не о чем — иначе
      // каждый перезаход объявил бы находкой всё, что игрок знал и раньше.
      if (layer.complete && !layer.pending && (deposits?.sim !== scene.sim || deposits.generation !== generation || deposits.revision !== layer.revision)) {
        const known = new Set<number>()
        let fresh: { x: number; y: number } | undefined
        // Тот же мир, что в прошлый раз: чего в нём не было, то найдено сейчас. Новый мир — всё в нём уже знакомо.
        const before = deposits?.sim === scene.sim && deposits.generation === generation ? deposits.known : undefined
        for (const spots of layer.cells.values()) {
          for (const spot of spots) {
            const key = tileKey(spot.x, spot.y)
            known.add(key)
            if (before && !before.has(key)) fresh ??= spot
          }
        }
        deposits = { sim: scene.sim, generation, revision: layer.revision, known }
        if (fresh && clock - foundAt >= FOUND_GAP) {
          foundAt = clock
          raise('found', 'oreFound', fresh.x + DEPOSIT_SIZE / 2, fresh.y + DEPOSIT_SIZE / 2)
        }
      }

      for (const [entity, shot] of world.query(Shot)) {
        if (shot.player !== player && shot.player !== 0 && !impacts.has(entity)) impacts.set(entity, { x: shot.toX, y: shot.toY, at: clock })
      }
      for (const [entity, impact] of impacts) if (clock - impact.at > HIT_MEMORY) impacts.delete(entity)
      /** Упал ли рядом с сущностью чужой выстрел: (x, y) — её угол, size — ширина и высота. */
      const shotAt = (x: number, y: number, width: number, height: number) => {
        for (const impact of impacts.values()) {
          const dx = Math.max(x - impact.x, 0, impact.x - (x + width))
          const dy = Math.max(y - impact.y, 0, impact.y - (y + height))
          if (Math.hypot(dx, dy) <= HIT_REACH) return true
        }
        return false
      }

      const next = new Map<Entity, number>()
      let hurt: Entity | undefined
      for (const [entity, life, owner] of world.query(Health, Owner)) {
        if (owner.player !== player) continue
        next.set(entity, life.value)
        const before = health.get(entity)
        if (hurt !== undefined || before === undefined || life.value >= before - 1e-6) continue
        const position = world.get(entity, Position)
        if (!position) continue
        const type = world.get(entity, Building)?.type
        const size = type === undefined ? { width: 0, height: 0 } : buildingSpec(type)
        if (shotAt(position.x, position.y, size.width, size.height)) hurt = entity
      }
      health = next
      if (hurt !== undefined && !warned) {
        warned = true
        const position = world.get(hurt, Position)
        if (position) {
          const { x, y } = centerOf(hurt, position)
          raise('attacked', world.has(hurt, Building) ? 'baseAttacked' : 'unitsAttacked', x, y)
        }
      }
      while (alerts.length && clock - alerts[0].at > ALERT_SECONDS) alerts.shift()
    },
    /** Уведомления, которые ещё видны: от старых к новым, с возрастом в секундах. */
    current: () => alerts.map((alert) => ({ ...alert, age: clock - alert.at })),
  }
}

export type Alerts = ReturnType<typeof createAlerts>
