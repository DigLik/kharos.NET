/**
 * Эталон графики base для тестов .NET: кадры чертежей из TS-клиента (unitArt.ts, buildingArt.ts, depositsPass.ts) —
 * сырые RGBA, сведённые к хешу на кадр. Запуск из корня репозитория (после `bun install`):
 *   bun dotnet/tests/Kharos.Base.Tests/Looks/dump.ts dotnet/tests/Kharos.Base.Tests/Looks/golden/art.json
 * Тот же порядок кадров, что у Look.Images клиента: фаза за фазой, внутри — направления. Для каждого вида — хеши всех
 * кадров (первые 8 байт SHA-256) и один кадр целиком (образец для разбора расхождения). Портреты — обрезанные по
 * закрашенному, в маске цвета стороны (как рисует base); огни кадра — как emit в TS.
 *
 * Рядом, в art-more.json (тот же каталог), — остальное, что рисует клиент TS (Н6.4, уровень 1): все здания (buildingsSprites:
 * варианты × кадры простоя и работы, огни), эффекты (взрыв, клубы, молнии), следы и остовы, значки тревог, дропы, покрытие,
 * значки грузов, портреты зданий, покрытия и уровней. У групп кадров разного размера есть sizes ("ШxВ" через пробел).
 */
import { Pixmap } from '../../../../packages/client/src/render/pixmap'
import { MASK_TEAM, TURRET_ART, UNIT_ART, UNIT_GAITS, UNIT_LIGHTS, UNIT_TRACES } from '../../../../packages/client/src/game/units/unitArt'
import { ART_FRAMES, BUILDING_ART } from '../../../../packages/client/src/game/buildings/buildingArt'
import { depositsSprites } from '../../../../packages/client/src/game/depositsPass'
import units from '../../../../packages/base/data/units.json'
import { buildingsSprites } from '../../../../packages/client/src/game/buildings/buildingsPass'
import { combatSprites } from '../../../../packages/client/src/game/combatPass'
import { decalsSprites } from '../../../../packages/client/src/game/decalsPass'
import { dropsSprites } from '../../../../packages/client/src/game/dropsPass'
import { pavingSprites } from '../../../../packages/client/src/game/pavingPass'
import { powerSprites } from '../../../../packages/client/src/game/powerPass'
import { selectionSprites } from '../../../../packages/client/src/game/selectionPass'
import { paintGood } from '../../../../packages/client/src/game/goodIcons'
import { buildingCanvas, levelCanvas, paveCanvas, type PaveIcon } from '../../../../packages/client/src/game/portraits'
import type { Sprite } from '../../../../packages/client/src/render/atlas'
import { BUILDING_TYPES, GOODS, UNIT_TYPES } from '@kharos/sim-ts'

const TURN = Math.PI * 2
const DIRECTIONS = 64
const PHASES = 4
const PORTRAIT_ANGLE = -Math.PI / 4

const hash = (data: Uint8Array) => new Bun.CryptoHasher('sha256').update(data).digest('hex').slice(0, 16)
const base64 = (data: Uint8Array) => Buffer.from(data).toString('base64')

type Light = [number, number, number, number]
interface Entry {
  width: number
  height: number
  /** Хеши кадров через пробел. */
  hashes: string
  /** Кадр целиком — только у юнита багги, турели стрелка и завода. */
  sample?: { index: number; rgba: string }
  lights?: Light[][]
  portrait?: { width: number; height: number; hash: string }
  /** Юниты: походка, огни ([вдоль, поперёк, радиус]), луч ([вдоль, длина, ближняя, расширение, яркость]), колея. */
  gait?: string
  lamps?: number[][]
  beam?: number[]
  trace?: { sides: number[]; width: number }
}

/** Обрезка по закрашенному, как Look.Crop в клиенте: прямоугольник непрозрачных пикселей. */
function crop(image: Pixmap) {
  const { width, height, data } = image
  let left = width
  let top = height
  let right = 0
  let bottom = 0
  for (let y = 0; y < height; y++)
    for (let x = 0; x < width; x++)
      if (data[(y * width + x) * 4 + 3] !== 0) {
        left = Math.min(left, x)
        top = Math.min(top, y)
        right = Math.max(right, x + 1)
        bottom = Math.max(bottom, y + 1)
      }
  if (right <= left) return { width: 1, height: 1, hash: hash(new Uint8Array(4)) }
  const w = right - left
  const h = bottom - top
  const out = new Uint8Array(w * h * 4)
  for (let y = 0; y < h; y++) out.set(data.subarray(((top + y) * width + left) * 4, ((top + y) * width + left + w) * 4), y * w * 4)
  return { width: w, height: h, hash: hash(out) }
}

const SAMPLES = new Set(['units/buggy', 'turrets/gunner', 'buildings/factory'])

function entry(name: string, frames: Pixmap[], sampleAt: number, lights?: Light[][], portrait?: Pixmap): Entry {
  return {
    width: frames[0].width,
    height: frames[0].height,
    hashes: frames.map((frame) => hash(frame.data)).join(' '),
    ...(SAMPLES.has(name) ? { sample: { index: sampleAt, rgba: base64(frames[sampleAt].data) } } : {}),
    ...(lights ? { lights } : {}),
    ...(portrait ? { portrait: crop(portrait) } : {}),
  }
}

const out: Record<string, Entry> = {}

// Юниты: 4 фазы хода × 64 направления (летающие — одна фаза). Портрет: угол вправо-вверх, турели — на своих местах
// (крепления — из данных юнита, как drawMounts в portraits.ts).
const MOUNTS = Object.fromEntries(Object.entries(units as Record<string, { mounts?: { turret: string; along: number; across: number }[] }>).map(([name, unit]) => [name, unit.mounts ?? []])) as Record<string, { turret: keyof typeof TURRET_ART; along: number; across: number }[]>
for (const [name, art] of Object.entries(UNIT_ART)) {
  const air = UNIT_GAITS[name as keyof typeof UNIT_GAITS] === 'air'
  const frames: Pixmap[] = []
  for (let phase = 0; phase < (air ? 1 : PHASES); phase++)
    for (let direction = 0; direction < DIRECTIONS; direction++) {
      const image = new Pixmap(32, 32)
      image.originX = image.originY = 16
      art(image, (direction / DIRECTIONS) * TURN, MASK_TEAM, phase)
      frames.push(image)
    }
  const portrait = new Pixmap(32, 32)
  portrait.originX = portrait.originY = 16
  art(portrait, PORTRAIT_ANGLE, MASK_TEAM, 0)
  for (const mount of MOUNTS[name] ?? []) {
    const x = (Math.cos(PORTRAIT_ANGLE) * mount.along - Math.sin(PORTRAIT_ANGLE) * mount.across) * 16
    const y = (Math.sin(PORTRAIT_ANGLE) * mount.along + Math.cos(PORTRAIT_ANGLE) * mount.across) * 16
    portrait.originX += x
    portrait.originY += y
    TURRET_ART[mount.turret](portrait, PORTRAIT_ANGLE, MASK_TEAM, 0)
    portrait.originX -= x
    portrait.originY -= y
  }
  const key = name as keyof typeof UNIT_GAITS
  const light = UNIT_LIGHTS[key]
  out[`units/${name}`] = {
    ...entry(`units/${name}`, frames, air ? 9 : 64 + 9, undefined, portrait),
    gait: UNIT_GAITS[key],
    lamps: light.lamps.map((lamp) => [lamp.along, lamp.across, lamp.glow]),
    beam: [light.beam.along, light.beam.length, light.beam.near, light.beam.spread, light.beam.level],
    ...(UNIT_TRACES[key] ? { trace: UNIT_TRACES[key] } : {}),
  }
}

// Турели: 64 направления.
for (const [name, art] of Object.entries(TURRET_ART)) {
  const frames: Pixmap[] = []
  for (let direction = 0; direction < DIRECTIONS; direction++) {
    const image = new Pixmap(32, 32)
    image.originX = image.originY = 16
    art(image, (direction / DIRECTIONS) * TURN, MASK_TEAM, 0)
    frames.push(image)
  }
  out[`turrets/${name}`] = entry(`turrets/${name}`, frames, 9)
}

// Завод: 16 кадров цикла, огни кадра и портрет (первый кадр работы с полем в 2 пикселя).
{
  const art = BUILDING_ART.factory
  const frames: Pixmap[] = []
  const lights: Light[][] = []
  for (let i = 0; i < ART_FRAMES; i++) {
    const image = new Pixmap(art.width * 16 + 32, art.height * 16 + 32)
    image.originX = image.originY = 16
    const frame: Light[] = []
    art.draw(image, i / ART_FRAMES, (x, y, size, level) => frame.push([x, y, size, level]))
    frames.push(image)
    lights.push(frame)
  }
  const portrait = new Pixmap(art.width * 16 + 4, art.height * 16 + 4)
  portrait.originX = portrait.originY = 2
  art.draw(portrait, 0, () => {}, 0, true)
  out['buildings/factory'] = entry('buildings/factory', frames, 5, lights, portrait)
}

// Месторождения: один кадр 32 × 32 на вид.
const types = ['metal', 'silicon', 'fuel', 'kharite', 'lithium', 'uranium']
depositsSprites().forEach((sprite, i) => {
  const image = new Pixmap(32, 32)
  sprite.draw(image)
  out[`deposits/${types[i]}`] = entry(`deposits/${types[i]}`, [image], 0, undefined, image)
})

// ---- art-more.json -------------------------------------------------------------------------------------------------

interface More {
  width: number
  height: number
  hashes: string
  /** Размеры кадров, если они разные. */
  sizes?: string
  names?: string[]
  /** Здания: число вариантов, кадров на вариант (простой, затем работа), огни каждого варианта. */
  variants?: number
  perVariant?: number
  working?: boolean
  lights?: { x: number; y: number; size: number; levels: (number | null)[] }[][]
  portrait?: { width: number; height: number; hash: string }
  sample?: { index: number; rgba: string }
}
const more: Record<string, More> = {}

function render(sprite: Sprite) {
  const image = new Pixmap(sprite.width, sprite.height)
  sprite.draw(image)
  return image
}

function group(name: string, sprites: Sprite[], extra: Partial<More> = {}, sampleAt?: number) {
  const images = sprites.map(render)
  const same = images.every((i) => i.width === images[0].width && i.height === images[0].height)
  more[name] = {
    width: images[0].width,
    height: images[0].height,
    hashes: images.map((i) => hash(i.data)).join(' '),
    ...(same ? {} : { sizes: images.map((i) => `${i.width}x${i.height}`).join(' ') }),
    ...(sampleAt !== undefined ? { sample: { index: sampleAt, rgba: base64(images[sampleAt].data) } } : {}),
    ...extra,
  }
}

// Портреты строит toCanvas браузера (холст DOM); подставляем холст, который запоминает вырезанный drawImage прямоугольник
// кадра: это те самые обрезанные по закрашенному пиксели, дальше (масштаб) идёт уже работа браузера.
class FakeImageData {
  constructor(public data: Uint8ClampedArray, public width: number, public height: number) {}
}
class FakeCanvas {
  width = 0
  height = 0
  source?: FakeImageData
  crop?: { width: number; height: number; hash: string }
  getContext() {
    const canvas = this
    return {
      imageSmoothingEnabled: false,
      imageSmoothingQuality: 'low',
      putImageData(image: FakeImageData) {
        canvas.source = image
      },
      drawImage(from: FakeCanvas, left: number, top: number, w: number, h: number) {
        const { data, width } = from.source!
        const part = new Uint8Array(w * h * 4)
        for (let y = 0; y < h; y++) part.set(data.subarray(((top + y) * width + left) * 4, ((top + y) * width + left + w) * 4), y * w * 4)
        canvas.crop = { width: w, height: h, hash: hash(part) }
      },
    }
  }
}
const globals = globalThis as unknown as { document: unknown; ImageData: unknown }
globals.ImageData = FakeImageData
globals.document = { createElement: () => new FakeCanvas() }
const portraitOf = (canvas: unknown) => (canvas as FakeCanvas).crop!

// Здания: по типу — варианты, в каждом кадры простоя (ART_FRAMES) и, если есть вид работы, ещё столько же кадров работы;
// порядок как в атласе TS (тип, вариант, кадр).
{
  const { sprites, lights } = buildingsSprites()
  let first = 0
  for (const type of BUILDING_TYPES) {
    const art = BUILDING_ART[type]
    const variants = art.variants ?? 1
    const perVariant = ART_FRAMES * (art.working ? 2 : 1)
    const mine = sprites.slice(first, (first += variants * perVariant))
    group(`buildings/${type}`, mine, {
      variants,
      perVariant,
      working: !!art.working,
      lights: lights[type].map((sheet) => sheet.map((l) => ({ x: l.x, y: l.y, size: l.size, levels: Array.from(l.levels, (v) => v ?? null) }))),
      portrait: portraitOf(buildingCanvas(type, 0)),
    })
  }
  if (first !== sprites.length) throw new Error(`зданий нарисовано ${sprites.length}, посчитано ${first}`)
}

// Эффекты боя: взрыв 8 кадров 48 × 48, затем 3 вида клубов.
{
  const sprites = combatSprites()
  group('effects/blast', sprites.slice(0, 8), {}, 3)
  group('effects/puff', sprites.slice(8))
}

// Следы: белый пиксель, гарь шести размеров × 3 варианта, щербинки ×3, остовы по типам юнитов × 16 направлений.
{
  const sprites = decalsSprites()
  const scorches = 6 * 3
  group('decals/white', sprites.slice(0, 1))
  group('decals/scorch', sprites.slice(1, 1 + scorches))
  group('decals/pock', sprites.slice(1 + scorches, 4 + scorches))
  const wrecks = sprites.slice(4 + scorches)
  UNIT_TYPES.forEach((type, i) => group(`decals/wreck/${type}`, wrecks.slice(i * 16, (i + 1) * 16)))
  if (wrecks.length !== UNIT_TYPES.length * 16) throw new Error('остовы: число кадров')
}

// Над зданиями: молнии перегруза, значки, белый квадрат, значки нехватки груза по GOODS, «денег нет», «не подъехать».
{
  const sprites = powerSprites()
  group('power/bolt', sprites.slice(0, 6), {}, 2)
  group('power/icon', sprites.slice(6, 7))
  group('power/white', sprites.slice(7, 8))
  group('power/off', sprites.slice(8, 9))
  group('power/lack', sprites.slice(9, 9 + GOODS.length), { names: [...GOODS] })
  group('power/broke', sprites.slice(9 + GOODS.length, 10 + GOODS.length))
  group('power/blocked', sprites.slice(10 + GOODS.length))
  if (sprites.length !== 11 + GOODS.length) throw new Error('powerSprites: число кадров')
}

{
  const [ring, white] = selectionSprites()
  group('selection/ring', [ring])
  group('selection/white', [white])
}

// Дропы: кучки ящиков каждого груза, по CRATES штук.
{
  const sprites = dropsSprites()
  const crates = sprites.length / GOODS.length
  GOODS.forEach((good, i) => group(`drops/${good}`, sprites.slice(i * crates, (i + 1) * crates)))
}

// Покрытие: фундамент, дорога, мост × 16 наборов соседей.
['foundation', 'road', 'bridge'].forEach((look, i) => group(`paving/${look}`, pavingSprites().slice(i * 16, (i + 1) * 16)))

// Мини-значки грузов 10 × 10.
for (const good of GOODS) {
  const image = new Pixmap(10, 10)
  paintGood(good, (x, y, [r, g, b]) => image.data.set([r, g, b, 255], (y * 10 + x) * 4))
  more[`goods/${good}`] = { width: 10, height: 10, hashes: hash(image.data) }
}

// Портреты покрытия и уровней: кадр, обрезанный по закрашенному (box = 0, без масштаба).
for (const icon of ['foundation', 'road', 'bridge', 'remove', 'peak', 'cliff'] as PaveIcon[]) {
  const crop = portraitOf(paveCanvas(icon, 0))
  more[`portraits/pave/${icon}`] = { width: crop.width, height: crop.height, hashes: crop.hash, portrait: crop }
}
for (const level of [1, 2, 3]) {
  const crop = portraitOf(levelCanvas(level, 0))
  more[`portraits/level/${level}`] = { width: crop.width, height: crop.height, hashes: crop.hash, portrait: crop }
}

const moreFile = process.argv[2].replace(/art\.json$/, 'art-more.json')
await Bun.write(moreFile, JSON.stringify({ version: 1, frames: more }, null, 1))
console.log(`${Object.keys(more).length} групп, ${Object.values(more).reduce((n, e) => n + e.hashes.split(' ').length, 0)} кадров → ${moreFile}`)

await Bun.write(process.argv[2], JSON.stringify({ version: 1, frames: out }, null, 1))
console.log(`${Object.keys(out).length} видов, ${Object.values(out).reduce((n, e) => n + e.hashes.split(' ').length, 0)} кадров → ${process.argv[2]}`)
