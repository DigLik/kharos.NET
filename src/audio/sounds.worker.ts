import { SYNTH_RATE, soundPieces } from './bank'

/** Сообщения воркера звука: очередная запись с долей готового — и в конце done. */
export type SoundsMessage = { key: string; channels: Float32Array<ArrayBuffer>[]; share: number } | { done: true }

/** Воркер звука: синтезирует весь набор вне страницы и отдаёт записи по одной, без копирования. */
const post = (message: SoundsMessage, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(message, transfer)
const pieces = [...soundPieces()]
pieces.forEach(([key, make], i) => {
  const channels = make(SYNTH_RATE) as Float32Array<ArrayBuffer>[]
  post({ key, channels, share: (i + 1) / pieces.length }, [...new Set(channels.map((channel) => channel.buffer))])
})
post({ done: true })
