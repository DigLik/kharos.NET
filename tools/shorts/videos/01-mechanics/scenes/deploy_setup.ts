import { readFileSync } from 'fs'
export default async (c: any) => {
  // Если игра открылась сразу партией — выйти в главное меню.
  if (await c.at(c.t('game.menu'))) { await c.press(c.t('game.menu')); await c.sleep(700); await c.press(c.t('game.exitToMenu')); await c.sleep(1500) }
  await c.press(c.t('menu.newGame')); await c.sleep(800)
  await c.evaluate(`(()=>{const i=document.querySelector('input[type=number]'); const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set; set.call(i,'14'); i.dispatchEvent(new Event('input',{bubbles:true}))})()`)
  await c.sleep(300); await c.press(c.t('menu.start')); await c.sleep(5000)
  await c.evaluate(`(()=>{const cam=__scene.camera; cam.zoomTo(48); cam.centerOn(0.5, 2.5)})()`)
}
