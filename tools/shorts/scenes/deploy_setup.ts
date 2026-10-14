import { readFileSync } from 'fs'
export default async (c: any) => {
  await c.press('Новая игра'); await c.sleep(800)
  await c.evaluate(`(()=>{const i=document.querySelector('input[type=number]'); const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set; set.call(i,'14'); i.dispatchEvent(new Event('input',{bubbles:true}))})()`)
  await c.sleep(300); await c.press('Начать'); await c.sleep(5000)
  await c.evaluate(`(()=>{const cam=__scene.camera; cam.zoomTo(48); cam.centerOn(0.5, 2.5)})()`)
}
