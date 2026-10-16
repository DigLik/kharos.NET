/**
 * Настройки графики игрока. Эффекты не выключаются, а упрощаются: у каждого три уровня детализации. Хранятся
 * в браузере; рендер читает их каждый кадр, так что смена в меню видна сразу.
 */

/** Уровень детализации: 0 — низкий, 1 — средний, 2 — высокий. */
export type Detail = 0 | 1 | 2

export interface Graphics {
  /** Доля физических пикселей экрана, в которых рисуется мир; плотнее двух на пиксель CSS не бывает. Интерфейс не трогает. */
  resolution: 0.5 | 0.75 | 1
  /** Земля издалека, когда тексель мельче двух пикселей экрана: усреднять 4, 2 или 1 точку в пикселе. */
  terrain: Detail
  /** Тени от фар: сколько раз луч проверяется на преграду. */
  shadows: Detail
  /** Капли, всплески и хлопья: сколько соседних капель ищется и сколько слоёв снега. */
  weather: Detail
  /** Предел кадров в секунду; 0 — без предела. */
  fps: 0 | 30 | 60
}

export type Preset = 'low' | 'medium' | 'high'

export const PRESETS: Record<Preset, Graphics> = {
  low: { resolution: 0.5, terrain: 0, shadows: 0, weather: 0, fps: 30 },
  medium: { resolution: 0.75, terrain: 1, shadows: 1, weather: 1, fps: 60 },
  high: { resolution: 1, terrain: 2, shadows: 2, weather: 2, fps: 0 },
}

export const RESOLUTIONS: readonly Graphics['resolution'][] = [0.5, 0.75, 1]
export const FPS_LIMITS: readonly Graphics['fps'][] = [30, 60, 0]

const KEY = 'kharos.graphics'

/** Какому набору соответствуют настройки; null — подобраны вручную. */
export function presetOf(value: Graphics): Preset | null {
  for (const preset of Object.keys(PRESETS) as Preset[]) {
    const sample = PRESETS[preset]
    if ((Object.keys(sample) as (keyof Graphics)[]).every((key) => sample[key] === value[key])) return preset
  }
  return null
}

/** Без сохранённого выбора: телефону и планшету — средний набор, остальным — высокий. */
function defaults(): Graphics {
  const touch = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches && !matchMedia('(pointer: fine)').matches
  return { ...PRESETS[touch ? 'medium' : 'high'] }
}

const pick = <T>(value: unknown, allowed: readonly T[], fallback: T) => (allowed.includes(value as T) ? (value as T) : fallback)

function load(): Graphics {
  const base = defaults()
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) ?? 'null') as Partial<Graphics> | null
    if (!saved) return base
    const details: readonly Detail[] = [0, 1, 2]
    return {
      resolution: pick(saved.resolution, RESOLUTIONS, base.resolution),
      terrain: pick(saved.terrain, details, base.terrain),
      shadows: pick(saved.shadows, details, base.shadows),
      weather: pick(saved.weather, details, base.weather),
      fps: pick(saved.fps, FPS_LIMITS, base.fps),
    }
  } catch {
    return base
  }
}

let current: Graphics | null = null

/** Текущие настройки. Объект заменяется целиком при смене — не меняй его. */
export function graphics(): Graphics {
  current ??= load()
  return current
}

export function setGraphics(next: Graphics) {
  current = { ...next }
  try {
    localStorage.setItem(KEY, JSON.stringify(current))
  } catch {
    // Хранилище недоступно: настройки действуют до перезагрузки.
  }
}
