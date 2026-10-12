/** Самый длинный кадр, который отдаётся игре. */
const MAX_FRAME_SECONDS = 0.25
/** На сколько миллисекунд кадр браузера может прийти раньше срока и всё же быть нарисован: интервалы между ними дрожат. */
const SLACK_MS = 2

/**
 * Вызывает frame каждый кадр браузера с реальным временем, прошедшим с прошлого кадра, в секундах.
 * Возвращает остановку. Ошибка в frame останавливает кадры: с onError она передаётся туда, без него — уходит в консоль.
 * limit — предел кадров в секунду, читается каждый кадр; 0 — без предела. Лишние кадры браузера пропускаются.
 */
export function startFrames(frame: (seconds: number) => void, onError?: (error: unknown) => void, limit?: () => number) {
  let handle = 0
  let last: number | undefined
  /** Раньше какого времени следующий кадр не рисуется. Срок копится, а не отсчитывается от кадра: так на 144 Гц предел 60 и даёт 60. */
  let due = 0
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
    // Вкладка была в фоне — кадр «длился» минуты. Игре такой скачок ни к чему.
    const seconds = last === undefined ? 0 : Math.min((now - last) / 1000, MAX_FRAME_SECONDS)
    last = now
    try {
      frame(seconds)
    } catch (error) {
      if (!onError) throw error
      onError(error)
      return
    }
    handle = requestAnimationFrame(tick)
  }
  handle = requestAnimationFrame(tick)
  return () => cancelAnimationFrame(handle)
}
