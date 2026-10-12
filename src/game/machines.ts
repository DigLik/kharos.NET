import type { Audio } from '../audio/audio'
import type { LoopName } from '../audio/synth'
import { Assembly, Building, Inventory, Owner, Position, Producer, Site, amountOf, buildingSpec } from '../sim'
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
/** За краем экрана звук стихает на этом расстоянии, в долях полуширины экрана. */
const HEARING = 0.6

/**
 * Звук работы зданий. Техника и пехота на ходу — в motors.ts, ветер, погода и даль — в audio/ambience.ts. Каждый вид звука — одна петля, сколько бы ни звучало источников: громкость —
 * по тому, сколько их и насколько они близки к середине экрана, сторона — по тому, где они. Звучит только своё
 * работающее: электростанция гудит всегда, шахта бурит, цех и переработка стучат, пока работают, завод — пока
 * в очереди есть заказ.
 */
export function createMachines(scene: Scene, audio: Audio) {
  let since = PERIOD
  const sum = new Map<LoopName, number>()
  const side = new Map<LoopName, number>()

  const add = (name: LoopName, x: number, y: number) => {
    const near = nearness(scene, x, y, HEARING)
    if (near <= 0) return
    sum.set(name, (sum.get(name) ?? 0) + near)
    side.set(name, (side.get(name) ?? 0) + near * sideOf(scene, x))
  }

  return {
    update(seconds: number) {
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
        if ((spec.power ?? 0) > 0) add('hum', x, y)
        if (spec.extract) add('drill', x, y)
        const assembly = world.get(entity, Assembly)
        const inventory = world.get(entity, Inventory)
        const working =
          (assembly && assembly.progress > 0) ||
          (spec.refines && inventory && amountOf(inventory, spec.refines) > 1e-9) ||
          (world.get(entity, Producer)?.queue.length ?? 0) > 0
        if (working) add('machinery', x, y)
      }

      for (const name of Object.keys(LEVELS) as LoopName[]) {
        const total = sum.get(name) ?? 0
        audio.loop(name, LEVELS[name]! * (1 - Math.exp(-total / SATURATE)), total ? (side.get(name) ?? 0) / total : 0)
      }
    },
  }
}
