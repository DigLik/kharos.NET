import { glide } from './host'
/** Чужая крепость: камера медленно проходит над ней. */
export default async (c: any) => {
  await c.evaluate(`(()=>{const cam=__scene.camera;cam.zoomTo(17);cam.centerOn(__stage.foe.x+4,__stage.foe.y-3)})()`)
  await glide(c, '__stage.foe.x-2', '__stage.foe.y+2', 23, 9000)
}
