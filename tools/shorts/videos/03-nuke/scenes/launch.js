// Пуск из своей шахты в (TX, TY) с LEFT секундами до старта — без интерфейса, для плана с перехватом.
async (s) => { const m = await import('/src/sim/index.ts'); for (const [e, x] of s.world.query(m.Silo)) { const r = m.orderLaunch(s, 1, e, TX, TY); x.start = Math.max(0, s.time.tick - Math.round((30 - LEFT) / s.time.step)); return r } }
