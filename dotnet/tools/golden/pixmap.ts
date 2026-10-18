// Эталоны холста обликов для Kharos.Client.Tests (docs/dotnet.md, Н3): Pixmap из TS рисует набор фигур, Canvas в C#
// должен повторить их до байта. Запуск из корня репозитория: bun dotnet/tools/golden/pixmap.ts. Пишет в
// tests/Kharos.Client.Tests/Looks/golden/:
//   pixmap.json — случаи: размер холста, начало координат, операции (с числами как есть) и SHA-256 картинки;
//   pixmap.bin  — картинки RGBA подряд, для разбора расхождений.
// Многоугольник, пиксель и сдвиг — как Canvas в apps/client/src/atlas.rs (у Pixmap их нет): через его fill.
import { createHash } from 'node:crypto'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { Pixmap, toned } from '../../../packages/client/src/render/pixmap'

const dir = join(import.meta.dir, '..', '..', 'tests', 'Kharos.Client.Tests', 'Looks', 'golden')
mkdirSync(dir, { recursive: true })

type Op = [string, ...number[]]
type Case = { name: string; width: number; height: number; ox: number; oy: number; ops: Op[] }

// Детерминированный генератор (mulberry32): эталон не меняется от запуска к запуску.
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

function inside(points: number[], x: number, y: number) {
  let result = false
  const count = points.length / 2
  for (let i = 0, j = count - 1; i < count; j = i++) {
    const xi = points[i * 2], yi = points[i * 2 + 1], xj = points[j * 2], yj = points[j * 2 + 1]
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) result = !result
  }
  return result
}

function run(image: Pixmap, ops: Op[]) {
  const fill = (image as any).fill.bind(image) as (l: number, t: number, r: number, b: number, c: number, f: (x: number, y: number) => boolean) => Pixmap
  for (const [op, ...a] of ops) {
    switch (op) {
      case 'rect': image.rect(a[0], a[1], a[2], a[3], a[4]); break
      case 'circle': image.circle(a[0], a[1], a[2], a[3]); break
      case 'ring': image.ring(a[0], a[1], a[2], a[3], a[4]); break
      case 'line': image.line(a[0], a[1], a[2], a[3], a[4], a[5]); break
      case 'translate': image.originX += a[0]; image.originY += a[1]; break
      case 'pixel': {
        const x = Math.floor(a[0]), y = Math.floor(a[1])
        fill(x, y, x + 1, y + 1, a[2], () => true)
        break
      }
      case 'poly': {
        const points = a.slice(0, -1)
        if (points.length < 6) break
        const xs = points.filter((_, i) => i % 2 === 0), ys = points.filter((_, i) => i % 2 === 1)
        fill(Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys), a[a.length - 1], (x, y) => inside(points, x, y))
        break
      }
      default: throw new Error(op)
    }
  }
}

const TURN = Math.PI * 2
const cases: Case[] = []
const add = (name: string, width: number, height: number, ox: number, oy: number, ops: Op[]) => cases.push({ name, width, height, ox, oy, ops })

// Простые фигуры на целых и половинных координатах: центры пикселей ровно на границе.
add('rect', 16, 16, 0, 0, [['rect', 2, 3, 5, 4, 0xff0000], ['rect', 7.5, 7.5, 3, 3, 0x00ff00], ['rect', 12.4, 1.6, 2.2, 2.2, 0x0000ff]])
add('rect-black', 8, 8, 0, 0, [['rect', 1, 1, 6, 6, 0x000000]])
add('circle', 24, 24, 12, 12, [['circle', 0, 0, 8, 0x41617f], ['circle', 0, 0, 3.5, 0x9bb9d1], ['circle', 5.5, -5.5, 2, 0xffffff], ['circle', -6, 6, 0.5, 0xff00ff], ['circle', 6, 6, 0, 0xff00ff]])
add('ring', 32, 32, 16, 16, [['ring', 0, 0, 10, 2, 0x1f63d8], ['ring', 0, 0, 5.5, 1, 0xf0c95a], ['ring', 0.25, -0.25, 13, 3.3, 0x5aa9ff]])
add('line', 32, 32, 16, 16, [['line', -12, -12, 12, 12, 1, 0xffffff], ['line', -12, 10, 12, 10, 2, 0xff0000], ['line', 0, -14, 0, 14, 1.2, 0x00ff00], ['line', 3, 3, 3, 3, 4, 0x0000ff], ['line', -10.3, 5.7, 8.9, -6.1, 2.5, 0xffb070]])
add('toned', 16, 16, 8, 8, [['rect', -6, -6, 12, 12, toned(0x10358f, 0)], ['circle', 0, 0, 4, toned(0x1f63d8, 1)], ['line', -6, 0, 6, 0, 1, toned(0x5aa9ff, 2)]])
add('clip', 12, 12, 6, 6, [['circle', -8, -8, 6, 0xff0000], ['rect', 3, 3, 20, 20, 0x00ff00], ['line', -20, 0, 20, 0, 3, 0x0000ff], ['ring', 6, -6, 4, 2, 0xffffff]])
add('poly', 24, 24, 12, 12, [['poly', -8, -8, 8, -6, 4, 9, -9, 5, 0x8fe0a8], ['poly', -3, -3, 3, -3, 0, 4, 0xff7a66], ['poly', -10, 0, -4, -10, 2, 0, 0x8fd0ff], ['poly', 0, 0, 1, 1, 0x000000]])
add('star', 32, 32, 16, 16, [['poly', ...star(0, 0, 14, 6, 5), 0xf0c95a]])
add('pixel', 8, 8, 4, 4, [['pixel', -4, -4, 0xff0000], ['pixel', 0.9, -0.1, 0x00ff00], ['pixel', 3.99, 3.5, 0x0000ff], ['pixel', -2.5, 1.5, 0xffffff]])
add('translate', 32, 32, 16, 16, [['circle', 0, 0, 4, 0x41617f], ['translate', 7.3, -4.6], ['circle', 0, 0, 4, 0x9bb9d1], ['line', -3, 0, 3, 0, 1, 0xff0000], ['translate', -14.6, 9.2], ['rect', -2, -2, 4, 4, 0x00ff00]])
add('fraction-origin', 20, 20, 9.5, 10.25, [['circle', 0, 0, 6, 0xff6b5a], ['ring', 0, 0, 7.5, 1, 0xffffff], ['rect', -1.5, -1.5, 3, 3, 0x000000]])

function star(x: number, y: number, outer: number, inner: number, points: number) {
  const out: number[] = []
  for (let i = 0; i < points * 2; i++) {
    const angle = (i / (points * 2)) * TURN - Math.PI / 2
    const r = i % 2 ? inner : outer
    out.push(x + Math.cos(angle) * r, y + Math.sin(angle) * r)
  }
  return out
}

// Повёрнутый корпус по 16 направлениям — как чертежи видов: прямоугольник многоугольником, колёса и ствол линиями.
for (let direction = 0; direction < 16; direction++) {
  const angle = (direction / 16) * TURN
  const c = Math.cos(angle), s = Math.sin(angle)
  const at = (along: number, across: number): [number, number] => [c * along - s * across, s * along + c * across]
  const body = [at(-9, -5), at(9, -5), at(9, 5), at(-9, 5)].flat()
  const ops: Op[] = [['poly', ...body, toned(0x1f63d8, 1)]]
  for (const [along, across] of [[-6, -6], [6, -6], [-6, 6], [6, 6]]) {
    const [x, y] = at(along, across)
    ops.push(['circle', x, y, 2.2, 0x0b111b])
  }
  const [x0, y0] = at(0, 0), [x1, y1] = at(13, 0)
  ops.push(['line', x0, y0, x1, y1, 2, 0x9bb9d1], ['ring', x0, y0, 3, 1, toned(0x5aa9ff, 2)])
  add(`turn-${direction}`, 32, 32, 16, 16, ops)
}

// Случайные фигуры с дробными координатами.
const next = random(20261018)
const coord = (span: number) => (next() - 0.5) * span
for (let index = 0; index < 24; index++) {
  const width = 8 + Math.floor(next() * 40), height = 8 + Math.floor(next() * 40)
  const ops: Op[] = []
  for (let k = 0; k < 12; k++) {
    const color = Math.floor(next() * 0x1000000) + (next() < 0.3 ? Math.floor(next() * 3 + 1) * 0x1000000 : 0)
    const kind = Math.floor(next() * 6)
    const span = Math.max(width, height) * 1.4
    if (kind === 0) ops.push(['rect', coord(span), coord(span), next() * 20, next() * 20, color])
    else if (kind === 1) ops.push(['circle', coord(span), coord(span), next() * 12, color])
    else if (kind === 2) ops.push(['ring', coord(span), coord(span), next() * 12, next() * 4, color])
    else if (kind === 3) ops.push(['line', coord(span), coord(span), coord(span), coord(span), next() * 4, color])
    else if (kind === 4) ops.push(['poly', ...Array.from({ length: 6 + 2 * Math.floor(next() * 4) }, () => coord(span)), color])
    else ops.push(['translate', coord(4), coord(4)])
  }
  add(`random-${index}`, width, height, width / 2 + coord(3), height / 2 + coord(3), ops)
}

const images: Uint8Array[] = []
const out = cases.map(({ name, width, height, ox, oy, ops }) => {
  const image = new Pixmap(width, height)
  image.originX = ox
  image.originY = oy
  run(image, ops)
  images.push(image.data)
  return { name, width, height, ox, oy, ops, sha256: createHash('sha256').update(image.data).digest('hex') }
})
writeFileSync(join(dir, 'pixmap.json'), '[\n' + out.map(one => JSON.stringify(one)).join(',\n') + '\n]\n')
writeFileSync(join(dir, 'pixmap.bin'), Buffer.concat(images))
console.log(`pixmap: ${out.length} случаев, ${images.reduce((sum, image) => sum + image.length, 0)} байт`)
