import { packAtlas, type PackedAtlas, type Sprite } from '../render/atlas'
import { buildingsSprites, type BuildingsArt } from './buildings/buildingsPass'
import { combatSprites } from './combatPass'
import { decalsSprites } from './decalsPass'
import { depositsSprites } from './depositsPass'
import { dropsSprites } from './dropsPass'
import { pavingSprites } from './pavingPass'
import { powerSprites } from './powerPass'
import { selectionSprites } from './selectionPass'
import { unitsSprites, type UnitsArt } from './units/unitsPass'

/**
 * Вся неизменная графика игры — атласы спрайтов. Рисуется один раз за жизнь страницы, в воркере (см. loadArt.ts),
 * и годится для любой игры: каждая игра и каждый новый контекст WebGL лишь заливают атласы на свою видеокарту.
 */
export interface Art {
  units: UnitsArt
  buildings: BuildingsArt
  combat: PackedAtlas
  decals: PackedAtlas
  deposits: PackedAtlas
  drops: PackedAtlas
  paving: PackedAtlas
  power: PackedAtlas
  selection: PackedAtlas
}

/** Рисует всю графику. progress получает долю готового, от 0 до 1, после каждой картинки. Работает без браузера. */
export function drawArt(progress: (share: number) => void = () => {}): Art {
  const units = unitsSprites()
  const buildings = buildingsSprites()
  const simple = {
    combat: combatSprites(),
    decals: decalsSprites(),
    deposits: depositsSprites(),
    drops: dropsSprites(),
    paving: pavingSprites(),
    power: powerSprites(),
    selection: selectionSprites(),
  }
  const total = units.sprites.length + buildings.sprites.length + Object.values(simple).reduce((sum, sprites) => sum + sprites.length, 0)
  let done = 0
  const pack = (sprites: Sprite[]) => packAtlas(sprites, () => progress(++done / total))
  return {
    units: { atlas: pack(units.sprites), sheets: units.sheets },
    // Огни зданий собираются, пока рисуются их кадры.
    buildings: { atlas: pack(buildings.sprites), lights: buildings.lights },
    combat: pack(simple.combat),
    decals: pack(simple.decals),
    deposits: pack(simple.deposits),
    drops: pack(simple.drops),
    paving: pack(simple.paving),
    power: pack(simple.power),
    selection: pack(simple.selection),
  }
}

/** Все атласы графики: большие — юнитов и зданий — первыми. */
export function artAtlases(art: Art): PackedAtlas[] {
  const { units, buildings, ...simple } = art
  return [units.atlas, buildings.atlas, ...Object.values(simple)]
}

/** Пиксели всех атласов: их воркер передаёт странице без копирования. */
export function artBuffers(art: Art): ArrayBuffer[] {
  return artAtlases(art).map((atlas) => atlas.data.buffer)
}
