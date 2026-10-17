/**
 * Та же ходовая, что в base/moves/wheels.luau, — так, как пишется симуляция сейчас (packages/sim-ts): мир @kharos/ecs,
 * компоненты — объекты, система обходит world.query и правит их на месте. Для сравнения с Rust и Luau в main.rs.
 *
 *   bun crates/content-bench/ts.ts [юнитов]                   — с JIT
 *   BUN_JSC_useJIT=0 bun crates/content-bench/ts.ts [юнитов]  — без JIT, как JS на iOS
 */
import { World, component } from '@kharos/ecs'

const UNITS = Number(process.argv[2] ?? 10_000)
const TICKS = 200
const DT = 0.05
const TURN = Math.PI * 2
const SPEED = 4
const TURN_RATE = 3

const Position = component('Position', { x: 0, y: 0 })
const Unit = component('Unit', { type: 'buggy', facing: 0 })
const Target = component('Target', { x: 0, y: 0 })

const wrap = (angle: number) => angle - TURN * Math.floor(angle / TURN + 0.5)

function retarget(i: number, tick: number): [number, number] {
  const seed = (i * 7919 + tick * 104729) % 1000003
  return [(seed % 500) + 0.5, (Math.floor(seed / 500) % 500) + 0.5]
}

const world = new World()
const spread = (i: number, salt: number) => ((i * 2654435761 + salt) % 500) + 0.5
for (let i = 0; i < UNITS; i++) {
  world.spawn(Position({ x: spread(i, 1), y: spread(i, 7) }), Unit({ type: 'buggy', facing: 0 }), Target({ x: spread(i, 13), y: spread(i, 29) }))
}

/** Система хода: обход запроса, правка компонентов на месте — как moveUnits в sim-ts. */
function moveUnits(tick: number) {
  const step = SPEED * DT
  const turn = TURN_RATE * DT
  for (const [entity, position, unit, target] of world.query(Position, Unit, Target)) {
    let dx = target.x - position.x
    let dy = target.y - position.y
    if (dx * dx + dy * dy < 0.25) {
      ;[target.x, target.y] = retarget(entity, tick)
      dx = target.x - position.x
      dy = target.y - position.y
    }
    const off = wrap(Math.atan2(dy, dx) - unit.facing)
    unit.facing += Math.abs(off) <= turn ? off : Math.sign(off) * turn
    const ahead = Math.cos(off)
    if (ahead > 0) {
      position.x += Math.cos(unit.facing) * step * ahead
      position.y += Math.sin(unit.facing) * step * ahead
    }
  }
}

for (let tick = 0; tick < 20; tick++) moveUnits(tick)
const start = performance.now()
for (let tick = 20; tick < 20 + TICKS; tick++) moveUnits(tick)
const jit = process.env.BUN_JSC_useJIT === '0' ? 'без JIT' : 'с JIT'
console.log(`TS как сейчас (@kharos/ecs, объекты), ${jit}: ${((performance.now() - start) / TICKS).toFixed(3)} мс на тик, ${UNITS} юнитов`)
