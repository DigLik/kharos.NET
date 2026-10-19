/**
 * Эталоны кадров TS для Kharos.Client.Tests / Kharos.Gpu.Tests (docs/dotnet-n6.md, Н6.4, уровень 2): сцены из frames/scenes.ts рисует настоящий
 * клиент TS в headless Chrome со SwiftShader (окон нет), кадры и описания сцен ложатся в
 *   dotnet/tests/Kharos.Client.Tests/Frames/golden/<сцена>.png и scenes.json (описания + что вышло: тик, взрывы, снаряды).
 * Запуск из корня репозитория (после `bun install`), путь к Chrome — CHROME:
 *   CHROME="C:/Program Files/Google/Chrome/Application/chrome.exe" bun dotnet/tools/golden/frames.ts [имя сцены ...]
 * Нет Chrome или он не поднялся — остановка с причиной. Страница собирается Vite (frames/vite.config.ts) в dotnet/.cache/frames-build.
 */
import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { scenes } from './frames/scenes'

const here = dirname(fileURLToPath(import.meta.url))
const repo = join(here, '..', '..', '..')
const build = join(repo, 'dotnet', '.cache', 'frames-build')
const profile = join(repo, 'dotnet', '.cache', 'frames-chrome')
const out = join(repo, 'dotnet', 'tests', 'Kharos.Client.Tests', 'Frames', 'golden')
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

const chromePath = process.env.CHROME
if (!chromePath || !existsSync(chromePath)) {
  console.error(`Chrome не найден: задайте CHROME (сейчас: ${chromePath ?? 'не задан'}). Браузеры скрипт не ставит.`)
  process.exit(2)
}

// 1. Сборка страницы.
const vite = spawnSync(process.execPath, ['x', 'vite', 'build', '--config', join(here, 'frames', 'vite.config.ts')], { cwd: repo, encoding: 'utf8' })
if (vite.status !== 0) {
  console.error('сборка Vite не удалась:\n' + vite.stdout + vite.stderr)
  process.exit(1)
}

// 2. Статика страницы на loopback.
const types: Record<string, string> = { html: 'text/html', js: 'text/javascript', css: 'text/css', woff2: 'font/woff2', png: 'image/png', json: 'application/json' }
const server = Bun.serve({
  hostname: '127.0.0.1',
  port: 0,
  fetch(request) {
    const path = new URL(request.url).pathname
    const file = join(build, path === '/' ? 'index.html' : decodeURIComponent(path))
    if (!file.startsWith(build) || !existsSync(file)) return new Response('нет', { status: 404 })
    return new Response(Bun.file(file), { headers: { 'content-type': types[file.split('.').pop()!] ?? 'application/octet-stream' } })
  },
})

// 3. Chrome: без окна, программный GL.
rmSync(profile, { recursive: true, force: true })
const PORT = 9347
const chrome = spawn(
  chromePath,
  [
    '--headless=new',
    `--user-data-dir=${profile}`,
    `--remote-debugging-port=${PORT}`,
    '--window-size=640,400',
    '--force-device-scale-factor=1',
    '--hide-scrollbars',
    '--mute-audio',
    '--no-first-run',
    '--no-default-browser-check',
    '--use-gl=angle',
    '--use-angle=swiftshader',
    '--enable-unsafe-swiftshader',
    '--ignore-gpu-blocklist',
    'about:blank',
  ],
  { stdio: 'ignore' },
)
let code = 0
const finish = () => {
  try {
    chrome.kill()
  } catch {}
  server.stop(true)
}
process.on('exit', finish)

try {
  let target: { webSocketDebuggerUrl: string } | undefined
  for (let i = 0; i < 100 && !target; i++) {
    await sleep(200)
    try {
      const list = (await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()) as { type: string; webSocketDebuggerUrl: string }[]
      target = list.find((page) => page.type === 'page')
    } catch {}
  }
  if (!target) throw new Error('Chrome не поднялся в headless-режиме')

  const socket = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((resolve) => (socket.onopen = resolve))
  let id = 0
  const pending = new Map<number, (value: any) => void>()
  const pageErrors: string[] = []
  socket.onmessage = (event) => {
    const message = JSON.parse(String(event.data))
    if (message.id && pending.has(message.id)) pending.get(message.id)!(message.error ? { error: message.error } : message.result)
    if (message.method === 'Runtime.exceptionThrown') pageErrors.push('исключение: ' + (message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text))
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') pageErrors.push('console.error: ' + message.params.args.map((a: any) => a.value ?? a.description).join(' '))
  }
  const send = (method: string, params: object = {}) =>
    new Promise<any>((resolve) => {
      pending.set(++id, resolve)
      socket.send(JSON.stringify({ id, method, params }))
    })
  const evaluate = async (expression: string) => {
    const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
    if (result.error || result.exceptionDetails) throw new Error(JSON.stringify(result.error ?? result.exceptionDetails.exception?.description ?? result.exceptionDetails))
    return result.result.value
  }

  await send('Runtime.enable')
  await send('Page.enable')
  await send('Emulation.setDeviceMetricsOverride', { width: 640, height: 400, deviceScaleFactor: 1, mobile: false })
  await send('Page.navigate', { url: `http://127.0.0.1:${server.port}/index.html` })
  // Страница рисует всю графику клиента сама (десяток секунд на программном GL): ждём её сигнала.
  for (let i = 0; i < 600 && !(await evaluate('window.framesReady === true').catch(() => false)); i++) await sleep(500)
  const failure = await evaluate('window.framesError ?? null')
  if (failure) throw new Error('страница не запустилась:\n' + failure)
  const renderer = await evaluate("(() => { const c = document.createElement('canvas'); const gl = c.getContext('webgl2'); if (!gl) return 'нет WebGL 2'; const e = gl.getExtension('WEBGL_debug_renderer_info'); return e ? gl.getParameter(e.UNMASKED_RENDERER_WEBGL) : 'без сведений' })()")
  console.log(`GL: ${renderer}`)

  const wanted = process.argv.slice(2)
  const all = scenes().filter((scene) => !wanted.length || wanted.includes(scene.name))
  mkdirSync(out, { recursive: true })
  const summary: Record<string, unknown>[] = []
  for (const spec of all) {
    const shot = (await evaluate(`window.frames.render(${JSON.stringify(spec.name)})`)) as { png: string; errors: string[]; [key: string]: unknown }
    const { png, ...info } = shot
    writeFileSync(join(out, `${spec.name}.png`), Buffer.from(png.slice(png.indexOf(',') + 1), 'base64'))
    summary.push({ ...spec, render: info })
    console.log(`${spec.name}: тик ${info.tick}, взрывов ${info.blasts}, снарядов ${info.shots}, площадок ${info.sites}${shot.errors.length ? ', ОШИБКИ ' + shot.errors.join('; ') : ''}`)
    if (shot.errors.length) code = 1
  }
  // При выборе части сцен остальные описания в файле сохраняются.
  const file = join(out, 'scenes.json')
  const previous: { name: string }[] = wanted.length && existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')).scenes : []
  const merged = [...previous.filter((scene) => !summary.some((one) => one.name === scene.name)), ...summary]
  const order = scenes().map((scene) => scene.name)
  merged.sort((a, b) => order.indexOf(a.name) - order.indexOf(b.name))
  writeFileSync(file, JSON.stringify({ version: 1, width: 640, height: 400, gl: renderer, scenes: merged }, null, 1))
  if (pageErrors.length) {
    console.log('ошибки страницы:\n' + pageErrors.join('\n'))
    code = 1
  }
  socket.close()
} catch (error) {
  console.error(String(error))
  code = 1
} finally {
  finish()
}
process.exit(code)
