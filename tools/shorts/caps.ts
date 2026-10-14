import { writeFileSync, readFileSync } from 'fs'
const port = 9334
const t: any[] = await (await fetch(`http://127.0.0.1:${port}/json`)).json()
const ws = new WebSocket(t.find((x) => x.type === 'page').webSocketDebuggerUrl); await new Promise((r) => (ws.onopen = r))
let id = 0; const pend = new Map()
ws.onmessage = (e) => { const m = JSON.parse(String(e.data)); pend.get(m.id)?.(m) }
const send = (method: string, params: any = {}) => new Promise<any>((r) => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })) })
const ev = async (expression: string) => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })).result?.result?.value
await send('Emulation.setDeviceMetricsOverride', { width: 1080, height: 1920, deviceScaleFactor: 1, mobile: false })
await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } })
await ev(readFileSync(`${import.meta.dir}/cap_setup.js`, 'utf8'))
const caps = JSON.parse(readFileSync('caps.json', 'utf8'))
for (const [name, html] of Object.entries(caps)) {
  await ev(`document.body.innerHTML = ${JSON.stringify(html)}; document.fonts.ready.then(()=>1)`)
  await new Promise((r) => setTimeout(r, 150))
  const r = await send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(`caps/${name}.png`, Buffer.from(r.result.data, 'base64'))
}
process.exit(0)
