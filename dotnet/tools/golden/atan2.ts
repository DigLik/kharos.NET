// Эталон Math.atan2 для DetMath.Atan2 (docs/dotnet-n5.md, Н5.3): Kharos.Engine.Tests сверяет его с переносом musl —
// не дальше ulp, а закреплённая сумма битов DetMath одна на всех ОС. Запуск из корня репозитория:
// bun dotnet/tools/golden/atan2.ts. Пишет tests/Kharos.Engine.Tests/Math/golden/atan2.bin:
//   "KAT2" count:u32 (y:f64 x:f64 atan2:f64)×count — little-endian.
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const mathDir = join(import.meta.dir, '..', '..', 'tests', 'Kharos.Engine.Tests', 'Math', 'golden')
mkdirSync(mathDir, { recursive: true })

const view = new DataView(new ArrayBuffer(8))
const bits = (x: number) => (view.setFloat64(0, x, true), view.getBigUint64(0, true))
const fromBits = (b: bigint) => (view.setBigUint64(0, b & 0xffffffffffffffffn, true), view.getFloat64(0, true))
/** Соседнее число на step ulp. */
const ulp = (x: number, step: number) => fromBits(bits(x) + BigInt(step))

/** xorshift32 — тот же ряд при каждом запуске. */
let state = 20261018
const next = () => {
  state ^= state << 13
  state ^= state >>> 17
  state ^= state << 5
  return (state >>> 0) / 4294967296
}
const logUniform = (minExp: number, maxExp: number) => {
  const value = 2 ** (minExp + next() * (maxExp - minExp)) * (1 + next())
  return next() < 0.5 ? -value : value
}

const pairs: [number, number][] = []
// Особые значения во всех сочетаниях.
const special = [0, -0, 1, -1, 2, -2, 0.5, -0.5, 5e-324, -5e-324, 1e-300, -1e300, Number.MAX_VALUE, -Number.MAX_VALUE, Infinity, -Infinity, NaN]
for (const y of special) for (const x of special) pairs.push([y, x])
// Границы ветвей atan (7/16, 11/16, 19/16, 39/16, 2^66, 2^-27) и их соседи — при x = 1 и через деление.
for (const edge of [0.4375, 0.6875, 1.1875, 2.4375, 2 ** 66, 2 ** -27, 1, 1.5, 0.5]) {
  for (const value of [edge, ulp(edge, 1), ulp(edge, -1)]) {
    pairs.push([value, 1], [-value, 1], [value, -1], [-value, -1], [value * 3, 3], [1, value], [-1, -value])
  }
}
// Отношения около 2^64 и 2^-64 (ветви «прямой угол» и «ноль при x < 0»).
for (const exponent of [60, 63, 64, 65, 70]) {
  pairs.push([2 ** exponent, 1], [1, -(2 ** exponent)], [-1, -(2 ** exponent)], [2 ** exponent * 3, 3], [3, -(2 ** exponent) * 3])
}
// Как в игре: разности координат на карте, единичные векторы, крупные и мелкие порядки.
for (let i = 0; i < 2000; i++) pairs.push([(next() - 0.5) * 400, (next() - 0.5) * 400])
for (let i = 0; i < 1000; i++) {
  const angle = (next() - 0.5) * 2 * Math.PI
  pairs.push([Math.sin(angle), Math.cos(angle)])
}
for (let i = 0; i < 1000; i++) pairs.push([Math.round((next() - 0.5) * 64) / 2, Math.round((next() - 0.5) * 64) / 2])
for (let i = 0; i < 800; i++) pairs.push([logUniform(-40, 40), logUniform(-40, 40)])
for (let i = 0; i < 200; i++) pairs.push([logUniform(-1070, 1020), logUniform(-1070, 1020)])

const out = new DataView(new ArrayBuffer(8 + pairs.length * 24))
out.setUint32(0, 0x3254414b, true) // "KAT2"
out.setUint32(4, pairs.length, true)
let at = 8
for (const [y, x] of pairs) {
  out.setFloat64(at, y, true)
  out.setFloat64(at + 8, x, true)
  out.setFloat64(at + 16, Math.atan2(y, x), true)
  at += 24
}
writeFileSync(join(mathDir, 'atan2.bin'), new Uint8Array(out.buffer))
console.log(`atan2: ${pairs.length} пар (${process.platform})`)
