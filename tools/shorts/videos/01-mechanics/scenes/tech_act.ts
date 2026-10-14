export default async (c: any) => {
  await c.sleep(600); await c.press(c.t('grid.techTree')); await c.sleep(1400)
  for (const name of ['research.pipes', 'research.radar', 'research.roads'].map(c.t)) {
    const p = await c.evaluate(`(()=>{const e=[...document.querySelectorAll('.tech button')].find(b=>b.textContent.includes(${JSON.stringify(name)})); if(!e) return null; const r=e.getBoundingClientRect(); return [r.x+r.width/2,r.y+r.height/2]})()`)
    if (p) { await c.click(p[0], p[1]); await c.sleep(900) }
  }
  await c.evaluate(`document.querySelector('.tech__list')?.scrollBy({top: 900, behavior: 'smooth'})`)
}
