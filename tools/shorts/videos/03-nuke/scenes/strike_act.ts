import { follow, glide, host, screen } from './host'
/** Шахта → «Пуск» → щелчок по крепости → чат и таймер → старт, полёт, взрыв, воронки. */
export default async (c: any) => {
  await c.evaluate(`(()=>{const cam=__scene.camera;cam.zoomTo(30);cam.centerOn(__stage.silo.x,__stage.silo.y-1)})()`)
  await c.sleep(700)
  const s = await screen(c, '__stage.silo.x', '__stage.silo.y'); await c.click(s[0], s[1])
  await c.sleep(1300); await c.press(c.t('grid.launch'))
  await glide(c, '__stage.foe.x', '__stage.foe.y', 24, 1600)
  await c.sleep(500)
  const f = await screen(c, '__stage.foe.x', '__stage.foe.y'); await c.move(f[0] - 40, f[1] - 30); await c.sleep(300); await c.move(f[0], f[1]); await c.sleep(300); await c.click(f[0], f[1]); await c.sleep(200); await c.move(360, 110)
  await c.sleep(2500)
  await glide(c, '__stage.silo.x', '__stage.silo.y', 26, 1500)
  // Если щелчок не навёл ракету (попал не туда), пуск ставится напрямую — иначе ролик без удара.
  const foe = await c.evaluate('__stage.foe')
  await host(c, 'launch.js', { TX: foe.x, TY: foe.y, LEFT: 30 })
  await host(c, 'rush.js', { LEFT: 2.5 })
  await follow(c, 20, 4500)
}
