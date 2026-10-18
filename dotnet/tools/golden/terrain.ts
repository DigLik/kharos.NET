// Эталоны земли и математики для Kharos.Engine.Tests (docs/dotnet.md, Н1): генератор TS — эталон, C# сверяется с ним
// до байта. Запуск из корня репозитория: bun dotnet/tools/golden/terrain.ts. Пишет:
//   tests/Kharos.Engine.Tests/Terrain/golden/land-<имя>.json — суммы SHA-256 земли 1024² по рядам чанков, дальних
//     чанков, saveLand и схемы sampleTerrain, плюс один чанк байтами;
//   tests/Kharos.Engine.Tests/Terrain/golden/edits.json — правки карты, суммы после них и сжатая saveLand;
//   tests/Kharos.Engine.Tests/Math/golden/math.bin — Math.sin, Math.cos, Math.hypot на входах с краевыми случаями;
//   tests/Kharos.Engine.Tests/Math/golden/js.json — hash, Math.round и ToInt32.
import { createHash } from 'node:crypto'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import {
  CHUNK_SIZE,
  DEFAULT_CONFIG,
  applyEdits,
  areaOf,
  batchLand,
  createLand,
  getChunk,
  hash,
  loadLand,
  patchSavedTile,
  sampleTerrain,
  saveLand,
  setPeakTile,
  setTile,
  tileBytes,
  type GeneratorConfig,
  type Land,
  type TileEdit,
} from '../../../packages/shared/src/terrain'

const tests = join(import.meta.dir, '..', '..', 'tests', 'Kharos.Engine.Tests')
const landDir = join(tests, 'Terrain', 'golden')
const mathDir = join(tests, 'Math', 'golden')
mkdirSync(landDir, { recursive: true })
mkdirSync(mathDir, { recursive: true })

const sha = (...parts: Uint8Array[]) => {
  const hasher = createHash('sha256')
  for (const part of parts) hasher.update(part)
  return hasher.digest('hex')
}
const base64 = (bytes: Uint8Array) => Buffer.from(bytes).toString('base64')
const deflate = async (bytes: Uint8Array) => new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new CompressionStream('deflate-raw'))).arrayBuffer())

// Детерминированный генератор входов (mulberry32): эталон не меняется от запуска к запуску.
function random(seed: number) {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let t = state
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

// ---- Земля -------------------------------------------------------------------------------------------------------

/** Пресеты из packages/client/src/gui/screens/menu.ts и случаи, которые трогают края арифметики. */
const CONFIGS: { name: string; config: GeneratorConfig }[] = [
  { name: 'default', config: DEFAULT_CONFIG },
  // Сид 3: у гор на стыке чанков выводимое зависело от порядка сборки (packages/tests/land.test.ts).
  { name: 'seed3', config: { ...DEFAULT_CONFIG, seed: 3 } },
  // Скалистая: сид у края int32 — seed + 17011 переполняется при ToInt32.
  { name: 'rocky', config: { ...DEFAULT_CONFIG, rockLevel: 0.5, swampLevel: 0.3, peakChance: 0.5, seed: 2147480000 } },
  // Острова с отрицательным сидом.
  { name: 'islands', config: { ...DEFAULT_CONFIG, rockLevel: 0.6, swampLevel: 0.5, zoneScale: 40, zoneWarp: 10, seed: -7 } },
  // Пустыня, где горы всюду и почти всегда со спутниками: синус и косинус в деле.
  {
    name: 'peaks',
    config: { ...DEFAULT_CONFIG, rockLevel: 0.45, swampLevel: 0.2, peakChance: 1, peakMinRadius: 1.5, peakMaxRadius: 2, doubleChance: 0.5, tripleChance: 0.5, cliffShare: 0.8, seed: 90210 },
  },
  { name: 'blank', config: { ...DEFAULT_CONFIG, blank: true, seed: 5 } },
]

/** Дальние чанки, вне карты: край мира из генератора, в том числе далеко от нуля. */
const FAR: [number, number][] = [
  [16, 0],
  [-17, -17],
  [-1, 16],
  [1000, -2000],
  [-30000, 29000],
  [32000, 32000],
]

const HALF = 512
const SAMPLE_STEP = 7
const SAMPLE_SIZE = 256

for (const { name, config } of CONFIGS) {
  const started = performance.now()
  const area = areaOf({ left: -HALF, top: -HALF, right: HALF, bottom: HALF })
  const land = createLand(config, area)
  const rows: string[] = []
  for (let cy = area.top; cy < area.top + area.height; cy++) {
    const parts: Uint8Array[] = []
    for (let cx = area.left; cx < area.left + area.width; cx++) parts.push(getChunk(land, cx, cy))
    rows.push(sha(...parts))
  }
  const generated = performance.now() - started
  const saved = saveLand(land)
  const far = FAR.map(([x, y]) => ({ x, y, sha256: sha(getChunk(land, x, y)) }))
  const schema = new Uint8Array(SAMPLE_SIZE * SAMPLE_SIZE)
  const from = -(SAMPLE_SIZE / 2) * SAMPLE_STEP
  for (let y = 0; y < SAMPLE_SIZE; y++) {
    for (let x = 0; x < SAMPLE_SIZE; x++) {
      const { terrain, biome } = sampleTerrain(config, from + x * SAMPLE_STEP, from + y * SAMPLE_STEP)
      schema[y * SAMPLE_SIZE + x] = terrain | (biome << 2)
    }
  }
  const golden = {
    name,
    config,
    area,
    rows,
    save: { length: saved.length, sha256: sha(saved) },
    far,
    chunk: { x: 0, y: 0, base64: base64(getChunk(land, 0, 0)) },
    sample: { from, step: SAMPLE_STEP, size: SAMPLE_SIZE, sha256: sha(schema) },
  }
  writeFileSync(join(landDir, `land-${name}.json`), JSON.stringify(golden, null, 1) + '\n')
  console.log(`land-${name}: ${area.width}×${area.height} чанков за ${generated.toFixed(0)} мс`)
}

// ---- Правки карты ------------------------------------------------------------------------------------------------

type Op =
  | { op: 'tile'; x: number; y: number; terrain: number; biome: number; tier: number; cliff: boolean; dunes?: number }
  | { op: 'peak'; x: number; y: number; biome: number; peakX: number; peakY: number; radius: number }
  | { op: 'batch'; ops: Op[] }
  | { op: 'edits'; edits: number[] }

{
  const config = DEFAULT_CONFIG
  const bounds = { left: -64, top: -64, right: 64, bottom: 64 }
  const area = areaOf(bounds)
  const next = random(20261018)
  const int = (from: number, to: number) => from + Math.floor(next() * (to - from + 1))
  const tileOp = (x: number, y: number): Op => {
    const terrain = int(0, 3)
    const op: Op = { op: 'tile', x, y, terrain, biome: int(0, 255), tier: int(0, 3), cliff: next() < 0.5 }
    if (next() < 0.7) op.dunes = int(0, 2)
    return op
  }
  const ops: Op[] = []
  // Стыки чанков, края карты, тайлы вне её — правки там ничего не делают.
  for (const [x, y] of [[0, 0], [-1, -1], [31, 32], [32, 31], [-64, -64], [63, 63], [-64, 63], [63, -64], [500, 500], [-65, 0], [0, 64]] as const) ops.push(tileOp(x, y))
  for (let i = 0; i < 40; i++) ops.push(tileOp(int(-64, 63), int(-64, 63)))
  for (let i = 0; i < 12; i++) {
    const x = int(-64, 63)
    const y = int(-64, 63)
    ops.push({ op: 'peak', x, y, biome: int(0, 255), peakX: x + 0.5 + int(-10, 10) / 2, peakY: y + 0.5 + int(-10, 10) / 2, radius: int(1, 14) / 4 })
  }
  const inner: Op[] = []
  for (let i = 0; i < 30; i++) inner.push(tileOp(int(-20, 20), int(-20, 20)))
  inner.push({ op: 'peak', x: 3, y: 4, biome: 64, peakX: 4, peakY: 4.5, radius: 1.75 })
  ops.push({ op: 'batch', ops: inner })
  const edits: number[] = []
  for (let i = 0; i < 10; i++) edits.push(int(-64, 63), int(-64, 63), int(0, 2), int(0, 255), 0, int(0, 3) | (next() < 0.5 ? 4 : 0))
  ops.push({ op: 'edits', edits })

  const land = createLand(config, area)
  const run = (op: Op) => {
    if (op.op === 'tile') setTile(land, op.x, op.y, { terrain: op.terrain, biome: op.biome, tier: op.tier, cliff: op.cliff, ...(op.dunes !== undefined && { dunes: op.dunes }) } as TileEdit)
    else if (op.op === 'peak') setPeakTile(land, op.x, op.y, op.biome, op.peakX, op.peakY, op.radius)
    else if (op.op === 'batch') batchLand(land, () => op.ops.forEach(run))
    else applyEdits(land, op.edits)
  }
  ops.forEach(run)

  const chunks = (target: Land) => {
    const parts: Uint8Array[] = []
    for (let cy = area.top; cy < area.top + area.height; cy++) for (let cx = area.left; cx < area.left + area.width; cx++) parts.push(getChunk(target, cx, cy))
    return sha(...parts)
  }
  const saved = saveLand(land)
  const loaded = loadLand(config, saved)
  if (chunks(loaded) !== chunks(land)) throw new Error('loadLand(saveLand) не совпала с картой')
  const patched = saved.slice()
  const patches = [
    [0, 0, 2, 0x40, 0, 1],
    [-64, 63, 0, 0xc0, 0, 0],
    [63, -64, 3, 0x10, 77, 0x88],
    [100, 0, 2, 0, 0, 1],
  ]
  for (const [x, y, ...tile] of patches) patchSavedTile(patched, x, y, tile)
  const stored: number[] = []
  for (const [x, y] of [[0, 0], [-1, -1], [31, 32], [-64, -64], [63, 63], [3, 4], [5, -7]]) stored.push(x, y, ...tileBytes(land, x, y))

  const golden = {
    config,
    bounds,
    area,
    ops,
    revision: land.revision,
    chunks: chunks(land),
    stored,
    save: { length: saved.length, sha256: sha(saved), deflated: base64(await deflate(saved)) },
    pristine: { sha256: sha(saveLand(createLand(config, area))), deflated: base64(await deflate(saveLand(createLand(config, area)))) },
    patches,
    patched: sha(patched),
  }
  writeFileSync(join(landDir, 'edits.json'), JSON.stringify(golden) + '\n')
  console.log(`edits: ${ops.length} правок, ревизия ${land.revision}`)
}

// ---- Математика ---------------------------------------------------------------------------------------------------

const view = new DataView(new ArrayBuffer(8))
const bits = (x: number) => (view.setFloat64(0, x, true), view.getBigUint64(0, true))
const fromBits = (b: bigint) => (view.setBigUint64(0, b & 0xffffffffffffffffn, true), view.getFloat64(0, true))
const hex = (x: number) => bits(x).toString(16).padStart(16, '0')
/** Соседнее число на step ulp (знак — направление по модулю). */
const ulp = (x: number, step: number) => fromBits(bits(x) + BigInt(step))

const next = random(1337)
const logUniform = (minExp: number, maxExp: number) => {
  const value = 2 ** (minExp + next() * (maxExp - minExp)) * (1 + next())
  return next() < 0.5 ? -value : value
}

// Синус и косинус: особые значения, границы ветвей musl (π/4, 3π/4, 5π/4, 7π/4, 9π/4, 2^20·π/2), кратные π/2 и их
// соседи, углы как у спутников гор (hash · 2π) и числа во всём диапазоне.
const angles: number[] = [
  0, -0, 5e-324, -5e-324, 2.2250738585072014e-308, 1e-300, 1e-20, 2 ** -27, 2 ** -26, 1.4901161193847656e-8, 1e-8, 1e-5, 0.1, 0.5, 1, 2, 3, 4, 5, 6, 7, 10, 100, 1e5,
  Math.PI / 4, Math.PI / 2, Math.PI, (3 * Math.PI) / 4, (5 * Math.PI) / 4, (3 * Math.PI) / 2, (7 * Math.PI) / 4, 2 * Math.PI, (9 * Math.PI) / 4,
  2 ** 20 * (Math.PI / 2), 1e6, 1e7, 1e9, 1e15, 2 ** 52, 2 ** 53, 1e22, 1e100, 1e200, 1e300, Number.MAX_VALUE, NaN, Infinity, -Infinity,
]
for (const value of [...angles]) if (value !== 0 && Number.isFinite(value)) angles.push(-value, ulp(value, 1), ulp(value, -1))
for (let k = -64; k <= 64; k++) {
  const value = (k * Math.PI) / 2
  angles.push(value, ulp(Math.abs(value), 1) * Math.sign(value || 1), ulp(Math.abs(value) || 1, -1) * Math.sign(value || 1))
}
for (let i = 0; i < 2000; i++) angles.push(hash(i, i >> 3, 5003 + 1337) * Math.PI * 2)
for (let i = 0; i < 1000; i++) angles.push((next() - 0.5) * 20)
for (let i = 0; i < 800; i++) angles.push(logUniform(-40, 30))
for (let i = 0; i < 200; i++) angles.push(logUniform(30, 1023))

// Гипотенуза: разности как у вершин гор (кратные 0.5 и дробные), обычные числа, крайние порядки и особые значения.
const pairs: [number, number][] = [
  [0, 0], [-0, -0], [0, -0], [3, 4], [-3, 4], [5e-324, 5e-324], [1e-310, 3e-310], [1e308, 1e308], [Number.MAX_VALUE, 1], [1e300, 1e-300],
  [Infinity, NaN], [NaN, Infinity], [-Infinity, 1], [1, -Infinity], [NaN, 1], [1, NaN], [NaN, NaN], [0, NaN], [Infinity, Infinity],
]
for (let a = -10; a <= 10; a++) for (let b = -10; b <= 10; b++) pairs.push([a / 2, b / 2])
for (let i = 0; i < 600; i++) pairs.push([(next() - 0.5) * 8, (next() - 0.5) * 8])
for (let i = 0; i < 1200; i++) pairs.push([(next() - 0.5) * 200, (next() - 0.5) * 200])
for (let i = 0; i < 400; i++) pairs.push([logUniform(-1070, 1020), logUniform(-1070, 1020)])
for (let i = 0; i < 200; i++) {
  const big = logUniform(-30, 30)
  pairs.push([big, big * logUniform(-60, -20)])
}

{
  const records = angles.length * 3 + pairs.length * 3
  const out = new DataView(new ArrayBuffer(12 + records * 8))
  out.setUint32(0, 0x48544d4b, true) // "KMTH"
  out.setUint32(4, angles.length, true)
  out.setUint32(8, pairs.length, true)
  let at = 12
  const put = (value: number) => {
    out.setFloat64(at, value, true)
    at += 8
  }
  for (const x of angles) {
    put(x)
    put(Math.sin(x))
    put(Math.cos(x))
  }
  // Math.hypot двух чисел в Bun совпал с √(a² + b²) в double с масштабом для крайних порядков (см. scaledHypot) — так
  // считает DetMath.Hypot. V8 считает иначе (деление на наибольший и сумма Кэхэна), но для разностей, кратных 0.5, как в
  // генераторе, сумма квадратов точна и обе схемы дают корректно округлённый корень.
  let mismatches = 0
  for (const [a, b] of pairs) {
    const value = Math.hypot(a, b)
    if (!Object.is(value, scaledHypot(a, b))) mismatches++
    put(a)
    put(b)
    put(value)
  }
  writeFileSync(join(mathDir, 'math.bin'), new Uint8Array(out.buffer))
  console.log(`math: ${angles.length} углов, ${pairs.length} пар, расхождений Math.hypot с √(a² + b²): ${mismatches}`)
}

function scaledHypot(a: number, b: number) {
  if (Math.abs(a) === Infinity || Math.abs(b) === Infinity) return Infinity
  if (Number.isNaN(a) || Number.isNaN(b)) return NaN
  a = Math.abs(a)
  b = Math.abs(b)
  if (a < b) [a, b] = [b, a]
  if (b === 0 || b < a * 2 ** -60) return a
  const scale = a > 2 ** 500 ? 2 ** -600 : b < 2 ** -500 ? 2 ** 600 : 1
  a *= scale
  b *= scale
  return Math.sqrt(a * a + b * b) / scale
}

{
  const hashes: number[][] = []
  for (const [x, y, seed] of [
    [0, 0, 1337],
    [-5, 7, 6340],
    [123456, -654321, 4294967305],
    [2147483647, -2147483648, -1],
    [-1, -1, 2147480000 + 17011],
  ]) hashes.push([x, y, seed, hash(x, y, seed)])
  for (let i = 0; i < 300; i++) {
    const x = Math.floor((next() - 0.5) * 2 ** (1 + next() * 32))
    const y = Math.floor((next() - 0.5) * 2 ** (1 + next() * 32))
    const seed = Math.floor((next() - 0.5) * 2 ** (1 + next() * 33))
    hashes.push([x, y, seed, hash(x, y, seed)])
  }
  const roundInputs = [0, -0, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 0.49999999999999994, -0.49999999999999994, 0.4, -0.4, 4503599627370495.5, -4503599627370495.5, 2 ** 52, 2 ** 53 + 2, 1e300, -1e300, 5e-324, -5e-324, Infinity, -Infinity, NaN]
  for (let i = 0; i < 200; i++) roundInputs.push((next() - 0.5) * 2 ** (next() * 60), Math.floor((next() - 0.5) * 1e6) + 0.5)
  const int32Inputs = [0, -0, 1.9, -1.9, 2147483647, 2147483648, -2147483648, -2147483649, 4294967295, 4294967296, 4294967297, 4294967305, -4294967297, 2 ** 53, 2 ** 53 + 2, 2 ** 63, 2 ** 64 + 4096 * 3, 1e20, -1e20, 1e300, 5e-324, Infinity, -Infinity, NaN]
  for (let i = 0; i < 200; i++) int32Inputs.push((next() - 0.5) * 2 ** (next() * 80))
  const golden = {
    hash: hashes,
    round: roundInputs.map((x) => [hex(x), hex(Math.round(x))]),
    toInt32: int32Inputs.map((x) => [hex(x), x | 0]),
    sign: [0, -0, 3, -3, 5e-324, -5e-324, Infinity, -Infinity, NaN].map((x) => [hex(x), hex(Math.sign(x))]),
  }
  writeFileSync(join(mathDir, 'js.json'), JSON.stringify(golden) + '\n')
  console.log(`js: ${hashes.length} хешей`)
}

console.log(`готово: ${CHUNK_SIZE}×${CHUNK_SIZE} тайлов в чанке`)
