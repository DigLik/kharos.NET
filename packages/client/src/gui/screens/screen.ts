import type { Overlay } from '../pass'
import type { Ui } from '../ui'

/**
 * Экран приложения на холсте: загрузка, меню над витриной, игра, редактор. Экран описывает свои панели каждый кадр
 * интерфейса и отвечает, сколько с краёв экрана ими закрыто, — игра ставит камеру между ними.
 */
export interface Screen {
  draw(ui: Ui): void
  /** Экрану нужен кадр интерфейса каждый кадр, а не раз в 100 мс: на нём что-то движется (рамки камер игроков). */
  readonly animate?: boolean
  /** Сколько пикселей CSS с краёв экрана закрыто панелями экрана. */
  insets?(width: number, height: number): { top: number; bottom: number; right: number }
  destroy(): void
}

/** Холст интерфейса для игры (см. GameOptions.overlay): кладёт его в кадр и не отдаёт миру нажатия по панелям. */
export type ScreenOverlay = Overlay & {
  covers(x: number, y: number): boolean
  insets(width: number, height: number): { top: number; bottom: number; right: number }
}
