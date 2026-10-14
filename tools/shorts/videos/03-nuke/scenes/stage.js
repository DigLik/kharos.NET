// Постановка в ?sandbox — выполняется в воркере хоста через __host (hooks.patch): своя ракетная шахта с готовой боеголовкой и чужая база-крепость (TRAINING_PLAYER) в DX тайлах
// восточнее, у неё свой радар игрока — чтобы цель была видна сквозь туман. window.__stage — где что стоит.
// INTERCEPT=true — у чужой базы ещё и перехватчик с полным пакетом ракет.
async (s) => { const DX = DXV, INTERCEPT = ICV
  const m = await import('/src/sim/index.ts')
  const b = await import('/src/sim/buildings.ts')
  const u = await import('/src/sim/units.ts')
  const c = await import('/src/sim/combat.ts')
  const f = await import('/src/sim/fortify.ts')
  const me = 1, foe = c.TRAINING_PLAYER
  let core
  for (const [, B, p, o] of s.world.query(m.Building, m.Position, m.Owner)) if (B.type === 'command' && o.player === me) core = { x: p.x, y: p.y }
  const near = (type, x, y, gap, R = 14) => {
    for (let r = 0; r <= R; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++)
      if (Math.max(Math.abs(dx), Math.abs(dy)) === r && b.canPlace(s, type, x + dx, y + dy, gap)) return { x: x + dx, y: y + dy }
  }
  const put = (type, x, y, player, gap = 1, R) => { const t = near(type, x, y, gap, R); return t && { e: b.placeBuilding(s.world, type, t.x, t.y, player), ...t } }
  // Своя шахта — к западу от главного здания, на открытом месте.
  const silo = put('silo', core.x - 2, core.y + 9, me, 1, 20)
  Object.assign(s.world.get(silo.e, m.Silo), { ready: true })
  // Чужая база: главное здание, вокруг заводы и склады, спереди — укрепление.
  const fc = near('command', Math.round(core.x + DX), Math.round(core.y), 6, 30)
  b.placeBuilding(s.world, 'command', fc.x, fc.y, foe)
  const fx = fc.x + 1, fy = fc.y + 1
  let seed = 7; const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647)
  for (const { type, u: U, v } of f.fortification(rnd)) { const x = fx - U, y = fy + v; if (b.canPlace(s, type, x, y)) b.placeBuilding(s.world, type, x, y, foe) }
  let icp
  if (INTERCEPT) { icp = put('interceptor', fx + 3, fy + 3, foe, 1, 16); Object.assign(s.world.get(icp.e, m.Interceptor), { missiles: 4 }) }
  for (const t of ['generator', 'generator', 'generator', 'factory', 'barracks', 'lab', 'radar', 'smelter', 'metalYard', 'spaceport', 'matter', 'ammoPlant', 'airfield', 'shieldGenerator'])
    put(t, fx + 3, fy, foe, 1, 12)
  m.connectAll(s, foe)
  const tiles = u.freeTilesNear(s, fx + 2, fy, 14, 2)
  const army = ['tank', 'tank', 'tank', 'infantry', 'infantry', 'infantry', 'infantry', 'rocketeer', 'rocketeer', 'buggy', 'tesla', 'tank', 'infantry', 'infantry']
  for (let i = 0; i * 2 < tiles.length; i++) u.spawnUnit(s, army[i], foe, tiles[i * 2], tiles[i * 2 + 1])
  // Свой радар на краю круга взрыва: цель видна, а сам он уцелеет.
  const eye = put('radar', fx - 15, fy - 2, me, 0, 8)
  return { silo: { x: silo.x + 1.5, y: silo.y + 1.5 }, foe: { x: fx, y: fy }, eye, icp: icp && { x: icp.x + 1, y: icp.y + 1 } }
}
