import { drawArt, type Art } from './art'
import type { ArtMessage } from './art.worker'

let art: Art | null = null
let loading: Promise<Art> | null = null
const listeners = new Set<(share: number) => void>()

/**
 * Рисует графику игры в воркере, один раз за жизнь страницы; повторный вызов ждёт того же. onProgress получает долю
 * готового. Если воркер не запустился, графика рисуется прямо на странице — так она замрёт, но игра откроется.
 */
export function loadArt(onProgress?: (share: number) => void): Promise<Art> {
  if (onProgress) listeners.add(onProgress)
  const done = () => onProgress && listeners.delete(onProgress)
  loading ??= new Promise<Art>((resolve) => {
    const finish = (result: Art) => {
      art = result
      listeners.forEach((listener) => listener(1))
      resolve(result)
    }
    let worker: Worker | undefined
    const fallback = () => {
      worker?.terminate()
      finish(drawArt())
    }
    try {
      worker = new Worker(new URL('./art.worker.ts', import.meta.url), { type: 'module', name: 'kharos-art' })
    } catch {
      return fallback()
    }
    worker.onmessage = ({ data }: MessageEvent<ArtMessage>) => {
      if ('progress' in data) return listeners.forEach((listener) => listener(data.progress))
      worker.terminate()
      finish(data.art)
    }
    worker.onerror = fallback
  })
  return loading.finally(done)
}

/** Готовая графика. Игру создают только после loadArt — до неё это ошибка. */
export function loadedArt(): Art {
  if (!art) throw new Error('Графика игры ещё не нарисована: сначала дождитесь loadArt')
  return art
}
