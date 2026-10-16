/**
 * Замер машины модов: сколько стоит завести машину, загрузить мод, отдать событие и вызвать функцию хоста. Одна и та
 * же функция идёт в Bun (`bun tools/modbench.ts`) и в браузере — её зовёт страница с воркером.
 */
import { compileLuau, createMachine, type Machine } from '@kharos/mods/luau'

export interface BenchLine {
  name: string
  value: number
  unit: string
}

const time = (run: () => void) => {
  const began = performance.now()
  run()
  return performance.now() - began
}

export async function benchMods(bytes: ArrayBuffer): Promise<BenchLine[]> {
  const lines: BenchLine[] = []
  const add = (name: string, value: number, unit: string) => lines.push({ name, value: Math.round(value * 1000) / 1000, unit })

  let module!: WebAssembly.Module
  const compileStarted = performance.now()
  module = await compileLuau(bytes)
  add('компиляция luau.wasm', performance.now() - compileStarted, 'мс')

  let said = 0
  const make = () =>
    createMachine(module, {
      memory: 16 << 20,
      events: ['built', 'quiet'],
      functions: { say: () => void said++ },
      log: () => {},
    })
  let vm!: Machine
  add('новая машина', time(() => (vm = make())), 'мс')
  add('память новой машины', vm.memory / 1024, 'КБ')
  const machines = 20
  add(`ещё ${machines} машин, на одну`, time(() => Array.from({ length: machines }, make)) / machines, 'мс')

  const source = `
    local count = 0
    local total = 0
    kharos.on("built", function(e) count += 1 total += e.x end)
    function heavy(n) local s = 0 for i = 1, n do s += i % 7 end return s end
  `
  add('загрузка мода', time(() => vm.load('bench', source, 100)), 'мс')

  const event = { entity: 12, player: 1, building: 'mine', x: 10, y: 20 }
  const count = 100_000
  add('событие без подписчиков', (time(() => {
    for (let i = 0; i < count; i++) vm.emit('quiet', event, 5)
  }) * 1e6) / count, 'нс')
  add('событие с подписчиком', (time(() => {
    for (let i = 0; i < count; i++) vm.emit('built', event, 5)
  }) * 1e6) / count, 'нс')

  const caller = make()
  caller.load('caller', 'kharos.on("built", function(e) kharos.say("x") end)', 100)
  add('событие с вызовом хоста', (time(() => {
    for (let i = 0; i < count; i++) caller.emit('built', event, 5)
  }) * 1e6) / count, 'нс')

  const loops = 10_000_000
  const looper = make()
  looper.load('loop', `local s = 0 for i = 1, ${loops} do s += i % 7 end`, 0)
  add(`цикл Luau, ${loops / 1e6} млн шагов`, time(() => looper.load('loop', `local s = 0 for i = 1, ${loops} do s += i % 7 end`, 0)), 'мс')
  add(`тот же цикл в JS`, time(() => {
    let s = 0
    for (let i = 1; i <= loops; i++) s += i % 7
    if (s < 0) console.log(s)
  }), 'мс')
  add('память машины после замеров', vm.memory / 1024, 'КБ')
  benchSystems(module, add)
  return lines
}

/** Рулёжка к цели: та же работа, что у системы мода, — для сравнения с JS. */
const STEER = `
  local d = math.sqrt(dx * dx + dy * dy)
  if d > 0.01 then
    local s = math.min(0.5, d) / d
    vx, vy = dx * s, dy * s
  else
    vx, vy = 0, 0
  end`

/**
 * Система мода над N юнитами: столбцы buffer (граница — раз за проход, копирование столбцов входит в замер), вызов
 * движка на каждого юнита и та же работа на JS.
 */
function benchSystems(module: WebAssembly.Module, add: (name: string, value: number, unit: string) => void) {
  const units = 10_000
  const passes = 100
  const columns = { x: new Float32Array(units), y: new Float32Array(units), tx: new Float32Array(units), ty: new Float32Array(units), vx: new Float32Array(units), vy: new Float32Array(units) }
  for (let i = 0; i < units; i++) {
    columns.x[i] = Math.random() * 1000
    columns.y[i] = Math.random() * 1000
    columns.tx[i] = Math.random() * 1000
    columns.ty[i] = Math.random() * 1000
  }
  const make = (functions = {}) => createMachine(module, { memory: 16 << 20, events: [], functions, log: () => {} })

  const vm = make()
  vm.load('steer', `
    kharos.system("steer", function(c, n)
      local px, py, tx, ty, ox, oy = c[1], c[2], c[3], c[4], c[5], c[6]
      for i = 0, n - 1 do
        local o = i * 4
        local dx = buffer.readf32(tx, o) - buffer.readf32(px, o)
        local dy = buffer.readf32(ty, o) - buffer.readf32(py, o)
        local vx, vy ${STEER}
        buffer.writef32(ox, o, vx)
        buffer.writef32(oy, o, vy)
      end
    end)`, 100)
  const views = ['x', 'y', 'tx', 'ty', 'vx', 'vy'].map((_, i) => vm.buffer(i + 1, units * 4))
  const pass = () => {
    views[0]().set(columns.x)
    views[1]().set(columns.y)
    views[2]().set(columns.tx)
    views[3]().set(columns.ty)
    const result = vm.system('steer', units, 50)
    if (result.status !== 'ok') throw new Error(result.error)
    columns.vx.set(views[4]())
    columns.vy.set(views[5]())
  }
  pass()
  const buffered = time(() => {
    for (let i = 0; i < passes; i++) pass()
  }) / passes
  add(`система, ${units / 1000} тыс. юнитов, столбцы buffer`, buffered, 'мс за проход')
  add('  на юнита', (buffered * 1e6) / units, 'нс')
  const check = columns.vx[7]

  const calls = make({
    unit: ([i]) => {
      const n = i as number
      return [columns.x[n], columns.y[n], columns.tx[n], columns.ty[n]]
    },
    steer: ([i, vx, vy]) => {
      columns.vx[i as number] = vx as number
      columns.vy[i as number] = vy as number
      return undefined
    },
  })
  calls.load('steer', `
    kharos.system("steer", function(_, n)
      for i = 0, n - 1 do
        local u = kharos.unit(i)
        local dx, dy = u[3] - u[1], u[4] - u[2]
        local vx, vy ${STEER}
        kharos.steer(i, vx, vy)
      end
    end)`, 100)
  calls.system('steer', units, 1000)
  const called = time(() => {
    for (let i = 0; i < passes / 10; i++) calls.system('steer', units, 1000)
  }) / (passes / 10)
  add(`то же, вызов движка на юнита`, called, 'мс за проход')
  add('  на юнита', (called * 1e6) / units, 'нс')

  const js = () => {
    for (let i = 0; i < units; i++) {
      const dx = columns.tx[i] - columns.x[i]
      const dy = columns.ty[i] - columns.y[i]
      const d = Math.sqrt(dx * dx + dy * dy)
      if (d > 0.01) {
        const s = Math.min(0.5, d) / d
        columns.vx[i] = dx * s
        columns.vy[i] = dy * s
      } else columns.vx[i] = columns.vy[i] = 0
    }
  }
  js()
  if (Math.abs(columns.vx[7] - check) > 1e-6) throw new Error('система мода и JS считают по-разному')
  add('то же на JS', time(() => {
    for (let i = 0; i < passes; i++) js()
  }) / passes, 'мс за проход')
}

if (import.meta.main) {
  const bytes = await Bun.file(new URL('../packages/mods/src/luau.wasm', import.meta.url)).arrayBuffer()
  for (const line of await benchMods(bytes)) console.log(`${line.name.padEnd(36)} ${String(line.value).padStart(10)} ${line.unit}`)
}
