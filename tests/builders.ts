import type { Entity } from '../src/ecs'
import type { Sim } from '../src/sim'
import { freeTilesNear, spawnUnit } from '../src/sim/units'

/**
 * Строители игрока рядом с (x, y), каждый на своём свободном тайле за стартовым набором. В стартовом наборе их нет — их заказывают в главном
 * здании, — а тестам стройки они нужны сразу.
 */
export function addBuilders(sim: Sim, player: number, x: number, y: number, count = 2): Entity[] {
  // Со второго кольца: ближние тайлы занял стартовый набор.
  const tiles = freeTilesNear(sim, x, y, count + 8, 2)
  const taken = new Set<string>()
  const builders: Entity[] = []
  for (let i = 0; i < tiles.length && builders.length < count; i += 2) {
    const key = `${tiles[i]},${tiles[i + 1]}`
    if (taken.has(key)) continue
    taken.add(key)
    builders.push(spawnUnit(sim, 'builder', player, tiles[i], tiles[i + 1]))
  }
  return builders
}
