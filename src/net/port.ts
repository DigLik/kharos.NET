import { createLocalServer, type LocalPort } from './local'

/**
 * Порт к хосту локальной игры слота name: общий воркер, если браузер их умеет, — тогда все вкладки с одним name
 * играют в один мир; нет — обычный; нет и его (нативная оболочка) — хост в том же потоке.
 */
export function openLocalPort(name: string): LocalPort {
  if (typeof SharedWorker !== 'undefined') {
    return new SharedWorker(new URL('./local.worker.ts', import.meta.url), { type: 'module', name }).port as unknown as LocalPort
  }
  if (typeof Worker !== 'undefined') return new Worker(new URL('./local.worker.ts', import.meta.url), { type: 'module', name }) as unknown as LocalPort
  return inProcess()
}

/** Общий воркер слота name для разовой посылки, если браузер умеет общие воркеры; иначе undefined. Порт закрывает тот, кто взял. */
export function sharedLocalPort(name: string): MessagePort | undefined {
  if (typeof SharedWorker === 'undefined') return undefined
  return new SharedWorker(new URL('./local.worker.ts', import.meta.url), { type: 'module', name }).port
}

/**
 * Хост локальной игры в том же потоке — когда воркеров нет (нативная оболочка): тот же хост, что в воркере, а вкладка
 * говорит с ним парой портов, как через postMessage: посылка доходит следующей задачей и копией, а не тем же массивом.
 */
function inProcess(): LocalPort {
  const deliver = (port: LocalPort, data: unknown) => {
    const copy = data instanceof Uint8Array ? data.slice() : data
    setTimeout(() => port.onmessage?.({ data: copy }), 0)
  }
  const tab: LocalPort = { onmessage: null, postMessage: (data) => deliver(host, data) }
  const host: LocalPort = { onmessage: null, postMessage: (data) => deliver(tab, data) }
  const server = createLocalServer()
  server.connect(host)
  // Как воркер локальной игры (local.worker.ts): хост продвигает мир 20 раз в секунду по часам вкладки.
  let last = performance.now()
  setInterval(() => {
    const now = performance.now()
    server.advance((now - last) / 1000)
    last = now
  }, 1000 / 20)
  return tab
}
