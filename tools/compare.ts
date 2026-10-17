/**
 * Сверка Rust с TS (docs/boundary.md, Г4): одна и та же сцена идёт в TS-симуляции и в Rust-симуляции — системы,
 * перенесённые на Rust, там идут в Rust, — с одними командами, и раз в EVERY тиков сравнивается сохранение целиком:
 * мир, слои, памяти. Печатает первое расхождение — тик, сущность, компонент и оба значения. Тесты смотрят исход, а
 * сверка — весь мир: так видно и то, чего не замечает ни один тест.
 *
 *   bun tools/compare.ts [сцена] [секунд]    сцены: sandbox, march, battle; без имени — все. Нужна сборка wasm.
 *   bun tools/compare.ts 30                  все сцены по 30 с — так в tools/smoke/all.sh.
 */
import type { Entity } from '@kharos/ecs'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Terrain, isCliffFoot, terrainAt } from '@kharos/shared/terrain'
import { createRustSim, loadSimWasm } from '@kharos/sim-rust'
import { spawnBattle, driveBattle, randomArmy } from '@kharos/sim-ts/battle'
import { TRAINING_PLAYER } from '@kharos/sim-ts/combat'
import { spawnSandbox } from '@kharos/sim-ts/sandbox'
import { createSim, type Sim, type SimOptions } from '@kharos/sim-ts/sim'
import { spawnStartingUnits, spawnUnit, type UnitType } from '@kharos/sim-ts/units'
import { Building, Position, Unit } from '@kharos/sim-ts/components'

const TICK = 1 / 20
/** Раз во сколько тиков сравнивается мир. */
const EVERY = 20

interface Scene {
  options: SimOptions
  /** Ставит сцену: одинаково в обеих симуляциях. */
  setup(sim: Sim): void
  /** Раз в секунду: команды сцены. */
  drive?(sim: Sim): void
}

/** Первое здание или юнит этого вида. */
function first(sim: Sim, type: string): Entity | undefined {
  for (const [entity, building] of sim.world.query(Building)) if (building.type === type) return entity
  for (const [entity, unit] of sim.world.query(Unit)) if (unit.type === type) return entity
  return undefined
}

/** Левый верхний тайл ровной скалы 20×12: на ней разворачивается главное здание. */
function plateau(sim: Sim) {
  for (let y = 0; y < 400; y++) {
    search: for (let x = 0; x < 400; x++) {
      for (let tileY = y; tileY < y + 12; tileY++) {
        for (let tileX = x; tileX < x + 20; tileX++) if (terrainAt(sim.land, tileX, tileY) !== Terrain.Rock || isCliffFoot(sim.land, tileX, tileY)) continue search
      }
      return { x, y }
    }
  }
  throw new Error('В мире не нашлось скалы под базу')
}

/** Случайные числа с зерном: армии сцен одинаковы от прогона к прогону. */
function seeded(seed: number) {
  return () => {
    seed = (seed * 1103515245 + 12345) % 2147483648
    return seed / 2147483648
  }
}

const SCENES: Record<string, Scene> = {
  // Готовая база: шахта, переработка, цеха, грузовики, энергия, исследования.
  sandbox: {
    options: { generator: DEFAULT_SETTINGS.generator, size: 1024 },
    setup: (sim) => void spawnSandbox(sim, 1),
  },
  // Две армии идут навстречу через поле и сходятся: ход, объезд, расталкивание, бой.
  march: {
    options: { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false } },
    setup(sim) {
      const types: UnitType[] = ['infantry', 'buggy', 'tank', 'rocketeer']
      const units: Record<number, Entity[]> = { 1: [], 2: [] }
      for (const player of [1, 2]) {
        for (let i = 0; i < 120; i++) units[player].push(spawnUnit(sim, types[i % types.length], player, (player === 1 ? -40 : 40) + (i % 11) * 1.2, -6 + Math.floor(i / 11) * 1.2))
      }
      sim.send(1, { type: 'move', units: units[1], x: 40, y: 0 })
      sim.send(2, { type: 'move', units: units[2], x: -40, y: 0 })
    },
  },
  // Стартовый набор: MCV разворачивается главным зданием, сворачивается обратно и снова разворачивается рядом.
  deploy: {
    options: { generator: DEFAULT_SETTINGS.generator, size: 1024 },
    setup(sim) {
      const at = plateau(sim)
      const units = spawnStartingUnits(sim, 1, at.x + 2, at.y + 2)
      const mcv = units.find((entity) => sim.world.get(entity, Unit)?.type === 'mcv')!
      sim.send(1, { type: 'deploy', unit: mcv })
    },
    drive(sim) {
      const second = Math.round(sim.time.tick * sim.time.step)
      const core = first(sim, 'command')
      const mcv = first(sim, 'mcv')
      if (second === 8 && core !== undefined) sim.send(1, { type: 'pack', building: core })
      if (second === 22 && mcv !== undefined) {
        const { x, y } = sim.world.get(mcv, Position)!
        sim.send(1, { type: 'deploy', unit: mcv, x: Math.floor(x) + 6, y: Math.floor(y) })
      }
    },
  },
  // Показательный бой случайных армий с землёй и воздухом.
  battle: {
    options: { generator: DEFAULT_SETTINGS.generator, size: 1024 },
    setup(sim) {
      const random = seeded(7)
      spawnBattle(sim, 1, 0, 0, randomArmy(undefined, {}, random), randomArmy(undefined, {}, random))
    },
    drive(sim) {
      driveBattle(sim, 1)
      driveBattle(sim, TRAINING_PLAYER)
    },
  },
}

/** Первое расхождение двух сохранений: путь к значению и оба значения. */
function difference(a: unknown, b: unknown, path = ''): string | undefined {
  if (JSON.stringify(a) === JSON.stringify(b)) return undefined
  if (a && b && typeof a === 'object' && typeof b === 'object') {
    if (Array.isArray(a) && Array.isArray(b) && a.length !== b.length && !a.every((item) => item && typeof item === 'object')) return `${path}: длина ${a.length} против ${b.length}`
    const keys = new Set([...Object.keys(a), ...Object.keys(b)])
    for (const key of keys) {
      const found = difference((a as Record<string, unknown>)[key], (b as Record<string, unknown>)[key], `${path}.${key}`)
      if (found) return found
    }
  }
  return `${path}: TS ${JSON.stringify(a)?.slice(0, 300)} против Rust ${JSON.stringify(b)?.slice(0, 300)}`
}

/** Мир сохранения по номерам сущностей: так расхождение называет сущность, а не место в списке. */
const byEntity = (save: ReturnType<Sim['save']>) => {
  const world = (save as unknown as { world: { entities: [number, unknown][] } }).world
  return { ...save, world: { ...world, entities: Object.fromEntries(world.entities) } }
}

const wasm = loadSimWasm(await Bun.file(`${import.meta.dir}/../target/wasm32-unknown-unknown/release/kharos_sim_wasm.wasm`).arrayBuffer())
const args = process.argv.slice(2)
// Число первым — длина для всех сцен.
const [only, length = '60'] = /^\d/.test(args[0] ?? '') ? [undefined, args[0]] : args
let failed = false
for (const [name, scene] of Object.entries(SCENES)) {
  if (only && only !== name) continue
  const ts = createSim(scene.options)
  const rust = createRustSim(wasm, scene.options)
  scene.setup(ts)
  scene.setup(rust)
  const ticks = Math.round(Number(length) / TICK)
  const started = performance.now()
  let found: string | undefined
  let tick = 0
  for (; tick < ticks && !found; tick++) {
    if (scene.drive && tick % Math.round(1 / TICK) === 0) {
      scene.drive(ts)
      scene.drive(rust)
    }
    ts.advance(TICK)
    rust.advance(TICK)
    if ((tick + 1) % EVERY === 0 || tick + 1 === ticks) found = difference(byEntity(ts.save()), byEntity(rust.save()))
  }
  const seconds = ((performance.now() - started) / 1000).toFixed(1)
  if (found) {
    failed = true
    console.log(`${name}: расхождение на тике ${tick} (${seconds} с)\n  ${found}`)
  } else console.log(`${name}: ${tick} тиков — совпадает (${seconds} с)`)
  ts.destroy()
  rust.destroy()
}
process.exit(failed ? 1 : 0)
