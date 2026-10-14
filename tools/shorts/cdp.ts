// Одна команда к уже запущенному Chrome (порт 9334):
// bun cdp.ts eval '<js>' | shot <file> | click <x> <y> [right] | drag x1 y1 x2 y2 | key <key> | nav <url> | text '<button text>'
import { writeFileSync } from 'fs'
const port = 9334
const [cmd, ...args] = process.argv.slice(2)
const targets: any[] = await (await fetch(`http://127.0.0.1:${port}/json`)).json()
const page = targets.find((t) => t.type === 'page')
const ws = new WebSocket(page.webSocketDebuggerUrl)
await new Promise((r) => (ws.onopen = r))
let id = 0
const pending = new Map<number, (v: any) => void>()
ws.onmessage = (e) => { const m = JSON.parse(String(e.data)); if (m.id && pending.has(m.id)) { pending.get(m.id)!(m); pending.delete(m.id) } }
const send = (method: string, params: any = {}) => new Promise<any>((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })) })
const evaluate = async (expression: string) => { const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }); return r.result?.exceptionDetails ? r.result.exceptionDetails : r.result?.result?.value }
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))
if (process.env.VP) {
  const [vw, vh, vd] = process.env.VP.split('x').map(Number)
  await send('Emulation.setDeviceMetricsOverride', { width: vw, height: vh, deviceScaleFactor: vd, mobile: false })
  await sleep(Number(process.env.VP_WAIT ?? 400))
}
const mouse = (type: string, x: number, y: number, button = 'left', buttons = 0) => send('Input.dispatchMouseEvent', { type, x, y, button, buttons, clickCount: 1 })

if (cmd === 'eval') console.log(JSON.stringify(await evaluate(args[0]), null, 1))
else if (cmd === 'shot') { const r = await send('Page.captureScreenshot', { format: 'jpeg', quality: 80 }); writeFileSync(args[0], Buffer.from(r.result.data, 'base64')) }
else if (cmd === 'click') {
  const [x, y] = args.map(Number); const b = args[2] === 'right' ? 'right' : 'left'; const bs = b === 'right' ? 2 : 1
  await mouse('mouseMoved', x, y); await mouse('mousePressed', x, y, b, bs); await sleep(60); await mouse('mouseReleased', x, y, b, 0)
} else if (cmd === 'drag') {
  const [x1, y1, x2, y2] = args.map(Number)
  await mouse('mouseMoved', x1, y1); await mouse('mousePressed', x1, y1, 'left', 1)
  for (let i = 1; i <= 10; i++) { await mouse('mouseMoved', x1 + ((x2 - x1) * i) / 10, y1 + ((y2 - y1) * i) / 10, 'left', 1); await sleep(30) }
  await mouse('mouseReleased', x2, y2, 'left', 0)
} else if (cmd === 'move') { const [x, y] = args.map(Number); await mouse('mouseMoved', x, y) }
else if (cmd === 'key') { await send('Input.dispatchKeyEvent', { type: 'keyDown', key: args[0], code: args[1] ?? '' }); await send('Input.dispatchKeyEvent', { type: 'keyUp', key: args[0], code: args[1] ?? '' }) }
else if (cmd === 'nav') await send('Page.navigate', { url: args[0] })
else if (cmd === 'text') {
  console.log(await evaluate(`(() => { const b = [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === ${JSON.stringify(args[0])}); if (!b) return 'not found'; const r = b.getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2] })()`))
} else if (cmd === 'buttons') console.log(await evaluate(`[...document.querySelectorAll('button')].filter((b) => b.offsetParent).map((b) => { const r = b.getBoundingClientRect(); return (b.textContent.trim() || b.title || b.getAttribute('aria-label')) + ' @' + Math.round(r.x + r.width / 2) + ',' + Math.round(r.y + r.height / 2) }).join('\\n')`))
ws.close()
process.exit(0)
