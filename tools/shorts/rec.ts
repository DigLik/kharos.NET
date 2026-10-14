// Запись кадров игры через CDP screencast.
// bun rec.ts <url> <outDir> <seconds> [--warm=ms] [--css=file] [--script=file.js] [--size=540x960]
import { mkdirSync, writeFileSync, readFileSync, rmSync } from 'fs'
import { spawn } from 'child_process'

const [url, outDir, secondsArg, ...rest] = process.argv.slice(2)
const opt = Object.fromEntries(rest.map((a) => a.replace(/^--/, '').split('=')))
const seconds = Number(secondsArg)
const warm = Number(opt.warm ?? 3000)
const [w, h] = (opt.size ?? '540x960').split('x').map(Number)
const port = 9333
const profile = `${process.env.SHORTS_TMP ?? "/tmp"}/kharos-chrome-rec`
rmSync(profile, { recursive: true, force: true })
rmSync(outDir, { recursive: true, force: true })
mkdirSync(outDir, { recursive: true })

const chrome = spawn('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, `--window-size=${w},${h}`,
  `--force-device-scale-factor=${opt.dpr ?? 2}`, '--hide-scrollbars', '--mute-audio', '--enable-gpu', '--ignore-gpu-blocklist',
  'about:blank',
], { stdio: 'ignore' })
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

let targets: any[] = []
for (let i = 0; i < 50; i++) {
  try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.some((t) => t.type === 'page')) break } catch {}
  await sleep(200)
}
const page = targets.find((t) => t.type === 'page')
const ws = new WebSocket(page.webSocketDebuggerUrl)
await new Promise((r) => (ws.onopen = r))
let id = 0
const pending = new Map<number, (v: any) => void>()
const handlers: ((m: any) => void)[] = []
ws.onmessage = (e) => {
  const m = JSON.parse(String(e.data))
  if (m.id && pending.has(m.id)) { pending.get(m.id)!(m); pending.delete(m.id) } else handlers.forEach((f) => f(m))
}
const send = (method: string, params: any = {}) => new Promise<any>((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })) })
const evaluate = async (expression: string) => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })).result?.result?.value

await send('Page.enable')
await send('Runtime.enable')
handlers.push((m) => { if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') console.error('console:', m.params.args.map((a: any) => a.value ?? a.description).join(' ')) })
await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: Number(opt.dpr ?? 2), mobile: false })
await send('Page.navigate', { url })
for (let i = 0; i < 100; i++) {
  const clicked = await evaluate(`(() => { const b = [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Продолжить'); if (b) { b.click(); return true } return false })()`)
  if (clicked) break
  await sleep(200)
}
await sleep(800)
if (opt.css) await evaluate(`(() => { const s = document.createElement('style'); s.textContent = ${JSON.stringify(readFileSync(opt.css, 'utf8'))}; document.head.append(s) })()`)
const mouse = (type: string, x: number, y: number, button = 'left', buttons = 0) => send('Input.dispatchMouseEvent', { type, x, y, button, buttons, clickCount: 1 })
const ctx = {
  evaluate, send, sleep,
  async click(x: number, y: number, right = false) {
    const b = right ? 'right' : 'left'
    await mouse('mouseMoved', x, y); await mouse('mousePressed', x, y, b, right ? 2 : 1); await sleep(70); await mouse('mouseReleased', x, y, b, 0)
  },
  move: (x: number, y: number) => mouse('mouseMoved', x, y),
  async drag(x1: number, y1: number, x2: number, y2: number, steps = 12) {
    await mouse('mouseMoved', x1, y1); await mouse('mousePressed', x1, y1, 'left', 1)
    for (let i = 1; i <= steps; i++) { await mouse('mouseMoved', x1 + ((x2 - x1) * i) / steps, y1 + ((y2 - y1) * i) / steps, 'left', 1); await sleep(30) }
    await mouse('mouseReleased', x2, y2, 'left', 0)
  },
  /** Центр видимой кнопки с таким текстом (начало текста), или null. */
  async at(text: string) {
    return evaluate(`(() => { const b = [...document.querySelectorAll('button')].find((b) => b.offsetParent && b.textContent.trim().startsWith(${JSON.stringify(text)})); if (!b) return null; const r = b.getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2] })()`)
  },
  async press(text: string) { const p = await ctx.at(text); if (!p) { console.error('no button', text); return false } await ctx.click(p[0], p[1]); return true },
}
if (opt.setup) await (await import(opt.setup)).default(ctx)
await sleep(warm)

const frames: { file: string; t: number }[] = []
handlers.push((m) => {
  if (m.method !== 'Page.screencastFrame') return
  const { data, metadata, sessionId } = m.params
  const file = `${outDir}/${String(frames.length).padStart(5, '0')}.jpg`
  writeFileSync(file, Buffer.from(data, 'base64'))
  frames.push({ file, t: metadata.timestamp })
  send('Page.screencastFrameAck', { sessionId })
})
await send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: 1080, maxHeight: 1920 })
const acting = opt.act ? (await import(opt.act)).default(ctx) : null
await sleep(seconds * 1000)
await acting
await send('Page.stopScreencast')
writeFileSync(`${outDir}/frames.json`, JSON.stringify(frames))
const span = frames.length > 1 ? frames.at(-1)!.t - frames[0].t : 0
console.log(`${frames.length} frames over ${span.toFixed(1)}s = ${(frames.length / span).toFixed(1)} fps`)
ws.close()
chrome.kill()
process.exit(0)
