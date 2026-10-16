/** Самый длинный кадр, который отдаётся игре. */
const MAX_FRAME_SECONDS = 0.25
/** На сколько миллисекунд кадр браузера может прийти раньше срока и всё же быть нарисован: интервалы между ними дрожат. */
const SLACK_MS = 2
/** Как часто идут кадры скрытой вкладки, в миллисекундах. Таймеры звучащей вкладки браузер не замедляет. */
const BACKGROUND_MS = 50

/**
 * Вызывает frame каждый кадр браузера с реальным временем, прошедшим с прошлого кадра, в секундах.
 * Возвращает остановку. Ошибка в frame останавливает кадры: с onError она передаётся туда, без него — уходит в консоль.
 * limit — предел кадров в секунду, читается каждый кадр; 0 — без предела. Лишние кадры браузера пропускаются.
 * background — кадры идут и в скрытой вкладке, где браузер их не даёт: по таймеру, с hidden = true. Рисовать в них
 * незачем, а вот игре и её звуку надо идти.
 */
export function startFrames(frame: (seconds: number, hidden: boolean) => void, onError?: (error: unknown) => void, limit?: () => number, background = false) {
  let handle = 0
  let timer: ReturnType<typeof setInterval> | undefined
  let last: number | undefined
  /** Раньше какого времени следующий кадр не рисуется. Срок копится, а не отсчитывается от кадра: так на 144 Гц предел 60 и даёт 60. */
  let due = 0
  const stop = () => {
    cancelAnimationFrame(handle)
    clearInterval(timer)
  }
  /** Отдаёт кадр игре; false — кадры остановлены ошибкой. */
  const step = (now: number, hidden: boolean) => {
    // Вкладка была в фоне — кадр «длился» минуты. Игре такой скачок ни к чему.
    const seconds = last === undefined ? 0 : Math.max(0, Math.min((now - last) / 1000, MAX_FRAME_SECONDS))
    last = now
    try {
      frame(seconds, hidden)
    } catch (error) {
      stop()
      if (!onError) throw error
      onError(error)
      return false
    }
    return true
  }
  const tick = (now: number) => {
    const fps = limit?.() ?? 0
    if (fps > 0 && last !== undefined) {
      if (now < due - SLACK_MS) {
        handle = requestAnimationFrame(tick)
        return
      }
      const interval = 1000 / fps
      due = Math.max(due, now - interval) + interval
    }
    if (step(now, false)) handle = requestAnimationFrame(tick)
  }
  handle = requestAnimationFrame(tick)
  if (background) timer = setInterval(() => document.hidden && step(performance.now(), true), BACKGROUND_MS)
  return stop
}
