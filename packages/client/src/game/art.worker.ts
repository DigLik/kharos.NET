import { artBuffers, drawArt } from './art'

/** Сообщения воркера графики: доли готового по ходу рисования и в конце — сама графика. */
export type ArtMessage = { progress: number } | { art: ReturnType<typeof drawArt> }

/** Сколько готового набирается между сообщениями о ходе: чаще странице незачем перерисовывать полосу. */
const PROGRESS_STEP = 0.01

/** Воркер графики: рисует атласы вне страницы, чтобы она не замирала и не держала в памяти тысячи отдельных кадров. */
const post = (message: ArtMessage, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(message, transfer)
let told = 0
const art = drawArt((share) => {
  if (share - told < PROGRESS_STEP) return
  told = share
  post({ progress: share })
})
post({ art }, artBuffers(art))
