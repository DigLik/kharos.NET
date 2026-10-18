/**
 * Кадры чертежей TS сырыми RGBA — образец для переноса графики в моды (apps/client, `cargo run --example looks`).
 * bun tools/art/dump.ts <файл>: багги (4 фазы × 64 направления), стрелок (64) и завод (16 кадров), подряд.
 */
import { Pixmap } from '../../packages/client/src/render/pixmap'
import { MASK_TEAM, TURRET_ART, UNIT_ART } from '../../packages/client/src/game/units/unitArt'
import { ART_FRAMES, BUILDING_ART } from '../../packages/client/src/game/buildings/buildingArt'

const out: Uint8Array[] = []
const TURN = Math.PI * 2
for (let phase = 0; phase < 4; phase++) {
  for (let direction = 0; direction < 64; direction++) {
    const image = new Pixmap(32, 32)
    image.originX = image.originY = 16
    UNIT_ART.buggy(image, (direction / 64) * TURN, MASK_TEAM, phase)
    out.push(image.data)
  }
}
for (let direction = 0; direction < 64; direction++) {
  const image = new Pixmap(32, 32)
  image.originX = image.originY = 16
  TURRET_ART.gunner(image, (direction / 64) * TURN, MASK_TEAM, 0)
  out.push(image.data)
}
for (let i = 0; i < ART_FRAMES; i++) {
  const art = BUILDING_ART.factory
  const image = new Pixmap(art.width * 16 + 32, art.height * 16 + 32)
  image.originX = image.originY = 16
  art.draw(image, i / ART_FRAMES, () => {})
  out.push(image.data)
}
await Bun.write(process.argv[2], Buffer.concat(out))
