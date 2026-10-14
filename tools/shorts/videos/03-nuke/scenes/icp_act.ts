import { glide, host } from './host'
/** Та же ракета, но у крепости перехватчик: сбит над нашей же базой; потом — целая крепость. */
export default async (c: any) => {
  await c.evaluate(`(()=>{const cam=__scene.camera;cam.zoomTo(26);cam.centerOn(__stage.silo.x*.75+__stage.foe.x*.25,__stage.silo.y*.75+__stage.foe.y*.25)})()`)
  const foe = await c.evaluate('__stage.foe')
  await host(c, 'launch.js', { TX: foe.x, TY: foe.y, LEFT: 1.5 })
  await c.sleep(6000)
  await glide(c, '__stage.foe.x', '__stage.foe.y', 24, 2500)
}
