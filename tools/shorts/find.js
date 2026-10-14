(async () => {
  const m = await import('/src/sim/index.ts')
  const sc = window.__scene, s = sc.sim, cam = sc.camera
  let mcv = null
  for (const [e, u, p, o] of s.world.query(m.Unit, m.Position, m.Owner)) if (u.type === 'mcv' && o.player === sc.player) mcv = { x: p.x, y: p.y }
  if (!mcv) return 'no mcv ' + JSON.stringify(Object.keys(sc))
  let spot = null
  for (let r = 3; r < 9 && !spot; r++) for (const [dx, dy] of [[0, r], [r, 0], [-r, 0], [0, -r], [r, r], [-r, r]]) {
    const x = Math.floor(mcv.x) - 1 + dx, y = Math.floor(mcv.y) + dy
    if (m.canPlace(s, m.CORE, x, y, 1)) { spot = { x, y }; break }
  }
  return { mcv, spot, mcvScreen: cam.tileToScreen(mcv.x, mcv.y), zoom: cam.zoom }
})()
