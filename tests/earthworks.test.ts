import { expect, test } from 'bun:test'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import { Terrain, isCliffFoot, peakOf, terrainAt } from '../src/map/terrain'
import { CLIFF_COST, Dig, PEAK_COST, canDig, createSim, creditsOf, isWalkable, peakTiles, type Sim } from '../src/sim'
import { addCredits } from '../src/sim/economy'
import { addBuilders } from './builders'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024, rules: { techTree: false } }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** Первый тайл от середины карты по спирали, где выполняется fits. */
function near(sim: Sim, fits: (x: number, y: number) => boolean) {
  for (let radius = 0; radius < 400; radius++) {
    for (let dy = -radius; dy <= radius; dy++) {
      for (let dx = -radius; dx <= radius; dx++) if (Math.max(Math.abs(dx), Math.abs(dy)) === radius && fits(dx, dy)) return { x: dx, y: dy }
    }
  }
  throw new Error('Не нашлось места')
}

test('гору срывают целиком: строители работают с края, на её месте — скала; плата по числу её тайлов', () => {
  const sim = createSim(options)
  addCredits(sim, 1, 5000)
  const at = near(sim, (x, y) => peakOf(sim.land, x, y) !== null)
  const peak = peakOf(sim.land, at.x, at.y)!
  const tiles = peakTiles(sim, peak)
  const builders = addBuilders(sim, 1, Math.floor(peak.x), Math.floor(peak.y), 3)
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'dig', kind: 'peak', tiles: [at.x, at.y], builders })
  seconds(sim, TICK)
  expect(creditsOf(sim, 1)).toBe(credits - (tiles.length / 2) * PEAK_COST)
  // Ту же гору второй раз не заказывают.
  expect(canDig(sim, 1, 'peak', at.x, at.y)).toBe(false)
  seconds(sim, 60)
  for (let i = 0; i < tiles.length; i += 2) {
    expect(terrainAt(sim.land, tiles[i], tiles[i + 1])).toBe(Terrain.Rock)
    expect(isWalkable(sim, tiles[i], tiles[i + 1])).toBe(true)
  }
  expect(sim.world.count(Dig)).toBe(0)
})

test('обрыв сглаживают по кромке: рамка по подножию берёт кромки над ним, и подножие становится проездом', () => {
  const sim = createSim(options)
  addCredits(sim, 1, 5000)
  // Подножие, к которому можно подъехать: снизу проходимый тайл.
  const foot = near(sim, (x, y) => isCliffFoot(sim.land, x, y) && isWalkable(sim, x, y + 2) && terrainAt(sim.land, x, y) !== Terrain.Mountain)
  expect(isWalkable(sim, foot.x, foot.y)).toBe(false)
  const builders = addBuilders(sim, 1, foot.x, foot.y + 3, 3)
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'dig', kind: 'cliff', tiles: [foot.x, foot.y], builders })
  seconds(sim, TICK)
  let edges = 0
  for (const [, dig] of sim.world.query(Dig)) if (dig.kind === 'cliff') edges++
  expect(edges).toBeGreaterThan(0)
  expect(creditsOf(sim, 1)).toBe(credits - edges * CLIFF_COST)
  seconds(sim, 60)
  expect(sim.world.count(Dig)).toBe(0)
  expect(isCliffFoot(sim.land, foot.x, foot.y)).toBe(false)
  expect(isWalkable(sim, foot.x, foot.y)).toBe(true)
})

test('отмена землеройной работы возвращает кредиты; без исследования — нельзя', () => {
  const sim = createSim(options)
  addCredits(sim, 1, 5000)
  const at = near(sim, (x, y) => peakOf(sim.land, x, y) !== null)
  const builders = addBuilders(sim, 1, at.x + 6, at.y, 1)
  const credits = creditsOf(sim, 1)
  sim.send(1, { type: 'dig', kind: 'peak', tiles: [at.x, at.y], builders: [] })
  seconds(sim, TICK)
  expect(creditsOf(sim, 1)).toBeLessThan(credits)
  sim.send(1, { type: 'unpave', tiles: [at.x, at.y], builders })
  seconds(sim, TICK)
  expect(creditsOf(sim, 1)).toBe(credits)
  expect(sim.world.count(Dig)).toBe(0)

  // Без исследования землеройных работ гору не тронуть.
  const closed = createSim({ ...options, rules: { techTree: true } })
  expect(canDig(closed, 1, 'peak', at.x, at.y)).toBe(false)
})
