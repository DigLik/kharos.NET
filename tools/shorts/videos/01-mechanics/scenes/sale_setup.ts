export default async (c: any) => {
  await c.sleep(2500)
  await c.evaluate(`(()=>{const cam=__scene.camera; cam.zoomTo(52); cam.centerOn(-49.5,-33.5)})()`)
}
