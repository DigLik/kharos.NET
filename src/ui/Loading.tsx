import { t } from '../i18n'

/** Экран загрузки: название игры и полоса готовности графики; share — доля готового, от 0 до 1. */
export function Loading({ share }: { share: number }) {
  const percent = Math.floor(share * 100)
  return (
    <main class="loading">
      <h1 class="loading__title">Kharos</h1>
      <div class="hud__progress loading__bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
        <span style={{ width: `${percent}%` }} />
      </div>
      <p class="loading__caption" role="status">{t('game.loadingArt', { percent })}</p>
    </main>
  )
}
