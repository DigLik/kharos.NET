import { readFileSync } from 'fs'
const find = readFileSync(`${import.meta.dir}/../find.js`, 'utf8')
export default async (c: any) => {
  const f = await c.evaluate(find)
  const cam = await c.evaluate(`(()=>{const cam=__scene.camera; const s=${JSON.stringify(f.spot)}; const p=cam.tileToScreen(s.x+1.5, s.y+1.5); const m=cam.tileToScreen(${f.mcv.x}, ${f.mcv.y}); return {p, m}})()`)
  console.error('deploy', JSON.stringify(f), JSON.stringify(cam))
  await c.sleep(600)
  await c.click(cam.m.x, cam.m.y); await c.sleep(900)
  await c.press('Развернуть'); await c.sleep(500)
  for (let i = 0; i <= 15; i++) { await c.move(cam.m.x + (cam.p.x - cam.m.x) * i / 15, cam.m.y + (cam.p.y - cam.m.y) * i / 15); await c.sleep(40) }
  await c.sleep(500)
  await c.click(cam.p.x, cam.p.y)
  // Ждём главное здание и заказываем строителя.
  for (let i = 0; i < 40; i++) { await c.sleep(250); if (await c.at('Строитель')) break; const done = await c.evaluate(`!!document.querySelector('.steps li.is-current') && document.querySelector('.steps li.is-current').textContent.includes('строител')`); if (done) { await c.sleep(300); await c.click(cam.p.x, cam.p.y); await c.sleep(600) } }
  await c.press('Строитель'); await c.sleep(450); await c.press('Строитель')
}
