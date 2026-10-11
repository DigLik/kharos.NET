/**
 * Двоичные изменения мира. Каждый тик меняются в основном места и повороты едущих — их сотни и тысячи, и в JSON
 * каждое стоило бы под сотню байт. Здесь движение идёт записями по 11–13 байт, а всё остальное, редкое, — JSON
 * следом. Кадр: тип (1 байт), тик (4), число записей движения (варинт), записи, остаток — JSON.
 * Запись: номер сущности (варинт), флаги (1 байт: младший бит — есть ли поворот, остальные семь — за сколько тиков
 * набежало это движение, без единицы, см. SPAN_MOST), x и y (int32, в 1/POSITION тайла), поворот (int16,
 * в 1/FACING радиана).
 */

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

const encoder = new TextEncoder()
const decoder = new TextDecoder()

/** Место и поворот в единицах сети: их и сравнивает хост, и шлёт. */
export const quantize = (x: number, y: number, facing?: number): Motion => [
  Math.round(x * POSITION),
  Math.round(y * POSITION),
  facing === undefined ? null : Math.round(facing * FACING),
]

/** Собирает кадр изменений: motions — номер сущности, её движение и за сколько тиков оно набежало; json — остальное. */
export function encodeDelta(tick: number, motions: [id: number, motion: Motion, span: number][], json: string): Uint8Array {
  const text = encoder.encode(json)
  const bytes = new Uint8Array(1 + 4 + 5 + motions.length * 16 + text.length)
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
  const rest = JSON.parse(decoder.decode(bytes.subarray(at)))
  return { tick, motion, ...rest }
}
