import { Pixmap } from '../render/pixmap'
import { buildingSpec, unitSpec, type BuildingType, type MountSpec, type UnitType } from '../sim'
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
 * Пиксмап в адрес картинки для <img>. Прозрачные поля обрезаются; с box картинка вписывается в квадрат box×box:
 * мелкий спрайт увеличивается в целое число раз — пиксели остаются ровными, — а крупный уменьшается сглаженно.
 * Такой квадрат показывается 1:1, без растяжения стилями.
 */
function toUrl(image: Pixmap, box = 0) {
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
  return canvas.toDataURL()
}

const cache = new Map<string, string>()

/**
 * Портрет юнита для панели выбранного: тот же спрайт, что на карте, в цветах стороны, вписанный в квадрат box
 * (см. toUrl). Рисуется один раз на размер.
 */
export function unitPortrait(type: UnitType, box = 0, team: Team = 'own') {
  const key = `unit:${team}:${type}:${box}`
  let url = cache.get(key)
  if (!url) {
    const image = new Pixmap(UNIT_FRAME, UNIT_FRAME)
    image.originX = image.originY = UNIT_FRAME / 2
    UNIT_ART[type](image, ANGLE, TEAMS[team], 0)
    drawMounts(image, unitSpec(type).mounts, ANGLE, team)
    cache.set(key, (url = toUrl(image, box)))
  }
  return url
}

/** Портрет здания: первый кадр его анимации, вписанный в квадрат box. */
export function buildingPortrait(type: BuildingType, box = 0) {
  const key = `building:${type}:${box}`
  let url = cache.get(key)
  if (!url) {
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
    cache.set(key, (url = toUrl(image, box)))
  }
  return url
}

/** Значок покрытия: как оно выглядит на карте. Снятие — фундамент с красным крестом. */
export type PaveIcon = 'foundation' | 'road' | 'bridge' | 'remove'

/** Плитки значка покрытия: вид и набор соседей каждой, рядами сверху вниз. */
const PAVE_TILES: Record<PaveIcon, ['foundation' | 'road' | 'bridge', number][][]> = {
  // Площадка 2×2: у каждой плитки соседи — две другие. Биты соседей: сверху 1, справа 2, снизу 4, слева 8.
  foundation: [[['foundation', 6], ['foundation', 12]], [['foundation', 3], ['foundation', 9]]],
  remove: [[['foundation', 6], ['foundation', 12]], [['foundation', 3], ['foundation', 9]]],
  road: [[['road', 2], ['road', 8]]],
  bridge: [[['bridge', 2], ['bridge', 8]]],
}

/** Значок покрытия для сетки команд, вписанный в квадрат box. Рисуется один раз на размер. */
export function pavePortrait(icon: PaveIcon, box = 0) {
  const key = `pave:${icon}:${box}`
  let url = cache.get(key)
  if (!url) {
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
    if (icon === 'remove') {
      image.line(7, 7, 25, 25, 4, 0x1a0806)
      image.line(25, 7, 7, 25, 4, 0x1a0806)
      image.line(7, 7, 25, 25, 2, 0xe0402c)
      image.line(25, 7, 7, 25, 2, 0xe0402c)
    }
    cache.set(key, (url = toUrl(image, box)))
  }
  return url
}
