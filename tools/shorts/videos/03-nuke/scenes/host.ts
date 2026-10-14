import { readFileSync } from 'fs'
/** Выполняет файл-функцию (sim) => … в воркере хоста (хук __host из hooks.patch), подставив имена из vars. */
export async function host(c: any, file: string, vars: Record<string, string | number | boolean> = {}) {
  let code = readFileSync(`${import.meta.dir}/${file}`, 'utf8')
  for (const [k, v] of Object.entries(vars)) code = code.replaceAll(k, String(v))
  return c.evaluate(`__host(${JSON.stringify(code)})`)
}
/** Камера плавно к тайлу (x, y) и масштабу zoom за ms миллисекунд. */
export const glide = (c: any, x: number | string, y: number | string, zoom: number, ms: number) => c.evaluate(`new Promise((done)=>{const cam=__scene.camera,f={...cam.focus},z0=cam.zoom,t0=performance.now();const X=${x},Y=${y};
  const step=()=>{const k=Math.min(1,(performance.now()-t0)/${ms}),e=k<.5?2*k*k:1-(-2*k+2)**2/2;cam.zoomTo(z0+(${zoom}-z0)*e);cam.centerOn(f.x+(X-f.x)*e,f.y+(Y-f.y)*e);k<1?requestAnimationFrame(step):done(1)};step()})`)
/** Экранная точка тайла. */
export const screen = (c: any, x: number | string, y: number | string) => c.evaluate(`(()=>{const q=__scene.camera.tileToScreen(${x},${y});return [q.x,q.y]})()`)
/** Камера ведёт летящую ядерную ракету, пока она есть, и ms после. */
export const follow = (c: any, zoom: number, ms: number) => c.evaluate(`(async()=>{const m=await import('/src/sim/index.ts');const cam=__scene.camera,s=__scene.sim;let seen=false,end=0;
  await new Promise((done)=>{const step=()=>{let p=null;for(const [,x] of s.world.query(m.Missile)){const dx=x.toX-x.fromX,dy=x.toY-x.fromY,d=Math.hypot(dx,dy)||1,k=Math.min(1,(s.time.tick-x.born)*s.time.step*8/d);p={x:x.fromX+dx*k,y:x.fromY+dy*k}}
    if(p){seen=true;const f=cam.focus;cam.centerOn(f.x+(p.x-f.x)*.12,f.y+(p.y-f.y)*.12);cam.zoomTo(cam.zoom+(${zoom}-cam.zoom)*.05)}
    else if(seen&&!end) end=performance.now();
    end&&performance.now()-end>${ms}?done(1):requestAnimationFrame(step)};step()});return seen})()`)
