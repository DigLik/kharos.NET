import { createWorld } from '../../src/net/local'
import { DEFAULT_CONFIG } from '../../src/map/terrain'
import { canPlace, CORE, Unit, Position } from '../../src/sim'
for (const seed of [1000, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]) {
  const sim = createWorld({ generator: { ...DEFAULT_CONFIG, seed }, size: 1024 } as any, 'play', {} as any)
  let mcv: any
  for (const [, u, p] of sim.world.query(Unit, Position)) if (u.type === 'mcv') mcv = p
  // Ближние места, куда MCV развернётся почти на месте.
  const near: string[] = []
  for (let dy = -3; dy <= 3; dy++) for (let dx = -3; dx <= 3; dx++) {
    const x = Math.floor(mcv.x) + dx, y = Math.floor(mcv.y) + dy
    if (canPlace(sim, CORE, x, y, 0)) near.push(`${x},${y}`)
  }
  console.log(seed, mcv.x, mcv.y, near.length, near.slice(0, 6).join(' '))
}
process.exit(0)
