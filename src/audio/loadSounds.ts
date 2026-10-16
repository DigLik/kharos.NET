import { SYNTH_RATE, soundPieces } from './bank'
import type { SoundsMessage } from './sounds.worker'

const buffers = new Map<string, AudioBuffer>()
let loading: Promise<void> | null = null
const listeners = new Set<(share: number) => void>()
/** Без воркера звук синтезируется на странице кусками по столько миллисекунд — между ними идут кадры. */
const SLICE = 12

/** Запись в буфер звука. Буфер не привязан к контексту: один на страницу годится и меню, и любой игре. */
function store(key: string, channels: Float32Array[]) {
  // Без Web Audio звука нет вовсе — и хранить незачем.
  if (typeof AudioBuffer === 'undefined') return
  const buffer = new AudioBuffer({ numberOfChannels: channels.length, length: channels[0].length, sampleRate: SYNTH_RATE })
  channels.forEach((data, channel) => buffer.copyToChannel(data as Float32Array<ArrayBuffer>, channel))
  buffers.set(key, buffer)
}

/**
 * Синтезирует весь звук в воркере, один раз за жизнь страницы; повторный вызов ждёт того же. Записи сразу уходят
 * в буферы, и отдельные массивы не копятся. onProgress получает долю готового. Если воркер не запустился или упал
 * (в нативной оболочке воркеров нет), недостающее синтезируется прямо на странице — кусками между кадрами.
 */
export function loadSounds(onProgress?: (share: number) => void): Promise<void> {
  if (onProgress) listeners.add(onProgress)
  const done = () => onProgress && listeners.delete(onProgress)
  loading ??= new Promise<void>((resolve) => {
    // Звука нет вовсе: синтезировать незачем — класть некуда.
    if (typeof AudioBuffer === 'undefined') {
      listeners.forEach((listener) => listener(1))
      return resolve()
    }
    let worker: Worker | undefined
    let finishing = false
    const finish = async () => {
      if (finishing) return
      finishing = true
      worker?.terminate()
      // Недостающее — кусками между кадрами: страница не замирает, а полоса загрузки идёт.
      const pieces = soundPieces()
      let since = performance.now()
      for (const [key, make] of pieces) {
        if (buffers.has(key)) continue
        store(key, make(SYNTH_RATE))
        if (performance.now() - since < SLICE) continue
        listeners.forEach((listener) => listener(buffers.size / pieces.size))
        await new Promise((next) => setTimeout(next))
        since = performance.now()
      }
      listeners.forEach((listener) => listener(1))
      resolve()
    }
    try {
      worker = new Worker(new URL('./sounds.worker.ts', import.meta.url), { type: 'module', name: 'kharos-sounds' })
    } catch {
      return finish()
    }
    worker.onmessage = ({ data }: MessageEvent<SoundsMessage>) => {
      if ('done' in data) return finish()
      store(data.key, data.channels)
      listeners.forEach((listener) => listener(data.share))
    }
    worker.onerror = finish
  })
  return loading.finally(done)
}

/**
 * Готовая запись key. rate — частота контекста, для которого нужна запись с той же частотой (эхо свёртки):
 * если контекст не взял SYNTH_RATE, запись синтезируется под него заново.
 */
export function soundBuffer(key: string, rate = SYNTH_RATE): AudioBuffer {
  if (rate !== SYNTH_RATE) {
    const channels = soundPieces().get(key)!(rate)
    const buffer = new AudioBuffer({ numberOfChannels: channels.length, length: channels[0].length, sampleRate: rate })
    channels.forEach((data, channel) => buffer.copyToChannel(data as Float32Array<ArrayBuffer>, channel))
    return buffer
  }
  const buffer = buffers.get(key)
  if (!buffer) throw new Error(`Звук ${key} ещё не готов: сначала дождитесь loadSounds`)
  return buffer
}

/** Контекст звука на частоте набора; браузер, который частоту выбрать не даёт, создаёт свой. */
export function createContext(Context: typeof AudioContext) {
  try {
    return new Context({ sampleRate: SYNTH_RATE })
  } catch {
    return new Context()
  }
}
