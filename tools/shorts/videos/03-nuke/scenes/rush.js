// Таймер идущего пуска — до старта остаётся LEFT секунд.
async (s) => { const m = await import('/src/sim/index.ts'); for (const [, x] of s.world.query(m.Silo)) if (x.start >= 0) x.start = Math.max(0, s.time.tick - Math.round((30 - LEFT) / s.time.step)) }
