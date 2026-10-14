import { host } from './host'
/** Чужая база-крепость в 42 тайлах и своя шахта с готовой боеголовкой; INTERCEPT — у базы перехватчик. */
export const stage = async (c: any, intercept: boolean) => {
  await c.sleep(2500)
  await c.evaluate(`window.__stage = ${JSON.stringify(await host(c, 'stage.js', { DXV: 42, ICV: intercept }))}`)
  // Таймер пуска прокручивается сдвигом его начала назад: нужно, чтобы с начала мира прошло больше 30 с.
  while ((await c.evaluate('__scene.sim.time.tick')) < 640) await c.sleep(500)
}
export default (c: any) => stage(c, false)
