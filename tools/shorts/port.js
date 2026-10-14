(async () => {
  const m = await import('/src/sim/index.ts')
  const sc = window.__scene, s = sc.sim, cam = sc.camera
  const all = {}
  for (const [e, b, p] of s.world.query(m.Building, m.Position)) if (['spaceport', 'lab', 'smelter', 'command'].includes(b.type)) all[b.type] = { x: p.x, y: p.y, w: m.BUILDINGS[b.type].width, h: m.BUILDINGS[b.type].height }
  return all
})()
