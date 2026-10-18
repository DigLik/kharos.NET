/**
 * Эталон графики base для тестов .NET: кадры чертежей из TS-клиента (unitArt.ts, buildingArt.ts, depositsPass.ts) —
 * сырые RGBA, сведённые к хешу на кадр. Запуск из корня репозитория (после `bun install`):
 *   bun dotnet/tests/Kharos.Base.Tests/Looks/dump.ts dotnet/tests/Kharos.Base.Tests/Looks/golden/art.json
 * Тот же порядок кадров, что у Look.Images клиента: фаза за фазой, внутри — направления. Для каждого вида — хеши всех
 * кадров (первые 8 байт SHA-256) и один кадр целиком (образец для разбора расхождения). Портреты — обрезанные по
 * закрашенному, в маске цвета стороны (как рисует base); огни кадра — как emit в TS.
 */
import { Pixmap } from '../../../../packages/client/src/render/pixmap'
import { MASK_TEAM, TURRET_ART, UNIT_ART, UNIT_GAITS, UNIT_LIGHTS, UNIT_TRACES } from '../../../../packages/client/src/game/units/unitArt'
import { ART_FRAMES, BUILDING_ART } from '../../../../packages/client/src/game/buildings/buildingArt'
import { depositsSprites } from '../../../../packages/client/src/game/depositsPass'

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

// Юниты: 4 фазы хода × 64 направления (летающие — одна фаза). Портрет: угол вправо-вверх, у багги — турель на месте.
const MOUNTS: Record<string, { turret: keyof typeof TURRET_ART; along: number; across: number }[]> = {
  buggy: [{ turret: 'gunner', along: -0.16, across: 0 }],
}
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

await Bun.write(process.argv[2], JSON.stringify({ version: 1, frames: out }, null, 1))
console.log(`${Object.keys(out).length} видов, ${Object.values(out).reduce((n, e) => n + e.hashes.split(' ').length, 0)} кадров → ${process.argv[2]}`)
