import { createTexture, isShared, shared, type Texture } from '../gl'
import { Pixmap } from './pixmap'

/** Место картинки в атласе, в долях текстуры: левый верхний угол и размер. */
export interface AtlasFrame {
  u: number
  v: number
  width: number
  height: number
}

/** Картинка будущего атласа: её размер и чертёж. draw рисует на чистом холсте этого размера с началом координат в углу. */
export interface Sprite {
  width: number
  height: number
  draw(image: Pixmap): void
}

/** Атлас в памяти: пиксели RGBA всей текстуры и места картинок. Его можно передать из воркера и залить на видеокарту. */
export interface PackedAtlas {
  width: number
  height: number
  data: Uint8Array<ArrayBuffer>
  frames: AtlasFrame[]
}

/** Ширина атласа не меньше этой и не больше той, что берут все видеокарты. */
const MIN_WIDTH = 1024
const MAX_WIDTH = 4096

/** Уже нарисованная картинка как картинка атласа. */
export const ready = (image: Pixmap): Sprite => ({ width: image.width, height: image.height, draw: (target) => target.data.set(image.data) })

/**
 * Складывает картинки в одну текстуру, чтобы все спрайты рисовались одним вызовом.
 * Картинки ставятся в ряд слева направо; когда ряд кончается, начинается следующий.
 * Кладите картинки близкой высоты подряд — тогда в рядах не остаётся пустот. Ширина — степень двойки: атлас
 * растёт вширь, пока он выше, чем шире, иначе много кадров вытянули бы его за предел высоты текстуры.
 * Картинки рисуются по одной на общий холст своего размера и сразу переносятся в атлас: в памяти не копятся тысячи
 * отдельных кадров. После каждой зовётся drawn.
 */
export function packAtlas(sprites: Sprite[], drawn: () => void = () => {}): PackedAtlas {
  const area = sprites.reduce((sum, sprite) => sum + sprite.width * sprite.height, 0)
  let atlasWidth = MIN_WIDTH
  while (atlasWidth < MAX_WIDTH && area > atlasWidth * atlasWidth) atlasWidth *= 2
  const places: { x: number; y: number }[] = []
  let x = 0
  let y = 0
  let rowHeight = 0
  for (const sprite of sprites) {
    if (sprite.width > atlasWidth) throw new Error(`Картинка шире атласа: ${sprite.width} > ${atlasWidth}`)
    if (x + sprite.width > atlasWidth) {
      x = 0
      y += rowHeight
      rowHeight = 0
    }
    places.push({ x, y })
    x += sprite.width
    rowHeight = Math.max(rowHeight, sprite.height)
  }
  const height = Math.max(1, y + rowHeight)

  // Пустые места атласа прозрачные: новый массив заполнен нулями.
  const data = new Uint8Array(atlasWidth * height * 4)
  const canvases = new Map<string, Pixmap>()
  const frames = sprites.map((sprite, i) => {
    const place = places[i]
    const size = `${sprite.width}x${sprite.height}`
    let image = canvases.get(size)
    if (!image) canvases.set(size, (image = new Pixmap(sprite.width, sprite.height)))
    image.clear()
    image.originX = image.originY = 0
    sprite.draw(image)
    const row = sprite.width * 4
    for (let line = 0; line < sprite.height; line++) {
      data.set(image.data.subarray(line * row, (line + 1) * row), ((place.y + line) * atlasWidth + place.x) * 4)
    }
    drawn()
    return { u: place.x / atlasWidth, v: place.y / height, width: sprite.width / atlasWidth, height: sprite.height / height }
  })
  return { width: atlasWidth, height, data, frames }
}

/** Атлас на видеокарте. Текстура общая на контекст: её destroy ничего не освобождает. */
export interface UploadedAtlas {
  texture: Texture
  frames: AtlasFrame[]
}

/** Сколько байт пикселей уходит на видеокарту за один шаг заливки: полоса атласа шириной 4096 — 64 строки. */
const STRIP_BYTES = 1 << 20

/** Сколько шагов займёт заливка атласа по частям. */
export function uploadSteps(atlas: PackedAtlas) {
  return Math.ceil(atlas.height / stripRows(atlas))
}

const stripRows = (atlas: PackedAtlas) => Math.max(1, Math.floor(STRIP_BYTES / (atlas.width * 4)))

/**
 * Заливает атлас на видеокарту полосами, отдавая управление после каждой: атлас зданий — 39 МБ, и залитый одним
 * вызовом он на слабой видеокарте останавливает весь браузер (процесс видеокарты у вкладок общий). Готовый атлас
 * становится общим на контекст — после этого uploadAtlas берёт его сразу. Уже залитый шагов не делает.
 */
export function* uploadAtlasSlowly(gl: WebGL2RenderingContext, atlas: PackedAtlas): Generator<void, UploadedAtlas> {
  if (isShared(gl, atlas)) return uploadAtlas(gl, atlas)
  const { width, height, data } = atlas
  const texture = createTexture(gl, { width, height, fixed: true })
  const rows = stripRows(atlas)
  for (let y = 0; y < height; y += rows) {
    const count = Math.min(rows, height - y)
    texture.write(data.subarray(y * width * 4, (y + count) * width * 4), 0, y, width, count)
    yield
  }
  // Кто-то успел залить атлас целиком, пока шли полосы: общий — его, эта копия лишняя.
  if (isShared(gl, atlas)) texture.destroy()
  return shared(gl, atlas, () => ({ texture: lasting(texture), frames: atlas.frames }))
}

/** Та же текстура, которую нельзя уничтожить: ею пользуются все игры контекста. */
const lasting = (texture: Texture): Texture => ({
  handle: texture.handle,
  width: texture.width,
  height: texture.height,
  write: texture.write,
  resize: texture.resize,
  destroy() {},
})

/**
 * Атлас на видеокарте, общий на контекст: заливается при первом обращении, целиком и сразу. На экране загрузки
 * атласы заливаются заранее и по частям (см. warmUp.ts) — тогда здесь они уже готовы; сразу — только после
 * восстановления потерянного контекста.
 */
export function uploadAtlas(gl: WebGL2RenderingContext, atlas: PackedAtlas): UploadedAtlas {
  return shared(gl, atlas, () => ({
    texture: lasting(createTexture(gl, { width: atlas.width, height: atlas.height, data: atlas.data, fixed: true })),
    frames: atlas.frames,
  }))
}
