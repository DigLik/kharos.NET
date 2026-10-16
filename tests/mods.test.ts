import { expect, test } from 'bun:test'
import { readFileSync } from 'node:fs'
import { DEFAULT_CONFIG } from '@kharos/shared/terrain'
import { MOD_API, luauDefinitions, tsDefinitions } from '@kharos/mods/api'
import { compileLuau, createMachine, type MachineOptions } from '@kharos/mods/luau'
import { createMods, DEFAULT_MOD_LIMITS, type ModReport } from '@kharos/mods/runtime'
import { createHost } from '@kharos/host'
import type { ServerMessage } from '@kharos/protocol'
import { createSim } from '@kharos/sim-ts'

const module = await compileLuau(readFileSync(new URL('../packages/mods/src/luau.wasm', import.meta.url)))

/** Машина с функциями say и echo: say копит строки, echo отдаёт аргумент обратно. */
function machine(options: Partial<MachineOptions> = {}) {
  const said: string[] = []
  const logged: string[] = []
  const vm = createMachine(module, {
    memory: 4 << 20,
    events: ['built', 'tick'],
    functions: {
      say: ([text]) => void said.push(String(text)),
      echo: ([value]) => value,
      fail: () => {
        throw new Error('nope')
      },
    },
    log: (text) => logged.push(text),
    ...options,
  })
  return { vm, said, logged }
}

test('мод подписывается на событие, получает его данные и зовёт функции хоста', () => {
  const { vm, said, logged } = machine()
  expect(vm.load('hello', `kharos.on("built", function(e) kharos.say(e.building .. " " .. e.x .. " " .. tostring(e.extra.ok)) end) print("loaded", 1, nil)`, 50)).toEqual({ status: 'ok' })
  expect(logged).toEqual(['loaded\t1\tnil'])
  expect(vm.emit('built', { building: 'mine', x: 3.5, extra: { ok: true } }, 5)).toEqual({ status: 'ok' })
  expect(said).toEqual(['mine 3.5 true'])
})

test('значения ходят в обе стороны как в JSON: числа, строки, массивы, словари, nil', () => {
  const { vm, said } = machine()
  vm.load('values', `
    local v = kharos.echo({ 1, 2, "три", { a = { b = false } } })
    kharos.say(tostring(#v) .. v[3] .. tostring(v[4].a.b))
    local m = kharos.echo({ x = 1, [5] = "пять" })
    kharos.say(tostring(m.x) .. m["5"])
    kharos.say(tostring(kharos.echo(nil)) .. tostring(kharos.echo("юникод ✓")))
  `, 50)
  expect(said).toEqual(['4триfalse', '1пять', 'nilюникод ✓'])
})

test('песочница: нет os, io, require и загрузки кода, библиотеки и kharos не правятся, данные события тоже', () => {
  const { vm, said } = machine()
  expect(vm.load('probe', `kharos.say(table.concat({ type(os), type(io), type(require), type(loadstring), type(load), type(dofile) }, ","))`, 50).status).toBe('ok')
  expect(said).toEqual(['nil,nil,nil,nil,nil,nil'])
  expect(vm.load('a', 'string.upper = nil', 50)).toMatchObject({ status: 'error', error: expect.stringContaining('readonly') })
  expect(vm.load('b', 'kharos.say = print', 50)).toMatchObject({ status: 'error', error: expect.stringContaining('readonly') })
  // Свои глобальные у мода есть.
  expect(vm.load('c', 'counter = 1 counter += 1 kharos.say(tostring(counter))', 50).status).toBe('ok')
  expect(said.at(-1)).toBe('2')
  vm.load('d', 'kharos.on("tick", function(e) e.x = 2 end)', 50)
  expect(vm.emit('tick', { x: 1 }, 5)).toMatchObject({ status: 'error', error: expect.stringContaining('readonly') })
})

test('бесконечный цикл кончается по сроку, и pcall его не спасает; машина после этого работает', () => {
  const { vm, said } = machine()
  const began = performance.now()
  expect(vm.load('loop', 'while true do end', 20)).toMatchObject({ status: 'timeout', error: expect.stringContaining('time budget exceeded') })
  expect(vm.load('pcall', 'while true do pcall(function() while true do end end) end', 20).status).toBe('timeout')
  expect(vm.load('xpcall', 'xpcall(function() while true do end end, function() while true do end end)', 20).status).toBe('timeout')
  expect(performance.now() - began).toBeLessThan(500)
  vm.load('after', 'kharos.say("жив")', 20)
  expect(said).toEqual(['жив'])
})

test('предел памяти: скрипт, который копит строки, получает ошибку памяти, а машина живёт', () => {
  const { vm, said } = machine({ memory: 2 << 20 })
  expect(vm.load('hog', 'local t = {} for i = 1, 1e7 do t[i] = string.rep("x", 1000) .. i end', 1000)).toMatchObject({ status: 'memory' })
  expect(vm.memory).toBeLessThan(2 << 20)
  vm.load('after', 'kharos.say("жив")', 20)
  expect(said).toEqual(['жив'])
})

test('глубокая вложенность кода — ошибка компиляции, а не падение', () => {
  const { vm } = machine()
  expect(vm.load('deep', `local x = ${'('.repeat(5000)}1${')'.repeat(5000)}`, 50)).toMatchObject({ status: 'error' })
  expect(vm.load('recursion', 'local function f() return f() + 1 end f()', 50)).toMatchObject({ status: 'error', error: expect.stringContaining('stack overflow') })
  expect(vm.broken).toBe(false)
})

test('ошибка в функции хоста приходит скрипту с местом вызова, ошибка подписчика не мешает другим', () => {
  const { vm, said } = machine()
  expect(vm.load('fail', 'kharos.fail()', 50)).toMatchObject({ status: 'error', error: 'fail:1: nope' })
  vm.load('handlers', 'kharos.on("tick", function() error("первый") end) kharos.on("tick", function() kharos.say("второй") end)', 50)
  expect(vm.emit('tick', {}, 5)).toMatchObject({ status: 'error', error: expect.stringContaining('первый') })
  expect(said).toEqual(['второй'])
  expect(vm.load('unknown', 'kharos.on("nope", function() end)', 50)).toMatchObject({ status: 'error', error: expect.stringContaining("unknown event 'nope'") })
})

test('моды: аргументы проверяются по схеме, превысивший срок трижды выключается', () => {
  const reports: ModReport[] = []
  const said: string[] = []
  const mods = createMods(
    module,
    [
      { name: 'good', source: 'kharos.on("built", function(e) kharos.say(kharos.playerName(e.player) or "?") end)' },
      { name: 'typo', source: 'kharos.say(42)' },
      { name: 'slow', source: 'kharos.on("built", function() while true do end end)' },
    ],
    { say: (text) => void said.push(text), playerName: (player) => (player === 1 ? 'Вася' : undefined) },
    (report) => reports.push(report),
    { ...DEFAULT_MOD_LIMITS, event: 2, tick: 4 },
  )
  expect(reports).toContainEqual({ mod: 'typo', kind: 'error', text: "typo:1: bad argument #1 to 'say' (string expected, got number)" })
  expect(mods.list.map((mod) => [mod.name, mod.enabled])).toEqual([['good', true], ['typo', false], ['slow', true]])
  const event = { type: 'built' as const, entity: 7 as never, player: 1, building: 'mine' as const, x: 0, y: 0 }
  for (let i = 0; i < 3; i++) mods.dispatch([event, { ...event, player: 2 }])
  expect(said).toEqual(['Вася', '?', 'Вася', '?', 'Вася', '?'])
  expect(mods.list.map((mod) => [mod.name, mod.enabled])).toEqual([['good', true], ['typo', false], ['slow', false]])
  expect(reports.filter((report) => report.mod === 'slow').map((report) => report.kind)).toEqual(['timeout', 'timeout', 'timeout', 'disabled'])
})

test('хост раздаёт модам события мира, и мод пишет в чат от имени сервера', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256 })
  const advance = sim.advance
  // Стройку до конца здесь не довести быстро — событие подкладывается, как его положила бы стройка.
  sim.advance = (seconds) => {
    const ticks = advance(seconds)
    sim.events.push({ type: 'built', entity: 1 as never, player: 1, building: 'generator', x: 0, y: 0 })
    return ticks
  }
  const source = 'kharos.on("built", function(e) kharos.say(kharos.playerName(e.player) .. " построил " .. e.building) end)'
  const host = createHost(sim, undefined, undefined, { mods: { module, sources: [{ name: 'hello', source }] }, log: () => {} })
  const lines: string[] = []
  host.join((message) => {
    if (typeof message !== 'string') return
    const parsed = JSON.parse(message) as ServerMessage
    if (parsed.type === 'chat') lines.push(...parsed.lines.map((line) => `${line.name}: ${line.text}`))
  }, undefined, 'Вася')
  host.advance(0.05)
  expect(lines.filter((line) => line.includes('построил'))[0]).toMatch(/: Вася построил generator$/)
})

test('описания типов для мододелов совпадают со схемой (bun tools/modapi.ts)', () => {
  expect(readFileSync(new URL('../mods/types/kharos.d.luau', import.meta.url), 'utf8')).toBe(luauDefinitions(MOD_API))
  expect(readFileSync(new URL('../mods/types/kharos.d.ts', import.meta.url), 'utf8')).toBe(tsDefinitions(MOD_API))
})
