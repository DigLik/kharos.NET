import { Pixmap } from '../render/pixmap'
import { buildingSpec, unitSpec, type BuildingType, type MountSpec, type UnitType } from '@kharos/sim-ts'
import { ART_TILE, BUILDING_ART } from './buildings/buildingArt'
import { TEAMS, TURRET_ART, UNIT_ART, UNIT_FRAME, type Team } from './units/unitArt'
import { PAVE_ART } from './pavingPass'

/** Юнит на портрете смотрит вправо-вверх: так видно и бок, и башню. */
const ANGLE = -Math.PI / 4
/** Поле вокруг здания на портрете, в пикселях чертежа. */
const PAD = 2

/** Башни на корпусе: у юнита — под его углом, у турели — на месте. */
function drawMounts(image: Pixmap, mounts: MountSpec[] | undefined, angle: number, team: Team = 'own') {
  for (const mount of mounts ?? []) {
    const x = (Math.cos(angle) * mount.along - Math.sin(angle) * mount.across) * 16
    const y = (Math.sin(angle) * mount.along + Math.cos(angle) * mount.across) * 16
    image.originX += x
    image.originY += y
    TURRET_ART[mount.turret](image, angle, TEAMS[team], 0)
    image.originX -= x
    image.originY -= y
  }
}

/**
 * Пиксмап в холст картинки. Прозрачные поля обрезаются; с box картинка вписывается в квадрат box×box:
 * мелкий спрайт увеличивается в целое число раз — пиксели остаются ровными, — а крупный уменьшается сглаженно.
 * Такой квадрат показывается 1:1, без растяжения стилями.
 */
function toCanvas(image: Pixmap, box = 0) {
  const { width, height, data } = image
  let left = width
  let top = height
  let right = -1
  let bottom = -1
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (!data[(y * width + x) * 4 + 3]) continue
      if (x < left) left = x
      if (x > right) right = x
      if (y < top) top = y
      if (y > bottom) bottom = y
    }
  }
  if (right < 0) left = top = right = bottom = 0
  const source = document.createElement('canvas')
  source.width = width
  source.height = height
  source.getContext('2d')!.putImageData(new ImageData(new Uint8ClampedArray(data), width, height), 0, 0)
  const w = right - left + 1
  const h = bottom - top + 1
  const canvas = document.createElement('canvas')
  canvas.width = box || w
  canvas.height = box || h
  const context = canvas.getContext('2d')!
  const fit = box ? Math.min(box / w, box / h) : 1
  // Вдвое и больше — в целое число раз; от одного до двух — как есть, без сглаживания: иначе крупное здание
  // осталось бы в окне портрета маленьким. Меньше одного — сглаженно, выпадение пикселей заметнее.
  const scale = fit >= 2 ? Math.floor(fit) : fit
  context.imageSmoothingEnabled = scale < 1
  context.imageSmoothingQuality = 'high'
  const dw = Math.round(w * scale)
  const dh = Math.round(h * scale)
  context.drawImage(source, left, top, w, h, Math.floor((canvas.width - dw) / 2), Math.floor((canvas.height - dh) / 2), dw, dh)
  return canvas
}

const cache = new Map<string, HTMLCanvasElement>()
const urls = new WeakMap<HTMLCanvasElement, string>()

/** Адрес картинки холста для <img>: считается один раз на холст. */
function urlOf(canvas: HTMLCanvasElement) {
  let url = urls.get(canvas)
  if (!url) urls.set(canvas, (url = canvas.toDataURL()))
  return url
}

/**
 * Портрет юнита для панели выбранного: тот же спрайт, что на карте, в цветах стороны, вписанный в квадрат box
 * (см. toCanvas). Рисуется один раз на размер.
 */
export function unitCanvas(type: UnitType, box = 0, team: Team = 'own') {
  const key = `unit:${team}:${type}:${box}`
  let canvas = cache.get(key)
  if (!canvas) {
    const image = new Pixmap(UNIT_FRAME, UNIT_FRAME)
    image.originX = image.originY = UNIT_FRAME / 2
    UNIT_ART[type](image, ANGLE, TEAMS[team], 0)
    drawMounts(image, unitSpec(type).mounts, ANGLE, team)
    cache.set(key, (canvas = toCanvas(image, box)))
  }
  return canvas
}

/** То же — адресом картинки для <img> (страница). */
export const unitPortrait = (type: UnitType, box = 0, team: Team = 'own') => urlOf(unitCanvas(type, box, team))

/** Портрет здания: первый кадр его анимации, вписанный в квадрат box. */
export function buildingCanvas(type: BuildingType, box = 0) {
  const key = `building:${type}:${box}`
  let canvas = cache.get(key)
  if (!canvas) {
    const art = BUILDING_ART[type]
    const image = new Pixmap(art.width * ART_TILE + PAD * 2, art.height * ART_TILE + PAD * 2)
    image.originX = image.originY = PAD
    art.draw(image, 0, () => {}, 0, true)
    const { mounts } = buildingSpec(type)
    if (mounts) {
      image.originX += (art.width * ART_TILE) / 2
      image.originY += (art.height * ART_TILE) / 2
      drawMounts(image, mounts, ANGLE)
    }
    cache.set(key, (canvas = toCanvas(image, box)))
  }
  return canvas
}

/** То же — адресом картинки для <img> (страница). */
export const buildingPortrait = (type: BuildingType, box = 0) => urlOf(buildingCanvas(type, box))

/**
 * Значок покрытия: как оно выглядит на карте. Снятие — фундамент с красным крестом. Землеройные работы — гора
 * с киркой и обрыв со спуском, см. earthworks.ts.
 */
export type PaveIcon = 'foundation' | 'road' | 'bridge' | 'remove' | 'peak' | 'cliff'

/** Плитки значка покрытия: вид и набор соседей каждой, рядами сверху вниз. */
const PAVE_TILES: Record<PaveIcon, ['foundation' | 'road' | 'bridge', number][][]> = {
  // Площадка 2×2: у каждой плитки соседи — две другие. Биты соседей: сверху 1, справа 2, снизу 4, слева 8.
  foundation: [[['foundation', 6], ['foundation', 12]], [['foundation', 3], ['foundation', 9]]],
  remove: [[['foundation', 6], ['foundation', 12]], [['foundation', 3], ['foundation', 9]]],
  road: [[['road', 2], ['road', 8]]],
  bridge: [[['bridge', 2], ['bridge', 8]]],
  peak: [],
  cliff: [],
}

const OUTLINE = 0x1a1410

/** Гора с киркой: треугольник скалы со снежной шапкой и кирка поперёк. */
function drawPeakIcon(image: Pixmap) {
  for (let row = 0; row < 20; row++) {
    const half = Math.round(row * 0.7) + 1
    const y = 6 + row
    image.rect(16 - half - 1, y, half * 2 + 2, 1, OUTLINE)
    image.rect(16 - half, y, half, 1, 0x9a8a74)
    image.rect(16, y, half, 1, 0x6e604f)
    if (row < 5) image.rect(16 - half, y, half * 2, 1, 0xdcd4c6)
  }
  image.rect(2, 26, 28, 2, OUTLINE)
  image.line(9, 27, 23, 11, 4, OUTLINE)
  image.line(9, 27, 23, 11, 2, 0x8a5426)
  image.line(17, 7, 28, 15, 5, OUTLINE)
  image.line(17, 7, 28, 15, 3, 0xc8ccd0)
}

/** Обрыв со спуском: уступ плато слева, земля справа, между ними — пологий пандус. */
function drawCliffIcon(image: Pixmap) {
  image.rect(2, 9, 15, 19, OUTLINE)
  image.rect(3, 10, 13, 17, 0x9a8a74)
  image.rect(3, 10, 13, 3, 0xb4a48c)
  image.rect(2, 22, 28, 6, OUTLINE)
  image.rect(3, 23, 26, 4, 0x7e705e)
  for (let step = 0; step < 12; step++) image.rect(16 + step, 11 + step, 2, 12 - step, step % 2 ? 0xc0ae92 : 0xb4a48c)
  image.line(16, 10, 29, 23, 2, OUTLINE)
  image.line(18, 9, 25, 16, 2, 0xe0902c)
  image.line(25, 16, 25, 11, 2, 0xe0902c)
  image.line(25, 16, 20, 16, 2, 0xe0902c)
}

/** Значок покрытия для сетки команд, вписанный в квадрат box. Рисуется один раз на размер. */
export function paveCanvas(icon: PaveIcon, box = 0) {
  const key = `pave:${icon}:${box}`
  let canvas = cache.get(key)
  if (!canvas) {
    const rows = PAVE_TILES[icon]
    const image = new Pixmap(32, 32)
    // Полоса дороги — посередине по высоте.
    const top = (32 - rows.length * 16) / 2
    rows.forEach((row, y) =>
      row.forEach(([look, mask], x) => {
        const tile = PAVE_ART[look](mask)
        for (let py = 0; py < 16; py++) {
          for (let px = 0; px < 16; px++) {
            const from = (py * 16 + px) * 4
            if (!tile.data[from + 3]) continue
            const to = ((top + y * 16 + py) * 32 + x * 16 + px) * 4
            image.data.set(tile.data.subarray(from, from + 4), to)
          }
        }
      }),
    )
    if (icon === 'peak') drawPeakIcon(image)
    if (icon === 'cliff') drawCliffIcon(image)
    if (icon === 'remove') {
      image.line(7, 7, 25, 25, 4, 0x1a0806)
      image.line(25, 7, 7, 25, 4, 0x1a0806)
      image.line(7, 7, 25, 25, 2, 0xe0402c)
      image.line(25, 7, 7, 25, 2, 0xe0402c)
    }
    cache.set(key, (canvas = toCanvas(image, box)))
  }
  return canvas
}

/** То же — адресом картинки для <img> (страница). */
export const pavePortrait = (icon: PaveIcon, box = 0) => urlOf(paveCanvas(icon, box))

/**
 * Значок модернизации — исследования уровня зданий: корпус здания и над ним шевроны цвета команды, по одному на уровень,
 * до которого исследование поднимает. Рисуется один раз на размер.
 */
export function levelCanvas(level: number, box = 0) {
  const key = `level:${level}:${box}`
  let canvas = cache.get(key)
  if (!canvas) {
    const INK = 0x0b111b
    const image = new Pixmap(32, 32)
    // Корпус с окнами.
    image.rect(5, 17, 22, 14, INK)
    image.rect(6, 18, 20, 12, 0x2d4560)
    image.rect(6, 18, 20, 1, 0x6184a3)
    image.rect(6, 27, 20, 3, 0x1c2b3e)
    for (const x of [8, 14, 20]) image.rect(x, 21, 4, 3, 0x5aa9ff)
    // Шевроны вверх: нижний — тусклее, верхний — ярче.
    const COLORS = [0x1f63d8, 0x5aa9ff, 0xe4f4ff]
    for (let i = 0; i < level; i++) {
      const y = 14 - i * 5
      const color = COLORS[Math.min(COLORS.length - 1, i + 3 - level)]
      image.line(9, y + 4, 16, y - 2, 4, INK)
      image.line(16, y - 2, 23, y + 4, 4, INK)
      image.line(9, y + 4, 16, y - 2, 2, color)
      image.line(16, y - 2, 23, y + 4, 2, color)
    }
    cache.set(key, (canvas = toCanvas(image, box)))
  }
  return canvas
}

/** То же — адресом картинки для <img> (страница). */
export const levelPortrait = (level: number, box = 0) => urlOf(levelCanvas(level, box))
