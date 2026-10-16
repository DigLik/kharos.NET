/**
 * Двоичные изменения мира. Каждый тик меняются в основном места и повороты едущих — их сотни и тысячи, и в JSON
 * каждое стоило бы под сотню байт. Здесь движение идёт записями по 11–13 байт, выстрелы — их в бою сотни за тик —
 * записями около 30 байт, а всё остальное, редкое, — JSON следом. Кадр: тип (1 байт), тик (4), число записей движения
 * (варинт), записи, число выстрелов (варинт), записи выстрелов (см. ShotRecord), остаток — JSON.
 * Запись: номер сущности (варинт), флаги (1 байт: младший бит — есть ли поворот, остальные семь — за сколько тиков
 * набежало это движение, без единицы, см. SPAN_MOST), x и y (int32, в 1/POSITION тайла), поворот (int16,
 * в 1/FACING радиана).
 */

import { WEAPONS, type WeaponType } from '@kharos/sim-ts/weapons'

/** Сколько долей тайла в единице координаты: 1/256 тайла глазу не видна. */
export const POSITION = 256
/** Сколько долей радиана в единице поворота: ±π укладывается в int16. */
export const FACING = 10000

const DELTA = 1
/** Кадр с картой мира: тип (1 байт), затем карта saveLand, сжатая deflate. */
export const LAND = 2
const HAS_FACING = 1
/**
 * Самый долгий срок движения, который влезает в запись, в тиках. Хост шлёт сущность не каждый тик, см. host.ts:
 * клиент ведёт её к новому месту столько тиков, сколько она ждала, а не прыжком за один.
 */
export const SPAN_MOST = 128

/** Сколько чисел на запись движения в разобранном кадре: номер, x, y, поворот, срок. */
export const MOTION_FIELDS = 5

/** Движение сущности в целых единицах сети: x, y и поворот; null — поворота у сущности нет. */
export type Motion = [x: number, y: number, facing: number | null]

/**
 * Выстрел в кадре: номер сущности, компонент Shot и тик рождения (см. wire.ts). Запись: номер (варинт), оружие (байт —
 * место в WEAPON_TYPES), флаги (байт: стена, по воздуху), игрок, стрелок + 1 и цель + 1 (варинты; ноль — никого),
 * fromX, fromY, toX, toY (int32, в тысячных тайла — как округляет JSON, см. wire.ts), тик рождения (4 байта), срок
 * в тиках (варинт). Клиенту он приходит так же, как пришёл бы в set.
 */
export type ShotRecord = [id: number, shot: { weapon: WeaponType; player: number; source: number; target: number; fromX: number; fromY: number; toX: number; toY: number; life: number; blocked: boolean; air: boolean }, born: number]
const WEAPON_TYPES = Object.keys(WEAPONS) as WeaponType[]
const WEAPON_INDEX = new Map(WEAPON_TYPES.map((weapon, index) => [weapon, index]))
/** Сколько долей тайла в единице координаты выстрела. */
const SHOT_POSITION = 1000
const BLOCKED = 1
const AIR = 2
/** Самая длинная запись выстрела в байтах: под неё берётся место. */
const SHOT_MOST = 5 + 1 + 1 + 5 * 3 + 16 + 4 + 5

const encoder = new TextEncoder()
const decoder = new TextDecoder()

/** Место и поворот в единицах сети: их и сравнивает хост, и шлёт. */
export const quantize = (x: number, y: number, facing?: number): Motion => [
  Math.round(x * POSITION),
  Math.round(y * POSITION),
  facing === undefined ? null : Math.round(facing * FACING),
]

/**
 * Собирает кадр изменений: motions — номер сущности, её движение и за сколько тиков оно набежало; shots — выстрелы;
 * json — остальное.
 */
export function encodeDelta(tick: number, motions: [id: number, motion: Motion, span: number][], shots: ShotRecord[], json: string): Uint8Array {
  const text = encoder.encode(json)
  const bytes = new Uint8Array(1 + 4 + 5 + motions.length * 16 + 5 + shots.length * SHOT_MOST + text.length)
  const view = new DataView(bytes.buffer)
  let at = 0
  const varint = (value: number) => {
    while (value >= 0x80) {
      bytes[at++] = (value & 0x7f) | 0x80
      value >>>= 7
    }
    bytes[at++] = value
  }
  bytes[at++] = DELTA
  view.setUint32(at, tick)
  at += 4
  varint(motions.length)
  for (const [id, [x, y, facing], span] of motions) {
    varint(id)
    bytes[at++] = (facing === null ? 0 : HAS_FACING) | ((Math.min(SPAN_MOST, Math.max(1, span)) - 1) << 1)
    view.setInt32(at, x)
    view.setInt32(at + 4, y)
    at += 8
    if (facing !== null) {
      view.setInt16(at, facing)
      at += 2
    }
  }
  varint(shots.length)
  for (const [id, shot, born] of shots) {
    varint(id)
    bytes[at++] = WEAPON_INDEX.get(shot.weapon) ?? 0
    bytes[at++] = (shot.blocked ? BLOCKED : 0) | (shot.air ? AIR : 0)
    varint(shot.player)
    varint(shot.source + 1)
    varint(shot.target + 1)
    view.setInt32(at, Math.round(shot.fromX * SHOT_POSITION))
    view.setInt32(at + 4, Math.round(shot.fromY * SHOT_POSITION))
    view.setInt32(at + 8, Math.round(shot.toX * SHOT_POSITION))
    view.setInt32(at + 12, Math.round(shot.toY * SHOT_POSITION))
    view.setInt32(at + 16, born)
    at += 20
    varint(shot.life)
  }
  bytes.set(text, at)
  return bytes.subarray(0, at + text.length)
}

/**
 * Разбирает кадр изменений. motion — номер, x, y, поворот и срок в тиках подряд, уже в тайлах и радианах; поворота
 * нет — NaN.
 */
export function decodeDelta(bytes: Uint8Array) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  let at = 0
  const varint = () => {
    let value = 0
    let shift = 0
    for (;;) {
      const byte = bytes[at++]
      value += (byte & 0x7f) * 2 ** shift
      if (byte < 0x80) return value
      shift += 7
    }
  }
  if (bytes[at++] !== DELTA) throw new Error('Неизвестный двоичный кадр')
  const tick = view.getUint32(at)
  at += 4
  const count = varint()
  const motion: number[] = new Array(count * MOTION_FIELDS)
  for (let i = 0; i < count; i++) {
    const record = i * MOTION_FIELDS
    motion[record] = varint()
    const flags = bytes[at++]
    motion[record + 1] = view.getInt32(at) / POSITION
    motion[record + 2] = view.getInt32(at + 4) / POSITION
    at += 8
    if (flags & HAS_FACING) {
      motion[record + 3] = view.getInt16(at) / FACING
      at += 2
    } else motion[record + 3] = NaN
    motion[record + 4] = (flags >> 1) + 1
  }
  const shots: [number, Record<string, object>][] = []
  for (let left = varint(); left > 0; left--) {
    const id = varint()
    const weapon = WEAPON_TYPES[bytes[at++]]
    const flags = bytes[at++]
    const player = varint()
    const source = varint() - 1
    const target = varint() - 1
    const coordinate = (offset: number) => view.getInt32(at + offset) / SHOT_POSITION
    const shot = { weapon, player, source, target, fromX: coordinate(0), fromY: coordinate(4), toX: coordinate(8), toY: coordinate(12), life: 0, blocked: !!(flags & BLOCKED), air: !!(flags & AIR), born: view.getInt32(at + 16) }
    at += 20
    shot.life = varint()
    shots.push([id, { Shot: shot }])
  }
  const rest = JSON.parse(decoder.decode(bytes.subarray(at))) as { set: [number, Record<string, object>][]; unset: [number, string[]][]; remove: number[] }
  // Выстрел клиенту — тот же set: новая сущность или поменявшийся компонент.
  return { tick, motion, ...rest, set: shots.length ? [...shots, ...rest.set] : rest.set }
}
