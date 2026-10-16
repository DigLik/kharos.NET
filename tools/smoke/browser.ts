/**
 * Игра в headless Chrome: открыть адрес, после загрузки нажать Enter (экран загрузки → игра или меню), подождать и
 * снять кадр. Печатает ошибки страницы (исключения и console.error); код выхода 1, если они были.
 * bun tools/smoke/browser.ts <url> <файл снимка> [секунд до Enter] [секунд после]. Путь к Chrome — CHROME.
 * SMOKE_NO_WORKERS=1 — страница без Worker и SharedWorker, как в нативной оболочке: локальная игра идёт хостом в том же
 * потоке.
 */
import { spawn } from 'node:child_process'
import { rmSync } from 'node:fs'

const [url, shot, loadSeconds = '6', playSeconds = '4'] = process.argv.slice(2)
if (!url || !shot) throw new Error('bun tools/smoke/browser.ts <url> <файл снимка> [секунд до Enter] [секунд после]')
const profile = new URL('../../.cache/smoke/chrome', import.meta.url).pathname
rmSync(profile, { recursive: true, force: true })
const PORT = 9333
const chrome = spawn(process.env.CHROME ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', ['--headless=new', `--user-data-dir=${profile}`, '--window-size=1280,800', `--remote-debugging-port=${PORT}`, 'about:blank'], { stdio: 'ignore' })
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

let target: { webSocketDebuggerUrl: string } | undefined
for (let i = 0; i < 50 && !target; i++) {
  await sleep(200)
  try {
    const list = (await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()) as { type: string; webSocketDebuggerUrl: string }[]
    target = list.find((page) => page.type === 'page')
  } catch {}
}
if (!target) throw new Error('Chrome не поднялся')

const socket = new WebSocket(target.webSocketDebuggerUrl)
await new Promise((resolve) => (socket.onopen = resolve))
let id = 0
const pending = new Map<number, (value: any) => void>()
const errors: string[] = []
socket.onmessage = (event) => {
  const message = JSON.parse(String(event.data))
  if (message.id && pending.has(message.id)) pending.get(message.id)!(message.result ?? message.error)
  if (message.method === 'Runtime.exceptionThrown') {
    const details = message.params.exceptionDetails
    errors.push('исключение: ' + (details.exception?.description ?? details.text))
  }
  if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
    errors.push('console.error: ' + message.params.args.map((arg: any) => arg.value ?? arg.description).join(' '))
  }
}
const send = (method: string, params: object = {}) =>
  new Promise<any>((resolve) => {
    pending.set(++id, resolve)
    socket.send(JSON.stringify({ id, method, params }))
  })

await send('Runtime.enable')
await send('Page.enable')
if (process.env.SMOKE_NO_WORKERS) await send('Page.addScriptToEvaluateOnNewDocument', { source: 'delete globalThis.SharedWorker; delete globalThis.Worker' })
await send('Page.navigate', { url })
await sleep(Number(loadSeconds) * 1000)
for (const type of ['keyDown', 'keyUp']) await send('Input.dispatchKeyEvent', { type, key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 })
await sleep(Number(playSeconds) * 1000)
const { data } = await send('Page.captureScreenshot', { format: 'png' })
await Bun.write(shot, Buffer.from(data, 'base64'))
socket.close()
chrome.kill()
if (errors.length) {
  console.log('ошибки страницы:\n' + errors.join('\n'))
  process.exit(1)
}
console.log(`ошибок нет, кадр: ${shot}`)
process.exit(0)
