// Постановка в ?sandbox — выполняется в воркере хоста через __host (hooks.patch): своя ракетная шахта с готовой боеголовкой и чужая база-крепость (TRAINING_PLAYER) в DX тайлах
// восточнее, у неё свой радар игрока — чтобы цель была видна сквозь туман. window.__stage — где что стоит.
// INTERCEPT=true — у чужой базы ещё и перехватчик с полным пакетом ракет.
async (s) => { const DX = DXV, INTERCEPT = ICV
  const m = await import('/src/sim/index.ts')
  const b = await import('/src/sim/buildings.ts')
  const u = await import('/src/sim/units.ts')
  const c = await import('/src/sim/combat.ts')
  const iv = await import('/src/sim/inventory.ts')
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
  const fc = near('command', Math.round(core.x + DX), Math.round(core.y), 12, 40)
  // Под ядро (электростанции, щиты) — фундамент: на голый песок встают только турели и стены.
  const pv = await import('/src/sim/paved.ts')
  for (let dy = -8; dy <= 8; dy++) for (let dx = -8; dx <= 8; dx++) s.paving.lay(fc.x + 1 + dx, fc.y + 1 + dy, pv.paveCode('foundation', foe))
  b.placeBuilding(s.world, 'command', fc.x, fc.y, foe)
  const fx = fc.x + 1, fy = fc.y + 1
  // Крепость слоями от центра (смещения от главного здания, левый верхний угол постройки):
  // ядро — электростанции, накопители, радар; четыре щита по четвертям — купола перекрывают друг друга;
  // кольцо 7 — лазеры против брони вперемешку с разрядными против пехоты; кольцо 9 — пушки и пулемёты по сторонам,
  // ракетные в углах против авиации, лазеры у ворот; кольца 11 и 12 — стены, посередине сторон — ворота.
  const at = (t, dx, dy) => b.canPlace(s, t, fx + dx, fy + dy) ? b.placeBuilding(s.world, t, fx + dx, fy + dy, foe) : undefined
  for (const [dx, dy] of [[-5, -5], [3, -5], [-5, 3], [3, 3]]) at('shieldGenerator', dx, dy)
  for (const [dx, dy] of [[-3, -5], [-1, -5], [1, -5], [-3, 3], [-1, 3], [1, 3], [-5, -1], [3, -1]]) at('generator', dx, dy)
  at('radar', -3, -3); at('accumulator', 2, -3); at('accumulator', -3, 1)
  let icp
  if (INTERCEPT) { icp = { x: fx + 2, y: fy + 1 }; const e = at('interceptor', 2, 1); Object.assign(s.world.get(e, m.Interceptor), { missiles: 4 }) }
  const square = (r, pick) => { const out = []
    for (let d = -r; d <= r; d++) for (const [x, y] of [[d, -r], [d, r], [-r, d], [r, d]]) if (!out.some(([a, c]) => a === x && c === y)) out.push([x, y])
    for (const [x, y] of out) { const t = pick(x, y); if (t) at(t, x, y) } }
  const corner = (x, y, r) => Math.abs(x) >= r - 1 && Math.abs(y) >= r - 1
  const gateSide = (x, y) => Math.abs(x) <= 1 || Math.abs(y) <= 1
  square(7, (x, y) => (x + y) % 2 ? null : Math.abs(x + y) % 4 ? 'arcTurret' : 'laserTurret')
  square(9, (x, y) => corner(x, y, 9) ? 'rocketTurret' : gateSide(x, y) ? 'laserTurret' : (x + y) % 2 ? null : Math.abs(x - y) % 4 ? 'turret' : 'cannonTurret')
  square(11, (x, y) => gateSide(x, y) ? 'gate' : 'wall')
  square(12, (x, y) => gateSide(x, y) ? 'gate' : 'wall')
  // Патроны турелям — сразу в склад: сцена про удар, а не про снабжение.
  for (const [e, B, o] of s.world.query(m.Building, m.Owner)) if (o.player === foe) { const inv = s.world.get(e, m.Inventory); if (inv && b.buildingSpec(B.type).ammo) iv.put(inv, 'ammo', 50) }
  // Армия — на дорожке между турелями и стеной: танки у ворот, ракетчики по углам, пехота и разрядники по сторонам.
  const post = (t, x, y) => u.spawnUnit(s, t, foe, fx + x, fy + y)
  for (const [x, y] of [[0, -10], [0, 10], [-10, 0], [10, 0]]) { post('tank', x - 1, y); post('tank', x + 1, y) }
  for (const [x, y] of [[-10, -10], [10, -10], [-10, 10], [10, 10]]) { post('rocketeer', x, y); post('rocketeer', x + Math.sign(-x), y) }
  for (const [x, y] of [[-5, -10], [5, -10], [-5, 10], [5, 10], [-10, -5], [-10, 5], [10, -5], [10, 5]]) { post('infantry', x, y); post('tesla', x + (x ? 0 : 1), y + (x ? 1 : 0)) }
  const eye = null
  return { silo: { x: silo.x + 1.5, y: silo.y + 1.5 }, foe: { x: fx, y: fy }, eye, icp: icp && { x: icp.x + 1, y: icp.y + 1 } }
}
