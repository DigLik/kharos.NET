import type { BattleConfig } from '../map/settings'
import type { SimOptions, SimSave } from '../sim'
import { createLocalServer, type LocalControl, type LocalNotice, type LocalPort, type LocalSetup } from './local'
import { PROTOCOL_VERSION, decodeServer } from './protocol'
import { createReplica, type Replica } from './replica'
import { t, textOf } from '@kharos/i18n'

/**
 * Игра на хосте: копия его симуляции и номер игрока, которого хост выдал этому клиенту. local — управление
 * локальной игрой; у сервера его нет: мир там один на всех, и начать его заново клиент не может.
 */
export interface Session {
  sim: Replica
  player: number
  local?: {
    restart(options: SimOptions, battle: BattleConfig): void
  }
}

const IDS_KEY = 'kharos.ids'
const PASSWORDS_KEY = 'kharos.passwords'

/** Что запомнено по адресу сервера: у каждого сервера своё. */
function loadByUrl(key: string): Record<string, string> {
  try {
    const items = JSON.parse(localStorage.getItem(key) ?? '{}') as unknown
    return typeof items === 'object' && items !== null ? (items as Record<string, string>) : {}
  } catch {
    return {}
  }
}

/** Запоминает значение для сервера url; undefined — забывает. */
function storeByUrl(key: string, url: string, value: string | undefined) {
  try {
    const items = loadByUrl(key)
    if (value === undefined) delete items[url]
    else items[url] = value
    localStorage.setItem(key, JSON.stringify(items))
  } catch {
    // Без хранилища после перезагрузки сервер выдаст нового игрока, а пароль придётся ввести снова.
  }
}

/** Сервер не пустил без пароля или с неверным: wrong — пароль был, но не подошёл. */
export class PasswordRequired extends Error {
  constructor(
    message: string,
    readonly wrong: boolean,
  ) {
    super(message)
  }
}

/** Страница открыта по https, а сервер — по незащищённому ws://: браузер такое соединение блокирует. */
export const isInsecure = (url: string) => location.protocol === 'https:' && /^ws:\/\//i.test(url.trim())

/** Пароль, с которым на сервер url уже пускали. */
export const savedPassword = (url: string) => loadByUrl(PASSWORDS_KEY)[url] ?? ''

/** Сколько ждать приветствия сервера, в миллисекундах. */
const CONNECT_TIMEOUT = 15_000

/**
 * Подключается к серверу по WebSocket и ждёт приветствия. С сервером, где уже играл, — с прежним id, и сервер
 * отдаёт прежнего игрока; id из приветствия запоминается. name — ник. password — пароль сервера; пустой — тот, с
 * которым сюда уже пускали. Пароль, с которым пустили, запоминается, не подошедший — забывается.
 * lag — отладка: искусственная задержка в миллисекундах в каждую сторону, чтобы почувствовать плохую сеть.
 * signal — отмена: сокет закрывается, обещание отклоняется. Сервер, не приславший приветствия за
 * CONNECT_TIMEOUT, считается недоступным: мёртвый адрес иначе висел бы минутами.
 */
export function connect(url: string, lag = 0, name = '', password = '', signal?: AbortSignal): Promise<Session> {
  return new Promise((resolve, reject) => {
    const address = new URL(url)
    const id = loadByUrl(IDS_KEY)[url]
    if (id) address.searchParams.set('id', id)
    if (name) address.searchParams.set('name', name)
    password ||= savedPassword(url)
    if (password) address.searchParams.set('password', password)
    address.searchParams.set('version', String(PROTOCOL_VERSION))
    let refused: string | undefined
    const socket = new WebSocket(address)
    // Изменения мира приходят двоичными, см. codec.ts.
    socket.binaryType = 'arraybuffer'
    const delayed = (action: () => void) => (lag > 0 ? void setTimeout(action, lag) : action())
    let sim: Replica | undefined
    const fail = (error: Error) => {
      refused = error.message
      clearTimeout(timer)
      socket.close()
      reject(error)
    }
    const timer = setTimeout(() => fail(new Error(t('errors.noReply', { url, seconds: CONNECT_TIMEOUT / 1000 }))), CONNECT_TIMEOUT)
    signal?.addEventListener('abort', () => !sim && fail(new DOMException(t('errors.cancelled'), 'AbortError')))

    socket.onmessage = (event) => {
      delayed(() => {
        const message = decodeServer(event.data as string | ArrayBuffer)
        if (message.type === 'refused') {
          const reason = textOf(message.reason)
          refused = message.password ? t('errors.enterPassword', { reason }) : reason
          if (message.password) storeByUrl(PASSWORDS_KEY, url, undefined)
          if (sim) sim.fail(refused)
          else reject(message.password ? new PasswordRequired(reason, Boolean(password)) : new Error(refused))
          return
        }
        if (sim) return sim.receive(message)
        if (message.type !== 'welcome') return
        clearTimeout(timer)
        if (message.id) storeByUrl(IDS_KEY, url, message.id)
        if (password) storeByUrl(PASSWORDS_KEY, url, password)
        sim = createReplica(
          message,
          (text) => delayed(() => socket.readyState === WebSocket.OPEN && socket.send(text)),
          () => socket.close(),
        )
        resolve({ sim, player: message.player })
      })
    }
    socket.onclose = () => {
      clearTimeout(timer)
      if (refused) return
      if (sim) sim.fail(t('errors.lost'))
      else reject(new Error(`${t('errors.cannotConnect', { url })}${isInsecure(url) ? `\n\n${t('errors.insecure')}` : ''}`))
    }
  })
}

/**
 * Подключается к локальной игре в воркере и ждёт приветствия. Воркер общий, если браузер их умеет: тогда все
 * вкладки с одним name играют в один мир. onSave получает сохранения, которые воркер присылает сам, — в том числе
 * последнее, когда вкладка уходит из игры.
 */
/**
 * Подменяет мир слота в его воркере, если в слот сейчас играют: так правка из редактора не затрётся автосохранением
 * открытой игры. Если не играют — воркер ничего не делает: мир возьмут из сохранения при входе.
 */
export function reloadLocal(name: string, save: SimSave) {
  if (typeof SharedWorker === 'undefined') return
  const { port } = new SharedWorker(new URL('./local.worker.ts', import.meta.url), { type: 'module', name })
  port.postMessage({ type: 'reload', save } satisfies LocalControl)
  port.close()
}

export function connectLocal(setup: LocalSetup, onSave: (save: SimSave) => void, name: string): Promise<Session> {
  return new Promise((resolve) => {
    // Общий воркер, если браузер их умеет; нет — обычный; нет и его (нативная оболочка) — хост в том же потоке.
    let port: LocalPort
    if (typeof SharedWorker !== 'undefined') {
      port = new SharedWorker(new URL('./local.worker.ts', import.meta.url), { type: 'module', name }).port as unknown as LocalPort
    } else if (typeof Worker !== 'undefined') {
      port = new Worker(new URL('./local.worker.ts', import.meta.url), { type: 'module', name }) as unknown as LocalPort
    } else port = inProcess()
    const control = (message: LocalControl) => port.postMessage(message)
    const leave = () => control({ type: 'leave' })
    let sim: Replica | undefined

    port.onmessage = ({ data }) => {
      if (typeof data !== 'string' && !(data instanceof Uint8Array)) return onSave((data as LocalNotice).save)
      const message = decodeServer(data)
      if (sim) return sim.receive(message)
      if (message.type !== 'welcome') return
      sim = createReplica(message, (text) => port.postMessage(text), () => {
        leave()
        window.removeEventListener('pagehide', leave)
      })
      resolve({
        sim,
        player: message.player,
        local: {
          restart: (options, battle) => control({ type: 'restart', options, battle }),
        },
      })
    }
    // Закрытую вкладку воркер должен забыть: иначе слал бы мир в никуда.
    window.addEventListener('pagehide', leave)
    // Чем хост подписывает игрока без ника и свои сообщения — на языке игрока: воркер словаря не знает.
    control({ type: 'start', ...setup, names: { player: t('server.player'), server: t('server.name') } })
  })
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
