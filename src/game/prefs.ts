/**
 * Настройки интерфейса игрока: то, что дело вкуса, а не мощности устройства, — поэтому не в наборах графики
 * (render/graphics.ts). Хранятся в браузере; отрисовка читает их каждый кадр, так что смена в меню видна сразу.
 */
export interface Prefs {
  /** Яркость колец и рамок выделения: доля от полной. */
  selection: 0.45 | 0.7 | 1
  /** Сторона мини-карты вместе с рамкой, в пикселях CSS. 140 — вровень с нижней панелью, крупнее — выступает над ней. */
  minimap: 140 | 200 | 260
}

export const SELECTION_LEVELS: readonly Prefs['selection'][] = [0.45, 0.7, 1]
export const MINIMAP_SIDES: readonly Prefs['minimap'][] = [140, 200, 260]

const DEFAULTS: Prefs = { selection: 0.7, minimap: 200 }
const KEY = 'kharos.prefs'

const pick = <T>(value: unknown, allowed: readonly T[], fallback: T) => (allowed.includes(value as T) ? (value as T) : fallback)

function load(): Prefs {
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) ?? 'null') as Partial<Prefs> | null
    if (!saved) return { ...DEFAULTS }
    return {
      selection: pick(saved.selection, SELECTION_LEVELS, DEFAULTS.selection),
      minimap: pick(saved.minimap, MINIMAP_SIDES, DEFAULTS.minimap),
    }
  } catch {
    return { ...DEFAULTS }
  }
}

let current: Prefs | null = null

/** Текущие настройки. Объект заменяется целиком при смене — не меняй его. */
export function prefs(): Prefs {
  current ??= load()
  return current
}

export function setPrefs(next: Prefs) {
  current = { ...next }
  try {
    localStorage.setItem(KEY, JSON.stringify(current))
  } catch {
    // Хранилище недоступно: настройки действуют до перезагрузки.
  }
}
