import type { Audio } from '../audio/audio'
import type { LoopName } from '../audio/sounds'
import type { Entity } from '../ecs'
import { Assembly, Building, Inventory, Owner, Position, Producer, Reactor, Site, amountOf, buildingSpec, isBurning } from '../sim'
import { nearness, sideOf } from './hearing'
import type { Scene } from './scene'

/** Раз во сколько секунд пересчитывается, что звучит: петли меняются плавно, и чаще не нужно. */
const PERIOD = 0.2
/** Громкость каждой петли при «полном» звучании: фон тише боя и интерфейса. */
const LEVELS: Partial<Record<LoopName, number>> = { hum: 0.22, machinery: 0.25, drill: 0.2 }
/**
 * Сколько вблизи звучащих набирают полную громкость: один танк уже слышно, десять — громче, но не вдесятеро.
 * Громкость — 1 − e^(−сумма/SATURATE).
 */
const SATURATE = 3
/** Здание работает, пока то, что в нём меняется, менялось не раньше этого, в секундах. Как в отрисовке. */
const WORK_HOLD = 0.6
/** За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана. */
const HEARING = 0.6

/**
 * Звук работы зданий. Техника и пехота на ходу — в motors.ts, ветер, погода и даль — в audio/ambience.ts. Каждый вид звука — одна петля, сколько бы ни звучало источников: громкость —
 * по тому, сколько их и насколько они близки к середине экрана, сторона — по тому, где они. Звучит только своё
 * работающее: электростанция гудит всегда, реактор — пока горит стержень; шахта бурит, пока в ней прибывает или
 * убывает руда, цех, переработка и завод стучат, пока движется их работа, — стоящие без материалов молчат.
 */
export function createMachines(scene: Scene, audio: Audio) {
  let since = PERIOD
  const sum = new Map<LoopName, number>()
  const side = new Map<LoopName, number>()
  let clock = 0
  /** Что менялось в здании в прошлый раз и до какой секунды оно поэтому работает. */
  const activity = new Map<Entity, { value: number; until: number }>()
  const busy = (entity: Entity, value: number) => {
    const state = activity.get(entity) ?? { value, until: 0 }
    if (Math.abs(value - state.value) > 1e-9) state.until = clock + WORK_HOLD
    state.value = value
    activity.set(entity, state)
    return clock < state.until
  }

  const add = (name: LoopName, x: number, y: number) => {
    const near = nearness(scene, x, y, HEARING)
    if (near <= 0) return
    sum.set(name, (sum.get(name) ?? 0) + near)
    side.set(name, (side.get(name) ?? 0) + near * sideOf(scene, x))
  }

  return {
    update(seconds: number) {
      clock += seconds
      since += seconds
      if (since < PERIOD) return
      since = 0
      sum.clear()
      side.clear()
      const { world } = scene.sim
      const player = scene.player

      for (const [entity, building, position, owner] of world.query(Building, Position, Owner)) {
        if (owner.player !== player || world.has(entity, Site)) continue
        const spec = buildingSpec(building.type)
        const x = position.x + spec.width / 2
        const y = position.y + spec.height / 2
        if ((spec.power ?? 0) > 0 && (!world.has(entity, Reactor) || isBurning(scene.sim, entity))) add('hum', x, y)
        const inventory = world.get(entity, Inventory)
        if (spec.extract && inventory && busy(entity, Object.values(inventory.items).reduce((total, amount) => total + (amount ?? 0), 0))) add('drill', x, y)
        const assembly = world.get(entity, Assembly)
        const producer = world.get(entity, Producer)
        const work = assembly ? assembly.progress : spec.refines && inventory ? amountOf(inventory, spec.refines) : producer?.queue.length ? producer.progress : undefined
        if (work !== undefined && busy(entity, work)) add('machinery', x, y)
      }
      for (const entity of activity.keys()) if (!world.has(entity, Building)) activity.delete(entity)

      for (const name of Object.keys(LEVELS) as LoopName[]) {
        const total = sum.get(name) ?? 0
        audio.loop(name, LEVELS[name]! * (1 - Math.exp(-total / SATURATE)), total ? (side.get(name) ?? 0) / total : 0)
      }
    },
  }
}
