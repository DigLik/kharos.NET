export default async (c: any) => {
  const p = await c.evaluate(`(()=>{const q=__scene.camera.tileToScreen(-49.5,-32.5); return [q.x,q.y]})()`)
  await c.sleep(700); await c.click(p[0], p[1]); await c.sleep(1000)
  await c.press('Продажа'); await c.sleep(800)
  await c.press('Металл'); await c.sleep(700)
  await c.press('+50'); await c.sleep(500)
  await c.press('Продажа ×')
}
