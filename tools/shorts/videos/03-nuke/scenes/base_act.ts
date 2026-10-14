import { glide } from './host'
/** Чужая крепость: камера медленно проходит над ней. */
export default async (c: any) => {
  await c.evaluate(`(()=>{const cam=__scene.camera;cam.zoomTo(30);cam.centerOn(__stage.foe.x+5,__stage.foe.y-4)})()`)
  await glide(c, '__stage.foe.x-3', '__stage.foe.y+3', 24, 9000)
}
