// Локальная игра в окне: тот же хост, что считает мир в воркере страницы (`src/net/local.worker.ts`), но в том же
// потоке — воркеров у оболочки пока нет. Вкладка и хост говорят через пару портов, как через postMessage: посылка
// доходит следующей задачей и копией, а не тем же массивом. Так копия мира у игры — та же реплика, что на странице, и
// игра не отличает окно от вкладки.

import type { Session } from '../../src/net/connect'
import { createLocalServer, type LocalControl, type LocalPort, type LocalSetup } from '../../src/net/local'
import { decodeServer } from '../../src/net/protocol'
import { createReplica, type Replica } from '../../src/net/replica'
import { t } from '../../src/i18n'

/** Как часто хост продвигает мир — тот же срок, что у воркера страницы. */
const TICK_MS = 1000 / 20

/** Доставка как у postMessage: следующей задачей и копией — хост может переиспользовать свой буфер. */
function deliver(port: LocalPort, data: unknown) {
  const copy = data instanceof Uint8Array ? data.slice() : data
  setTimeout(() => port.onmessage?.({ data: copy }), 0)
}

/** Пара связанных портов: что послано в один, приходит в другой. */
function pair(): [LocalPort, LocalPort] {
  const tab: LocalPort = { onmessage: null, postMessage: (data) => deliver(host, data) }
  const host: LocalPort = { onmessage: null, postMessage: (data) => deliver(tab, data) }
  return [tab, host]
}

/**
 * Заводит локальный мир и подключается к нему — как `connectLocal` у страницы, только хост здесь же. Мир идёт по
 * часам окна: при записи — ровным шагом вместе с кадрами.
 */
export function startLocal(setup: LocalSetup): Promise<Session> {
  const server = createLocalServer()
  const [tab, host] = pair()
  server.connect(host)
  let last = performance.now()
  setInterval(() => {
    const now = performance.now()
    server.advance((now - last) / 1000)
    last = now
  }, TICK_MS)
  const control = (message: LocalControl) => tab.postMessage(message)
  return new Promise((resolve) => {
    let sim: Replica | undefined
    tab.onmessage = ({ data }) => {
      // Сохранения хост шлёт объектом — их пока класть некуда: диска у окна нет (пункт 10.5 плана).
      if (typeof data !== 'string' && !(data instanceof Uint8Array)) return
      const message = decodeServer(data)
      if (sim) return sim.receive(message)
      if (message.type !== 'welcome') return
      sim = createReplica(message, (text) => tab.postMessage(text), () => control({ type: 'leave' }))
      resolve({ sim, player: message.player, local: { restart: (options, battle) => control({ type: 'restart', options, battle }) } })
    }
    control({ type: 'start', ...setup, names: { player: t('server.player'), server: t('server.name') } })
  })
}
