import { createTexture, type Texture } from '../gl'
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

/** Заливает собранный атлас на видеокарту одним вызовом. */
export function uploadAtlas(gl: WebGL2RenderingContext, atlas: PackedAtlas): { texture: Texture; frames: AtlasFrame[] } {
  return { texture: createTexture(gl, { width: atlas.width, height: atlas.height, data: atlas.data }), frames: atlas.frames }
}
