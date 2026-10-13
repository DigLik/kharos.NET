import { useEffect, useRef } from 'preact/hooks'
import { t } from '../i18n'

interface LoadingProps {
  /** Доля готового, от 0 до 1. */
  share: number
  /** Всё готово: вместо подписи — кнопка, и её нажатие разрешает браузеру звук. */
  ready: boolean
  proceed(): void
}

/** Экран загрузки: название игры, полоса готовности графики и звука, а когда всё готово, — кнопка «Продолжить». */
export function Loading({ share, ready, proceed }: LoadingProps) {
  const percent = Math.floor(share * 100)
  const button = useRef<HTMLButtonElement>(null)
  // Продолжить можно и с клавиатуры: Enter или пробел нажимают кнопку.
  useEffect(() => {
    if (ready) button.current?.focus()
  }, [ready])
  return (
    <main class="loading">
      <h1 class="loading__title">Kharos</h1>
      <div class="hud__progress loading__bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
        <span style={{ width: `${percent}%` }} />
      </div>
      {ready ? (
        <button ref={button} class="menu__primary" onClick={proceed}>
          {t('common.resume')}
        </button>
      ) : (
        <p class="loading__caption" role="status">
          {t('game.loadingAssets', { percent })}
        </p>
      )}
    </main>
  )
}
