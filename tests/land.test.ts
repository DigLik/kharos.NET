import { expect, test } from 'bun:test'
import { CHUNK_SIZE, DEFAULT_CONFIG, Terrain, applyEdits, areaOf, createLand, getChunk, loadLand, saveLand, setTile, terrainAt, tileBytes } from '@kharos/shared/terrain'

const bounds = { left: -64, top: -64, right: 64, bottom: 64 }
const area = areaOf(bounds)

/** Все чанки карты подряд — для сравнения местностей целиком. */
const chunks = (land: ReturnType<typeof createLand>) => {
  const all: number[] = []
  for (let y = area.top; y < area.top + area.height; y++) for (let x = area.left; x < area.left + area.width; x++) all.push(...getChunk(land, x, y))
  return all
}

test('область карты покрывает границы мира чанками', () => {
  expect(area).toEqual({ left: -2, top: -2, width: 4, height: 4 })
  expect(areaOf({ left: -50, top: -50, right: 50, bottom: 50 })).toEqual({ left: -2, top: -2, width: 4, height: 4 })
})

test('карта из сохранения та же байт в байт, а лениво собранная — та же, что из генератора', () => {
  const land = createLand(DEFAULT_CONFIG, area)
  const loaded = loadLand(DEFAULT_CONFIG, saveLand(land))
  expect(chunks(loaded)).toEqual(chunks(land))
  expect(chunks(land)).toEqual(chunks(createLand(DEFAULT_CONFIG, area)))
})

test('смена генератора не трогает сохранённый мир, а край мира берёт новый генератор', () => {
  const land = createLand(DEFAULT_CONFIG, area)
  const other = { ...DEFAULT_CONFIG, seed: DEFAULT_CONFIG.seed + 1 }
  const loaded = loadLand(other, saveLand(land))
  expect(chunks(loaded)).toEqual(chunks(land))
  expect(getChunk(loaded, 10, 10)).toEqual(getChunk(createLand(other), 10, 10))
})

test('правка тайла: пересчитывает соседей как полная загрузка, уходит в сохранение и переносится на другую сторону', () => {
  const land = createLand(DEFAULT_CONFIG, area)
  const replica = createLand(DEFAULT_CONFIG, area)
  // Скала посреди песка: глубина песков вокруг падает.
  let x = 0
  let y = 0
  search: for (y = bounds.top + 10; y < bounds.bottom - 10; y++) for (x = bounds.left + 10; x < bounds.right - 10; x++) if (terrainAt(land, x, y) === Terrain.Sand) break search
  const depth = (target: typeof land) => {
    const chunk = getChunk(target, Math.floor((x + 1) / CHUNK_SIZE), Math.floor(y / CHUNK_SIZE))
    return chunk[(((y % CHUNK_SIZE) + CHUNK_SIZE) % CHUNK_SIZE * CHUNK_SIZE + (((x + 1) % CHUNK_SIZE) + CHUNK_SIZE) % CHUNK_SIZE) * 4 + 2]
  }
  setTile(land, x, y, { terrain: Terrain.Rock, biome: 0, tier: 1, cliff: true })
  expect(terrainAt(land, x, y)).toBe(Terrain.Rock)
  expect(depth(land)).toBe(Math.round((0.5 / 8) * 255))
  expect(land.revision).toBe(1)

  expect(chunks(loadLand(DEFAULT_CONFIG, saveLand(land)))).toEqual(chunks(land))

  applyEdits(replica, [x, y, ...tileBytes(land, x, y)])
  expect(chunks(replica)).toEqual(chunks(land))
})

test('вне карты править нельзя', () => {
  const land = createLand(DEFAULT_CONFIG, area)
  setTile(land, 500, 500, { terrain: Terrain.Rock, biome: 0, tier: 1, cliff: false })
  expect(land.revision).toBe(0)
})

test('выводимое у чанков не зависит от порядка сборки и не меняется после загрузки', () => {
  // Сид 3: здесь у гор на стыке чанков выводимые биты зависели от того, собран ли сосед (баг, docs/bugs.md).
  const config = { ...DEFAULT_CONFIG, seed: 3 }
  const area = areaOf({ left: -256, top: -256, right: 256, bottom: 256 })
  const chunks: [number, number][] = []
  for (let y = 0; y < area.height; y++) for (let x = 0; x < area.width; x++) chunks.push([area.left + x, area.top + y])
  const forward = createLand(config, area)
  const backward = createLand(config, area)
  for (const [x, y] of chunks) getChunk(forward, x, y)
  for (const [x, y] of [...chunks].reverse()) getChunk(backward, x, y)
  const loaded = loadLand(config, saveLand(forward))
  for (const [x, y] of chunks) {
    expect(getChunk(backward, x, y)).toEqual(getChunk(forward, x, y))
    expect(getChunk(loaded, x, y)).toEqual(getChunk(forward, x, y))
  }
})
